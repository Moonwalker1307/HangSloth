using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

public class SendAndParallelTests
{
    [Fact]
    public void Ui_thread_blocked_on_work_that_Sends_back_to_it()
    {
        var ui = new ObjectRef(0x700, "MyApp.UiContext");
        var export = new TaskInfo { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "Task.Run(ExportWindow.Export)" };
        var snapshot = Process(
            Thread(1, [.. ResultWait, App("MyApp.ExportWindow", "OnExportClicked", "Export.cs", 12)]) with { SynchronizationContext = ui, BlockedOnTask = export },
            Thread(5, [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.ManualResetEventSlim", "Wait"),
                       App("MyApp.UiContext", "Send", "UiContext.cs", 9), App("MyApp.ExportWindow", "Export", "Export.cs", 20)], ".NET TP Worker")
                with { RunningTask = 0x500, SendingTo = ui });

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("wait-cycle", f.Id);
        Assert.Equal("Deadlock: Thread 1 waits for work that is waiting for it", f.Title);
        Assert.Contains(f.Details, d => d.StartsWith("Thread 5 (\".NET TP Worker\") is blocked in UiContext.Send, called from ExportWindow.Export (Export.cs:20), waiting for Thread 1 to run the callback"));
        Assert.Contains("use Post/BeginInvoke", f.Advice);
    }

    [Fact]
    public void Send_to_a_context_no_thread_owns_is_not_an_edge()
    {
        var snapshot = Process(
            Thread(5, [Fx("System.Threading.Monitor", "Wait"), App("MyApp.UiContext", "Send", "UiContext.cs", 9)]) with { SendingTo = new ObjectRef(0x700, "MyApp.UiContext") });
        Assert.Empty(WaitGraph.Build(snapshot));
    }

    [Fact]
    public void Parallel_loop_under_a_lock_its_body_needs()
    {
        var replica = new TaskInfo { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "a Parallel.For/ForEach worker" };
        var snapshot = Process(
            Thread(1, [.. ResultWait, Fx("System.Threading.Tasks.Parallel", "ForEach"), App("MyApp.Thumbnails", "RebuildAll", "Thumbnails.cs", 16)]) with { BlockedOnTask = replica },
            Thread(5, [.. MonitorEnter, App("MyApp.Thumbnails", "<RebuildAll>b__2_0", "Thumbnails.cs", 19)]) with { WaitingForLock = 0x100, RunningTask = 0x500 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Thumbnails._cacheLock", OwnerThreadId = 1, WaiterThreadIds = [5] }],
        };

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Deadlock: Parallel loop body needs a lock its caller holds", f.Title);
        Assert.Contains("runs the loop body on other threads", f.Advice);
    }
}

public class InlineContinuationTests
{
    private static readonly StackFrameInfo[] ContinuationInsideSetResult =
    [
        .. MonitorEnter,
        App("MyApp.Pipeline", "ShipWhenPaidAsync", "Pipeline.cs", 27),
        Fx("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox", "MoveNext"),
        Fx("System.Threading.Tasks.Task", "RunContinuations"),
        Fx("System.Threading.Tasks.Task`1", "TrySetResult"),
        Fx("System.Threading.Tasks.TaskCompletionSource`1", "SetResult"),
        App("MyApp.Pipeline", "MarkPaid", "Pipeline.cs", 20),
    ];

    [Fact]
    public void Recognises_a_continuation_running_inside_SetResult()
    {
        var (continuation, completer) = FrameClassifier.InlineContinuation(ContinuationInsideSetResult)!.Value;
        Assert.Equal("ShipWhenPaidAsync", continuation.MethodName);
        Assert.Equal("MarkPaid", completer.MethodName);
        Assert.Null(FrameClassifier.InlineContinuation([.. MonitorEnter, App("MyApp.A", "B", "A.cs", 1), App("MyApp.A", "C", "A.cs", 2)]));
    }

    [Fact]
    public void Lock_deadlock_explains_the_inline_continuation()
    {
        var snapshot = Process(
            Thread(4, [.. MonitorEnter, App("MyApp.Pipeline", "Report", "Pipeline.cs", 39)]) with { WaitingForLock = 0x100 },
            Thread(5, ContinuationInsideSetResult) with { WaitingForLock = 0x200 }) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Pipeline._pipelineLock", OwnerThreadId = 5, WaiterThreadIds = [4] },
                new MonitorLock { Object = new ObjectRef(0x200, "System.Object"), FieldName = "Pipeline.ShippingLock", OwnerThreadId = 4, WaiterThreadIds = [5] },
            ],
        };

        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains(f.Details, d => d.StartsWith("Thread 5 is running the rest of Pipeline.ShipWhenPaidAsync inline: Pipeline.MarkPaid (Pipeline.cs:20) completed the task"));
        Assert.Contains("RunContinuationsAsynchronously", f.Advice);
    }
}

