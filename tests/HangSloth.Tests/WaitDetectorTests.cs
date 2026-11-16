using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

public class WaitCycleDetectorTests
{
    private static readonly StackFrameInfo[] JoinWait = [Fx("System.Threading.Thread", "Join")];
    private static readonly StackFrameInfo[] ClassInitWait =
        [Fx("System.Runtime.CompilerServices.InitHelpers", "InitClassSlow"), Fx("System.Runtime.CompilerServices.StaticsHelpers", "GetGCStaticBaseSlow")];

    private static MonitorLock Lock(ulong address, string name, int owner, params int[] waiters) =>
        new() { Object = new ObjectRef(address, "System.Object"), FieldName = name, OwnerThreadId = owner, WaiterThreadIds = waiters };

    [Fact]
    public void Lock_holder_blocked_on_a_task_that_needs_the_lock()
    {
        var task = new TaskInfo { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "Task.Run(ReportService.CollectRows)" };
        var snapshot = Process(
            Thread(1, [.. ResultWait, App("MyApp.ReportService", "Generate", "Reports.cs", 14)]) with { BlockedOnTask = task },
            Thread(5, [.. MonitorEnter, App("MyApp.ReportService", "CollectRows", "Reports.cs", 20)], ".NET TP Worker") with { WaitingForLock = 0x100, RunningTask = 0x500 }) with
        {
            Locks = [Lock(0x100, "ReportService._sync", 1, 5)],
        };

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("wait-cycle", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread 1 and Thread 5 (\".NET TP Worker\") are waiting for each other, so neither can ever continue. Start at Reports.cs:14.", f.Summary);
        Assert.Equal("Thread 1 holds the lock on ReportService._sync and is blocked in .Result at ReportService.Generate (Reports.cs:14), waiting for Task.Run(ReportService.CollectRows), which is running on Thread 5 (\".NET TP Worker\").", f.Details[0]);
        Assert.Contains("Don't block on a task while holding a lock", f.Advice);
    }

    [Fact]
    public void Join_on_a_thread_that_needs_the_lock()
    {
        var snapshot = Process(
            Thread(1, [.. JoinWait, App("MyApp.Worker", "Stop", "Worker.cs", 22)]) with { JoiningThreadId = 4 },
            Thread(4, [.. MonitorEnter, App("MyApp.Worker", "OnStopped", "Worker.cs", 34)], "email-worker") with { WaitingForLock = 0x100 }) with
        {
            Locks = [Lock(0x100, "Worker._state", 1, 4)],
        };

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains(f.Details, d => d == "Thread 1 holds the lock on Worker._state and is waiting in Worker.Stop (Worker.cs:22) for Thread 4 (\"email-worker\") to finish (Thread.Join).");
        Assert.Contains("Don't Join a thread while holding a lock", f.Advice);
    }

    [Fact]
    public void Static_constructor_waiting_for_a_thread_that_needs_the_type()
    {
        var task = new TaskInfo { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "Task.Run(PriceTable.Load (lambda))" };
        var snapshot = Process(
            Thread(1,
            [
                .. ResultWait, App("MyApp.PriceTable", "Load", "PriceTable.cs", 23), App("MyApp.PriceTable", ".cctor", "PriceTable.cs", 13),
                .. ClassInitWait, App("MyApp.Program", "Main", "Program.cs", 3),
            ]) with { BlockedOnTask = task },
            Thread(5, [.. ClassInitWait, App("MyApp.PriceTable+<>c", "<Load>b__1_0", "PriceTable.cs", 23)]) with { RunningTask = 0x500 });

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("type-initializer-deadlock", f.Id);
        Assert.StartsWith("Thread 1 is running the static constructor of PriceTable and waits for Thread 5, but Thread 5 uses PriceTable", f.Summary);
        Assert.Equal("Thread 5 needs PriceTable in PriceTable.Load (lambda) (PriceTable.cs:23), so it waits for its static constructor, which Thread 1 is running.", f.Details[1]);
    }

    [Fact]
    public void Pure_lock_cycles_are_left_to_the_lock_deadlock_detector()
    {
        var snapshot = Process(
            Thread(1, [.. MonitorEnter, App("A", "M", "A.cs", 1)]) with { WaitingForLock = 0x200 },
            Thread(2, [.. MonitorEnter, App("B", "M", "B.cs", 1)]) with { WaitingForLock = 0x100 }) with
        {
            Locks = [Lock(0x100, "A._x", 1, 2), Lock(0x200, "B._y", 2, 1)],
        };
        Assert.Empty(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
    }

    [Fact]
    public void A_task_that_is_queued_but_not_running_is_not_a_cycle()
    {
        var task = new TaskInfo { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "Task.Run(X)" };
        var snapshot = Process(Thread(1, [.. ResultWait, App("A", "M", "A.cs", 1)]) with { BlockedOnTask = task });
        Assert.Empty(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
    }
}

public class ReaderWriterAndSameFieldLockTests
{
    [Fact]
    public void ReaderWriterLockSlim_writer_takes_part_in_a_lock_deadlock()
    {
        var snapshot = Process(
            Thread(4, [Fx("System.Threading.ReaderWriterLockSlim", "TryEnterReadLockCore"), App("MyApp.ConfigStore", "Get", "ConfigStore.cs", 14)]) with { WaitingForLock = 0x200 },
            Thread(5, [.. MonitorEnter, App("MyApp.AuditLog", "Record", "ConfigStore.cs", 40)]) with { WaitingForLock = 0x100 }) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "AuditLog._sync", OwnerThreadId = 4, WaiterThreadIds = [5] },
                new MonitorLock { Object = new ObjectRef(0x200, "System.Threading.ReaderWriterLockSlim"), Kind = LockKind.ReaderWriter, FieldName = "ConfigStore._rw", OwnerThreadId = 5, WaiterThreadIds = [4] },
            ],
        };

        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains(f.Details, d => d.Contains("is waiting in ConfigStore.Get (ConfigStore.cs:14) for ConfigStore._rw (a ReaderWriterLockSlim), which Thread 5 holds."));
        Assert.Contains(f.Details, d => d.StartsWith("Thread 5 holds the write lock on ConfigStore._rw and"));
    }

