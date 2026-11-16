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
public sealed partial class SnapshotBuilder
{
    private const int MaxFramesPerThread = 200;
    private const int MaxTaskChainDepth = 16;
    private const int MaxContextSearchObjects = 50_000;
    private const int MaxContextSearchDepth = 8;

    // Task.m_stateFlags bits (see System.Threading.Tasks.Task).
    private const int TaskStateStarted = 0x10000;
    private const int TaskStateDelegateInvoked = 0x20000;
    private const int TaskStateFaulted = 0x200000;
    private const int TaskStateCanceled = 0x400000;
    private const int TaskStateRanToCompletion = 0x1000000;

    private readonly ClrRuntime _runtime;
    private readonly ClrHeap _heap;
    private readonly SourceResolver _sources;
    private readonly List<string> _warnings = [];
    private readonly Dictionary<ulong, HashSet<ulong>> _contextDelegateTargets = [];
    private readonly Dictionary<ulong, ObjectRef> _contextsInUse = [];
    private readonly HashSet<ulong> _semaphoresInChains = [];
    private readonly List<ClrObject> _asyncThisObjects = [];
    private bool _heapAvailable = true;

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
            var result = snapshot with
            {
                RunningTask = FindRunningTask(roots),
                SendingTo = FindSendTarget(roots),
                WaitingForCallbackOnThread = FindCallbackThread(roots, snapshot.Frames, clr.ManagedThreadId),
                WaitingForProcess = FindProcessWait(roots, snapshot.Frames),
                Database = ReadDatabaseActivity(roots, FrameClassifier.Classify(snapshot.Frames)),
            };
            var kind = FrameClassifier.Classify(snapshot.Frames);
            switch (kind)
            {
                case BlockingKind.TaskWait:
                // host.Run() waits for the host's RunAsync task too: a host that never finishes starting looks idle otherwise.
                case BlockingKind.Idle when FrameClassifier.FrameworkPrefix(snapshot.Frames).Any(FrameClassifier.IsHostRunFrame):
                    var task = FindBlockedOnTask(roots);
                    result = result with { BlockedOnTask = task };
                    if (task is not null && DatabaseCommandInChain(task) is { } command)
                        result = result with { Database = AsyncDatabaseWait(command, result.Database) };
                    break;
                case BlockingKind.MonitorEnter:
                    result = result with { WaitingForLock = FindLockBeingEntered(clr, roots, locks, clrThreads) };
                    break;
                case BlockingKind.ReaderWriterLock:
                    result = result with { WaitingForLock = FindReaderWriterLock(roots, locks, clrThreads) };
                    break;
                case BlockingKind.SemaphoreWait:
                    result = result with { WaitingForSemaphore = FindSemaphoreBeingWaited(roots), WaitingOnCollection = FindCollectionWait(roots) };
                    break;
                case BlockingKind.MonitorWait:
                    result = result with { WaitingForPulse = FindPulseWait(clr, roots, snapshot.Frames) };
                    break;
                case BlockingKind.Join:
                    result = result with { JoiningThreadId = FindJoinTarget(roots, clr.ManagedThreadId) };
                    break;
                case BlockingKind.Database or BlockingKind.ConnectionPool:
                    result = result with { Database = ReadDatabaseActivity(roots, kind) };
                    break;
            }
            threads.Add(result);
        }

        // Fill in waiters from what each thread is trying to enter, and name each lock after the field holding it.
        var semaphoreAddresses = threads.Select(t => t.WaitingForSemaphore).OfType<ulong>().Concat(_semaphoresInChains).ToHashSet();
        var collections = threads.Select(t => t.WaitingOnCollection?.Collection.Address)
            .Concat(threads.Select(t => t.WaitingForPulse?.Object.Address)).OfType<ulong>();
        var named = NameObjects(locks.Keys.Concat(semaphoreAddresses).Concat(collections).ToHashSet(), partial.SelectMany(p => p.Roots));
        threads = threads
            .Select(t => t.WaitingOnCollection is { } c && named.TryGetValue(c.Collection.Address, out var n) ? t with { WaitingOnCollection = c with { FieldName = n.Name } } : t)
            .Select(t => t.WaitingForPulse is { } w && named.TryGetValue(w.Object.Address, out var n) ? t with { WaitingForPulse = w with { FieldName = n.Name } } : t)
            .ToList();
        var lockList = locks.Values
            .Select(l => l with
            {
                WaiterThreadIds = threads.Where(t => t.WaitingForLock == l.Object.Address).Select(t => t.ManagedId).ToList(),
                FieldName = named.GetValueOrDefault(l.Object.Address)?.Name,
            })
            .ToList();
        var semaphores = semaphoreAddresses
            .Select(a => ReadSemaphore(a, named.GetValueOrDefault(a), threads))
            .OfType<SemaphoreInfo>()
            .ToList();

        return new ProcessSnapshot
        {
            Source = source,
            RuntimeVersion = runtimeVersion,
            Threads = threads,
            Locks = lockList,
            Semaphores = semaphores,
            ThreadPool = ReadThreadPool(),
            AsyncPoolWaits = ReadAsyncPoolWaits(),
            Warnings = [.. _warnings, .. _sources.Warnings],
            HeapAvailable = _heapAvailable,
        };
    }

    // ---------------------------------------------------------------- threads

    /// Also read by the text report and the CLI, which then word the missing-heap warning for a dump taken mid-GC.
    public const string HeapNotWalkableWarning = "The GC heap could not be walked (was the dump taken mid-GC?). Thread names, SynchronizationContexts and task details are unavailable.";

    private Dictionary<int, ClrObject> FindManagedThreadObjects()
    {
        var result = new Dictionary<int, ClrObject>();
        if (!_heap.CanWalkHeap)
        {
            _warnings.Add(HeapNotWalkableWarning);
            _heapAvailable = false;
            return result;
        }
        var objects = 0;
        try
        {
            foreach (var obj in _heap.EnumerateObjects())
            {
                objects++;
                if (obj.Type?.Name != "System.Threading.Thread") continue;
                if (obj.TryReadField<int>("_managedThreadId", out var id) && id != 0)
                    result[id] = obj;
            }
        }
        catch (Exception e)
        {
            _warnings.Add($"Walking the heap failed part-way: {e.Message}");
        }
        // A running .NET process always has objects on its heap. A mini or triage dump leaves the heap's memory
        // out, and ClrMD then still says the heap can be walked but enumerates nothing.
        if (objects == 0) _heapAvailable = false;
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
        var code = new List<(ClrMethod? Method, ulong Ip, bool IsLeaf)>();
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
                    code.Add((method, frame.InstructionPointer, isFirst));
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
                    code.Add((null, 0, false));
                }
            }
        }
        catch (Exception e)
        {
            _warnings.Add($"Could not fully walk the stack of thread {thread.ManagedThreadId}: {e.Message}");
        }
        ResolveBlockingCall(frames, code);
        return frames;
    }

    /// The app frame that blocks on a task: read the blocking call from its IL, which gives the right line and
    /// the right kind of wait even when optimized code inlined Task&lt;T&gt;.Result and lost its frame.
    private void ResolveBlockingCall(List<StackFrameInfo> frames, List<(ClrMethod? Method, ulong Ip, bool IsLeaf)> code)
    {
        if (FrameClassifier.Classify(frames) != BlockingKind.TaskWait) return;
        var index = frames.FindIndex(FrameClassifier.IsUserFrame);
        if (index < 0 || code[index].Method is not { } method || method.Type?.Module is not { IsDynamic: false } module) return;
        try
        {
            var il = method.GetILOffset(code[index].IsLeaf ? code[index].Ip : code[index].Ip - 1);
            if (_sources.ResolveBlockingCall(module.Name, method.MetadataToken, il, () => module.Pdb?.Guid) is not { } call) return;
            frames[index] = frames[index] with { BlockingCall = call.Call, Source = call.Location ?? frames[index].Source };
        }
        catch
        {
            // Keep what the stack walk gave.
        }
    }

    private SourceLocation? ResolveSource(ClrMethod method, ulong ip, bool isLeaf)
    {
        var module = method.Type?.Module;
        if (module is null || module.IsDynamic || FrameClassifier.IsFrameworkModule(module.Name)) return null;
        try
        {
            // Non-leaf frames sit at a return address, which belongs to the *next* instruction.
            var il = method.GetILOffset(isLeaf ? ip : ip - 1);
            return _sources.Resolve(module.Name, method.MetadataToken, il, () => module.Pdb?.Guid);
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
        if (fallback is { } f) return DescribeTask(f, 0, []);

        // Task.WaitAll / WaitAny take an array: report its first pending task.
        foreach (var root in roots)
        {
            var obj = root.Object;
            if (obj.IsNull || obj.Type?.IsArray != true || !IsTask(obj.Type.ComponentType)) continue;
            var array = obj.AsArray();
            for (var i = 0; i < Math.Min(array.Length, 1000); i++)
            {
                var element = array.GetObjectValue(i);
                if (!element.IsNull && IsTask(element.Type) && ReadTaskState(element) == TaskState.Pending)
                    return DescribeTask(element, 0, []);
            }
        }
        return null;
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

        if (typeName == "System.Threading.SemaphoreSlim+TaskNode")
        {
            var semaphore = FindSemaphoreOfTaskNode(task);
            if (semaphore is { } sem) _semaphoresInChains.Add(sem);
            return info with { Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = semaphore };
        }
        if (typeName.Contains("DelayPromise", StringComparison.Ordinal))
            return info with { Kind = TaskKind.Delay, Description = "Task.Delay(...)" };
        if (typeName.Contains("WhenAllPromise", StringComparison.Ordinal))
            return info with { Kind = TaskKind.WhenAll, Description = "Task.WhenAll(...)" };
        if (typeName.StartsWith("System.Threading.Tasks.Task+TwoTaskWhenAnyPromise<", StringComparison.Ordinal))
            return DescribeWhenAny(task, info, depth, visited);
        if (typeName.Contains("WhenAnyPromise", StringComparison.Ordinal))
            return info with { Kind = TaskKind.WhenAny, Description = "Task.WhenAny(...)" };
        if (typeName.StartsWith("System.Threading.Tasks.Continuation", StringComparison.Ordinal))
            return DescribeContinuation(task, info, depth, visited);

        if (task.TryReadObjectField("m_action", out var action) && !action.IsNull)
        {
            var target = action.IsDelegate ? action.AsDelegate().GetDelegateTarget()?.Method : null;
            var name = target is null ? "a delegate" : Names.ShortMethod(target.Type?.Name ?? "", target.Name ?? "?");
            var description = target?.Type?.Name?.StartsWith("System.Threading.Tasks.TaskReplicator", StringComparison.Ordinal) == true
                ? "a Parallel.For/ForEach worker"
                : $"Task.Run({name})";
            var kind = DelegateTaskKind(task.ReadInt("m_stateFlags"));
            return info with { Kind = kind, Description = kind == TaskKind.NotStarted ? $"new Task({name})" : description };
        }

        if (!IsPlainTask(typeName))
            return DescribeWrapper(task, info, depth, visited);

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

        ClrObject self = default;
        if (smType?.GetFieldByName("<>4__this") is { IsObjectReference: true } thisField)
        {
            self = stateMachine.ReadObject(thisField);
            if (!self.IsNull)
            {
                _asyncThisObjects.Add(self);
                info = info with { ThisAddress = self.Address, CommandText = DatabaseCommandText(self), HttpPool = ReadHttpPool(self) };
            }
        }
        if (smType is not null)
        {
            info = info with { Http = ReadHttpCall(stateMachine, smType, self) };
            if (Describe.IsAppAsyncMethod(info))
                info = info with { HoldsHttpResponse = HoldsHttpResponse(stateMachine, smType) };
        }

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
        try { return _sources.ResolveAwait(module, token, state, () => _runtime.EnumerateModules().FirstOrDefault(m => m.Name == module)?.Pdb?.Guid); }
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
            QueuedWorkItems = TryCountQueuedWork(),
        };
    }

    /// Work items waiting for a pool thread: the global queues (normal, high and low priority, and the per-core
    /// queues of big machines) plus the local queue of each pool thread, where work queued from a pool thread goes.
    private int? TryCountQueuedWork()
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
            foreach (var queueField in new[] { "workItems", "highPriorityWorkItems", "lowPriorityWorkItems" })
            {
                if (workQueue.TryReadObjectField(queueField, out var queue) && !queue.IsNull && CountConcurrentQueue(queue) is { } n)
                {
                    total += n;
                    found = true;
                }
            }
            if (!found) return null;
            if (workQueue.TryReadObjectField("_assignableWorkItemQueues", out var assignable) && assignable.IsArray)
            {
                var queues = assignable.AsArray();
                for (var i = 0; i < queues.Length; i++)
                    if (queues.GetObjectValue(i) is { IsNull: false } queue && CountConcurrentQueue(queue) is { } n)
                        total += n;
            }
            // Local queues that can't be read leave an empty global queue unknown, not empty.
            return CountLocalQueues(bcl!, domain) is { } local ? total + local : total > 0 ? total : null;
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
            // A segment frozen for enqueues (a newer segment took over) has twice its capacity added to Tail.
            if (capacity != int.MaxValue && segment.TryReadField<bool>("_frozenForEnqueues", out var frozen) && frozen)
                tail -= capacity * 2;
            count += Math.Clamp(tail - head, 0, capacity);
            if (!segment.TryReadObjectField("_nextSegment", out var next)) break;
            segment = next;
        }
        return count;
    }

    /// The pool threads' own work-stealing queues (ThreadPoolWorkQueue.WorkStealingQueueList).
    /// Null when they could not be read, so "nothing queued" is only said when it is known.
    private static int? CountLocalQueues(ClrModule bcl, ClrAppDomain domain)
    {
        var total = 0;
        var failed = false;
        try
        {
            var list = bcl.GetTypeByName("System.Threading.ThreadPoolWorkQueue+WorkStealingQueueList");
            var field = list?.GetStaticFieldByName("s_queues") ?? list?.GetStaticFieldByName("_queues");
            if (field is null) return null;
            var queues = field.ReadObject(domain);
            if (queues.IsNull || !queues.IsArray) return null;
            var array = queues.AsArray();
            for (var i = 0; i < array.Length && i < 10_000; i++)
            {
                try
                {
                    var queue = array.GetObjectValue(i);
                    if (queue.IsNull) continue;
                    if (!queue.TryReadObjectField("m_array", out var items) || !items.IsArray
                        || !queue.TryReadField<int>("m_headIndex", out var head) || !queue.TryReadField<int>("m_tailIndex", out var tail))
                    {
                        failed = true;
                        continue;
                    }
                    var slots = items.AsArray();
                    var length = slots.Length;
                    if (length == 0) continue;
                    // Count the slots that still hold an item: a work item taken out of order leaves a null behind.
                    // Capped, so that indexes read from a damaged dump can't make it walk a huge array.
                    var span = Math.Min(Math.Clamp(tail - head, 0, length), 1 << 20);
                    for (var k = 0; k < span; k++)
                        if (!slots.GetObjectValue((int)((uint)(head + k) % (uint)length)).IsNull) total++;
                }
                catch
                {
                    failed = true;
                }
            }
        }
        catch
        {
            failed = true;
        }
        // Items that were read are known to be waiting; zero is only known when every queue was read.
        return total > 0 || !failed ? total : null;
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
