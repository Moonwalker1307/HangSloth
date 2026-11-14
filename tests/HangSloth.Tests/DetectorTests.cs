using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

public class SyncOverAsyncDetectorTests
{
    private static readonly ObjectRef Ctx = new(0x1000, "MyApp.UiSynchronizationContext");

    private static ThreadSnapshot BlockedUiThread(TaskInfo task) =>
        Thread(1, [.. ResultWait, App("MyApp.OrderController", "Get", "OrderController.cs", 21)]) with
        {
            SynchronizationContext = Ctx,
            BlockedOnTask = task,
        };

    [Fact]
    public void Reports_deadlock_when_continuation_is_queued_to_the_blocked_threads_context()
    {
        var task = new TaskInfo
        {
            Address = 0x2000,
            TypeName = "AsyncStateMachineBox",
            Kind = TaskKind.AsyncMethod,
            Description = "OrderService.GetOrderAsync",
            AwaitLocation = new SourceLocation("/src/OrderService.cs", 88),
            Awaiting = new TaskInfo { Address = 0x3000, TypeName = "DelayPromise", Kind = TaskKind.Delay, State = TaskState.RanToCompletion, Description = "Task.Delay(...)" },
            Resume = new ResumeTarget(Ctx, ResumeStatus.Queued),
        };

        var findings = new SyncOverAsyncDetector().Detect(Process(BlockedUiThread(task)), new HashSet<int>()).ToList();

        var f = Assert.Single(findings);
        Assert.Equal("sync-over-async-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread 1 is blocking with .Result on a task that's waiting for the same SynchronizationContext. Classic sync-over-async, in OrderController.cs:21.", f.Summary);
        Assert.Contains(f.Details, d => d.Contains("OrderService.GetOrderAsync (paused at OrderService.cs:88)"));
        Assert.Equal(21, f.Location?.Line);
    }

    [Fact]
    public void Finds_the_stuck_method_deep_in_the_await_chain()
    {
        var inner = new TaskInfo
        {
            Address = 0x2100, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "OrderRepository.LoadAsync",
            Resume = new ResumeTarget(Ctx, ResumeStatus.Registered),
            Awaiting = new TaskInfo { Address = 0x3000, TypeName = "Task", Kind = TaskKind.Promise, Description = "a TaskCompletionSource<int> task" },
        };
        var outer = new TaskInfo { Address = 0x2000, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "OrderService.GetOrderAsync", Awaiting = inner };

        var f = Assert.Single(new SyncOverAsyncDetector().Detect(Process(BlockedUiThread(outer)), new HashSet<int>()));
        Assert.Equal("sync-over-async-deadlock", f.Id);
        Assert.Contains("OrderRepository.LoadAsync", f.Advice);
    }

    [Fact]
    public void Different_context_is_not_a_deadlock()
    {
        var task = new TaskInfo
        {
            Address = 0x2000, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "X.YAsync",
            Resume = new ResumeTarget(new ObjectRef(0x9999, "Other"), ResumeStatus.Queued),
        };
        var findings = new SyncOverAsyncDetector().Detect(Process(BlockedUiThread(task)), new HashSet<int>()).ToList();
        Assert.DoesNotContain(findings, f => f.Id == "sync-over-async-deadlock");
    }

    [Fact]
    public void No_context_means_no_finding()
    {
        var task = new TaskInfo { Address = 0x2000, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "X.YAsync" };
        var thread = BlockedUiThread(task) with { SynchronizationContext = null };
        Assert.Empty(new SyncOverAsyncDetector().Detect(Process(thread), new HashSet<int>()));
    }
}

public class LockDetectorTests
{
    private static MonitorLock Lock(ulong addr, string field, int owner, params int[] waiters) => new()
    {
        Object = new ObjectRef(addr, "System.Object"),
        FieldName = field,
        OwnerThreadId = owner,
        WaiterThreadIds = waiters,
    };

    [Fact]
    public void Two_thread_cycle_is_a_deadlock()
    {
        var snapshot = Process(
            Thread(4, [.. MonitorEnter, App("MyApp.Billing", "Charge", "Billing.cs", 40)], "a") with { WaitingForLock = 0xB },
            Thread(5, [.. MonitorEnter, App("MyApp.Billing", "Refund", "Billing.cs", 60)], "b") with { WaitingForLock = 0xA }) with
        {
            Locks = [Lock(0xA, "Billing._accounts", 4, 5), Lock(0xB, "Billing._ledger", 5, 4)],
        };

        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("lock-deadlock", f.Id);
        Assert.Equal([4, 5], f.ThreadIds);
        Assert.Contains("Thread 4 (\"a\") holds the lock on Billing._accounts and is waiting in Billing.Charge (Billing.cs:40) for the lock on Billing._ledger", f.Details[0]);
    }

