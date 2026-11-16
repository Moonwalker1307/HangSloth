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
    /// Blocked in a synchronous network, pipe or console call (Socket.Receive, NetworkStream.Read, Console.ReadLine...).
    /// It can still return and carry on, so it doesn't rule the thread out as one that may release something.
    SyncIO,
}

public static class FrameClassifier
{
    public static BlockingKind Classify(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameworkPrefix(frames);
        if (prefix.Count == 0) return BlockingKind.None;

        // app.Run() / host.Run() parks the main thread until shutdown. That's a healthy app, not a hang.
        if (prefix.Any(IsHostRunFrame)) return BlockingKind.Idle;
        // A test runner's own main thread waiting for its engine while the tests run (vstest's testhost).
        if (IsTestHostWait(frames)) return BlockingKind.Idle;
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
        // A synchronous call on a non-blocking socket (HttpClient.Send, or a socket that was used async) waits for
        // the socket's event in Monitor.Wait: name it as the I/O it is.
        if (prefix.Any(IsSyncSocketOperation) && SyncIoFrame(prefix) is not null) return BlockingKind.SyncIO;
        if (prefix.Any(f => f.TypeName == "System.Threading.Monitor" && f.MethodName is "Wait" or "ObjWait")) return BlockingKind.MonitorWait;
        if (prefix.Any(f => f.TypeName == "System.Threading.Thread" && f.MethodName is "Sleep" or "SleepInternal")) return BlockingKind.Sleep;
        if (prefix.Any(f => f.TypeName == "System.Threading.Thread" && f.MethodName == "Join")) return BlockingKind.Join;
        if (prefix.Any(f => f.TypeName == "System.Threading.WaitHandle" && f.MethodName.StartsWith("Wait", StringComparison.Ordinal))) return BlockingKind.WaitHandle;
        // Last: a driver, a lock or a task wait above the I/O says more about what the thread waits for.
        if (SyncIoFrame(prefix) is not null) return BlockingKind.SyncIO;
        return BlockingKind.None;
    }

    /// A synchronous I/O call: what it does in words, and the async method to use instead (null when there is none).
    private sealed record SyncIoCall(string What, string? Async);

    /// The public synchronous I/O methods, which are the same on every OS (the Interop frames below them aren't).
    private static SyncIoCall? SyncIoOperation(StackFrameInfo f)
    {
        var m = f.MethodName;
        // The async methods and their internal helpers (SslStream.WriteAsyncInternal, Socket.SendAsyncForNetworkStream).
        if (m.Contains("Async", StringComparison.Ordinal)) return null;
        bool Is(string stem) => m.StartsWith(stem, StringComparison.Ordinal);
        SyncIoCall Call(string what, string? async) => new(what, async is null ? null : Names.ShortMethod(f.TypeName, async));
        switch (f.TypeName)
        {
            case "System.Net.Sockets.Socket":
                return Is("Receive") ? Call("socket read", m is "ReceiveFrom" or "ReceiveMessageFrom" ? m + "Async" : "ReceiveAsync")
                    : Is("Send") ? Call("socket write", m is "SendTo" or "SendFile" ? m + "Async" : "SendAsync")
                    : m == "Accept" ? Call("socket accept", "AcceptAsync")
                    : m == "Connect" ? Call("socket connect", "ConnectAsync")
                    : null;
            case "System.Net.Sockets.TcpClient":
                return m == "Connect" ? Call("socket connect", "ConnectAsync") : null;
            case "System.Net.Sockets.TcpListener":
                return Is("Accept") ? Call("socket accept", m is "AcceptSocket" ? "AcceptSocketAsync" : "AcceptTcpClientAsync") : null;
            case "System.Net.Sockets.NetworkStream":
                return Is("Read") ? Call("network read", "ReadAsync") : Is("Write") ? Call("network write", "WriteAsync") : null;
            case "System.Net.Security.SslStream":
                return Is("Read") ? Call("TLS read", "ReadAsync")
                    : Is("Write") ? Call("TLS write", "WriteAsync")
                    : Is("AuthenticateAs") ? Call("TLS handshake", m + "Async")
                    : null;
            case "System.Net.Dns":
                // Not GetHostName: that's the local machine's name, no lookup.
                return m is "GetHostAddresses" ? Call("DNS lookup", "GetHostAddressesAsync")
                    : m is "GetHostEntry" or "GetHostByName" or "GetHostByAddress" or "Resolve" ? Call("DNS lookup", "GetHostEntryAsync")
                    : null;
            case "System.Net.Http.HttpClient":
                return m == "Send" ? Call("HTTP request", "SendAsync") : null;
            case "System.Console":
                // No async alternative: Console.In.ReadLineAsync blocks the calling thread just like ReadLine.
                return m is "ReadLine" or "Read" or "ReadKey" ? new SyncIoCall("console read", null)
                    : null;
        }
        if (f.TypeName.StartsWith("System.IO.Pipes.", StringComparison.Ordinal))
            return Is("Read") ? Call("pipe read", "ReadAsync")
                : Is("Write") ? Call("pipe write", "WriteAsync")
                : m == "WaitForConnection" ? Call("wait for a pipe client", "WaitForConnectionAsync")
                : m == "Connect" ? Call("pipe connect", "ConnectAsync")
                : null;
        return null;
    }

