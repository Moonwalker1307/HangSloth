using HangSloth.Model;

namespace HangSloth.Analysis;

/// What a thread is doing, judged from the framework frames on top of its stack.
public enum BlockingKind
{
    /// Not blocked in a known wait (running, or nothing recognisable).
    None,
    /// Infrastructure thread parked waiting for work (idle pool worker, timer thread, gate thread).
    Idle,
    /// Synchronously waiting on a Task: .Result, .Wait(), .GetAwaiter().GetResult(), Task.WaitAll.
    TaskWait,
    /// Trying to enter a lock (Monitor.Enter / lock statement / System.Threading.Lock).
    MonitorEnter,
    /// Monitor.Wait: waiting to be pulsed.
    MonitorWait,
    Sleep,
    Join,
    SemaphoreWait,
    WaitHandle,
    /// Trying to enter a ReaderWriterLockSlim (read, write or upgradeable).
    ReaderWriterLock,
    /// Waiting for another thread to finish running a static constructor.
    TypeInitializer,
    /// Waiting for a database server to answer a command.
    Database,
    /// Waiting for a free connection from a database connection pool.
    ConnectionPool,
    /// GC.WaitForPendingFinalizers: waiting for the finalizer thread.
    Finalizers,
    /// Disposing a CancellationTokenRegistration whose callback is running on another thread.
    CancellationCallback,
    /// Process.WaitForExit.
    ProcessExit,
}

public static class FrameClassifier
{
    public static BlockingKind Classify(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameworkPrefix(frames);
        if (prefix.Count == 0) return BlockingKind.None;

        // app.Run() / host.Run() parks the main thread until shutdown. That's a healthy app, not a hang.
        if (prefix.Any(IsHostRunFrame)) return BlockingKind.Idle;
        if (prefix.Any(IsConnectionPoolFrame)) return BlockingKind.ConnectionPool;
        if (prefix.Any(IsDatabaseCommandFrame)) return BlockingKind.Database;
        if (prefix.Any(f => IsTaskWaitMethod(f.TypeName, f.MethodName))) return BlockingKind.TaskWait;
        if (IsIdleInfrastructure(frames)) return BlockingKind.Idle;
        if (prefix.Any(IsTypeInitializerWait)) return BlockingKind.TypeInitializer;
        if (prefix.Any(IsMonitorEnter)) return BlockingKind.MonitorEnter;
        if (prefix.Any(f => f.TypeName == "System.GC" && f.MethodName == "WaitForPendingFinalizers")) return BlockingKind.Finalizers;
        if (prefix.Any(f => f.MethodName == "WaitForCallbackToComplete" && f.TypeName.StartsWith("System.Threading.CancellationTokenSource", StringComparison.Ordinal)))
            return BlockingKind.CancellationCallback;
        if (prefix.Any(f => f.TypeName == "System.Diagnostics.Process" && f.MethodName.StartsWith("WaitForExit", StringComparison.Ordinal))) return BlockingKind.ProcessExit;
        if (prefix.Any(f => f.TypeName == "System.Threading.ReaderWriterLockSlim")) return BlockingKind.ReaderWriterLock;
        if (prefix.Any(f => f.TypeName == "System.Threading.SemaphoreSlim" && f.MethodName.StartsWith("Wait", StringComparison.Ordinal))) return BlockingKind.SemaphoreWait;
        if (prefix.Any(f => f.TypeName == "System.Threading.Monitor" && f.MethodName is "Wait" or "ObjWait")) return BlockingKind.MonitorWait;
        if (prefix.Any(f => f.TypeName == "System.Threading.Thread" && f.MethodName is "Sleep" or "SleepInternal")) return BlockingKind.Sleep;
        if (prefix.Any(f => f.TypeName == "System.Threading.Thread" && f.MethodName == "Join")) return BlockingKind.Join;
        if (prefix.Any(f => f.TypeName == "System.Threading.WaitHandle" && f.MethodName.StartsWith("Wait", StringComparison.Ordinal))) return BlockingKind.WaitHandle;
        return BlockingKind.None;
    }