public class BoundedQueueAndMonitorWaitTests
{
    private static readonly StackFrameInfo[] AddWait =
    [
        Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"),
        Fx("System.Collections.Concurrent.BlockingCollection`1", "TryAddWithNoTimeValidation"), Fx("System.Collections.Concurrent.BlockingCollection`1", "Add"),
    ];

    private static ProcessSnapshot Bus(string consumerType)
    {
        var queue = new CollectionWait { Collection = new ObjectRef(0x300, "System.Collections.Concurrent.BlockingCollection<System.String>"), Adding = true, Capacity = 2, Count = 2, FieldName = "EventBus._queue" };
        return Process(
            Thread(5, [.. AddWait, App("MyApp.EventBus", "Publish", "EventBus.cs", 16)], "publisher") with { WaitingOnCollection = queue },
            Thread(4, [.. MonitorEnter, App(consumerType, "Consume", "EventBus.cs", 26)], "consumer") with { WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "EventBus._subscribersLock", OwnerThreadId = 5, WaiterThreadIds = [4] }],
        };
    }

    [Fact]
    public void Producer_holding_a_lock_on_a_full_queue_the_consumer_needs()
    {
        var f = Assert.Single(new BoundedQueueDeadlockDetector().Detect(Bus("MyApp.EventBus"), new HashSet<int>()));
        Assert.Equal("bounded-queue-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.StartsWith("Thread 5 (\"publisher\") is blocked on EventBus._queue (BlockingCollection<string>), which is full (2 of 2), while holding the lock on EventBus._subscribersLock.", f.Summary);
        Assert.Equal([5, 4], f.ThreadIds);
    }

    [Fact]
    public void Lock_waiter_from_unrelated_code_is_only_a_warning()
    {
        var f = Assert.Single(new BoundedQueueDeadlockDetector().Detect(Bus("MyApp.Other"), new HashSet<int>()));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains(f.Details, d => d.StartsWith("If Thread 4 (\"consumer\") is what takes items out of EventBus._queue"));
    }

    [Fact]
    public void A_queue_with_room_is_not_reported()
    {
        var snapshot = Bus("MyApp.EventBus");
        snapshot = snapshot with { Threads = [snapshot.Threads[0] with { WaitingOnCollection = snapshot.Threads[0].WaitingOnCollection! with { Count = 1 } }, snapshot.Threads[1]] };
        Assert.Empty(new BoundedQueueDeadlockDetector().Detect(snapshot, new HashSet<int>()));
    }

    private static readonly StackFrameInfo[] DirectWait = [Fx("System.Threading.Monitor", "Wait")];

    [Fact]
    public void Lost_pulse_with_work_queued_and_a_joiner()
    {
        var wait = new PulseWait { Object = new ObjectRef(0x100, "System.Object"), FieldName = "WorkQueue._sync", PendingWork = ["WorkQueue._items (1 item)"] };
        var snapshot = Process(
            Thread(1, [Fx("System.Threading.Thread", "Join"), App("MyApp.Program", "Main", "Program.cs", 9)]) with { JoiningThreadId = 4 },
            Thread(4, [.. DirectWait, App("MyApp.WorkQueue", "Dequeue", "WorkQueue.cs", 25)], "queue-worker") with { WaitingForPulse = wait });

        var f = Assert.Single(new MonitorWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("monitor-wait", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.StartsWith("Thread 4 (\"queue-worker\") is waiting in Monitor.Wait on WorkQueue._sync although WorkQueue._items (1 item) isn't empty", f.Summary);
        Assert.Contains(f.Details, d => d == "Thread 1 is waiting for Thread 4 (\"queue-worker\") (Thread.Join) in Program.Main (Program.cs:9).");
        Assert.Equal([4, 1], f.ThreadIds);
    }

    [Fact]
    public void An_idle_consumer_in_Monitor_Wait_is_normal()
    {
        var wait = new PulseWait { Object = new ObjectRef(0x100, "System.Object"), FieldName = "WorkQueue._sync" };
        var snapshot = Process(Thread(4, [.. DirectWait, App("MyApp.WorkQueue", "Dequeue", "WorkQueue.cs", 25)]) with { WaitingForPulse = wait });
        Assert.Empty(new MonitorWaitDetector().Detect(snapshot, new HashSet<int>()));
    }
}

public class MoreDatabaseTests
{
    private static readonly StackFrameInfo[] QueryWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        new() { TypeName = "Npgsql.NpgsqlCommand", MethodName = "ExecuteNonQuery", ModuleName = "/app/Npgsql.dll" },
    ];

    private static DatabaseActivity Query(string sql, int session) => new()
    {
        Provider = "PostgreSQL", Waiting = DatabaseWaitKind.Query, CommandText = sql, SessionId = session, Connections = [new DatabaseConnection(session, false)],
    };

    [Fact]
    public void Advisory_lock_wait_names_the_idle_connection_kept_in_a_field()
    {
        var snapshot = Process(
            Thread(11, [.. QueryWait, App("MyApp.NightlyJob", "RunOnOtherNode", "Job.cs", 23)], "second") with { Database = Query("SELECT pg_advisory_lock(42)", 200) },
            Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 5)]) with
                { Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(100, false, "NightlyJob._leader")] } },
            Thread(4, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.NightlyJob", "Idle", "Job.cs", 27)], "scheduler") with
                { Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(100, false, "NightlyJob._leader")] } });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Waiting for a database advisory lock", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("Thread 4 (\"scheduler\") has a connection open and isn't using it", f.Summary);
        Assert.Single(f.Details, d => d.Contains("NightlyJob._leader"));
        Assert.Contains(f.Details, d => d.Contains("locktype = 'advisory'"));
    }

    [Fact]
    public void Plain_selects_behind_an_idle_transaction_point_at_a_table_lock()
    {
        var snapshot = Process(
            Thread(11, [.. QueryWait, App("MyApp.Catalog", "Count", "Catalog.cs", 9)]) with { Database = Query("SELECT count(*) FROM catalog", 200) },
            Thread(10, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Migrator", "AddColumn", "Migrator.cs", 13)]) with
                { Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(100, true)] } });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Database reads blocked by a table lock in an idle transaction", f.Title);
        Assert.Contains("ALTER TABLE", f.Summary);
        Assert.False(Db.IsPlainSelect("SELECT * FROM t FOR UPDATE"));
    }

    [Fact]
    public void Async_methods_holding_every_connection_while_awaiting_another()
    {
        var at = new SourceLocation("/src/Checkout.cs", 18);
        var snapshot = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)])) with
        {
            AsyncPoolWaits =
            [
                new AsyncPoolWait
                {
                    Provider = "PostgreSQL", Pool = new ConnectionPoolInfo(2, 2, 0), QueuedOpens = 3,
                    Methods =
                    [
                        new("Checkout.HandleAsync", at, true, true, "Audit.WriteAsync (Audit.cs:12)"),
                        new("Checkout.HandleAsync", at, true, true, "Audit.WriteAsync (Audit.cs:12)"),
                        new("Checkout.HandleAsync", new SourceLocation("/src/Checkout.cs", 14), false, true),
                    ],
                },
            ],
        };

        var f = Assert.Single(new ConnectionPoolDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("connection-pool-exhausted", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(f.Details, d => d == "2 calls of Checkout.HandleAsync hold an open connection and are paused at Checkout.cs:18, waiting for another connection from the same pool.");
        Assert.Contains("in Audit.WriteAsync (Audit.cs:12), use the connection Checkout.HandleAsync already has open", f.Advice);
        Assert.Equal([1], f.ThreadIds);
    }

    [Fact]
    public void Async_pool_wait_without_nested_holders_is_a_warning()
    {
        var snapshot = Process() with
        {
            AsyncPoolWaits = [new AsyncPoolWait { Provider = "PostgreSQL", Pool = new ConnectionPoolInfo(2, 2, 0), QueuedOpens = 1, Methods = [new("Jobs.RunAsync", null, false, true)] }],
        };
        Assert.Equal(Severity.Warning, Assert.Single(new ConnectionPoolDetector().Detect(snapshot, new HashSet<int>())).Severity);
    }
}

public class ThrottleTests
{
    [Fact]
    public void Throttle_with_every_slot_held_by_a_nested_waiter()
    {
        const ulong api = 0x900;
        var node = new TaskInfo { Address = 0x300, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = 0x100 };
        var customer = new TaskInfo { Address = 0x200, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "PartnerApi.GetCustomerAsync", ThisAddress = api, Awaiting = node };
        var order = new TaskInfo { Address = 0x150, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "PartnerApi.GetOrderAsync", ThisAddress = api, Awaiting = customer };
        var snapshot = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = order }) with
        {
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x100, "System.Threading.SemaphoreSlim"), FieldName = "PartnerApi._throttle", ContainerAddress = api, CurrentCount = 0, MaxCount = 3, AsyncWaiters = 3 }],
        };

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(f.Details, d => d.StartsWith("PartnerApi._throttle has 3 slots and 3 async callers waiting for one."));
    }
}