    /// The synchronous I/O call the thread is blocked in: the outermost one in the framework part of the stack,
    /// which is the one the app made (NetworkStream.Read rather than the Socket.Receive inside it).
    /// A database driver's own I/O is left to the database waits.
    public static StackFrameInfo? SyncIoFrame(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameworkPrefix(frames);
        if (prefix.Any(f => DatabaseProvider(f.TypeName) is not null)) return null;
        return prefix.LastOrDefault(f => SyncIoOperation(f) is not null);
    }

    /// "a synchronous socket read (Socket.Receive)".
    public static string DescribeSyncIo(IReadOnlyList<StackFrameInfo> frames) =>
        SyncIoFrame(frames) is { } f && SyncIoOperation(f) is { } call
            ? $"a synchronous {call.What} ({Names.ShortMethod(f.TypeName, f.MethodName)})"
            : "synchronous I/O";

    /// The synchronous I/O method the thread is blocked in ("Socket.Receive"), or null.
    public static string? SyncIoMethod(IReadOnlyList<StackFrameInfo> frames) =>
        SyncIoFrame(frames) is { } f ? Names.ShortMethod(f.TypeName, f.MethodName) : null;

    /// The async method to await instead of the synchronous I/O call ("Socket.ReceiveAsync"), or null when there is none.
    public static string? SyncIoAsyncAlternative(IReadOnlyList<StackFrameInfo> frames) =>
        SyncIoFrame(frames) is { } f ? SyncIoOperation(f)?.Async : null;

    /// Linux runs a synchronous call on a non-blocking socket as a wait for the socket's event.
    private static bool IsSyncSocketOperation(StackFrameInfo f) =>
        f.TypeName == "System.Net.Sockets.SocketAsyncContext" && f.MethodName.StartsWith("PerformSyncOperation", StringComparison.Ordinal);

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

    /// Renting a connection from the driver's pool (Npgsql's PoolingDataSource, ConnectorPool before 7.0, SqlClient's
    /// DbConnectionPool...). SqlClient before 6.0 keeps its pool in Microsoft.Data.ProviderBase.
    public static bool IsConnectionPoolFrame(StackFrameInfo f) =>
        (DatabaseProvider(f.TypeName) is not null || f.TypeName.StartsWith("Microsoft.Data.ProviderBase.", StringComparison.Ordinal))
        && (f.TypeName.Contains("PoolingDataSource", StringComparison.Ordinal) || f.TypeName.Contains("ConnectionPool", StringComparison.Ordinal)
            || f.TypeName.Contains("ConnectorPool", StringComparison.Ordinal))
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

