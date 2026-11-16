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
        var clrThreads = _runtime.Threads.Where(t => t.IsAlive && (t.State & ClrThreadState.TS_Unstarted) == 0).ToList();
        var byAddress = clrThreads.ToDictionary(t => t.Address, t => t.ManagedThreadId);
        var managedThreads = FindManagedThreadObjects(byAddress);

        // One unreadable object on one thread must not cost the whole report: such a thread keeps what could be read.
        // Each read is guarded on its own, so a failed detail never costs the wait that makes a deadlock.
        var incomplete = new SortedSet<int>();
        ThreadSnapshot Step(ThreadSnapshot current, int threadId, Func<ThreadSnapshot, ThreadSnapshot> read)
        {
            try
            {
                return read(current);
            }
            catch
            {
                incomplete.Add(threadId);
                return current;
            }
        }

        // First pass: stacks, names, contexts. Task chains need to know every context in use, so they come after.
        var partial = new List<(ClrThread Clr, ThreadSnapshot Snapshot, List<ClrStackRoot> Roots)>();
        foreach (var thread in clrThreads)
        {
            var snapshot = new ThreadSnapshot
            {
                ManagedId = thread.ManagedThreadId,
                OSThreadId = thread.OSThreadId,
                IsFinalizer = thread.IsFinalizer,
                IsBackground = (thread.State & ClrThreadState.TS_Background) != 0,
                Frames = [],
            };
            var id = thread.ManagedThreadId;
            var roots = SafeRoots(thread);
            snapshot = Step(snapshot, id, s =>
            {
                var frames = ReadFrames(thread, roots);
                return s with
                {
                    IsThreadPoolWorker = frames.Any(FrameClassifier.IsThreadPoolDispatchFrame)
                                         || ((thread.State & ClrThreadState.TS_TPWorkerThread) != 0 && !frames.Any(FrameClassifier.IsThreadPoolServiceFrame)),
                    Frames = frames,
                };
            });
            managedThreads.TryGetValue(id, out var threadObject);
            snapshot = Step(snapshot, id, s =>
            {
                ThreadReadFault?.Invoke(id, "context");
                var ctx = ReadSynchronizationContext(threadObject);
                if (ctx is not null) _contextsInUse[ctx.Address] = ctx;
                return s with { SynchronizationContext = ctx };
            });
            snapshot = Step(snapshot, id, s =>
            {
                ThreadReadFault?.Invoke(id, "name");
                return s with { Name = ReadThreadName(threadObject) };
            });
            partial.Add((thread, snapshot, roots));
        }

        var locks = ReadLocks(byAddress);

        var threads = new List<ThreadSnapshot>();
        foreach (var (clr, snapshot, roots) in partial)
        {
            var id = clr.ManagedThreadId;
            var result = snapshot;
            var kind = BlockingKind.None;
            try
            {
                kind = FrameClassifier.Classify(snapshot.Frames);
            }
            catch
            {
                incomplete.Add(id);
            }
            result = Step(result, id, r =>
            {
                ThreadReadFault?.Invoke(id, "running task");
                return r with { RunningTask = FindRunningTask(roots) };
            });
            result = Step(result, id, r => r with { SendingTo = FindSendTarget(clr, roots, snapshot.Frames) });
            result = Step(result, id, r => r with { WaitingForCallbackOnThread = FindCallbackThread(roots, snapshot.Frames, id) });
            result = Step(result, id, r => r with { WaitingForProcess = FindProcessWait(clr, roots, snapshot.Frames) });
            result = Step(result, id, r => r with { Database = ReadDatabaseActivity(roots, kind) });
            result = Step(result, id, r =>
            {
                ThreadReadFault?.Invoke(id, "waits");
                switch (kind)
                {
                    case BlockingKind.TaskWait:
                    // host.Run() waits for the host's RunAsync task too: a host that never finishes starting looks idle otherwise.
                    case BlockingKind.Idle when FrameClassifier.FrameworkPrefix(snapshot.Frames).Any(FrameClassifier.IsHostRunFrame):
                        r = r with { BlockedOnTask = FindBlockedOnTask(roots) };
                        break;
                    case BlockingKind.MonitorEnter:
                        r = r with { WaitingForLock = FindLockBeingEntered(clr, roots, locks, clrThreads) };
                        break;
                    case BlockingKind.ReaderWriterLock:
                        r = r with { WaitingForLock = FindReaderWriterLock(roots, locks, clrThreads) };
                        break;
                    case BlockingKind.SemaphoreWait:
                        r = r with { WaitingForSemaphore = FindSemaphoreBeingWaited(roots), WaitingOnCollection = FindCollectionWait(roots) };
                        break;
                    case BlockingKind.MonitorWait:
                        r = r with { WaitingForPulse = FindPulseWait(clr, roots, snapshot.Frames) };
                        break;
                    case BlockingKind.Join:
                        r = r with { JoiningThreadId = FindJoinTarget(clr, roots) };
                        break;
                    case BlockingKind.Database or BlockingKind.ConnectionPool:
                        r = r with { Database = ReadDatabaseActivity(roots, kind) };
                        break;
                }
                return r;
            });
            // A handler the JIT inlined into a compiled delegate has no frame of its own: the task it waits for says where it is.
            result = Step(result, id, r => kind == BlockingKind.TaskWait ? r with { Frames = WithInlinedWaiter(r.Frames, r.BlockedOnTask) } : r);
            result = Step(result, id, r => r.BlockedOnTask is { } task && DatabaseCommandInChain(task) is { } command
                ? r with { Database = AsyncDatabaseWait(command, r.Database) }
                : r);
            threads.Add(result);
        }
        if (incomplete.Count > 0)
            _warnings.Add(IncompleteThreadsWarning(incomplete));
        threads = ReadSemaphoreUses(partial, threads);

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

    /// One line for all threads that could not be read completely, so a damaged dump doesn't flood the notes.
    internal static string IncompleteThreadsWarning(IReadOnlyCollection<int> threadIds) =>
        $"{Describe.Plural(threadIds.Count, "thread")} could not be read completely from the dump ({string.Join(", ", threadIds.Select(id => $"Thread {id}"))}); " +
        $"the report shows what could be read of {(threadIds.Count == 1 ? "it" : "them")}.";

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

    private List<StackFrameInfo> ReadFrames(ClrThread thread, List<ClrStackRoot> roots)
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
        ResolveCalls(frames, code, roots);
        return frames;
    }

    /// Lines the stack walk can't give, and the call a task wait is in. ClrMD maps only the code a method was first
    /// compiled to: a frame in code the JIT compiled again with optimizations (tier 1, or OSR for a hot loop) has no
    /// IL offset, so its line is read from its IL instead, at the call that leads to the frame above it. When several
    /// calls can be it, the line is left out and a note names them: a wrong line is worse than none. When the IL or
    /// the PDB can't be read, the frame keeps what the stack walk gave.
    private void ResolveCalls(List<StackFrameInfo> frames, List<(ClrMethod? Method, ulong Ip, bool IsLeaf)> code, List<ClrStackRoot> roots)
    {
        var first = frames.FindIndex(FrameClassifier.IsUserFrame);
        if (first < 0) return;
        var taskWait = FrameClassifier.Classify(frames) == BlockingKind.TaskWait;
        for (var i = first; i < frames.Count; i++)
        {
            if (!FrameClassifier.IsUserFrame(frames[i]) || code[i].Method is not { } method || method.Type?.Module is not { IsDynamic: false } module) continue;
            try
            {
                var address = code[i].IsLeaf ? code[i].Ip : code[i].Ip - 1;
                var mapped = IsMapped(method.ILOffsetMap, address);
                var callee = i > 0 && frames[i - 1].TypeName.Length > 0 ? frames[i - 1] : null;
                if (taskWait && i == first)
                    frames[i] = WithBlockingCall(frames, frames[i], method, module, method.GetILOffset(address), mapped, callee, roots);
                else if (!mapped && callee is not null && FindCalls(frames[i], method, module, callee, innermost: i == first) is { HasSymbols: true } sites)
                    frames[i] = WithLines(frames[i], sites);
            }
            catch
            {
                // Keep what the stack walk gave.
            }
        }
    }

    /// The app frame that blocks on a task: read the blocking call from its IL, which gives the right line and the
    /// right kind of wait even when optimized code inlined Task&lt;T&gt;.Result and lost its frame. When the frame above
    /// is another framework method that waits for a task itself (Parallel.ForEach, Process.WaitForExit), that is the call.
    private StackFrameInfo WithBlockingCall(IReadOnlyList<StackFrameInfo> frames, StackFrameInfo frame, ClrMethod method, ClrModule module, int ilOffset, bool mapped,
        StackFrameInfo? callee, List<ClrStackRoot> roots)
    {
        if (callee is not null && !IsTaskWaitFrame(callee))
        {
            var call = CalleeCall(frames, callee);
            if (mapped) return frame with { BlockingCall = call };
            if (FindCalls(frame, method, module, callee, innermost: true) is { } calls)
                return (calls.HasSymbols ? WithLines(frame, calls) : frame) with { BlockingCall = call };
        }
        else if (callee is not null && !mapped)
        {
            var hint = new TaskWaitHint(WaitCall(callee), ResultType(callee.TypeName) ?? WaitedResultType(roots));
            if (_sources.ResolveBlockingCall(module.Name, method.MetadataToken, hint, () => module.Pdb?.Guid) is { } sites)
            {
                NoteUnknownLine(frame, sites, sites.Call is { } c ? $"blocks with {c}" : "blocks on a task", innermost: true);
                return (sites.HasSymbols ? WithLines(frame, sites) : frame) with { BlockingCall = sites.Call };
            }
        }
        return _sources.ResolveBlockingCall(module.Name, method.MetadataToken, ilOffset, () => module.Pdb?.Guid) is { } found
            ? frame with { BlockingCall = found.Call, Source = found.Location ?? frame.Source }
            : frame;
    }

    /// The framework method a task-wait thread's app frame called, named as its blocking call. Null for synchronous I/O
    /// that blocks on a task inside (HttpClient.Send): the detectors name that from the stack and advise its async version.
    /// Parallel's workers are what is left of Parallel.For and ForEach once optimized code inlined them.
    internal static string? CalleeCall(IReadOnlyList<StackFrameInfo> frames, StackFrameInfo callee)
    {
        if (FrameClassifier.SyncIoFrame(frames) is not null) return null;
        if (callee.TypeName == "System.Threading.Tasks.Parallel")
        {
            if (callee.MethodName.StartsWith("ForEachWorker", StringComparison.Ordinal) || callee.MethodName.StartsWith("PartitionerForEachWorker", StringComparison.Ordinal))
                return "Parallel.ForEach";
            if (callee.MethodName.StartsWith("ForWorker", StringComparison.Ordinal)) return "Parallel.For/ForEach";
        }
        return Names.ShortMethod(callee.TypeName, callee.MethodName);
    }

    /// The frame at the one line its IL leaves, or at none with the lines it can be at, so that no other frame's line is taken for it.
    private static StackFrameInfo WithLines(StackFrameInfo frame, CallSites sites) =>
        frame with { Source = sites.Line, CandidateLines = sites.Line is null ? sites.Lines : null };

    /// Where a frame can be calling the frame above it, read from its IL; null when the IL can't be read.
    private CallSites? FindCalls(StackFrameInfo frame, ClrMethod method, ClrModule module, StackFrameInfo callee, bool innermost)
    {
        if (_sources.ResolveCall(module.Name, method.MetadataToken, callee.TypeName, callee.MethodName, callee.ModuleName, () => module.Pdb?.Guid) is not { } sites) return null;
        NoteUnknownLine(frame, sites, PublicCall(callee), innermost);
        return sites;
    }

    /// What a frame above the app's code does, in the words of the call the app made: the runtime's own helpers
    /// (Monitor.Enter_Slowpath, SemaphoreSlim.WaitCore) are what is left of that call once optimized code inlined it.
    internal static string PublicCall(StackFrameInfo callee)
    {
        if (callee.TypeName is "System.Threading.Monitor" or "System.Threading.Lock"
            && (callee.MethodName.StartsWith("Enter", StringComparison.Ordinal) || callee.MethodName.StartsWith("TryEnter", StringComparison.Ordinal) || callee.MethodName == "ReliableEnter"))
            return "takes a lock";
        if (callee.TypeName == "System.Threading.SemaphoreSlim" && callee.MethodName is "Wait" or "WaitCore") return "calls SemaphoreSlim.Wait()";
        var name = callee.MethodName.StartsWith("get_", StringComparison.Ordinal) ? callee.MethodName[4..] : callee.MethodName;
        return $"calls {Names.ShortMethod(callee.TypeName, name)}";
    }

    private readonly HashSet<string> _notes = [];

    /// Says why a frame has no line, and where it can be: several calls, or (for the frame a thread waits in) none found.
    private void NoteUnknownLine(StackFrameInfo frame, CallSites sites, string what, bool innermost)
    {
        if (!sites.HasSymbols || sites.Lines.Count == 1 || (sites.Lines.Count == 0 && !innermost)) return;
        var method = Names.ShortMethod(frame.TypeName, frame.MethodName);
        var note = sites.Lines.Count == 0
            ? $"{method} runs optimized code that the dump has no line map for, and the place where it {what} can't be found in its IL, so its line is left out."
            : $"{method} runs optimized code that the dump has no line map for, and it {what} in more than one place ({string.Join(", ", sites.Lines)}), so its line is left out.";
        if (_notes.Add(note)) _warnings.Add(note);
    }

    /// Whether ClrMD's IL map covers an address. The map is that of the code the method was first compiled to; once the
    /// JIT has compiled it again, its frames lie outside the map and GetILOffset silently answers the first statement.
    internal static bool IsMapped(System.Collections.Immutable.ImmutableArray<ILToNativeMap> map, ulong address)
    {
        if (map.IsDefaultOrEmpty) return false;
        foreach (var entry in map)
        {
            if (entry.StartAddress <= address && address <= entry.EndAddress) return true;
        }
        return false;
    }

    /// A framework frame that is the app's own blocking call on a task, or what is left of it once optimized code inlined the call.
    private static bool IsTaskWaitFrame(StackFrameInfo f) =>
        FrameClassifier.IsTaskWaitMethod(f.TypeName, f.MethodName)
        || (f.TypeName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal) && f.MethodName == "get_Result")
        || (f.TypeName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) && f.MethodName is "GetResult" or "ValidateEnd" or "HandleNonSuccessAndDebuggerNotification");

    /// The blocking call that a task-wait frame right above the app's frame belongs to; null when only the wait itself
    /// is left (optimized code inlined .Result, .Wait() or GetResult() into the app's method).
    private static string? WaitCall(StackFrameInfo f)
    {
        if (f.TypeName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal)) return ".GetAwaiter().GetResult()";
        if (f.MethodName is "get_Result" or "GetResultCore") return ".Result";
        if (f.MethodName == "Wait") return ".Wait()";
        if (f.MethodName.StartsWith("WaitAll", StringComparison.Ordinal)) return "Task.WaitAll(...)";
        if (f.MethodName.StartsWith("WaitAny", StringComparison.Ordinal)) return "Task.WaitAny(...)";
        return null;
    }

    /// "System.Int32" for a frame of Task&lt;System.Int32&gt; or TaskAwaiter&lt;System.Int32&gt;; null when the frame's type has no
    /// type argument or is shared code for reference types (System.__Canon), which says nothing.
    internal static string? ResultType(string typeName)
    {
        var open = typeName.IndexOf('<');
        if (open <= 0 || !(char.IsLetterOrDigit(typeName[open - 1]) || typeName[open - 1] == '_')) return null;
        var depth = 0;
        for (var i = open; i < typeName.Length; i++)
        {
            if (typeName[i] == '<') depth++;
            else if (typeName[i] == '>') depth--;
            if (depth == 0 || (depth == 1 && typeName[i] == ','))
            {
                var argument = typeName[(open + 1)..i].Trim();
                return argument.Length == 0 || argument.Contains("__Canon", StringComparison.Ordinal) ? null : argument;
            }
        }
        return null;
    }

    /// The result type of the pending task that a task-wait frame holds ("" for a Task without one), or null when no
    /// wait frame holds one. Only a wait frame's own task: another pending task on the stack may not be the one waited for.
    private static string? WaitedResultType(List<ClrStackRoot> roots)
    {
        foreach (var root in roots)
        {
            var obj = root.Object;
            var frameMethod = root.StackFrame?.Method;
            if (obj.IsNull || frameMethod is null || !IsTask(obj.Type) || ReadTaskState(obj) != TaskState.Pending) continue;
            if (!FrameClassifier.IsTaskWaitMethod(frameMethod.Type?.Name ?? "", frameMethod.Name ?? "")) continue;
            for (var t = obj.Type; t is not null; t = t.BaseType)
            {
                if (t.Name is { } name && name.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal))
                    return ResultType(name);
            }
            return "";
        }
        return null;
    }

    /// A compiled delegate (lambda_method: a minimal API handler or an MVC action invoker, always optimized code) often
    /// has the app's small handler and service methods inlined into it, so a thread blocked on a task there has no app
    /// frame to say where. The task says which of the app's async methods it waits for, and the app's IL says which
    /// method calls that and blocks on it: when only one does, that method is put back on the stack, above the delegate.
    private IReadOnlyList<StackFrameInfo> WithInlinedWaiter(IReadOnlyList<StackFrameInfo> frames, TaskInfo? task)
    {
        if (task is null || !Describe.IsAppAsyncMethod(task)) return frames;
        var at = -1;
        for (var i = 0; i < frames.Count && at < 0; i++)
        {
            if (FrameClassifier.IsUserFrame(frames[i])) return frames;
            if (!frames[i].IsRuntimeFrame && frames[i].TypeName.Length == 0) at = i;
        }
        if (at < 1 || !IsTaskWaitFrame(frames[at - 1])) return frames;
        try
        {
            if (!TryReadStateMachine(_heap.GetObject(task.Address), out var stateMachine)
                || stateMachine.Type is not { Module: { } module } smType
                || smType.Methods.FirstOrDefault(m => m.Name == "MoveNext") is not { } moveNext
                || _sources.ResolveBlockingCallers(module.Name, moveNext.MetadataToken, AppModules(), () => module.Pdb?.Guid) is not { } callers)
                return frames;
            if (callers.Methods.Count > 1)
            {
                var note = $"Code inlined into {Names.ShortMethod("", frames[at].MethodName)} (a compiled delegate, such as a minimal API handler) is blocked on {task.Description}, " +
                           $"which is waited for in more than one place ({string.Join(", ", callers.Methods.Select(m => Names.ShortMethod(m.TypeName, m.MethodName) + (m.Call.Location is { } l ? $" ({l})" : "")))}), so the stack doesn't say where.";
                if (_notes.Add(note)) _warnings.Add(note);
            }
            if (!callers.Complete || callers.Methods.Count != 1) return frames;
            var caller = callers.Methods[0];
            var withCaller = frames.ToList();
            withCaller.Insert(at, new StackFrameInfo
            {
                TypeName = caller.TypeName,
                MethodName = caller.MethodName,
                ModuleName = caller.ModulePath,
                Source = caller.Call.Location,
                BlockingCall = caller.Call.Call,
                InlinedInto = frames[at].MethodName,
            });
            var inferred = $"{Names.ShortMethod(caller.TypeName, caller.MethodName)} is shown above {Names.ShortMethod("", frames[at].MethodName)} although the stack walk doesn't find it: " +
                           $"the JIT inlined it into that compiled delegate (such as a minimal API handler), and it is the only method in the app's own code that blocks on {task.Description}.";
            if (_notes.Add(inferred)) _warnings.Add(inferred);
            return withCaller;
        }
        catch
        {
            return frames;
        }
    }

    private List<(string Path, Func<Guid?>? DumpedBuild)>? _appModules;

    /// The app's own modules (not the framework's), where the callers of its methods are.
    private List<(string Path, Func<Guid?>? DumpedBuild)> AppModules()
    {
        if (_appModules is not null) return _appModules;
        _appModules = [];
        try
        {
            foreach (var module in _runtime.EnumerateModules())
            {
                if (module.IsDynamic || string.IsNullOrEmpty(module.Name) || FrameClassifier.IsFrameworkModule(module.Name)) continue;
                if (_appModules.Any(m => string.Equals(m.Path, module.Name, StringComparison.OrdinalIgnoreCase))) continue;
                _appModules.Add((module.Name, () => module.Pdb?.Guid));
            }
        }
        catch
        {
            // The modules found so far will do.
        }
        return _appModules;
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