    [Fact]
    public void Three_thread_cycle_is_reported_once()
    {
        var snapshot = Process(
            Thread(1, MonitorEnter) with { WaitingForLock = 0xB },
            Thread(2, MonitorEnter) with { WaitingForLock = 0xC },
            Thread(3, MonitorEnter) with { WaitingForLock = 0xA }) with
        {
            Locks = [Lock(0xA, "A", 1, 3), Lock(0xB, "B", 2, 1), Lock(0xC, "C", 3, 2)],
        };
        var f = Assert.Single(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(3, f.ThreadIds.Count);
    }

    [Fact]
    public void Waiting_without_cycle_is_contention_not_deadlock()
    {
        var owner = Thread(7, [Fx("System.Threading.Thread", "SleepInternal"), Fx("System.Threading.Thread", "Sleep"), App("MyApp.Cache", "Rebuild", "Cache.cs", 12)], "rebuilder");
        var snapshot = Process(
            owner,
            Thread(8, [.. MonitorEnter, App("MyApp.Cache", "Get", "Cache.cs", 30)]) with { WaitingForLock = 0xA },
            Thread(9, [.. MonitorEnter, App("MyApp.Cache", "Get", "Cache.cs", 30)]) with { WaitingForLock = 0xA }) with
        {
            Locks = [Lock(0xA, "Cache._sync", 7, 8, 9)],
        };

        Assert.Empty(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("2 threads are stuck waiting for the lock on Cache._sync, held by Thread 7 (\"rebuilder\"), which is sleeping (Thread.Sleep) in Cache.cs:12 while holding it.", f.Summary);
        Assert.Equal("Waiting at: Cache.cs:30 (2 threads).", f.Details[0]);
    }

    [Fact]
    public void Lock_without_living_owner_is_orphaned()
    {
        var snapshot = Process(Thread(8, MonitorEnter) with { WaitingForLock = 0xA }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0xA, "System.Object"), OwnerThreadId = null, WaiterThreadIds = [8] }],
        };
        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Orphaned lock", f.Title);
    }
}

public class ThreadPoolStarvationDetectorTests
{
    private static ThreadSnapshot BlockedWorker(int id) =>
        Thread(id, [.. ResultWait, App("MyApp.PriceController", "Get", "PriceController.cs", 17), .. PoolBottom]) with { IsThreadPoolWorker = true };

    private static ThreadSnapshot IdleWorker(int id) =>
        Thread(id, [Fx("System.Threading.LowLevelLifoSemaphore", "Wait"), Fx("System.Threading.PortableThreadPool+WorkerThread", "WorkerThreadStart")]) with { IsThreadPoolWorker = true };

    [Fact]
    public void Mostly_blocked_pool_is_starvation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 8).Select(BlockedWorker)]) with
        {
            ThreadPool = new ThreadPoolSnapshot { MinThreads = 4, MaxThreads = 8, ActiveWorkers = 8, QueuedWorkItems = 36 },
        };
        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in .Result at PriceController.cs:17), and 36 work items are waiting in the queue.", f.Summary);
        Assert.Contains(f.Details, d => d.Contains("maximum of 8"));
    }

    [Fact]
    public void Mostly_idle_pool_is_fine()
    {
        var snapshot = Process([.. Enumerable.Range(10, 4).Select(BlockedWorker), .. Enumerable.Range(20, 6).Select(IdleWorker)]);
        Assert.Empty(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
    }

    [Fact]
    public void A_few_blocked_threads_are_not_starvation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 3).Select(BlockedWorker)]);
        Assert.Empty(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
    }
}

public class BlockedOnTaskDetectorTests
{
    [Fact]
    public void Explains_chain_down_to_uncompleted_promise()
    {
        var task = new TaskInfo
        {
            Address = 1, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "Importer.ImportAsync",
            AwaitLocation = new SourceLocation("/src/Importer.cs", 33),
            Awaiting = new TaskInfo { Address = 2, TypeName = "Task<bool>", Kind = TaskKind.Promise, Description = "a TaskCompletionSource<bool> task" },
        };
        var thread = Thread(1, [Fx("System.Threading.Tasks.Task", "InternalWaitCore"), Fx("System.Threading.Tasks.Task", "Wait"), App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = task };

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(Process(thread), new HashSet<int>()));
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for Importer.ImportAsync (paused at Importer.cs:33), which awaits a TaskCompletionSource<bool> task that nothing has completed yet.", f.Summary);
        Assert.Equal(Severity.Warning, f.Severity);
    }
}

public class HangAnalyzerTests
{
    [Fact]
    public void Threads_explained_by_starvation_are_not_reported_again_as_blocked_on_task()
    {
        var task = new TaskInfo { Address = 1, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "X.YAsync" };
        var workers = Enumerable.Range(10, 6)
            .Select(id => Thread(id, [.. ResultWait, App("MyApp.C", "M", "C.cs", 1), .. PoolBottom]) with { IsThreadPoolWorker = true, BlockedOnTask = task })
            .ToArray();

        var report = new HangAnalyzer().Analyze(Process(workers));
        var f = Assert.Single(report.Findings);
        Assert.Equal("threadpool-starvation", f.Id);
    }

    [Fact]
    public void Healthy_process_has_no_findings()
    {
        var main = Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 3)]);
        Assert.Empty(new HangAnalyzer().Analyze(Process(main)).Findings);
    }
}
