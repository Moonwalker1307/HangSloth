using HangSloth.Analysis;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

/// Reads a ClrMD runtime and produces a <see cref="ProcessSnapshot"/>: threads with
/// source-resolved stacks, held monitors and who waits on them, the task each blocked
/// thread is waiting for (and what that task is itself waiting for), and thread pool state.
///
/// Everything here is about gathering facts. Judging whether those facts mean
/// "deadlock" is the analyzers' job.
public sealed class SnapshotBuilder
{
    private const int MaxFramesPerThread = 200;
    private const int MaxTaskChainDepth = 16;
    private const int MaxContextSearchObjects = 50_000;
    private const int MaxContextSearchDepth = 8;

    // Task.m_stateFlags bits (see System.Threading.Tasks.Task).
    private const int TaskStateFaulted = 0x200000;
    private const int TaskStateCanceled = 0x400000;
    private const int TaskStateRanToCompletion = 0x1000000;

    private readonly ClrRuntime _runtime;
    private readonly ClrHeap _heap;
    private readonly SourceResolver _sources;
    private readonly List<string> _warnings = [];
    private readonly Dictionary<ulong, HashSet<ulong>> _contextDelegateTargets = [];
    private readonly Dictionary<ulong, ObjectRef> _contextsInUse = [];

    private SnapshotBuilder(ClrRuntime runtime, SourceResolver sources)
    {
        _runtime = runtime;
        _heap = runtime.Heap;
        _sources = sources;
    }

    public static ProcessSnapshot Build(ClrRuntime runtime, string source, SourceResolver sources, string? runtimeVersion = null) =>
        new SnapshotBuilder(runtime, sources).Build(source, runtimeVersion);

    private ProcessSnapshot Build(string source, string? runtimeVersion)
    {
        var managedThreads = FindManagedThreadObjects();
        var clrThreads = _runtime.Threads.Where(t => t.IsAlive && (t.State & ClrThreadState.TS_Unstarted) == 0).ToList();
        var byAddress = clrThreads.ToDictionary(t => t.Address, t => t.ManagedThreadId);

        // First pass: stacks, names, contexts. Task chains need to know every context in use, so they come after.
        var partial = new List<(ClrThread Clr, ThreadSnapshot Snapshot, List<ClrStackRoot> Roots)>();
        foreach (var thread in clrThreads)
        {
            var frames = ReadFrames(thread);
            managedThreads.TryGetValue(thread.ManagedThreadId, out var threadObject);
            var ctx = ReadSynchronizationContext(threadObject);
            if (ctx is not null) _contextsInUse[ctx.Address] = ctx;

            var snapshot = new ThreadSnapshot
            {
                ManagedId = thread.ManagedThreadId,
                OSThreadId = thread.OSThreadId,
                Name = ReadThreadName(threadObject),
                IsThreadPoolWorker = frames.Any(FrameClassifier.IsThreadPoolDispatchFrame)
                                     || ((thread.State & ClrThreadState.TS_TPWorkerThread) != 0 && !frames.Any(FrameClassifier.IsThreadPoolServiceFrame)),
                IsFinalizer = thread.IsFinalizer,
                IsBackground = (thread.State & ClrThreadState.TS_Background) != 0,
                Frames = frames,
                SynchronizationContext = ctx,
            };
            partial.Add((thread, snapshot, SafeRoots(thread)));
        }

        var locks = ReadLocks(byAddress);

        var threads = new List<ThreadSnapshot>();
        foreach (var (clr, snapshot, roots) in partial)
        {
            var result = snapshot;
            var kind = FrameClassifier.Classify(snapshot.Frames);
            if (kind == BlockingKind.TaskWait)
                result = result with { BlockedOnTask = FindBlockedOnTask(roots) };
            if (kind == BlockingKind.MonitorEnter)
                result = result with { WaitingForLock = FindLockBeingEntered(clr, roots, locks, clrThreads) };
            threads.Add(result);
        }

        // Fill in waiters from what each thread is trying to enter, and name each lock after the field holding it.
        var fieldNames = NameLockObjects(locks.Keys.ToHashSet(), partial.SelectMany(p => p.Roots));
        var lockList = locks.Values
            .Select(l => l with
            {
                WaiterThreadIds = threads.Where(t => t.WaitingForLock == l.Object.Address).Select(t => t.ManagedId).ToList(),
                FieldName = fieldNames.GetValueOrDefault(l.Object.Address),
            })
            .ToList();

        return new ProcessSnapshot
        {
            Source = source,
            RuntimeVersion = runtimeVersion,
            Threads = threads,
            Locks = lockList,
            ThreadPool = ReadThreadPool(),
            Warnings = _warnings,
        };
    }