    [Fact]
    public void Same_field_on_two_objects_gets_lock_ordering_advice()
    {
        var snapshot = Process(
            Thread(1, [.. MonitorEnter, App("MyApp.Bank", "Transfer", "Bank.cs", 9)]) with { WaitingForLock = 0x200 },
            Thread(2, [.. MonitorEnter, App("MyApp.Bank", "Transfer", "Bank.cs", 9)]) with { WaitingForLock = 0x100 }) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Account.Sync (Id = \"alice\")", OwnerThreadId = 1, WaiterThreadIds = [2] },
                new MonitorLock { Object = new ObjectRef(0x200, "System.Object"), FieldName = "Account.Sync (Id = \"bob\")", OwnerThreadId = 2, WaiterThreadIds = [1] },
            ],
        };

        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith("These are all Account.Sync, on different objects", f.Advice);
    }

    [Fact]
    public void ReaderWriterLockSlim_held_by_readers_is_not_called_orphaned()
    {
        var snapshot = Process(
            Thread(4, [Fx("System.Threading.ReaderWriterLockSlim", "TryEnterWriteLockCore"), App("MyApp.Config", "Set", "Config.cs", 7)]) with { WaitingForLock = 0x200 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x200, "System.Threading.ReaderWriterLockSlim"), Kind = LockKind.ReaderWriter, FieldName = "Config._rw", ReaderCount = 2, WaiterThreadIds = [4] }],
        };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread 4 is waiting for Config._rw (a ReaderWriterLockSlim), which 2 threads hold for reading. Readers aren't recorded per thread, so look for code that entered the read lock and is slow or blocked before ExitReadLock.", f.Summary);
    }
}

