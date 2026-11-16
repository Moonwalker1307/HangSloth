using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using HangSloth.Reporting;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Threads in the same wait at the same place are one finding, a finding claims only the threads it explains, and
/// sentences fit what they describe.
public class NamesAndGroupingTests
{
    private static AnalysisReport Analyze(ProcessSnapshot snapshot) => new HangAnalyzer().Analyze(snapshot);

    private static readonly TaskInfo Feed =
        new() { Address = 0x900, TypeName = "System.Threading.Tasks.Task<System.String>", Kind = TaskKind.Promise, Description = "a TaskCompletionSource<string> task" };

    private static TaskInfo Fetch(int line, TaskInfo? awaiting = null) => new()
    {
        Address = 0x600, TypeName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox<MyApp.Imports+<FetchAsync>d__1>",
        Kind = TaskKind.AsyncMethod, Description = "Imports.FetchAsync", AwaitLocation = new SourceLocation("/src/Imports.cs", line), Awaiting = awaiting ?? Feed,
    };

    private static ThreadSnapshot Importer(int id, int pausedAt = 20, int line = 12) =>
        Thread(id, [.. ResultWait, App("MyApp.Imports", "Run", "Imports.cs", line)], "import-worker") with { BlockedOnTask = Fetch(pausedAt) };

    // ---------------------------------------------------------------- one finding for threads in the same wait