    // ---------------------------------------------------------------- threads

    private Dictionary<int, ClrObject> FindManagedThreadObjects()
    {
        var result = new Dictionary<int, ClrObject>();
        if (!_heap.CanWalkHeap)
        {
            _warnings.Add("The GC heap could not be walked (was the dump taken mid-GC?). Thread names, SynchronizationContexts and task details are unavailable.");
            return result;
        }
        try
        {
            foreach (var obj in _heap.EnumerateObjects())
            {
                if (obj.Type?.Name != "System.Threading.Thread") continue;
                if (obj.TryReadField<int>("_managedThreadId", out var id) && id != 0)
                    result[id] = obj;
            }
        }
        catch (Exception e)
        {
            _warnings.Add($"Walking the heap failed part-way: {e.Message}");
        }
        return result;
    }

    private static string? ReadThreadName(ClrObject threadObject)
    {
        if (threadObject.IsNull) return null;
        return threadObject.TryReadStringField("_name", null, out var name) ? name : null;
    }

    private static ObjectRef? ReadSynchronizationContext(ClrObject threadObject)
    {
        if (threadObject.IsNull) return null;
        if (!threadObject.TryReadObjectField("_synchronizationContext", out var ctx) || ctx.IsNull || ctx.Type is null)
            return null;
        return new ObjectRef(ctx.Address, ctx.Type.Name ?? "SynchronizationContext");
    }

    private List<StackFrameInfo> ReadFrames(ClrThread thread)
    {
        var frames = new List<StackFrameInfo>();
        try
        {
            var first = true;
            foreach (var frame in thread.EnumerateStackTrace(includeContext: false, MaxFramesPerThread))
            {
                var isFirst = first;
                first = false;
                var method = frame.Method;
                if (frame.Kind == ClrStackFrameKind.ManagedMethod && method is not null)
                {
                    frames.Add(new StackFrameInfo
                    {
                        TypeName = method.Type?.Name ?? "",
                        MethodName = method.Name ?? "?",
                        ModuleName = method.Type?.Module?.Name,
                        Source = ResolveSource(method, frame.InstructionPointer, isFirst),
                    });
                }
                else if (method is not null)
                {
                    // Runtime helper frames (e.g. a HelperMethodFrame for Monitor.ReliableEnter) carry the
                    // managed method they stand in for, which is exactly what tells us *why* a thread is waiting.
                    frames.Add(new StackFrameInfo
                    {
                        TypeName = method.Type?.Name ?? "",
                        MethodName = method.Name ?? "?",
                        ModuleName = method.Type?.Module?.Name,
                        IsRuntimeFrame = true,
                    });
                }
            }
        }
        catch (Exception e)
        {
            _warnings.Add($"Could not fully walk the stack of thread {thread.ManagedThreadId}: {e.Message}");
        }
        return frames;
    }

    private SourceLocation? ResolveSource(ClrMethod method, ulong ip, bool isLeaf)
    {
        var module = method.Type?.Module;
        if (module is null || module.IsDynamic || FrameClassifier.IsFrameworkModule(module.Name)) return null;
        try
        {
            // Non-leaf frames sit at a return address, which belongs to the *next* instruction.
            var il = method.GetILOffset(isLeaf ? ip : ip - 1);
            return _sources.Resolve(module.Name, method.MetadataToken, il);
        }
        catch
        {
            return null;
        }
    }

