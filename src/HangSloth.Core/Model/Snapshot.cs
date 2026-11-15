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
    public IReadOnlyList<SemaphoreInfo> Semaphores { get; init; } = [];
    public ThreadPoolSnapshot? ThreadPool { get; init; }
    /// Connection pools that are full with async callers queued for a connection. Async waiters don't
    /// block a thread, so they don't show up in any stack.
    public IReadOnlyList<AsyncPoolWait> AsyncPoolWaits { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public ThreadSnapshot? FindThread(int managedId) => Threads.FirstOrDefault(t => t.ManagedId == managedId);
    public MonitorLock? FindLock(ulong address) => Locks.FirstOrDefault(l => l.Object.Address == address);
    public SemaphoreInfo? FindSemaphore(ulong address) => Semaphores.FirstOrDefault(s => s.Object.Address == address);
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
    /// Also used for a ReaderWriterLockSlim the thread is trying to enter.
    public ulong? WaitingForLock { get; init; }

    /// Address of the SemaphoreSlim this thread is synchronously waiting on (SemaphoreSlim.Wait), if any.
    public ulong? WaitingForSemaphore { get; init; }

    /// The object this thread is waiting on with a direct Monitor.Wait from app code, if any.
    public PulseWait? WaitingForPulse { get; init; }

    /// The BlockingCollection this thread is waiting to add to (it is full) or take from (it is empty), if any.
    public CollectionWait? WaitingOnCollection { get; init; }

    /// The managed id of the thread this one is waiting for with Thread.Join, if any.
    public int? JoiningThreadId { get; init; }

    /// The SynchronizationContext this thread is synchronously Sending a callback to (Control.Invoke style), if any.
    /// The thread whose SynchronizationContext it is has to run the callback before this one can continue.
    public ObjectRef? SendingTo { get; init; }

    /// The task whose delegate this thread is currently running (a Task.Run body), if any.
    public ulong? RunningTask { get; init; }

    /// What this thread is doing with a database, if anything: a query it waits on, a pool it waits for,
    /// and the connections (and open transactions) reachable from its stack.
    public DatabaseActivity? Database { get; init; }

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
    SemaphoreWait,     // SemaphoreSlim.WaitAsync
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

    /// For async methods: the object the method was called on (its `this`), when it has one.
    public ulong? ThisAddress { get; init; }

    /// For SemaphoreSlim.WaitAsync tasks: the semaphore being waited for.
    public ulong? Semaphore { get; init; }

    /// For a database library's async command method: the SQL it is running.
    public string? CommandText { get; init; }
}

public enum ResumeStatus
{
    /// Registered on the awaited (still pending) task; will be posted to the context later.
    Registered,
    /// Already posted to the context's queue, waiting for the context's thread to run it.
    Queued,
}

public sealed record ResumeTarget(ObjectRef SynchronizationContext, ResumeStatus Status);

public enum LockKind
{
    /// lock / Monitor.Enter on any object.
    Monitor,
    /// .NET 9's System.Threading.Lock.
    Lock,
    /// ReaderWriterLockSlim; the owner is the thread holding the write (or upgradeable) lock.
    ReaderWriter,
}

public sealed record MonitorLock
{
    public required ObjectRef Object { get; init; }
    public LockKind Kind { get; init; }

    /// ReaderWriterLockSlim: how many threads hold it for reading.
    public int ReaderCount { get; init; }

    /// The field that holds the lock object, e.g. "OrderCache._sync" or "Inventory.Gate" (static), when found.
    public string? FieldName { get; init; }

    /// How to refer to this lock in a sentence.
    public string DisplayName => FieldName ?? Object.ShortTypeName;

    public int? OwnerThreadId { get; init; }
    public int Recursion { get; init; }
    public IReadOnlyList<int> WaiterThreadIds { get; init; } = [];
}

