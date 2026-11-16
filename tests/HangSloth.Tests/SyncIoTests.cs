using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

public class SyncIoClassificationTests
{
    /// What Linux puts under every synchronous socket call on a blocking socket.
    private static readonly StackFrameInfo[] SocketReceive =
    [
        Fx("Interop+Sys", "ReceiveMessage"),
        Fx("System.Net.Sockets.SocketPal", "SysReceive"),
        Fx("System.Net.Sockets.SocketPal", "TryCompleteReceiveFrom"),
        Fx("System.Net.Sockets.SocketAsyncContext", "ReceiveFrom"),
        Fx("System.Net.Sockets.Socket", "Receive"),
    ];

    /// A synchronous call on a non-blocking socket on Linux: it waits for the socket's event in Monitor.Wait.
    private static readonly StackFrameInfo[] SyncOperationWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Threading.ManualResetEventSlim", "Wait"),
        Fx("System.Net.Sockets.SocketAsyncContext", "PerformSyncOperation"),
        Fx("System.Net.Sockets.SocketAsyncContext", "ReceiveFrom"),
        Fx("System.Net.Sockets.Socket", "Receive"),
        Fx("System.Net.Sockets.NetworkStream", "Read"),
        Fx("System.Net.Http.HttpConnection", "Fill"),
    ];

    private static StackFrameInfo Caller => App("MyApp.MailFeed", "Poll", "MailFeed.cs", 21);

    [Theory]
    [InlineData("System.Net.Sockets.Socket", "Receive", "a synchronous socket read (Socket.Receive)", "Socket.ReceiveAsync")]
    [InlineData("System.Net.Sockets.Socket", "ReceiveFrom", "a synchronous socket read (Socket.ReceiveFrom)", "Socket.ReceiveFromAsync")]
    [InlineData("System.Net.Sockets.Socket", "ReceiveMessageFrom", "a synchronous socket read (Socket.ReceiveMessageFrom)", "Socket.ReceiveMessageFromAsync")]
    [InlineData("System.Net.Sockets.Socket", "Send", "a synchronous socket write (Socket.Send)", "Socket.SendAsync")]
    [InlineData("System.Net.Sockets.Socket", "SendTo", "a synchronous socket write (Socket.SendTo)", "Socket.SendToAsync")]
    [InlineData("System.Net.Sockets.Socket", "SendFile", "a synchronous socket write (Socket.SendFile)", "Socket.SendFileAsync")]
    [InlineData("System.Net.Sockets.Socket", "Accept", "a synchronous socket accept (Socket.Accept)", "Socket.AcceptAsync")]
    [InlineData("System.Net.Sockets.Socket", "Connect", "a synchronous socket connect (Socket.Connect)", "Socket.ConnectAsync")]
    [InlineData("System.Net.Sockets.TcpClient", "Connect", "a synchronous socket connect (TcpClient.Connect)", "TcpClient.ConnectAsync")]
    [InlineData("System.Net.Sockets.TcpListener", "AcceptTcpClient", "a synchronous socket accept (TcpListener.AcceptTcpClient)", "TcpListener.AcceptTcpClientAsync")]
    [InlineData("System.Net.Sockets.TcpListener", "AcceptSocket", "a synchronous socket accept (TcpListener.AcceptSocket)", "TcpListener.AcceptSocketAsync")]
    [InlineData("System.Net.Sockets.NetworkStream", "Read", "a synchronous network read (NetworkStream.Read)", "NetworkStream.ReadAsync")]
    [InlineData("System.Net.Sockets.NetworkStream", "Write", "a synchronous network write (NetworkStream.Write)", "NetworkStream.WriteAsync")]
    [InlineData("System.IO.Pipes.PipeStream", "Read", "a synchronous pipe read (PipeStream.Read)", "PipeStream.ReadAsync")]
    [InlineData("System.IO.Pipes.PipeStream", "Write", "a synchronous pipe write (PipeStream.Write)", "PipeStream.WriteAsync")]
    [InlineData("System.IO.Pipes.NamedPipeServerStream", "WaitForConnection", "a synchronous wait for a pipe client (NamedPipeServerStream.WaitForConnection)", "NamedPipeServerStream.WaitForConnectionAsync")]
    [InlineData("System.Net.Security.SslStream", "Read", "a synchronous TLS read (SslStream.Read)", "SslStream.ReadAsync")]
    [InlineData("System.Net.Security.SslStream", "Write", "a synchronous TLS write (SslStream.Write)", "SslStream.WriteAsync")]
    [InlineData("System.Net.Security.SslStream", "AuthenticateAsClient", "a synchronous TLS handshake (SslStream.AuthenticateAsClient)", "SslStream.AuthenticateAsClientAsync")]
    [InlineData("System.Net.Security.SslStream", "AuthenticateAsServer", "a synchronous TLS handshake (SslStream.AuthenticateAsServer)", "SslStream.AuthenticateAsServerAsync")]
    [InlineData("System.Net.Dns", "GetHostEntry", "a synchronous DNS lookup (Dns.GetHostEntry)", "Dns.GetHostEntryAsync")]
    [InlineData("System.Net.Dns", "GetHostAddresses", "a synchronous DNS lookup (Dns.GetHostAddresses)", "Dns.GetHostAddressesAsync")]
    [InlineData("System.Net.Http.HttpClient", "Send", "a synchronous HTTP request (HttpClient.Send)", "HttpClient.SendAsync")]
    [InlineData("System.IO.Pipes.NamedPipeClientStream", "Connect", "a synchronous pipe connect (NamedPipeClientStream.Connect)", "NamedPipeClientStream.ConnectAsync")]
    [InlineData("System.Net.Dns", "GetHostByName", "a synchronous DNS lookup (Dns.GetHostByName)", "Dns.GetHostEntryAsync")]
    [InlineData("System.Console", "ReadLine", "a synchronous console read (Console.ReadLine)", null)]
    [InlineData("System.Console", "Read", "a synchronous console read (Console.Read)", null)]
    [InlineData("System.Console", "ReadKey", "a synchronous console read (Console.ReadKey)", null)]
    public void Public_synchronous_io_calls_are_recognised(string type, string method, string described, string? async)
    {
        StackFrameInfo[] frames = [Fx("Interop+Sys", "Read"), Fx(type, method), Caller];
        Assert.Equal(BlockingKind.SyncIO, FrameClassifier.Classify(frames));
        Assert.True(FrameClassifier.IsBlocked(BlockingKind.SyncIO));
        Assert.Equal(described, FrameClassifier.DescribeSyncIo(frames));
        Assert.Equal(async, FrameClassifier.SyncIoAsyncAlternative(frames));
    }

    [Fact]
    public void The_call_the_app_made_is_named_rather_than_the_socket_call_inside_it()
    {
        StackFrameInfo[] frames = [.. SocketReceive, Fx("System.Net.Sockets.NetworkStream", "Read"), Fx("System.IO.StreamReader", "ReadLine"), Caller];
        Assert.Equal(BlockingKind.SyncIO, FrameClassifier.Classify(frames));
        Assert.Equal("a synchronous network read (NetworkStream.Read)", FrameClassifier.DescribeSyncIo(frames));
    }

    [Theory]
    [InlineData("System.Net.Sockets.Socket", "ReceiveAsync")]
    [InlineData("System.Net.Sockets.Socket", "get_ReceiveTimeout")]
    [InlineData("System.Net.Sockets.NetworkStream", "ReadAsync")]
    [InlineData("System.Net.Sockets.NetworkStream", "BeginRead")]
    [InlineData("System.Net.Http.HttpClient", "SendAsync")]
    [InlineData("System.Console", "WriteLine")]
    [InlineData("System.IO.Pipes.PipeStream", "get_ReadMode")]
    [InlineData("System.IO.Pipes.PipeStream", "ReadAsyncCore")]
    [InlineData("System.Net.Security.SslStream", "WriteAsyncInternal")]
    [InlineData("System.Net.Sockets.Socket", "SendAsyncForNetworkStream")]
    [InlineData("System.Net.Dns", "GetHostName")]
    public void Other_methods_of_those_types_are_not_synchronous_io(string type, string method)
    {
        Assert.Equal(BlockingKind.None, FrameClassifier.Classify([Fx(type, method), Caller]));
    }

    [Fact]
    public void A_socket_call_below_the_app_code_is_not_what_the_thread_is_doing()
    {
        Assert.Equal(BlockingKind.None, FrameClassifier.Classify([Caller, .. SocketReceive, App("MyApp.Program", "Main", "Program.cs", 4)]));
    }

    [Fact]
    public void Driver_lock_and_task_waits_above_the_io_keep_winning()
    {
        // A database driver's command is a database wait even though its socket read is on the stack.
        StackFrameInfo[] query = [.. SocketReceive, Lib("Npgsql.Internal.NpgsqlReadBuffer", "Ensure"), Lib("Npgsql.NpgsqlCommand", "ExecuteNonQuery"), Caller];
        Assert.Equal(BlockingKind.Database, FrameClassifier.Classify(query));
        Assert.Equal(BlockingKind.MonitorEnter, FrameClassifier.Classify([.. MonitorEnter, Fx("System.IO.SyncTextReader", "ReadLine"), Fx("System.Console", "ReadLine"), Caller]));
        Assert.Equal(BlockingKind.TaskWait, FrameClassifier.Classify([.. ResultWait, Fx("System.Net.Http.HttpClient", "Send"), Caller]));
        Assert.Equal(BlockingKind.Sleep, FrameClassifier.Classify([Fx("System.Threading.Thread", "Sleep"), Fx("System.IO.Pipes.NamedPipeClientStream", "Connect"), Fx("System.Net.Dns", "GetHostEntry"), Caller]));
    }

    [Fact]
    public void A_database_drivers_own_io_is_left_to_the_database_waits()
    {
        StackFrameInfo[] commit = [.. SocketReceive, Fx("System.Net.Sockets.NetworkStream", "Read"), Lib("Npgsql.Internal.NpgsqlReadBuffer", "Ensure"), Lib("Npgsql.NpgsqlTransaction", "Commit"), Caller];
        Assert.Equal(BlockingKind.None, FrameClassifier.Classify(commit));
    }

    [Fact]
    public void A_synchronous_operation_on_a_non_blocking_socket_is_io_not_a_monitor_wait()
    {
        StackFrameInfo[] frames = [.. SyncOperationWait, Fx("System.Net.Http.HttpClient", "Send"), Caller];
        Assert.Equal(BlockingKind.SyncIO, FrameClassifier.Classify(frames));
        Assert.Equal("a synchronous HTTP request (HttpClient.Send)", FrameClassifier.DescribeSyncIo(frames));
        // Without the socket's own wait, a Monitor.Wait above the I/O is still a Monitor.Wait.
        Assert.Equal(BlockingKind.MonitorWait, FrameClassifier.Classify([Fx("System.Threading.Monitor", "Wait"), Fx("System.Net.Sockets.Socket", "Receive"), Caller]));
    }

    [Fact]
    public void Activity_names_the_io_call()
    {
        var t = Thread(5, [.. SocketReceive, Caller]);
        Assert.Equal("blocked in a synchronous socket read (Socket.Receive) in MailFeed.cs:21", Describe.Activity(t));
        Assert.Equal("blocked in a synchronous socket read (Socket.Receive) in MailFeed.Poll (MailFeed.cs:21)", Describe.Activity(t, withMethod: true));
    }

    private static StackFrameInfo Lib(string type, string method) => new() { TypeName = type, MethodName = method, ModuleName = "/app/Npgsql.dll" };

    internal static StackFrameInfo[] Receiving(StackFrameInfo caller) => [.. SocketReceive, caller];
}