    private List<ClrStackRoot> SafeRoots(ClrThread thread)
    {
        try
        {
            return thread.EnumerateStackRoots().ToList();
        }
        catch
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- locks

    private Dictionary<ulong, MonitorLock> ReadLocks(Dictionary<ulong, int> threadIdByAddress)
    {
        var locks = new Dictionary<ulong, MonitorLock>();
        try
        {
            foreach (var sb in _heap.EnumerateSyncBlocks())
            {
                if (!sb.IsMonitorHeld || sb.Object == 0) continue;
                var type = _heap.GetObjectType(sb.Object)?.Name ?? "object";
                locks[sb.Object] = new MonitorLock
                {
                    Object = new ObjectRef(sb.Object, type),
                    OwnerThreadId = threadIdByAddress.TryGetValue(sb.HoldingThreadAddress, out var id) ? id : null,
                    Recursion = sb.RecursionCount,
                };
            }
        }
        catch (Exception e)
        {
            _warnings.Add($"Could not read the sync block table: {e.Message}");
        }
        return locks;
    }

    private static ulong? FindLockBeingEntered(ClrThread thread, List<ClrStackRoot> roots, Dictionary<ulong, MonitorLock> locks, List<ClrThread> allThreads)
    {
        // The object being entered is reported as a root on the waiting thread's stack (the helper frame
        // protects it). Roots come innermost frame first, so the first held lock we don't own wins.
        foreach (var root in roots)
        {
            if (locks.TryGetValue(root.Object.Address, out var l) && l.OwnerThreadId != thread.ManagedThreadId)
                return root.Object.Address;

            // .NET 9+ System.Threading.Lock is a managed lock (no sync block); it records its owner itself
            // (the OS thread id on CoreCLR, the managed id on other runtimes).
            if (root.Object.Type?.Name == "System.Threading.Lock"
                && root.Object.TryReadField<uint>("_owningThreadId", out var owner) && owner != 0)
            {
                var ownerThread = allThreads.FirstOrDefault(t => t.OSThreadId == owner)
                                  ?? allThreads.FirstOrDefault(t => t.ManagedThreadId == owner);
                if (ownerThread == thread) continue;
                locks[root.Object.Address] = new MonitorLock
                {
                    Object = new ObjectRef(root.Object.Address, "System.Threading.Lock"),
                    OwnerThreadId = ownerThread?.ManagedThreadId,
                    Recursion = root.Object.TryReadField<uint>("_recursionCount", out var r) ? (int)r : 0,
                };
                return root.Object.Address;
            }
        }
        return null;
    }

    /// "lock on object" says nothing; "lock on OrderCache._sync" does. Look for the static field, or the
    /// field of an object on an involved thread's stack, that references each lock object.
    private Dictionary<ulong, string> NameLockObjects(HashSet<ulong> lockAddresses, IEnumerable<ClrStackRoot> roots)
    {
        var names = new Dictionary<ulong, string>();
        if (lockAddresses.Count == 0) return names;
        try
        {
            var domain = _runtime.AppDomains.FirstOrDefault();
            if (domain is not null)
            {
                foreach (var module in _runtime.EnumerateModules())
                {
                    if (module.IsDynamic || FrameClassifier.IsFrameworkModule(module.Name)) continue;
                    foreach (var type in module.EnumerateTypesWithStaticFields())
                    {
                        foreach (var field in type.StaticFields)
                        {
                            if (!field.IsObjectReference || !field.IsInitialized(domain)) continue;
                            var value = field.ReadObject(domain);
                            if (!value.IsNull && lockAddresses.Contains(value.Address) && !names.ContainsKey(value.Address))
                                names[value.Address] = $"{Names.ShortType(type.Name ?? "")}.{field.Name}";
                        }
                    }
                }
            }

            foreach (var root in roots)
            {
                var obj = root.Object;
                if (obj.IsNull || obj.Type is null || lockAddresses.Contains(obj.Address) || FrameClassifier.IsFrameworkType(obj.Type.Name ?? "")) continue;
                foreach (var field in obj.Type.Fields)
                {
                    if (!field.IsObjectReference) continue;
                    var value = obj.ReadObjectField(field);
                    if (!value.IsNull && lockAddresses.Contains(value.Address) && !names.ContainsKey(value.Address))
                        names[value.Address] = $"{Names.ShortType(obj.Type.Name ?? "")}.{field.Name}";
                }
            }
        }
        catch
        {
            // Names are a nicety.
        }
        return names;
    }

    // ---------------------------------------------------------------- tasks

    private TaskInfo? FindBlockedOnTask(List<ClrStackRoot> roots)
    {
        // The waited task is a live root in the Task.Wait/GetResultCore frames. Prefer a pending one
        // reported by a wait frame; fall back to the innermost pending task on the stack.
        ClrObject? fallback = null;
        foreach (var root in roots)
        {
            var obj = root.Object;
            if (obj.IsNull || !IsTask(obj.Type)) continue;
            if (ReadTaskState(obj) != TaskState.Pending) continue;
            var frameMethod = root.StackFrame?.Method;
            if (frameMethod is not null && FrameClassifier.IsTaskWaitMethod(frameMethod.Type?.Name ?? "", frameMethod.Name ?? ""))
                return DescribeTask(obj, 0, []);
            fallback ??= obj;
        }
        return fallback is { } f ? DescribeTask(f, 0, []) : null;
    }

    private TaskInfo DescribeTask(ClrObject task, int depth, HashSet<ulong> visited)
    {
        visited.Add(task.Address);
        var typeName = task.Type?.Name ?? "System.Threading.Tasks.Task";
        var info = new TaskInfo
        {
            Address = task.Address,
            TypeName = typeName,
            State = ReadTaskState(task),
            Kind = TaskKind.Unknown,
            Description = Names.ShortType(typeName),
        };

        if (typeName.Contains("AsyncStateMachineBox", StringComparison.Ordinal))
            return DescribeAsyncMethod(task, info, depth, visited);

        if (typeName.Contains("DelayPromise", StringComparison.Ordinal))
            return info with { Kind = TaskKind.Delay, Description = "Task.Delay(...)" };
        if (typeName.Contains("WhenAllPromise", StringComparison.Ordinal))
            return info with { Kind = TaskKind.WhenAll, Description = "Task.WhenAll(...)" };
        if (typeName.Contains("WhenAnyPromise", StringComparison.Ordinal))
            return info with { Kind = TaskKind.WhenAny, Description = "Task.WhenAny(...)" };
        if (typeName.Contains("ContinuationTask", StringComparison.Ordinal))
            return info with { Kind = TaskKind.Continuation, Description = "a ContinueWith(...) task" };

        if (task.TryReadObjectField("m_action", out var action) && !action.IsNull)
        {
            var target = action.IsDelegate ? action.AsDelegate().GetDelegateTarget()?.Method : null;
            var name = target is null ? "a delegate" : Names.ShortMethod(target.Type?.Name ?? "", target.Name ?? "?");
            return info with { Kind = TaskKind.Delegate, Description = $"Task.Run({name})" };
        }

        var generic = GenericArgument(typeName, "Task<");
        return info with
        {
            Kind = TaskKind.Promise,
            Description = generic is null ? "a TaskCompletionSource task" : $"a TaskCompletionSource<{Names.ShortType(generic)}> task",
        };
    }

    private TaskInfo DescribeAsyncMethod(ClrObject box, TaskInfo info, int depth, HashSet<ulong> visited)
    {
        info = info with { Kind = TaskKind.AsyncMethod };
        var smTypeName = GenericArgument(info.TypeName, "AsyncStateMachineBox<");
        if (smTypeName is not null)
            info = info with { Description = Names.ShortMethod(smTypeName, "") };

        if (!TryReadStateMachine(box, out var stateMachine))
            return info;

        var smType = stateMachine.Type;
        if (smType?.Name is { } exactName)
            info = info with { Description = Names.ShortMethod(exactName, "") };

        var state = stateMachine.ReadInt("<>1__state");
        var moveNext = smType?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext is not null && state is { } s && s >= 0)
            info = info with { AwaitLocation = SafeResolveAwait(smType!.Module?.Name, moveNext.MetadataToken, s) };

        var awaited = smType is null ? null : FindAwaitedTask(stateMachine, smType);
        if (awaited is { } inner)
        {
            info = info with { Resume = FindResumeTarget(box, inner) };
            if (depth < MaxTaskChainDepth && !visited.Contains(inner.Address))
                info = info with { Awaiting = DescribeTask(inner, depth + 1, visited) };
        }
        else if (info.State == TaskState.Pending)
        {
            info = info with { Resume = FindQueuedResume(box) };
        }
        return info;
    }