public class SemaphoreDetectorTests
{
    private static readonly StackFrameInfo[] SemaphoreWait =
        [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "Wait")];

    private static SemaphoreInfo Sem(ulong address, string name, params int[] waiters) =>
        new() { Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"), FieldName = name, CurrentCount = 0, MaxCount = 1, WaiterThreadIds = waiters };

    /// The waiting code took this semaphore earlier and hasn't released it: what SnapshotBuilder reads from the IL.
    private static SemaphoreUse Holding(ulong semaphore) => new() { ReleasesIt = true, Holds = [new HeldSemaphore(semaphore, "M", null)] };

    [Fact]
    public void Two_taken_mutex_semaphores_with_crossed_waiters()
    {
        var snapshot = Process(
            Thread(4, [.. SemaphoreWait, App("MyApp.Shipping", "PrintThenBook", "Shipping.cs", 17)]) with { WaitingForSemaphore = 0x200, SemaphoreUse = Holding(0x100) },
            Thread(5, [.. SemaphoreWait, App("MyApp.Shipping", "BookThenPrint", "Shipping.cs", 29)]) with { WaitingForSemaphore = 0x100, SemaphoreUse = Holding(0x200) }) with
        {
            Semaphores = [Sem(0x100, "Shipping.Printer", 5), Sem(0x200, "Shipping.Carrier", 4)],
        };

        var f = Assert.Single(new SemaphoreDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("semaphore-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(f.Details, d => d == "Thread 4 is waiting in Shipping.PrintThenBook (Shipping.cs:17) for Shipping.Carrier (0 of 1 free).");
    }

    [Fact]
    public void Is_only_a_warning_while_other_app_code_is_still_running()
    {
        var snapshot = Process(
            Thread(4, [.. SemaphoreWait, App("A", "M", "A.cs", 1)]) with { WaitingForSemaphore = 0x200, SemaphoreUse = Holding(0x100) },
            Thread(5, [.. SemaphoreWait, App("B", "M", "B.cs", 1)]) with { WaitingForSemaphore = 0x100, SemaphoreUse = Holding(0x200) },
            Thread(6, [App("C", "Compute", "C.cs", 1)])) with
        {
            Semaphores = [Sem(0x100, "S.A", 5), Sem(0x200, "S.B", 4)],
        };
        Assert.Equal(Severity.Warning, Assert.Single(new SemaphoreDeadlockDetector().Detect(snapshot, new HashSet<int>())).Severity);
    }

    [Fact]
    public void Async_chain_waiting_for_a_semaphore_it_already_holds()
    {
        const ulong cache = 0x900;
        var node = new TaskInfo { Address = 0x300, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = 0x100 };
        var refresh = new TaskInfo { Address = 0x200, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "PriceCache.RefreshAsync", ThisAddress = cache, Awaiting = node, AwaitLocation = new SourceLocation("/src/PriceCache.cs", 30) };
        var get = new TaskInfo { Address = 0x100, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "PriceCache.GetAsync", ThisAddress = cache, Awaiting = refresh, AwaitLocation = new SourceLocation("/src/PriceCache.cs", 18) };
        var snapshot = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = get, SemaphoreUse = Holding(0x100) }) with
        {
            Semaphores = [Sem(0x100, "PriceCache._gate") with { ContainerAddress = cache, AsyncWaiters = 1 }],
        };

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Result at Program.cs:5, waiting for PriceCache.GetAsync (paused at PriceCache.cs:18), which awaits PriceCache.RefreshAsync (paused at PriceCache.cs:30), which is waiting to enter PriceCache._gate (SemaphoreSlim.WaitAsync(), 0 of 1 slots free).", f.Summary);
        Assert.StartsWith("PriceCache.GetAsync, earlier in this same async chain, runs on the same object that owns PriceCache._gate.", f.Details[0]);
    }
}