public class SyncIoDetectorTests
{
    private static readonly ThreadPoolSnapshot FullPool = new() { MinThreads = 4, MaxThreads = 8, ActiveWorkers = 8, QueuedWorkItems = 5 };

    private static ThreadSnapshot ReadingWorker(int id) =>
        Thread(id, [.. SyncIoClassificationTests.Receiving(App("MyApp.MailFeed", "ReadGreeting", "MailFeed.cs", 36)), App("MyApp.MailQueue", "Drain", "MailQueue.cs", 12), .. PoolBottom])
            with { IsThreadPoolWorker = true };

    private static ThreadSnapshot IdleWorker(int id) =>
        Thread(id, [Fx("System.Threading.LowLevelLifoSemaphore", "Wait"), Fx("System.Threading.PortableThreadPool+WorkerThread", "WorkerThreadStart")]) with { IsThreadPoolWorker = true };

    private static MonitorLock Lock(ulong addr, string field, int owner, params int[] waiters) => new()
    {
        Object = new ObjectRef(addr, "System.Object"),
        FieldName = field,
        OwnerThreadId = owner,
        WaiterThreadIds = waiters,
    };

    [Fact]
    public void Pool_threads_stuck_in_synchronous_socket_reads_are_starvation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 8).Select(ReadingWorker)]) with { ThreadPool = FullPool };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in a synchronous socket read (Socket.Receive) at MailFeed.cs:36), and 5 work items are waiting in the queue.", f.Summary);
        Assert.Contains("8 threads: a synchronous socket read (Socket.Receive) at MailFeed.ReadGreeting (MailFeed.cs:36).", f.Details);
        Assert.Contains("A thread in synchronous I/O keeps its thread pool thread for as long as the other side takes to answer, and forever when it never does.", f.Details);
        Assert.Equal("Don't do synchronous I/O on thread pool threads: await Socket.ReceiveAsync instead of calling Socket.Receive in MailFeed.cs:36. Raising ThreadPool.SetMinThreads only hides the problem.", f.Advice);
    }

    [Fact]
    public void Starvation_by_a_call_without_an_async_version_says_to_move_it_to_a_dedicated_thread()
    {
        var workers = Enumerable.Range(10, 6)
            .Select(id => Thread(id, [Fx("System.ConsolePal", "ReadKey"), Fx("System.Console", "ReadKey"), App("MyApp.Prompt", "Confirm", "Prompt.cs", 8), .. PoolBottom]) with { IsThreadPoolWorker = true });
        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(Process([.. workers]), new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 6 of 6 thread pool threads are blocked (all in a synchronous console read (Console.ReadKey) at Prompt.cs:8).", f.Summary);
        Assert.Equal("Don't do synchronous I/O on thread pool threads (Console.ReadKey in Prompt.cs:8): do it on one dedicated thread and hand the results over. Raising ThreadPool.SetMinThreads only hides the problem.", f.Advice);
    }

    [Fact]
    public void Starvation_advice_names_the_io_call_most_threads_are_in()
    {
        // A relay that reads from one socket and writes to another: the first thread is in the write, most are in the read.
        static ThreadSnapshot Relay(int id, string method) =>
            Thread(id, [Fx("System.Net.Sockets.Socket", method), App("MyApp.MailRelay", "Pump", "MailRelay.cs", 40), .. PoolBottom]) with { IsThreadPoolWorker = true };
        var snapshot = Process([Relay(10, "Send"), .. Enumerable.Range(11, 5).Select(id => Relay(id, "Receive"))]);

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 6 of 6 thread pool threads are blocked (all in a synchronous socket read (Socket.Receive) at MailRelay.cs:40).", f.Summary);
        Assert.Equal("Don't do synchronous I/O on thread pool threads: await Socket.ReceiveAsync instead of calling Socket.Receive in MailRelay.cs:40. Raising ThreadPool.SetMinThreads only hides the problem.", f.Advice);
    }

    [Fact]
    public void Starvation_by_Console_ReadLine_says_to_move_it_to_a_dedicated_thread()
    {
        // Console.In.ReadLineAsync blocks the calling thread too, so it is no way out.
        var workers = Enumerable.Range(10, 6)
            .Select(id => Thread(id, [Fx("System.IO.SyncTextReader", "ReadLine"), Fx("System.Console", "ReadLine"), App("MyApp.Prompt", "Ask", "Prompt.cs", 12), .. PoolBottom]) with { IsThreadPoolWorker = true });
        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(Process([.. workers]), new HashSet<int>()));
        Assert.Equal("Don't do synchronous I/O on thread pool threads (Console.ReadLine in Prompt.cs:12): do it on one dedicated thread and hand the results over. Raising ThreadPool.SetMinThreads only hides the problem.", f.Advice);
    }

    [Fact]
    public void Synchronous_io_on_a_few_pool_threads_is_not_starvation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 4).Select(ReadingWorker), .. Enumerable.Range(20, 6).Select(IdleWorker)]);
        Assert.Empty(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
    }

    [Fact]
    public void Lock_owner_in_synchronous_io_is_blocked_while_holding_the_lock()
    {
        var snapshot = Process(
            Thread(5, SyncIoClassificationTests.Receiving(App("MyApp.Cache", "Refresh", "Cache.cs", 111)), "refresher"),
            Thread(6, [.. MonitorEnter, App("MyApp.Cache", "Get", "Cache.cs", 117)]) with { WaitingForLock = 0xA },
            Thread(7, [.. MonitorEnter, App("MyApp.Cache", "Get", "Cache.cs", 117)]) with { WaitingForLock = 0xA }) with
        {
            Locks = [Lock(0xA, "Cache.Gate", 5, 6, 7)],
        };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("2 threads are stuck waiting for the lock on Cache.Gate, held by Thread 5 (\"refresher\"), which is blocked in a synchronous socket read (Socket.Receive) in Cache.cs:111 while holding it.", f.Summary);
        Assert.Equal("Don't do I/O while holding a lock: Socket.Receive waits for as long as the other side takes to answer, and every thread that needs Cache.Gate waits with it. " +
                     "Do the I/O before taking the lock and hold the lock only to update shared state; if the lock has to cover the I/O, use a SemaphoreSlim with WaitAsync and await Socket.ReceiveAsync.", f.Advice);
    }

    [Fact]
    public void A_thread_in_synchronous_io_still_counts_as_app_code_that_may_release()
    {
        var reading = Thread(6, SyncIoClassificationTests.Receiving(App("MyApp.Feed", "Poll", "Feed.cs", 9)));
        Assert.True(SemaphoreFacts.AppCodeRunningOutside(Process(reading), []));

        // So two crossed semaphore waits stay a warning while it may still come back and release one of them.
        StackFrameInfo[] semaphoreWait = [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "Wait")];
        SemaphoreInfo Sem(ulong address, string name, int waiter) =>
            new() { Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"), FieldName = name, CurrentCount = 0, MaxCount = 1, WaiterThreadIds = [waiter] };
        var snapshot = Process(
            Thread(4, [.. semaphoreWait, App("A", "M", "A.cs", 1)]) with { WaitingForSemaphore = 0x200 },
            Thread(5, [.. semaphoreWait, App("B", "M", "B.cs", 1)]) with { WaitingForSemaphore = 0x100 },
            reading) with
        {
            Semaphores = [Sem(0x100, "S.A", 5), Sem(0x200, "S.B", 4)],
        };
        Assert.Equal(Severity.Warning, Assert.Single(new SemaphoreDeadlockDetector().Detect(snapshot, new HashSet<int>())).Severity);
    }

    [Fact]
    public void A_main_thread_waiting_in_Console_ReadLine_is_a_healthy_app()
    {
        var main = Thread(1, [Fx("Interop+Sys", "Read"), Fx("System.ConsolePal", "Read"), Fx("System.IO.StreamReader", "ReadLine"), Fx("System.IO.SyncTextReader", "ReadLine"),
            Fx("System.Console", "ReadLine"), App("MyApp.Program", "Main", "Program.cs", 14)]);
        var snapshot = Process([main, .. Enumerable.Range(10, 4).Select(IdleWorker)]) with
        {
            ThreadPool = new ThreadPoolSnapshot { MinThreads = 4, MaxThreads = 32767, ActiveWorkers = 0, QueuedWorkItems = 0 },
        };

        Assert.Equal(BlockingKind.SyncIO, FrameClassifier.Classify(main.Frames));
        Assert.Empty(new HangAnalyzer().Analyze(snapshot).Findings);
    }
}