    private SourceLocation? SafeResolveAwait(string? module, int token, int state)
    {
        try { return _sources.ResolveAwait(module, token, state); }
        catch { return null; }
    }

    /// The state machine lives in the box's StateMachine field: a struct in Release builds, a class in Debug builds.
    private static bool TryReadStateMachine(ClrObject box, out FieldReader stateMachine)
    {
        stateMachine = default;
        var field = box.Type?.GetFieldByName("StateMachine");
        if (field is null) return false;
        try
        {
            if (field.IsValueType)
            {
                stateMachine = FieldReader.Of(box.ReadValueTypeField(field));
                return stateMachine.Type is not null;
            }
            var obj = box.ReadObjectField(field);
            if (obj.IsNull) return false;
            stateMachine = FieldReader.Of(obj);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// The compiler stores the in-flight awaiter in a "&lt;&gt;u__N" field and clears it after resuming,
    /// so a non-empty awaiter field holds the task the method is suspended on.
    private ClrObject? FindAwaitedTask(FieldReader stateMachine, ClrType smType)
    {
        foreach (var field in smType.Fields)
        {
            if (field.Name is null || !field.Name.StartsWith("<>u__", StringComparison.Ordinal)) continue;
            try
            {
                if (field.IsObjectReference)
                {
                    var obj = stateMachine.ReadObject(field);
                    if (!obj.IsNull && IsTask(obj.Type)) return obj;
                    continue;
                }
                if (field.IsValueType && stateMachine.ReadStruct(field) is { } awaiter && FindTaskInStruct(awaiter, 0) is { } found)
                    return found;
            }
            catch
            {
                // Unreadable awaiter; try the next one.
            }
        }
        return null;
    }

    private ClrObject? FindTaskInStruct(ClrValueType value, int depth)
    {
        if (value.Type is null || depth > 3) return null;
        foreach (var field in value.Type.Fields)
        {
            if (field.IsObjectReference)
            {
                var obj = value.ReadObjectField(field);
                if (!obj.IsNull && IsTask(obj.Type)) return obj;
            }
            else if (field.IsValueType && !field.IsPrimitive)
            {
                var inner = value.ReadValueTypeField(field);
                if (FindTaskInStruct(inner, depth + 1) is { } found) return found;
            }
        }
        return null;
    }

    /// Who will run <paramref name="box"/>'s continuation once <paramref name="awaited"/> finishes?
    private ResumeTarget? FindResumeTarget(ClrObject box, ClrObject awaited)
    {
        foreach (var continuation in EnumerateContinuations(awaited))
        {
            var typeName = continuation.Type?.Name ?? "";
            if (!typeName.EndsWith("SynchronizationContextAwaitTaskContinuation", StringComparison.Ordinal)) continue;
            if (!continuation.TryReadObjectField("m_action", out var action) || DelegateTarget(action) != box.Address) continue;
            if (!continuation.TryReadObjectField("m_syncContext", out var ctx) || ctx.IsNull) continue;
            return new ResumeTarget(new ObjectRef(ctx.Address, ctx.Type?.Name ?? "SynchronizationContext"), ResumeStatus.Registered);
        }

        // If the awaited task already finished, the continuation may be sitting in a context's queue.
        return ReadTaskState(awaited) == TaskState.Pending ? null : FindQueuedResume(box);
    }

    private IEnumerable<ClrObject> EnumerateContinuations(ClrObject task)
    {
        if (!task.TryReadObjectField("m_continuationObject", out var c) || c.IsNull) yield break;
        var typeName = c.Type?.Name ?? "";
        if (typeName.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal))
        {
            if (!c.TryReadObjectField("_items", out var items) || items.IsNull || !items.IsArray) yield break;
            var array = items.AsArray();
            var size = c.ReadInt("_size") ?? array.Length;
            for (var i = 0; i < Math.Min(size, array.Length); i++)
            {
                var element = array.GetObjectValue(i);
                if (!element.IsNull) yield return element;
            }
        }
        else
        {
            yield return c;
        }
    }

    private ResumeTarget? FindQueuedResume(ClrObject box)
    {
        foreach (var (address, ctx) in _contextsInUse)
        {
            if (!_contextDelegateTargets.TryGetValue(address, out var targets))
            {
                targets = CollectDelegateTargets(address);
                _contextDelegateTargets[address] = targets;
            }
            if (targets.Contains(box.Address))
                return new ResumeTarget(ctx, ResumeStatus.Queued);
        }
        return null;
    }

    /// Breadth-first walk from a SynchronizationContext, collecting the targets of every delegate it can reach.
    /// A posted continuation is a delegate whose target is the async method's box, held by the context's queue.
    private HashSet<ulong> CollectDelegateTargets(ulong contextAddress)
    {
        var targets = new HashSet<ulong>();
        var seen = new HashSet<ulong> { contextAddress };
        var frontier = new List<ulong> { contextAddress };
        try
        {
            for (var depth = 0; depth < MaxContextSearchDepth && frontier.Count > 0 && seen.Count < MaxContextSearchObjects; depth++)
            {
                var next = new List<ulong>();
                foreach (var address in frontier)
                {
                    var obj = _heap.GetObject(address);
                    if (!obj.IsValid) continue;
                    if (obj.IsDelegate && obj.TryReadObjectField("_target", out var target) && !target.IsNull)
                        targets.Add(target.Address);
                    // Don't wander into other tasks' graphs or thread objects; queues hold delegates and tuples.
                    if (IsTask(obj.Type) || obj.Type?.Name == "System.Threading.Thread") continue;
                    foreach (var reference in obj.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false))
                    {
                        if (seen.Count >= MaxContextSearchObjects) break;
                        if (seen.Add(reference)) next.Add(reference);
                    }
                }
                frontier = next;
            }
        }
        catch
        {
            // Partial results are still useful.
        }
        return targets;
    }

