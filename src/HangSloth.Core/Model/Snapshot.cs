namespace HangSloth.Model;

// The snapshot is a plain, ClrMD-free description of a process at one moment.
// The collection layer fills it in from a dump; analyzers only ever read it,
// which keeps every detector testable without a real dump.

public sealed record ProcessSnapshot
{
    public required string Source { get; init; }
    public string? RuntimeVersion { get; init; }
    public required IReadOnlyList<ThreadSnapshot> Threads { get; init; }
    public IReadOnlyList<MonitorLock> Locks { get; init; } = [];
    public ThreadPoolSnapshot? ThreadPool { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public ThreadSnapshot? FindThread(int managedId) => Threads.FirstOrDefault(t => t.ManagedId == managedId);
}

public sealed record ThreadSnapshot
{
    public required int ManagedId { get; init; }
    public uint OSThreadId { get; init; }
    public string? Name { get; init; }
    public bool IsThreadPoolWorker { get; init; }
    public bool IsFinalizer { get; init; }
    public bool IsBackground { get; init; }

    /// Managed call stack, innermost frame first.
    public required IReadOnlyList<StackFrameInfo> Frames { get; init; }

    /// The thread's current SynchronizationContext, if any.
    public ObjectRef? SynchronizationContext { get; init; }

    /// The task this thread is synchronously blocked on (via .Result, .Wait(), GetResult()), if any.
    public TaskInfo? BlockedOnTask { get; init; }

    /// Address of the object whose monitor (lock) this thread is trying to enter, if any.
    public ulong? WaitingForLock { get; init; }

    public string Label => Name is { Length: > 0 } ? $"Thread {ManagedId} (\"{Name}\")" : $"Thread {ManagedId}";
}

public sealed record StackFrameInfo
{
    public required string TypeName { get; init; }
    public required string MethodName { get; init; }
    public string? ModuleName { get; init; }
    public SourceLocation? Source { get; init; }

    /// A runtime/helper frame rather than managed code (e.g. HelperMethodFrame).
    public bool IsRuntimeFrame { get; init; }

    public string FullName => TypeName.Length == 0 ? MethodName : $"{TypeName}.{MethodName}";

    public override string ToString() => Source is null ? FullName : $"{FullName} ({Source})";
}

public sealed record SourceLocation(string File, int Line, int Column = 0)
{
    public string FileName => Path.GetFileName(File.Replace('\\', '/'));
    public override string ToString() => $"{FileName}:{Line}";
}

public sealed record ObjectRef(ulong Address, string TypeName)
{
    public string ShortTypeName => Names.ShortType(TypeName);
}

public enum TaskState { Pending, RanToCompletion, Faulted, Canceled }

public enum TaskKind
{
    Unknown,
    AsyncMethod,       // async Task method (state machine box)
    Delay,             // Task.Delay
    Promise,           // TaskCompletionSource / manual promise
    WhenAll,
    WhenAny,
    Delegate,          // Task.Run / Task.Factory.StartNew
    Continuation,      // ContinueWith
}

public sealed record TaskInfo
{
    public required ulong Address { get; init; }
    public required string TypeName { get; init; }
    public TaskKind Kind { get; init; }
    public TaskState State { get; init; }

    /// Human name: "OrderService.GetOrderAsync", "Task.Delay", "TaskCompletionSource<bool>.Task".
    public required string Description { get; init; }

    /// For async methods: where the method is currently paused (the await it is stuck on).
    public SourceLocation? AwaitLocation { get; init; }

    /// For async methods: the task the method is currently awaiting.
    public TaskInfo? Awaiting { get; init; }

    /// Where this task's continuation will run once its awaited task finishes.
    public ResumeTarget? Resume { get; init; }
}

public enum ResumeStatus
{
    /// Registered on the awaited (still pending) task; will be posted to the context later.
    Registered,
    /// Already posted to the context's queue, waiting for the context's thread to run it.
    Queued,
}

public sealed record ResumeTarget(ObjectRef SynchronizationContext, ResumeStatus Status);

public sealed record MonitorLock
{
    public required ObjectRef Object { get; init; }

    /// The field that holds the lock object, e.g. "OrderCache._sync" or "Inventory.Gate" (static), when found.
    public string? FieldName { get; init; }

    /// How to refer to this lock in a sentence.
    public string DisplayName => FieldName ?? Object.ShortTypeName;

    public int? OwnerThreadId { get; init; }
    public int Recursion { get; init; }
    public IReadOnlyList<int> WaiterThreadIds { get; init; } = [];
}

public sealed record ThreadPoolSnapshot
{
    public int MinThreads { get; init; }
    public int MaxThreads { get; init; }
    public int ActiveWorkers { get; init; }
    public int IdleWorkers { get; init; }
    /// Work items waiting in the global queue, when it could be read.
    public int? QueuedWorkItems { get; init; }
}