    private static readonly HashSet<string> CompletionMethods =
        ["SetResult", "TrySetResult", "SetException", "TrySetException", "SetCanceled", "TrySetCanceled", "TrySetCanceledCore"];

    /// An async method's continuation running inline, inside the SetResult (or Cancel) call that completed the
    /// task it awaited: the awaiting code and the completing code share one thread, and the completer's locks.
    /// Returns the async method's frame and the app frame that completed the task.
    public static (StackFrameInfo Continuation, StackFrameInfo Completer)? InlineContinuation(IReadOnlyList<StackFrameInfo> frames)
    {
        var first = frames.ToList().FindIndex(IsUserFrame);
        if (first < 0) return null;
        var completedAt = -1;
        for (var i = first + 1; i < frames.Count; i++)
        {
            if (IsUserFrame(frames[i]))
            {
                if (completedAt < 0) return null;
                return (frames[first], frames[i]);
            }
            if (frames[i].TypeName.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal) && CompletionMethods.Contains(frames[i].MethodName)
                || frames[i].TypeName == "System.Threading.CancellationTokenSource" && frames[i].MethodName.StartsWith("Cancel", StringComparison.Ordinal))
                completedAt = i;
        }
        return null;
    }

    /// The timer callback frame, when the thread is running a System.Threading.Timer callback.
    public static StackFrameInfo? TimerCallback(IReadOnlyList<StackFrameInfo> frames) =>
        frames.Any(f => f.TypeName.StartsWith("System.Threading.TimerQueueTimer", StringComparison.Ordinal) && f.MethodName is "Fire" or "CallCallback")
            ? FirstUserFrame(frames)
            : null;

    public static bool IsBlocked(BlockingKind kind) => kind is not (BlockingKind.None or BlockingKind.Idle);

    /// The framework frames at the top of the stack, above the first frame of the app's own code.
    public static IReadOnlyList<StackFrameInfo> FrameworkPrefix(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = new List<StackFrameInfo>();
        foreach (var frame in frames)
        {
            if (IsUserFrame(frame)) break;
            prefix.Add(frame);
        }
        return prefix;
    }

    public static StackFrameInfo? FirstUserFrame(IReadOnlyList<StackFrameInfo> frames) => frames.FirstOrDefault(IsUserFrame);

    /// The innermost user frame that has a source location, falling back to the innermost user frame.
    public static StackFrameInfo? BestUserFrame(IReadOnlyList<StackFrameInfo> frames) =>
        FirstUserFrame(frames) is { } first && first.Source is null
            ? frames.Where(IsUserFrame).FirstOrDefault(f => f.Source is not null) ?? first
            : FirstUserFrame(frames);

    public static bool IsUserFrame(StackFrameInfo frame) =>
        !frame.IsRuntimeFrame && !IsFrameworkType(frame.TypeName) && !IsFrameworkModule(frame.ModuleName);

    public static bool IsFrameworkType(string typeName) =>
        typeName.StartsWith("System.", StringComparison.Ordinal)
        || typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
        || typeName.StartsWith("Internal.", StringComparison.Ordinal)
        || typeName.Length == 0
        || IsLibraryType(typeName);

    /// Well-known third-party libraries (database drivers and the like) whose frames are plumbing,
    /// not the app's code: the interesting frame is the app's call into them.
    private static readonly string[] LibraryNamespaces =
        ["Npgsql.", "MySqlConnector.", "MySql.Data.", "Oracle.ManagedDataAccess.", "Dapper.", "Polly."];

    public static bool IsLibraryType(string typeName)
    {
        foreach (var ns in LibraryNamespaces)
            if (typeName.StartsWith(ns, StringComparison.Ordinal)) return true;
        return false;
    }

    /// Database drivers by namespace, and how to name the server they talk to.
    public static string? DatabaseProvider(string typeName) =>
        typeName.StartsWith("Npgsql.", StringComparison.Ordinal) ? "PostgreSQL"
        : typeName.StartsWith("Microsoft.Data.SqlClient.", StringComparison.Ordinal) || typeName.StartsWith("System.Data.SqlClient.", StringComparison.Ordinal) ? "SQL Server"
        : typeName.StartsWith("MySqlConnector.", StringComparison.Ordinal) || typeName.StartsWith("MySql.Data.", StringComparison.Ordinal) ? "MySQL"
        : typeName.StartsWith("Oracle.ManagedDataAccess.", StringComparison.Ordinal) ? "Oracle"
        : typeName.StartsWith("Microsoft.Data.Sqlite.", StringComparison.Ordinal) ? "SQLite"
        : null;

    /// A driver's command or reader method: the thread is waiting for the server's answer.
    public static bool IsDatabaseCommandFrame(StackFrameInfo f)
    {
        if (DatabaseProvider(f.TypeName) is null) return false;
        var type = f.TypeName;
        if (type.IndexOf('+') is var plus and >= 0) type = type[..plus];
        if (type.IndexOf('<') is var generic and >= 0) type = type[..generic];
        return (type.EndsWith("Command", StringComparison.Ordinal) && f.MethodName.StartsWith("Execute", StringComparison.Ordinal))
               || (type.EndsWith("Batch", StringComparison.Ordinal) && f.MethodName.StartsWith("Execute", StringComparison.Ordinal))
               || (type.EndsWith("DataReader", StringComparison.Ordinal) && f.MethodName is "Read" or "NextResult" or "ReadAsync" or "NextResultAsync");
    }

    /// Renting a connection from the driver's pool (Npgsql's PoolingDataSource, SqlClient's DbConnectionPool...).
    public static bool IsConnectionPoolFrame(StackFrameInfo f) =>
        DatabaseProvider(f.TypeName) is not null
        && (f.TypeName.Contains("PoolingDataSource", StringComparison.Ordinal) || f.TypeName.Contains("ConnectionPool", StringComparison.Ordinal))
        && (f.MethodName.Contains("Get", StringComparison.Ordinal) || f.MethodName.Contains("Rent", StringComparison.Ordinal) || f.MethodName.Contains("Wait", StringComparison.Ordinal));

    /// .NET 9+ runs class initialization through managed helpers; a thread parked in one, before any of
    /// the app's own frames, is waiting for another thread's static constructor.
    public static bool IsTypeInitializerWait(StackFrameInfo f) =>
        f.TypeName is "System.Runtime.CompilerServices.InitHelpers" or "System.Runtime.CompilerServices.StaticsHelpers"
        && (f.MethodName.StartsWith("InitClass", StringComparison.Ordinal) || f.MethodName.Contains("StaticBase", StringComparison.Ordinal));

    /// Types whose static constructor is running on this thread (a ".cctor" frame on the stack).
    public static IEnumerable<string> RunningStaticConstructors(IReadOnlyList<StackFrameInfo> frames) =>
        frames.Where(f => f.MethodName == ".cctor" && IsUserFrame(f)).Select(f => f.TypeName);

    public static bool IsFrameworkModule(string? modulePath)
    {
        if (string.IsNullOrEmpty(modulePath)) return false;
        var normalized = modulePath.Replace('\\', '/');
        if (normalized.Contains("/shared/Microsoft.", StringComparison.OrdinalIgnoreCase)) return true;
        var file = Path.GetFileName(normalized);
        return file.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
               || file.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
               || file.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase)
               || file.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTaskWaitMethod(string typeName, string method)
    {
        if (typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
        {
            return method is "Wait" or "WaitAll" or "WaitAllCore" or "WaitAllBlockingCore" or "WaitAny" or "WaitAnyCore"
                or "InternalWait" or "InternalWaitCore" or "SpinThenBlockingWait" or "GetResultCore" or "get_Result";
        }
        if (typeName.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal)
            || typeName.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable", StringComparison.Ordinal))
        {
            return method is "GetResult" or "ValidateEnd" or "HandleNonSuccessAndDebuggerNotification";
        }
        return false;
    }

    /// How the app's code blocked on the task, in the words a developer would use.
    public static string DescribeTaskWait(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameworkPrefix(frames);
        if (prefix.Any(f => f.TypeName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) && f.MethodName is "GetResult" or "HandleNonSuccessAndDebuggerNotification" or "ValidateEnd"))
            return ".GetAwaiter().GetResult()";
        if (prefix.Any(f => f.MethodName.StartsWith("WaitAll", StringComparison.Ordinal))) return "Task.WaitAll(...)";
        if (prefix.Any(f => f.MethodName.StartsWith("WaitAny", StringComparison.Ordinal))) return "Task.WaitAny(...)";
        if (prefix.Any(f => f.MethodName is "GetResultCore" or "get_Result")) return ".Result";
        if (prefix.Any(f => f.MethodName == "Wait")) return ".Wait()";
        return "a blocking wait";
    }

    public static bool IsThreadPoolDispatchFrame(StackFrameInfo frame) =>
        (frame.TypeName == "System.Threading.ThreadPoolWorkQueue" && frame.MethodName == "Dispatch")
        || (frame.TypeName.StartsWith("System.Threading.PortableThreadPool+WorkerThread", StringComparison.Ordinal) && frame.MethodName.StartsWith("WorkerThreadStart", StringComparison.Ordinal));

    /// The pool's own housekeeping threads (gate thread, wait thread), which are flagged as pool threads but never run work items.
    public static bool IsThreadPoolServiceFrame(StackFrameInfo frame) =>
        frame.TypeName.StartsWith("System.Threading.PortableThreadPool+GateThread", StringComparison.Ordinal)
        || frame.TypeName.StartsWith("System.Threading.PortableThreadPool+WaitThread", StringComparison.Ordinal);

    /// The generic host's blocking Run/WaitForShutdown (behind WebApplication.Run, IHost.Run, RunConsoleAsync().Wait()).
    public static bool IsHostRunFrame(StackFrameInfo f) =>
        (f.TypeName.StartsWith("Microsoft.Extensions.Hosting.", StringComparison.Ordinal) && f.MethodName is "Run" or "WaitForShutdown")
        || (f.TypeName == "Microsoft.AspNetCore.Builder.WebApplication" && f.MethodName == "Run");

    private static bool IsMonitorEnter(StackFrameInfo f) =>
        (f.TypeName == "System.Threading.Monitor" && (f.MethodName is "Enter" or "ReliableEnter" or "ReliableEnterTimeout" or "TryEnter" || f.MethodName.Contains("Slow", StringComparison.OrdinalIgnoreCase)))
        || (f.TypeName == "System.Threading.Lock" && f.MethodName.Contains("Enter", StringComparison.Ordinal));

    private static bool IsIdleInfrastructure(IReadOnlyList<StackFrameInfo> frames)
    {
        // Idle means: nothing but runtime plumbing on the stack, parked in its own wait.
        if (frames.Any(IsUserFrame)) return false;
        return frames.Any(f =>
            (f.TypeName.StartsWith("System.Threading.PortableThreadPool", StringComparison.Ordinal) && f.MethodName is "WorkerThreadStart" or "GateThreadStart")
            || f.TypeName == "System.Threading.TimerQueue" && f.MethodName == "TimerThread"
            || f.TypeName.StartsWith("System.Threading.PortableThreadPool+WaitThread", StringComparison.Ordinal)
            || f.TypeName.StartsWith("System.Threading.LowLevelLifoSemaphore", StringComparison.Ordinal) && frames.All(x => !IsUserFrame(x))
            || f.TypeName == "System.GC" && f.MethodName.Contains("Finaliz", StringComparison.Ordinal)
            || f.TypeName.Contains("Sockets", StringComparison.Ordinal) && f.MethodName.Contains("EventLoop", StringComparison.Ordinal));
    }
}