    private static ulong DelegateTarget(ClrObject del) =>
        !del.IsNull && del.TryReadObjectField("_target", out var target) && !target.IsNull ? target.Address : 0;

    private static TaskState ReadTaskState(ClrObject task)
    {
        if (!task.TryReadField<int>("m_stateFlags", out var flags)) return TaskState.Pending;
        if ((flags & TaskStateRanToCompletion) != 0) return TaskState.RanToCompletion;
        if ((flags & TaskStateFaulted) != 0) return TaskState.Faulted;
        if ((flags & TaskStateCanceled) != 0) return TaskState.Canceled;
        return TaskState.Pending;
    }

    private static bool IsTask(ClrType? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.Name == "System.Threading.Tasks.Task") return true;
        }
        return false;
    }

    /// "Foo<Bar+<Baz>d__0>" with marker "Foo<" -> "Bar+<Baz>d__0" (respects nested angle brackets).
    internal static string? GenericArgument(string typeName, string marker)
    {
        var start = typeName.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var depthCount = 1;
        for (var i = start; i < typeName.Length; i++)
        {
            if (typeName[i] == '<') depthCount++;
            else if (typeName[i] == '>' && --depthCount == 0) return typeName[start..i];
        }
        return null;
    }

    // ---------------------------------------------------------------- thread pool

    private ThreadPoolSnapshot? ReadThreadPool()
    {
        ClrThreadPool? pool;
        try { pool = _runtime.ThreadPool; }
        catch { pool = null; }
        if (pool is null) return null;
        return new ThreadPoolSnapshot
        {
            MinThreads = pool.MinThreads,
            MaxThreads = pool.MaxThreads,
            ActiveWorkers = pool.ActiveWorkerThreads,
            IdleWorkers = pool.IdleWorkerThreads,
            QueuedWorkItems = TryCountGlobalQueue(),
        };
    }

    private int? TryCountGlobalQueue()
    {
        try
        {
            var bcl = _runtime.BaseClassLibrary;
            var poolType = bcl?.GetTypeByName("System.Threading.ThreadPool");
            var field = poolType?.GetStaticFieldByName("s_workQueue");
            var domain = _runtime.AppDomains.FirstOrDefault();
            if (field is null || domain is null) return null;
            var workQueue = field.ReadObject(domain);
            if (workQueue.IsNull) return null;

            var total = 0;
            var found = false;
            foreach (var queueField in new[] { "workItems", "highPriorityWorkItems" })
            {
                if (workQueue.TryReadObjectField(queueField, out var queue) && !queue.IsNull && CountConcurrentQueue(queue) is { } n)
                {
                    total += n;
                    found = true;
                }
            }
            return found ? total : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? CountConcurrentQueue(ClrObject queue)
    {
        if (!queue.TryReadObjectField("_head", out var segment)) return null;
        var count = 0;
        for (var guard = 0; !segment.IsNull && guard < 10_000; guard++)
        {
            if (!segment.TryReadValueTypeField("_headAndTail", out var ht)) return null;
            var head = ht.ReadField<int>("Head");
            var tail = ht.ReadField<int>("Tail");
            var capacity = segment.TryReadObjectField("_slots", out var slots) && slots.IsArray ? slots.AsArray().Length : int.MaxValue;
            count += Math.Clamp(tail - head, 0, capacity);
            if (!segment.TryReadObjectField("_nextSegment", out segment)) break;
        }
        return count;
    }
}

/// Reads fields from either a boxed object or an inline struct, so state machines in
/// Release (struct) and Debug (class) builds are handled the same way.
internal readonly struct FieldReader
{
    private readonly ClrObject _object;
    private readonly ClrValueType _value;
    private readonly bool _isObject;

    private FieldReader(ClrObject obj, ClrValueType value, bool isObject)
    {
        _object = obj;
        _value = value;
        _isObject = isObject;
    }

    public static FieldReader Of(ClrObject obj) => new(obj, default, true);
    public static FieldReader Of(ClrValueType value) => new(default, value, false);

    public ClrType? Type => _isObject ? _object.Type : _value.Type;

    public int? ReadInt(string name)
    {
        try
        {
            if (_isObject) return _object.TryReadField<int>(name, out var v) ? v : null;
            return _value.Type?.GetFieldByName(name) is null ? null : _value.ReadField<int>(name);
        }
        catch { return null; }
    }

    public ClrObject ReadObject(ClrInstanceField field) => _isObject ? _object.ReadObjectField(field) : _value.ReadObjectField(field);

    public ClrValueType? ReadStruct(ClrInstanceField field) => _isObject ? _object.ReadValueTypeField(field) : _value.ReadValueTypeField(field);
}

internal static class ClrObjectExtensions
{
    public static int? ReadInt(this ClrObject obj, string name) => obj.TryReadField<int>(name, out var v) ? v : null;
}