/// A thread in Monitor.Wait(obj), waiting for another thread to call Monitor.Pulse(obj).
public sealed record PulseWait
{
    public required ObjectRef Object { get; init; }
    /// The field that holds the object, e.g. "WorkQueue._sync", when found.
    public string? FieldName { get; init; }
    public string DisplayName => FieldName ?? Names.ShortType(Object.TypeName);
    /// Collections in the same object that already hold items, e.g. "WorkQueue._items (1 item)":
    /// work is there, yet the thread waits to be told about it.
    public IReadOnlyList<string> PendingWork { get; init; } = [];
}

/// A thread blocked in BlockingCollection.Add (no room) or Take (nothing to take).
public sealed record CollectionWait
{
    public required ObjectRef Collection { get; init; }
    public bool Adding { get; init; }
    /// The bounded capacity, or null for an unbounded collection.
    public int? Capacity { get; init; }
    /// Items currently in the collection, when it could be read.
    public int? Count { get; init; }
    /// The field that holds the collection, e.g. "EventBus._queue", when found.
    public string? FieldName { get; init; }
    public string DisplayName => FieldName ?? Names.ShortType(Collection.TypeName);
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

public sealed record SemaphoreInfo
{
    public required ObjectRef Object { get; init; }

    /// The field that holds the semaphore, e.g. "PriceCache._gate", when found.
    public string? FieldName { get; init; }
    public string DisplayName => FieldName ?? "a SemaphoreSlim";

    /// The object whose field holds the semaphore, when found (for instance fields).
    public ulong? ContainerAddress { get; init; }

    public int CurrentCount { get; init; }
    /// int.MaxValue when the semaphore was created without a maximum.
    public int MaxCount { get; init; }
    public IReadOnlyList<int> WaiterThreadIds { get; init; } = [];
    public int AsyncWaiters { get; init; }
}

public enum DatabaseWaitKind
{
    /// The thread isn't waiting on the database (it may still hold connections).
    None,
    /// Waiting for the server to answer a command.
    Query,
    /// Waiting for a free connection from the connection pool.
    ConnectionPool,
}

public sealed record DatabaseActivity
{
    /// "PostgreSQL", "SQL Server", "MySQL"...
    public required string Provider { get; init; }
    public DatabaseWaitKind Waiting { get; init; }

    /// The SQL of the command being waited on, when known.
    public string? CommandText { get; init; }

    /// The server session the command runs on (PostgreSQL backend PID), when known.
    public int? SessionId { get; init; }

    /// Open connections reachable from the thread's stack, including the one it waits on.
    public IReadOnlyList<DatabaseConnection> Connections { get; init; } = [];

    /// For ConnectionPool waits: the pool being waited on.
    public ConnectionPoolInfo? Pool { get; init; }
}

/// <param name="Field">Set when the connection was found in a field of an object on the stack
/// (e.g. "NightlyJob._leaderConnection") rather than as a local: it outlives the current call.</param>
public sealed record DatabaseConnection(int? SessionId, bool InTransaction, string? Field = null);

public sealed record ConnectionPoolInfo(int MaxSize, int OpenConnections, int IdleConnections);

public sealed record AsyncPoolWait
{
    public required string Provider { get; init; }
    public required ConnectionPoolInfo Pool { get; init; }
    /// Callers queued inside the pool for a connection (OpenAsync).
    public int QueuedOpens { get; init; }
    /// The app's paused async methods that use this pool.
    public IReadOnlyList<AsyncConnectionUser> Methods { get; init; } = [];
}

/// <param name="HoldsConnection">One of its locals is an open connection from the pool.</param>
/// <param name="WaitingForConnection">It is awaiting, directly or through other async methods, an OpenAsync on the pool.</param>
/// <param name="OpeningIn">The app method in its await chain that calls OpenAsync, with where, e.g. "AuditLog.WriteAsync (Audit.cs:12)".</param>
public sealed record AsyncConnectionUser(string Method, SourceLocation? PausedAt, bool HoldsConnection, bool WaitingForConnection, string? OpeningIn = null);