    /// Said of a task wait when neither the app's IL nor the framework frames tell which call it was.
    public const string UnknownTaskWait = ".Result/.Wait()";

    /// How the app's code blocked on the task, in the words a developer would use.
    public static string DescribeTaskWait(IReadOnlyList<StackFrameInfo> frames)
    {
        // Read from the call in the app's IL: right even when optimized code inlined the framework frames.
        if (FirstUserFrame(frames)?.BlockingCall is { } call) return call;
        var prefix = FrameworkPrefix(frames);
        if (prefix.Any(f => f.TypeName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) && f.MethodName is "GetResult" or "HandleNonSuccessAndDebuggerNotification" or "ValidateEnd"))
            return ".GetAwaiter().GetResult()";
        if (prefix.Any(f => IsTaskType(f) && f.MethodName.StartsWith("WaitAll", StringComparison.Ordinal))) return "Task.WaitAll(...)";
        if (prefix.Any(f => IsTaskType(f) && f.MethodName.StartsWith("WaitAny", StringComparison.Ordinal))) return "Task.WaitAny(...)";
        if (prefix.Any(f => IsTaskType(f) && f.MethodName is "GetResultCore" or "get_Result")) return ".Result";
        // Only Task.Wait itself: ManualResetEventSlim.Wait and Monitor.Wait sit above every kind of task wait.
        if (prefix.Any(f => IsTaskType(f) && f.MethodName == "Wait")) return ".Wait()";
        // Optimized code can inline Task<T>.Result (and Task.Wait()) into the app's method, leaving only
        // Task.InternalWait: don't guess which one it was.
        if (prefix.Any(f => IsTaskType(f) && f.MethodName.StartsWith("InternalWait", StringComparison.Ordinal))) return UnknownTaskWait;
        return "a blocking wait";
    }

    private static bool IsTaskType(StackFrameInfo f) =>
        f.TypeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal);

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

    /// The test platform's own code, as in vstest's testhost.dll (Microsoft.VisualStudio.TestPlatform.TestHost.Program.Main).
    public static bool IsTestPlatformFrame(StackFrameInfo f) =>
        f.TypeName.StartsWith("Microsoft.VisualStudio.TestPlatform.", StringComparison.Ordinal)
        || f.TypeName.StartsWith("Microsoft.Testing.Platform.", StringComparison.Ordinal);

    /// A thread with none of the app's code on it, where the test platform waits (testhost's Main waiting for
    /// its engine to run the tests). The tests themselves run on other threads, which are judged on their own.
    public static bool IsTestHostWait(IReadOnlyList<StackFrameInfo> frames) =>
        !frames.Any(IsUserFrame) && frames.Any(IsTestPlatformFrame);

    /// A task that is the generic host running until shutdown (host.RunAsync(), WaitForShutdownAsync()),
    /// as in Main calling RunAsync().GetAwaiter().GetResult(). Waiting for it is what a healthy host does.
    public static bool IsHostRunTask(TaskInfo task) =>
        (task.TypeName.Contains("Microsoft.Extensions.Hosting.", StringComparison.Ordinal)
         || task.TypeName.Contains("Microsoft.AspNetCore.Builder.WebApplication", StringComparison.Ordinal))
        && (task.TypeName.Contains("<RunAsync>", StringComparison.Ordinal)
            || task.TypeName.Contains("<WaitForShutdownAsync>", StringComparison.Ordinal)
            || task.TypeName.Contains("<RunConsoleAsync>", StringComparison.Ordinal));

    /// The innermost frame below the framework's wait frames, for naming the waiting code when none of it is
    /// the app's (e.g. "Program.Main" in a host's own assembly).
    public static StackFrameInfo? WaitingFrame(IReadOnlyList<StackFrameInfo> frames) =>
        FirstUserFrame(frames) ?? frames.FirstOrDefault(f => !f.IsRuntimeFrame
            && !f.TypeName.StartsWith("System.Threading", StringComparison.Ordinal)
            && !f.TypeName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal));

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