public class DatabaseDetectorTests
{
    private static readonly StackFrameInfo[] QueryWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Net.Sockets.Socket", "Receive"),
        Lib("Npgsql.Internal.NpgsqlReadBuffer", "Ensure"),
        Lib("Npgsql.NpgsqlCommand", "ExecuteNonQuery"),
    ];

    private static readonly StackFrameInfo[] PoolWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Threading.SemaphoreSlim", "Wait"),
        Lib("Npgsql.PoolingDataSource", "Get"),
        Lib("Npgsql.NpgsqlConnection", "Open"),
    ];

    private static StackFrameInfo Lib(string type, string method) => new() { TypeName = type, MethodName = method, ModuleName = "/app/Npgsql.dll" };

    private static DatabaseActivity Query(string sql, int session, bool inTx = false) => new()
    {
        Provider = "PostgreSQL", Waiting = DatabaseWaitKind.Query, CommandText = sql, SessionId = session,
        Connections = [new DatabaseConnection(session, inTx)],
    };

    private static DatabaseActivity OpenTx(int session) => new() { Provider = "PostgreSQL", Connections = [new DatabaseConnection(session, true)] };

    [Fact]
    public void Driver_frames_are_library_code_and_classify_as_database_waits()
    {
        Assert.False(FrameClassifier.IsUserFrame(Lib("Npgsql.NpgsqlCommand", "ExecuteNonQuery")));
        Assert.Equal(BlockingKind.Database, FrameClassifier.Classify([.. QueryWait, App("MyApp.Repo", "Save", "Repo.cs", 3)]));
        Assert.Equal(BlockingKind.ConnectionPool, FrameClassifier.Classify([.. PoolWait, App("MyApp.Repo", "Save", "Repo.cs", 3)]));
    }

    [Fact]
    public void App_lock_held_while_waiting_on_a_row_locked_by_the_lock_waiter()
    {
        var snapshot = Process(
            Thread(11, [.. QueryWait, App("MyApp.Cache", "Refresh", "Cache.cs", 22)], "cache-refresh") with { Database = Query("UPDATE products SET views = views + 1", 200) },
            Thread(10, [.. MonitorEnter, App("MyApp.Cache", "Invalidate", "Cache.cs", 29)], "price-change") with { WaitingForLock = 0x100, Database = OpenTx(100) }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Cache._lock", OwnerThreadId = 11, WaiterThreadIds = [10] }],
        };

        var f = Assert.Single(new DatabaseDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("database-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread 11 (\"cache-refresh\") holds the lock on Cache._lock and is waiting in Cache.Refresh (Cache.cs:22) for PostgreSQL to answer `UPDATE products SET views = views + 1` (session 200).", f.Details[0]);
        Assert.Contains(f.Details, d => d.Contains("WHERE pid IN (200, 100)"));
    }

    [Fact]
    public void Query_blocked_by_a_transaction_left_open()
    {
        var snapshot = Process(
            Thread(11, [.. QueryWait, App("MyApp.Refunds", "Refund", "Refunds.cs", 22)], "refund") with { Database = Query("UPDATE wallets SET balance = balance + 5", 200) },
            Thread(10, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Refunds", "Charge", "Refunds.cs", 15)], "charge") with { Database = OpenTx(100) });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Database query blocked by an idle transaction", f.Title);
        Assert.Contains(f.Details, d => d.StartsWith("Thread 10 (\"charge\") has an open transaction (session 100) but isn't talking to the database: it is sleeping (Thread.Sleep) in Refunds.Charge (Refunds.cs:15)."));
    }

    [Fact]
    public void Two_waiting_transactions_are_a_possible_database_deadlock()
    {
        var snapshot = Process(
            Thread(10, [.. QueryWait, App("MyApp.Ledger", "Transfer", "Ledger.cs", 20)]) with { Database = Query("UPDATE ledger SET b = b + 1 WHERE id = 2", 100, inTx: true) },
            Thread(11, [.. QueryWait, App("MyApp.Ledger", "Transfer", "Ledger.cs", 20)]) with { Database = Query("UPDATE ledger SET b = b + 1 WHERE id = 1", 200, inTx: true) });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Possible database deadlock", f.Title);
        Assert.StartsWith("Thread 10 and Thread 11 are both waiting for PostgreSQL, each inside its own open transaction.", f.Summary);
    }

    [Fact]
    public void A_lone_query_is_only_informational()
    {
        var snapshot = Process(Thread(11, [.. QueryWait, App("MyApp.Report", "Run", "Report.cs", 9)]) with { Database = Query("SELECT pg_sleep(60)", 200) });
        Assert.Equal(Severity.Info, Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>())).Severity);
    }

    [Fact]
    public void Nested_connections_exhaust_the_pool()
    {
        var pool = new ConnectionPoolInfo(2, 2, 0);
        ThreadSnapshot Request(int id, bool holding) =>
            Thread(id, [.. PoolWait, App("MyApp.Db", "Open", "Db.cs", 26), App(holding ? "MyApp.Audit" : "MyApp.Checkout", holding ? "Write" : "Handle", "Checkout.cs", holding ? 26 : 13)]) with
            {
                Database = new DatabaseActivity
                {
                    Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ConnectionPool, Pool = pool,
                    Connections = holding ? [new DatabaseConnection(100 + id, false)] : [],
                },
            };
        var snapshot = Process(Request(1, true), Request(2, true), Request(3, false));

        var f = Assert.Single(new ConnectionPoolDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("connection-pool-exhausted", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.StartsWith("Connection pool deadlock: 3 threads are waiting for a free PostgreSQL connection and the pool is full (2 of 2 connections in use, 0 idle).", f.Summary);
        Assert.Contains(f.Details, d => d == "Waiting for a connection in Db.Open (Db.cs:26), called from Audit.Write (Checkout.cs:26) (2 threads).");
    }

    [Fact]
    public void Pool_waits_without_nested_connections_are_a_warning()
    {
        var snapshot = Process(
            Thread(1, [.. PoolWait, App("MyApp.Db", "Open", "Db.cs", 26)]) with
            {
                Database = new DatabaseActivity { Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ConnectionPool, Pool = new ConnectionPoolInfo(1, 1, 0) },
            },
            Thread(2, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Job", "Run", "Job.cs", 4)]) with { Database = OpenTx(100) });

        var f = Assert.Single(new ConnectionPoolDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains(f.Details, d => d == "Thread 2 holds a connection and is sleeping (Thread.Sleep) in Job.Run (Job.cs:4).");
    }
}