    [Fact]
    public void Thirty_threads_blocked_at_one_line_on_one_chain_are_one_finding()
    {
        var report = Analyze(Process([.. Enumerable.Range(10, 30).Select(id => Importer(id))]));

        var f = Assert.Single(report.Findings);
        Assert.Equal("blocked-on-task", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("30 \"import-worker\" threads (Thread 10, Thread 11, Thread 12, Thread 13, Thread 14 and 25 more) are blocked in .Result at Imports.cs:12, " +
                     "waiting for Imports.FetchAsync (paused at Imports.cs:20), which awaits a TaskCompletionSource<string> task that nothing has completed yet.", f.Summary);
        Assert.Equal(Enumerable.Range(10, 30), f.ThreadIds);
    }

    [Fact]
    public void One_thread_keeps_the_sentence_it_always_had()
    {
        var f = Assert.Single(Analyze(Process(Importer(10))).Findings);
        Assert.Equal("Thread 10 (\"import-worker\") is blocked in .Result at Imports.cs:12, waiting for Imports.FetchAsync (paused at Imports.cs:20), " +
                     "which awaits a TaskCompletionSource<string> task that nothing has completed yet.", f.Summary);
        Assert.Equal([10], f.ThreadIds);
    }

    [Fact]
    public void Threads_at_one_line_paused_at_different_awaits_are_two_findings()
    {
        var report = Analyze(Process(Importer(10, pausedAt: 20), Importer(11, pausedAt: 24), Importer(12, pausedAt: 20)));

        Assert.Equal(2, report.Findings.Count);
        Assert.Equal([10, 12], report.Findings[0].ThreadIds);
        Assert.Equal([11], report.Findings[1].ThreadIds);
        Assert.StartsWith("2 \"import-worker\" threads (Thread 10 and Thread 12) are blocked in .Result at Imports.cs:12", report.Findings[0].Summary);
    }

    [Fact]
    public void Threads_with_different_names_are_listed_by_label()
    {
        var report = Analyze(Process(Importer(10) with { Name = "north" }, Importer(11) with { Name = null }));
        Assert.StartsWith("2 threads (Thread 10 (\"north\") and Thread 11) are blocked in .Result at Imports.cs:12", Assert.Single(report.Findings).Summary);
    }

    [Fact]
    public void Two_threads_at_one_line_waiting_for_two_semaphores_with_one_name_are_two_findings()
    {
        TaskInfo Pack(ulong semaphore) => new()
        {
            Address = 0x600 + semaphore, TypeName = "AsyncStateMachineBox<MyApp.Packing+<PackAsync>d__2>", Kind = TaskKind.AsyncMethod, Description = "Packing.PackAsync",
            AwaitLocation = new SourceLocation("/src/Packing.cs", 30),
            Awaiting = new TaskInfo { Address = 0x700 + semaphore, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = semaphore },
        };
        SemaphoreInfo Gate(ulong address) => new() { Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"), FieldName = "Bay.Gate", CurrentCount = 0, MaxCount = 1, AsyncWaiters = 1 };
        ThreadSnapshot Packer(int id, ulong semaphore) => Thread(id, [.. ResultWait, App("MyApp.Packing", "Run", "Packing.cs", 12)], "packer") with { BlockedOnTask = Pack(semaphore) };

        var apart = Analyze(Process(Packer(10, 0x10), Packer(11, 0x20)) with { Semaphores = [Gate(0x10), Gate(0x20)] });
        Assert.Equal(2, apart.Findings.Count);
        Assert.All(apart.Findings, f => Assert.Equal("semaphore-wait", f.Id));
        Assert.Equal(apart.Findings[0].Summary.Replace("Thread 10", "Thread 11"), apart.Findings[1].Summary);

        var together = Analyze(Process(Packer(10, 0x10), Packer(11, 0x10)) with { Semaphores = [Gate(0x10)] });
        var f = Assert.Single(together.Findings);
        Assert.Equal([10, 11], f.ThreadIds);
        Assert.StartsWith("2 \"packer\" threads (Thread 10 and Thread 11) are blocked in .Result at Packing.cs:12, waiting for Packing.PackAsync", f.Summary);
    }

    [Fact]
    public void Long_lists_of_threads_name_the_first_few_and_count_the_rest()
    {
        var threads = Enumerable.Range(1, 40).Select(id => Thread(id, [])).ToList();
        Assert.Equal("Thread 1, Thread 2, Thread 3, Thread 4, Thread 5 and 35 more", Describe.Labels(threads));
        Assert.Equal("Thread 1, Thread 2 and Thread 3", Describe.Labels(threads.Take(3)));
        Assert.Equal("Thread 1", Describe.Many(threads.Take(1).ToList()));
        Assert.Equal("40 threads (Thread 1, Thread 2, Thread 3, Thread 4, Thread 5 and 35 more)", Describe.Many(threads));
    }

    [Fact]
    public void A_list_is_never_cut_to_leave_out_a_single_thread()
    {
        var threads = Enumerable.Range(1, 7).Select(id => Thread(id, [])).ToList();
        Assert.Equal("Thread 1, Thread 2, Thread 3, Thread 4, Thread 5 and Thread 6", Describe.Labels(threads.Take(6)));
        Assert.Equal("Thread 1, Thread 2, Thread 3, Thread 4, Thread 5 and 2 more", Describe.Labels(threads));
    }

    // ---------------------------------------------------------------- HTTP

    private static TaskInfo HttpChain(string uri, ulong id, TimeSpan? timeout = null)
    {
        var reading = new TaskInfo { Address = id + 3, TypeName = "Box<System.Net.Http.HttpConnection+<InitialFillAsync>d>", Kind = TaskKind.AsyncMethod, Description = "HttpConnection.InitialFillAsync" };
        var client = new TaskInfo
        {
            Address = id + 2, TypeName = "Box<System.Net.Http.HttpClient+<SendAsync>d>", Kind = TaskKind.AsyncMethod, Description = "HttpClient.SendAsync",
            Http = new HttpCall("GET", uri, timeout ?? TimeSpan.FromSeconds(100), false), Awaiting = reading,
        };
        return new TaskInfo
        {
            Address = id + 1, TypeName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox<MyApp.Quotes+<GetAsync>d__2>",
            Kind = TaskKind.AsyncMethod, Description = "Quotes.GetAsync", AwaitLocation = new SourceLocation("/src/Quotes.cs", 40), Awaiting = client,
        };
    }

    private static ThreadSnapshot Requester(int id, string uri, int line = 70) =>
        Thread(id, [.. ResultWait, App("MyApp.Program", "Quote", "Program.cs", line), .. PoolBottom], ".NET TP Worker") with { IsThreadPoolWorker = true, BlockedOnTask = HttpChain(uri, (ulong)id * 0x100) };

    [Fact]
    public void Thirty_requests_to_one_server_at_one_line_are_one_http_finding()
    {
        var snapshot = Process([.. Enumerable.Range(10, 30).Select(id => Requester(id, $"http://shop.test:8080/quote/{id}"))]);

        var f = Assert.Single(new HttpDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("http-wait", f.Id);
        Assert.Equal(30, f.ThreadIds.Count);
        Assert.Equal("30 \".NET TP Worker\" threads (Thread 10, Thread 11, Thread 12, Thread 13, Thread 14 and 25 more) are blocked in .Result at Program.cs:70, " +
                     "waiting for `GET` requests to http://shop.test:8080 (for example `GET http://shop.test:8080/quote/10`): the requests were sent and the server hasn't answered. " +
                     "The HttpClient's timeout is 100 seconds.", f.Summary);
    }

    [Fact]
    public void Requests_to_two_servers_are_two_http_findings_and_one_request_keeps_its_sentence()
    {
        var snapshot = Process(Requester(10, "http://shop.test/quote/1"), Requester(11, "http://stock.test/quote/1"));

        var findings = new HttpDetector().Detect(snapshot, new HashSet<int>()).ToList();
        Assert.Equal(2, findings.Count);
        Assert.Equal("Thread 10 (\".NET TP Worker\") is blocked in .Result at Program.cs:70, waiting for `GET http://shop.test/quote/1`: the request was sent and the server hasn't answered. " +
                     "The HttpClient's timeout is 100 seconds.", findings[0].Summary);
    }

    [Fact]
    public void A_thread_waiting_for_an_http_response_is_reported_once()
    {
        // Not a pool thread, so no starvation finding claims it: the Info http-wait does.
        var thread = Thread(3, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = HttpChain("http://shop.test/quote/1", 0x100) };
        var f = Assert.Single(Analyze(Process(thread)).Findings);
        Assert.Equal("http-wait", f.Id);
    }

    [Fact]
    public void A_pool_with_one_allowed_connection_says_so_in_the_singular()
    {
        var queued = new TaskInfo { Address = 0x30, TypeName = "System.Threading.Tasks.TaskCompletionSource<System.Net.Http.HttpConnection>", Kind = TaskKind.Promise, Description = "a TaskCompletionSource<HttpConnection> task" };
        TaskInfo Chain(int max, int open, int? pending) => new()
        {
            Address = 0x20, TypeName = "Box<System.Net.Http.HttpConnectionPool+<SendWithVersionDetectionAndRetryAsync>d>", Kind = TaskKind.AsyncMethod,
            Description = "HttpConnectionPool.SendWithVersionDetectionAndRetryAsync", HttpPool = new HttpPoolInfo("api:80", max, open, pending), Awaiting = queued,
        };
        var full = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = Chain(1, 1, 0) });
        var f = Assert.Single(new HttpDetector().Detect(full, new HashSet<int>()));
        Assert.Equal("Thread 1 is waiting for a free HTTP connection to api:80, and the only allowed connection is in use. Start at Program.cs:5.", f.Summary);
        Assert.Contains("The pool for api:80 allows 1 connection (MaxConnectionsPerServer) and 1 is open.", f.Details);

        var connecting = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = Chain(1, 1, 1) });
        var c = Assert.Single(new HttpDetector().Detect(connecting, new HashSet<int>()));
        Assert.Equal("http-connect", c.Id);
        Assert.Contains("The pool for api:80 allows 1 connection (MaxConnectionsPerServer); its only connection is still being established.", c.Details);
    }

    // ---------------------------------------------------------------- databases

    private static readonly StackFrameInfo[] QueryWait =
        [Fx("Npgsql.Internal.NpgsqlConnector", "ReadMessage"), Fx("Npgsql.NpgsqlCommand", "ExecuteNonQuery")];

    [Fact]
    public void Database_waits_beside_a_starvation_finding_are_one_line_per_statement_and_call_site()
    {
        const string update = "UPDATE orders SET state = 'paid' WHERE id = 7";
        ThreadSnapshot Request(int id, string sql, int line) =>
            Thread(id, [.. QueryWait, App("MyApp.Orders", "Pay", "Orders.cs", line), .. PoolBottom], ".NET TP Worker") with
            {
                IsThreadPoolWorker = true,
                Database = new DatabaseActivity { Provider = "PostgreSQL", Waiting = DatabaseWaitKind.Query, CommandText = sql, SessionId = 100 + id, Connections = [new DatabaseConnection(100 + id, false)] },
            };
        var snapshot = Process([.. Enumerable.Range(10, 12).Select(id => Request(id, update, 41)), Request(30, update, 52), Request(31, "SELECT 1", 41)]);

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("14 threads are waiting for PostgreSQL to answer. One dump can't tell a slow query from one blocked inside the database; the server's view of the session(s) can.", f.Summary);
        Assert.Equal(
        [
            "12 \".NET TP Worker\" threads (Thread 10, Thread 11, Thread 12, Thread 13, Thread 14 and 7 more) are waiting in Orders.Pay (Orders.cs:41) for PostgreSQL to answer `" + update + "`.",
            "Thread 30 (\".NET TP Worker\") is waiting in Orders.Pay (Orders.cs:52) for PostgreSQL to answer `" + update + "` (session 130).",
            "Thread 31 (\".NET TP Worker\") is waiting in Orders.Pay (Orders.cs:41) for PostgreSQL to answer `SELECT 1` (session 131).",
        ], f.Details.Take(3));
        // Every session is still there to look up on the server.
        Assert.Contains("110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 130, 131", f.Details[3]);
        Assert.Equal(14, f.ThreadIds.Count);
    }

    [Fact]
    public void A_possible_database_deadlock_of_many_threads_names_the_first_few()
    {
        var snapshot = Process([.. Enumerable.Range(1, 8).Select(id => Thread(id, [.. QueryWait, App("MyApp.Ledger", "Transfer", "Ledger.cs", 20)]) with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Waiting = DatabaseWaitKind.Query, CommandText = $"UPDATE ledger SET b = b + 1 WHERE id = {id}", SessionId = id, Connections = [new DatabaseConnection(id, true)] },
        })]);
        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith("Thread 1, Thread 2, Thread 3, Thread 4, Thread 5 and 3 more are all waiting for PostgreSQL, each inside its own open transaction.", f.Summary);
    }

    [Fact]
    public void Connection_holders_stay_open_for_the_finding_that_explains_what_blocks_them()
    {
        // A pool of 3: one checkout keeps an idle transaction, two refunds wait for the row it locked, two more wait for a connection.
        StackFrameInfo[] poolWait = [Fx("System.Threading.SemaphoreSlim", "Wait"), new() { TypeName = "Npgsql.PoolingDataSource", MethodName = "Get", ModuleName = "/app/Npgsql.dll" }];
        var pool = new ConnectionPoolInfo(3, 3, 0);
        var charge = Thread(10, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Wallets", "WaitForApproval", "Wallets.cs", 49)], "charge") with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(100, true)] },
        };
        ThreadSnapshot Refund(int id) => Thread(id, [.. QueryWait, App("MyApp.Wallets", "Refund", "Wallets.cs", 56)], $"refund-{id}") with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Waiting = DatabaseWaitKind.Query, CommandText = "UPDATE wallets SET balance = balance + 5 WHERE id = 1", SessionId = 100 + id, Connections = [new DatabaseConnection(100 + id, false)] },
        };
        ThreadSnapshot Queued(int id) => Thread(id, [.. poolWait, App("MyApp.Wallets", "Refund", "Wallets.cs", 54)], $"queued-{id}") with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ConnectionPool, Pool = pool },
        };

        var report = Analyze(Process(charge, Refund(11), Refund(12), Queued(13), Queued(14)));

        Assert.Equal(["connection-pool-exhausted", "database-wait"], report.Findings.Select(f => f.Id));
        Assert.Equal(Severity.Warning, report.Findings[0].Severity);
        var blocked = report.Findings[1];
        Assert.Equal("Database query blocked by an idle transaction", blocked.Title);
        Assert.StartsWith("2 threads are waiting for PostgreSQL, while Thread 10 (\"charge\") keeps a transaction open and waits for something else in Wallets.cs:49.", blocked.Summary);
    }

    // ---------------------------------------------------------------- claiming

    [Fact]
    public void A_lock_owner_blocked_on_a_task_gets_the_finding_that_follows_its_task()
    {
        var supplier = new TaskInfo
        {
            Address = 0x600, TypeName = "AsyncStateMachineBox<MyApp.PriceList+<LoadFromSupplierAsync>d__3>", Kind = TaskKind.AsyncMethod, Description = "PriceList.LoadFromSupplierAsync",
            AwaitLocation = new SourceLocation("/src/PriceList.cs", 30), Awaiting = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task<System.Int32>", Kind = TaskKind.Promise, Description = "a TaskCompletionSource<int> task" },
        };
        var snapshot = Process(
            Thread(4, [.. ResultWait, App("MyApp.PriceList", "Rebuild", "PriceList.cs", 18)], "rebuilder") with { BlockedOnTask = supplier },
            Thread(5, [.. MonitorEnter, App("MyApp.PriceList", "Get", "PriceList.cs", 24)], "reader") with { WaitingForLock = 0x100 },
            Thread(6, [.. MonitorEnter, App("MyApp.PriceList", "Get", "PriceList.cs", 24)], "reader") with { WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "PriceList._sync", OwnerThreadId = 4, WaiterThreadIds = [5, 6] }],
        };

        var report = Analyze(snapshot);

        Assert.Equal(["lock-contention", "blocked-on-task"], report.Findings.Select(f => f.Id));
        Assert.Equal([4, 5, 6], report.Findings[0].ThreadIds);
        Assert.Equal("Thread 4 (\"rebuilder\") is blocked in .Result at PriceList.cs:18, waiting for PriceList.LoadFromSupplierAsync (paused at PriceList.cs:30), " +
                     "which awaits a TaskCompletionSource<int> task that nothing has completed yet.", report.Findings[1].Summary);
    }

    [Fact]
    public void A_lock_owner_that_is_only_sleeping_is_still_explained_by_the_contention()
    {
        var snapshot = Process(
            Thread(4, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.PriceList", "Rebuild", "PriceList.cs", 18)], "rebuilder"),
            Thread(5, [.. MonitorEnter, App("MyApp.PriceList", "Get", "PriceList.cs", 24)], "reader") with { WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "PriceList._sync", OwnerThreadId = 4, WaiterThreadIds = [5] }],
        };
        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Null(f.ExplainedThreadIds);
    }

    // ---------------------------------------------------------------- sentences

    [Fact]
    public void A_semaphore_without_a_name_is_not_named_twice()
    {
        var snapshot = Process(Thread(4, [Fx("System.Threading.SemaphoreSlim", "Wait"), App("MyApp.Worker", "Run", "Worker.cs", 17)], "worker-a") with { WaitingForSemaphore = 0x10 }) with
        {
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x10, "System.Threading.SemaphoreSlim"), CurrentCount = 0, MaxCount = 1, WaiterThreadIds = [4] }],
        };
        var f = Assert.Single(new SemaphoreWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith("Thread 4 (\"worker-a\") is waiting in Worker.Run (Worker.cs:17) for a SemaphoreSlim with 0 of 1 slots free.", f.Summary);
    }

    [Fact]
    public void Two_semaphores_in_a_deadlock_that_the_dump_cannot_tell_apart_are_not_one_name_twice()
    {
        StackFrameInfo[] wait = [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "Wait")];
        SemaphoreUse Holds(ulong semaphore, string method, int line) =>
            new() { ReleasesIt = true, Holds = [new HeldSemaphore(semaphore, method, new SourceLocation("/src/Billing.cs", line))] };
        SemaphoreInfo Sem(ulong address, int waiter) =>
            new() { Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"), FieldName = "SharedGate.slot", CurrentCount = 0, MaxCount = 1, InitialCount = 1, WaiterThreadIds = [waiter] };
        var snapshot = Process(
            Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 9)]),
            Thread(4, [.. wait, App("MyApp.Billing", "Post", "Billing.cs", 22)], "post-invoice") with { WaitingForSemaphore = 0x200, SemaphoreUse = Holds(0x100, "Billing.Post", 18) },
            Thread(5, [.. wait, App("MyApp.Billing", "Close", "Billing.cs", 34)], "close-month") with { WaitingForSemaphore = 0x100, SemaphoreUse = Holds(0x200, "Billing.Close", 30) }) with
        {
            Semaphores = [Sem(0x100, 5), Sem(0x200, 4)],
        };

        var f = Assert.Single(Analyze(snapshot).Findings, f => f.Id == "semaphore-deadlock");
        Assert.Contains("that is already taken (2 different semaphores, each SharedGate.slot).", f.Summary);
        Assert.StartsWith("Take these semaphores in one consistent order everywhere, or replace them with a single semaphore.", f.Advice);
    }

    [Fact]
    public void A_full_queue_whose_count_could_not_be_read_says_its_capacity()
    {
        var snapshot = Process(
            Thread(4, [Fx("System.Threading.SemaphoreSlim", "Wait"), App("MyApp.EventBus", "Publish", "EventBus.cs", 14)], "publisher") with
            {
                WaitingOnCollection = new CollectionWait { Collection = new ObjectRef(0x20, "System.Collections.Concurrent.BlockingCollection<System.String>"), Adding = true, Capacity = 5, FieldName = "EventBus._queue" },
            },
            Thread(5, [.. MonitorEnter, App("MyApp.EventBus", "Drain", "EventBus.cs", 30)], "consumer") with { WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "EventBus._sync", OwnerThreadId = 4, WaiterThreadIds = [5] }],
        };
        var f = Assert.Single(new BoundedQueueDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith("Thread 4 (\"publisher\") is blocked on EventBus._queue (BlockingCollection<string>), which is full (capacity 5), while holding the lock on EventBus._sync.", f.Summary);
    }

    [Fact]
    public void Lazy_values_whose_factories_need_each_other_are_not_explained_as_lock_order()
    {
        var snapshot = Process(
            Thread(5, [.. MonitorEnter, Fx("System.Lazy`1", "ExecutionAndPublication"), App("MyApp.RateTables", "Get", "RateTables.cs", 21)], "retail") with { WaitingForLock = 0x200 },
            Thread(6, [.. MonitorEnter, Fx("System.Lazy`1", "ExecutionAndPublication"), App("MyApp.RateTables", "Get", "RateTables.cs", 21)], "wholesale") with { WaitingForLock = 0x100 }) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "System.LazyHelper"), FieldName = "Lazy<RateTable>", OwnerThreadId = 5, WaiterThreadIds = [6] },
                new MonitorLock { Object = new ObjectRef(0x200, "System.LazyHelper"), FieldName = "Lazy<RateTable>", OwnerThreadId = 6, WaiterThreadIds = [5] },
            ],
        };

        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("lock-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: Lazy<T> values whose factories need each other", f.Title);
        Assert.Equal("Thread 5 (\"retail\") is running the value factory of a Lazy<RateTable> and, in RateTables.Get (RateTables.cs:21), needs the Value of another Lazy<RateTable>, " +
                     "whose factory Thread 6 (\"wholesale\") is running.", f.Details[0]);
        Assert.DoesNotContain("or the reverse", f.Advice);
        Assert.Contains("Break the cycle between the factories", f.Advice);
    }

    [Fact]
    public void Two_locks_the_dump_cannot_tell_apart_get_no_ordering_of_one_name()
    {
        var snapshot = Process(
            Thread(5, [.. MonitorEnter, App("MyApp.Ledger", "MoveTo", "Ledger.cs", 15)]) with { WaitingForLock = 0x200 },
            Thread(6, [.. MonitorEnter, App("MyApp.Ledger", "MoveTo", "Ledger.cs", 15)]) with { WaitingForLock = 0x100 }) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "MyApp.Ledger"), OwnerThreadId = 5, WaiterThreadIds = [6] },
                new MonitorLock { Object = new ObjectRef(0x200, "MyApp.Ledger"), OwnerThreadId = 6, WaiterThreadIds = [5] },
            ],
        };
        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith("These are 2 different Ledger objects, taken in a different order by each thread.", f.Advice);
    }

    [Theory]
    [InlineData("System.Object", null, "These are 2 different lock objects (each a new object()), taken in a different order by each thread.")]
    [InlineData("System.Object", "Philosopher.right", "These are 2 different locks, each Philosopher.right, taken in a different order by each thread.")]
    public void Two_plain_locks_the_dump_cannot_tell_apart_are_not_object_objects(string type, string? field, string advice)
    {
        var snapshot = Process(
            Thread(5, [.. MonitorEnter, App("MyApp.Worker", "Run", "Worker.cs", 15)]) with { WaitingForLock = 0x200 },
            Thread(6, [.. MonitorEnter, App("MyApp.Worker", "Run", "Worker.cs", 15)]) with { WaitingForLock = 0x100 }) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, type), FieldName = field, OwnerThreadId = 5, WaiterThreadIds = [6] },
                new MonitorLock { Object = new ObjectRef(0x200, type), FieldName = field, OwnerThreadId = 6, WaiterThreadIds = [5] },
            ],
        };
        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith(advice, f.Advice);
    }

    [Fact]
    public void Two_locks_told_apart_by_their_owners_ids_get_the_advice_for_objects_of_one_type()
    {
        var snapshot = Process(
            Thread(5, [.. MonitorEnter, App("MyApp.Ledger", "MoveTo", "Ledger.cs", 15)]) with { WaitingForLock = 0x200 },
            Thread(6, [.. MonitorEnter, App("MyApp.Ledger", "MoveTo", "Ledger.cs", 15)]) with { WaitingForLock = 0x100 }) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "MyApp.Ledger"), FieldName = "Ledger (Id = \"cash\")", OwnerThreadId = 5, WaiterThreadIds = [6] },
                new MonitorLock { Object = new ObjectRef(0x200, "MyApp.Ledger"), FieldName = "Ledger (Id = \"bank\")", OwnerThreadId = 6, WaiterThreadIds = [5] },
            ],
        };
        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains("holds the lock on Ledger (Id = \"cash\") and is waiting in Ledger.MoveTo (Ledger.cs:15) for the lock on Ledger (Id = \"bank\")", f.Details[0]);
        Assert.StartsWith("These are all Ledger objects, taken in a different order by each thread", f.Advice);
    }

    [Fact]
    public void A_send_whose_caller_was_optimized_away_is_not_called_from_itself()
    {
        var ui = new ObjectRef(0x700, "MyApp.UiContext");
        var export = new TaskInfo { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "Task.Run(ExportWindow.Export)" };
        var snapshot = Process(
            Thread(1, [.. ResultWait, App("MyApp.ExportWindow", "OnExportClicked", "Export.cs", 12)]) with { SynchronizationContext = ui, BlockedOnTask = export },
            Thread(5, [Fx("System.Threading.Monitor", "Wait"), App("MyApp.UiContext", "Send", "UiContext.cs", 9), Fx("System.Threading.Tasks.Task", "InnerInvoke")], ".NET TP Worker")
                with { RunningTask = 0x500, SendingTo = ui });

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains(f.Details, d => d.StartsWith("Thread 5 (\".NET TP Worker\") is blocked in UiContext.Send (UiContext.cs:9), waiting for Thread 1 to run the callback"));
        Assert.DoesNotContain(f.Details, d => d.Contains("called from"));
    }

    private static ProcessSnapshot OnTimerThread(params StackFrameInfo[] timerFrames) =>
        Process(
            Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Settlement", "Run", "Settlement.cs", 12)]),
            Thread(6, [.. MonitorEnter, App("MyApp.Settlement+<CloseDayAsync>d__2", "MoveNext", "Settlement.cs", 20), .. timerFrames, .. PoolBottom], ".NET TP Worker")
                with { IsThreadPoolWorker = true, WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Settlement.Books", OwnerThreadId = 1, WaiterThreadIds = [6] }],
        };

    [Fact]
    public void The_rest_of_an_async_method_after_Task_Delay_is_not_a_timer_callback()
    {
        var snapshot = OnTimerThread(Fx("System.Threading.Tasks.Task", "TrySetResult"), Fx("System.Threading.Tasks.Task+DelayPromise", "CompleteTimedOut"), Fx("System.Threading.TimerQueueTimer", "Fire"));
        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains("Thread 6 (\".NET TP Worker\") is running the continuation of a Task.Delay (Settlement.CloseDayAsync (Settlement.cs:20)): " +
                        "the rest of an async method after await Task.Delay(...) runs on the thread that fires the delay's timer.", f.Details);
        Assert.DoesNotContain(f.Details, d => d.Contains("timer callback"));
    }

    [Fact]
    public void A_timer_callback_of_the_app_is_still_called_one()
    {
        var snapshot = OnTimerThread(Fx("System.Threading.TimerQueueTimer", "CallCallback"), Fx("System.Threading.TimerQueueTimer", "Fire"));
        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains("Thread 6 (\".NET TP Worker\") is running a timer callback (Settlement.CloseDayAsync (Settlement.cs:20)).", f.Details);
    }

    [Fact]
    public void A_thread_held_by_Parallel_ForEach_is_not_told_to_await_it()
    {
        var replica = new TaskInfo { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "a Parallel.For/ForEach worker" };
        var snapshot = Process(Thread(1, [.. ResultWait, Fx("System.Threading.Tasks.Parallel", "ForEach"), App("MyApp.Repricer", "Run", "Repricer.cs", 11)])
            with { BlockedOnTask = replica, Frames = [.. ResultWait, Fx("System.Threading.Tasks.Parallel", "ForEach"), App("MyApp.Repricer", "Run", "Repricer.cs", 11) with { BlockingCall = "Parallel.ForEach" }] });
        var f = Assert.Single(new BlockedOnTaskDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread 1 is blocked in Parallel.ForEach at Repricer.cs:11, waiting for a Parallel.For/ForEach worker, which is still running.", f.Summary);
        Assert.DoesNotContain("Prefer await", f.Advice);
        Assert.StartsWith("Parallel.For/ForEach holds the thread that calls it until every iteration has finished, so there is nothing to await here", f.Advice);
        Assert.Contains("Parallel.ForEachAsync", f.Advice);
    }

    [Fact]
    public void WaitForExit_waiting_for_the_end_of_redirected_output_says_so()
    {
        var pipe = new TaskInfo { Address = 0x520, TypeName = "AsyncStateMachineBox<System.IO.Pipes.PipeStream+<ReadAsyncCore>d__1>", Kind = TaskKind.AsyncMethod, Description = "PipeStream.ReadAsyncCore" };
        var reader = new TaskInfo { Address = 0x510, TypeName = "AsyncStateMachineBox<System.Diagnostics.AsyncStreamReader+<ReadBufferAsync>d__16>", Kind = TaskKind.AsyncMethod, Description = "AsyncStreamReader.ReadBufferAsync", Awaiting = pipe };
        StackFrameInfo[] frames =
        [
            .. ResultWait, Fx("System.Runtime.CompilerServices.TaskAwaiter", "HandleNonSuccessAndDebuggerNotification"),
            Fx("System.Diagnostics.Process", "WaitForExitCore"), Fx("System.Diagnostics.Process", "WaitForExit"),
            App("MyApp.Shell", "Run", "Shell.cs", 24) with { BlockingCall = "Process.WaitForExit" },
        ];
        var snapshot = Process(Thread(1, frames) with { BlockedOnTask = reader, WaitingForProcess = new ProcessWait(4242, "sh -c sleep 120 &", []) });

        var f = Assert.Single(Analyze(snapshot).Findings);
        Assert.Equal("blocked-on-task", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Waiting for the end of a child process's output", f.Title);
        Assert.Equal("Thread 1 is blocked in Process.WaitForExit() at Shell.cs:24 for `sh -c sleep 120 &` (pid 4242). The child process has exited, and WaitForExit() now waits for the end of its " +
                     "redirected output, which BeginOutputReadLine or BeginErrorReadLine reads in the background. That output is still open.", f.Summary);
        Assert.DoesNotContain("Prefer await", f.Advice);
        Assert.Contains("WaitForExit(milliseconds)", f.Advice);
    }

    [Fact]
    public void A_followed_WhenAny_says_how_many_tasks_it_has()
    {
        var promise = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.Promise, Description = "a TaskCompletionSource task", FirstOf = 3 };
        var whenAny = new TaskInfo { Address = 0x800, TypeName = "System.Threading.Tasks.Task+WhenAnyPromise<System.Threading.Tasks.Task>", Kind = TaskKind.Wrapper, Description = "Task.WhenAny(...)", Awaiting = promise };
        var waitCall = new[] { Fx("System.Threading.Tasks.Task", "InternalWaitCore"), Fx("System.Threading.Tasks.Task", "Wait") };

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(Process(Thread(1, [.. waitCall, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = whenAny }), new HashSet<int>()));
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for Task.WhenAny(...), which waits for the first of its 3 tasks: a TaskCompletionSource task that nothing has completed yet.", f.Summary);

        var waitAny = Assert.Single(new BlockedOnTaskDetector().Detect(Process(Thread(1, [.. waitCall, App("MyApp.Program", "Main", "Program.cs", 5) with { BlockingCall = "Task.WaitAny(...)" }]) with { BlockedOnTask = promise }), new HashSet<int>()));
        Assert.EndsWith("waiting for the first of its 3 tasks: a TaskCompletionSource task that nothing has completed yet.", waitAny.Summary);
    }

    [Fact]
    public void Unknown_SqlClient_pool_internals_only_cost_the_proof_that_the_pool_is_full()
    {
        Assert.Equal("Microsoft.Data.SqlClient 5.2.3: connection pool internals not recognised, so the pool's size and use are unknown: " +
                     "threads waiting for a connection are still reported, but not whether the pool is full.",
            SnapshotBuilder.UnrecognisedWarning("Microsoft.Data.SqlClient 5.2.3", ["connection pool internals"]));
    }

    [Fact]
    public void Two_hundred_threads_in_one_wait_give_a_short_report()
    {
        var report = Analyze(Process([.. Enumerable.Range(10, 200).Select(id => Importer(id))]));
        var text = new StringWriter();
        TextReporter.Write(report, text);
        var findings = text.ToString().Split('\n').TakeWhile(l => !l.StartsWith("Stacks of", StringComparison.Ordinal)).ToList();
        Assert.True(findings.Count < 20, string.Join("\n", findings));
        Assert.Contains("200 \"import-worker\" threads (Thread 10, Thread 11, Thread 12, Thread 13, Thread 14 and 195 more)", string.Join(" ", findings.Select(l => l.Trim())));
    }
}
