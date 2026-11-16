using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Thread pool starvation only when work waits for a thread, named after what the threads wait for.
public class StarvationAccuracyTests
{
    private static readonly ThreadPoolSnapshot EmptyQueue = new() { MinThreads = 4, MaxThreads = 32767, ActiveWorkers = 4, QueuedWorkItems = 0 };
    private static readonly ThreadPoolSnapshot FullPool = new() { MinThreads = 4, MaxThreads = 8, ActiveWorkers = 8, QueuedWorkItems = 32 };

    private static readonly StackFrameInfo[] SemaphoreCore =
        [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "WaitCore")];

    private static ThreadSnapshot Worker(int id, IEnumerable<StackFrameInfo> frames) =>
        Thread(id, [.. frames, .. PoolBottom]) with { IsThreadPoolWorker = true };

    private static ThreadSnapshot Consumer(int id) =>
        Worker(id, [.. SemaphoreCore, Fx("System.Collections.Concurrent.BlockingCollection<System.__Canon>", "TryTakeWithNoTimeValidation"),
            Fx("System.Collections.Concurrent.BlockingCollection<System.__Canon>", "GetConsumingEnumerable"), App("MyApp.Mailer", "Consume", "Mailer.cs", 32)]) with
        {
            WaitingForSemaphore = 0x500,
            WaitingOnCollection = new CollectionWait { Collection = new ObjectRef(0x300, "System.Collections.Concurrent.BlockingCollection<System.String>"), Adding = false, Count = 0, FieldName = "Mailer._outbox" },
        };

    private static ThreadSnapshot Poller(int id) =>
        Worker(id, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.OutboxPoller", "Poll", "OutboxPoller.cs", 14)]);

    private static ThreadSnapshot InLock(int id, ulong lockAddress = 0xA) =>
        Worker(id, [.. MonitorEnter, App("MyApp.Catalog", "Count", "Catalog.cs", 25)]) with { WaitingForLock = lockAddress };

    private static ThreadSnapshot Rebuilder(int id, bool pool = false) =>
        Thread(id, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Catalog", "Rebuild", "Catalog.cs", 40), .. pool ? PoolBottom : []], pool ? null : "catalog-rebuild") with { IsThreadPoolWorker = pool };

    private static MonitorLock CatalogLock(int owner, IEnumerable<int> waiters) =>
        new() { Object = new ObjectRef(0xA, "System.Object"), FieldName = "Catalog._sync", OwnerThreadId = owner, WaiterThreadIds = waiters.ToList() };

    private static AnalysisReport Analyze(ProcessSnapshot snapshot) => new HangAnalyzer().Analyze(snapshot);

    [Fact]
    public void Pool_threads_consuming_an_empty_queue_are_not_starvation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 4).Select(Consumer)]) with
        {
            ThreadPool = EmptyQueue,
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x500, "System.Threading.SemaphoreSlim"), MaxCount = int.MaxValue, WaiterThreadIds = [10, 11, 12, 13] }],
        };
        Assert.Empty(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.DoesNotContain(Analyze(snapshot).Findings, f => f.Severity >= Severity.Warning);
    }

    [Fact]
    public void Pool_threads_parked_in_sleep_with_nothing_queued_are_only_noted()
    {
        var snapshot = Process([.. Enumerable.Range(10, 4).Select(Poller)]) with { ThreadPool = EmptyQueue };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("threadpool-parked", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("4 of 4 thread pool threads are parked in blocking calls (all in Thread.Sleep at OutboxPoller.cs:14), and nothing is waiting in the queue.", f.Summary);
        Assert.Contains("Nothing is queued, so no work is waiting for a thread: this is not starvation. But new work has to wait for a new thread, which the pool adds slowly once it is above its minimum of 4.", f.Details);
        Assert.DoesNotContain(Analyze(snapshot).Findings, x => x.Severity >= Severity.Warning);
    }

    [Fact]
    public void An_unknown_queue_length_is_still_starvation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 4).Select(Poller)]);
        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public void Task_waits_with_nothing_queued_are_still_starvation()
    {
        // The continuation they wait for may sit anywhere (a timer, an I/O callback) until a pool thread is free.
        var snapshot = Process([.. Enumerable.Range(10, 6).Select(id => Worker(id, [.. ResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 23)]))]) with { ThreadPool = EmptyQueue };
        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public void Overlapping_timer_callbacks_with_nothing_queued_are_still_starvation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 4).Select(id => Worker(id,
            [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Sync", "Tick", "Sync.cs", 9), Fx("System.Threading.TimerQueueTimer", "Fire")]))]) with { ThreadPool = EmptyQueue };
        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("threadpool-starvation", f.Id);
    }

    [Fact]
    public void Starvation_behind_one_lock_names_the_lock_and_its_holder()
    {
        var waiters = Enumerable.Range(10, 8).ToList();
        var snapshot = Process([Rebuilder(4), .. waiters.Select(id => InLock(id))]) with { ThreadPool = FullPool, Locks = [CatalogLock(4, waiters)] };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in lock (Monitor.Enter) at Catalog.cs:25), and 32 work items are waiting in the queue.", f.Summary);
        Assert.Contains("All of them are waiting for the lock on Catalog._sync, held by Thread 4 (\"catalog-rebuild\"), which is sleeping (Thread.Sleep) in Catalog.cs:40 while holding it.", f.Details);
        Assert.Equal("Don't do slow or blocking work while holding the lock on Catalog._sync, as Thread 4 (\"catalog-rebuild\") does in Catalog.cs:40: copy what you need, release the lock, then do the slow part. " +
                     "Every thread pool thread that needs the lock waits until then; raising ThreadPool.SetMinThreads only adds more waiters.", f.Advice);
        Assert.Contains(4, f.ThreadIds);
        Assert.Equal(new SourceLocation("/src/Catalog.cs", 40), f.Location);
    }

    [Fact]
    public void Starvation_behind_a_lock_does_not_hide_the_lock_finding()
    {
        var waiters = Enumerable.Range(10, 8).ToList();
        var snapshot = Process([Rebuilder(4), .. waiters.Select(id => InLock(id))]) with { ThreadPool = FullPool, Locks = [CatalogLock(4, waiters)] };

        var report = Analyze(snapshot);
        Assert.Equal(["threadpool-starvation", "lock-contention"], report.Findings.Select(x => x.Id));
        Assert.Equal("8 threads are stuck waiting for the lock on Catalog._sync, held by Thread 4 (\"catalog-rebuild\"), which is sleeping (Thread.Sleep) in Catalog.cs:40 while holding it.", report.Findings[1].Summary);
    }

    [Fact]
    public void A_lock_held_by_a_pool_thread_is_named_with_the_number_of_threads_behind_it()
    {
        // A web request that sleeps inside the lock, and ten more requests waiting for it.
        var waiters = Enumerable.Range(10, 10).ToList();
        var snapshot = Process([Rebuilder(7, pool: true), .. waiters.Select(id => InLock(id))]) with { ThreadPool = FullPool, Locks = [CatalogLock(7, waiters)] };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 11 of 11 thread pool threads are blocked (10 of them in lock (Monitor.Enter) at Catalog.cs:25), and 32 work items are waiting in the queue.", f.Summary);
        Assert.Contains("10 of them are waiting for the lock on Catalog._sync, held by Thread 7, which is sleeping (Thread.Sleep) in Catalog.cs:40 while holding it.", f.Details);
        Assert.StartsWith("Don't do slow or blocking work while holding the lock on Catalog._sync, as Thread 7 does in Catalog.cs:40", f.Advice);
    }

    [Fact]
    public void A_lock_that_only_a_few_threads_wait_for_is_not_named()
    {
        var snapshot = Process([Rebuilder(4), InLock(10), InLock(11), .. Enumerable.Range(12, 6).Select(Poller)]) with { ThreadPool = FullPool, Locks = [CatalogLock(4, [10, 11])] };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.DoesNotContain(f.Details, d => d.Contains("Catalog._sync"));
        Assert.DoesNotContain(4, f.ThreadIds);
    }

    [Fact]
    public void Starvation_behind_an_idle_transaction_names_the_transaction_and_keeps_the_database_finding()
    {
        StackFrameInfo[] update = [Fx("Npgsql.Internal.NpgsqlConnector", "ReadMessage"), Fx("Npgsql.NpgsqlCommand", "ExecuteNonQuery"), App("MyApp.Wallets", "Refund", "Wallets.cs", 56)];
        var refunds = Enumerable.Range(10, 6).Select(id => Worker(id, update) with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Waiting = DatabaseWaitKind.Query, CommandText = "UPDATE wallets SET balance = balance + 5 WHERE id = 1", SessionId = 100 + id },
        });
        var charge = Thread(5, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Wallets", "Charge", "Wallets.cs", 30)], "charge") with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(99, InTransaction: true)] },
        };
        var snapshot = Process([charge, .. refunds]) with { ThreadPool = FullPool };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 6 of 6 thread pool threads are blocked (all in NpgsqlCommand.ExecuteNonQuery at Wallets.cs:56), and 32 work items are waiting in the queue.", f.Summary);
        Assert.Contains("6 of them are waiting for PostgreSQL to answer `UPDATE wallets SET balance = balance + 5 WHERE id = 1`. A slow query is enough: each request holds a thread for as long as the query runs.", f.Details);
        Assert.Contains("Thread 5 (\"charge\") has an open transaction (session 99) but isn't talking to the database: it is sleeping (Thread.Sleep) in Wallets.Charge (Wallets.cs:30). " +
                        "The queries are most likely waiting for a lock that transaction holds.", f.Details);

        var report = Analyze(snapshot);
        Assert.Equal(["threadpool-starvation", "database-wait"], report.Findings.Select(x => x.Id));
        Assert.Equal(Severity.Warning, report.Findings[1].Severity);
    }

    [Fact]
    public void Starvation_behind_one_semaphore_names_it_and_keeps_the_semaphore_finding()
    {
        var waiters = Enumerable.Range(10, 6).ToList();
        var snapshot = Process([.. waiters.Select(id => Worker(id, [.. SemaphoreCore, Fx("System.Threading.SemaphoreSlim", "Wait"), App("MyApp.PartnerApi", "Call", "PartnerApi.cs", 18)]) with { WaitingForSemaphore = 0x700 })]) with
        {
            ThreadPool = FullPool,
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x700, "System.Threading.SemaphoreSlim"), FieldName = "PartnerApi._throttle", CurrentCount = 0, MaxCount = 3, WaiterThreadIds = waiters }],
        };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains("All of them are waiting for PartnerApi._throttle, a SemaphoreSlim with 0 of 3 slots free: they can only go on when the code holding its slots calls Release().", f.Details);
        Assert.Equal(["threadpool-starvation", "semaphore-wait"], Analyze(snapshot).Findings.Select(x => x.Id));
    }

    [Fact]
    public void Starvation_names_the_call_the_app_made_rather_than_the_primitive_inside_it()
    {
        var snapshot = Process([.. Enumerable.Range(10, 8).Select(id => Worker(id,
            [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.ManualResetEventSlim", "Wait"), App("MyApp.FeedGate", "Publish", "FeedGate.cs", 18)]))]) with { ThreadPool = FullPool };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in ManualResetEventSlim.Wait at FeedGate.cs:18), and 32 work items are waiting in the queue.", f.Summary);
        Assert.Contains("8 threads: ManualResetEventSlim.Wait at FeedGate.Publish (FeedGate.cs:18).", f.Details);
        Assert.Equal("Avoid blocking thread pool threads (ManualResetEventSlim.Wait at FeedGate.cs:18); move long blocking work to a dedicated thread or make it async.", f.Advice);
    }

    [Fact]
    public void A_task_wait_inside_HttpClient_Send_is_named_HttpClient_Send()
    {
        var snapshot = Process([.. Enumerable.Range(10, 8).Select(id => Worker(id,
            [.. ResultWait.Take(4), Fx("System.Runtime.CompilerServices.TaskAwaiter`1", "GetResult"), Fx("System.Net.Http.HttpMessageInvoker", "Send"),
             Fx("System.Net.Http.HttpClient", "Send"), App("MyApp.PriceClient", "Fetch", "PriceClient.cs", 34)]))]) with { ThreadPool = FullPool };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in HttpClient.Send at PriceClient.cs:34), and 32 work items are waiting in the queue.", f.Summary);
        Assert.Equal("Don't do synchronous I/O on thread pool threads: await HttpClient.SendAsync instead of calling HttpClient.Send in PriceClient.cs:34. Raising ThreadPool.SetMinThreads only hides the problem.", f.Advice);
        Assert.DoesNotContain(f.Details, d => d.Contains(".Result/.Wait()"));
    }

    [Fact]
    public void Consumers_with_a_backlog_are_still_starvation()
    {
        // Every pool thread is taken by a consumer, so the queued work never gets one.
        var snapshot = Process([.. Enumerable.Range(10, 6).Select(Consumer)]) with { ThreadPool = FullPool with { QueuedWorkItems = 50 } };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread pool starvation: 6 of 6 thread pool threads are blocked (all in BlockingCollection<T>.GetConsumingEnumerable at Mailer.cs:32), and 50 work items are waiting in the queue.", f.Summary);
    }

    [Fact]
    public void A_few_consumers_do_not_hide_Result_starvation()
    {
        var results = Enumerable.Range(20, 8).Select(id => Worker(id, [.. ResultWait, App("MyApp.Orders", "Load", "Orders.cs", 17)]));
        var snapshot = Process([.. Enumerable.Range(10, 6).Select(Consumer), .. results]) with { ThreadPool = FullPool with { QueuedWorkItems = 100 } };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread pool starvation: 14 of 14 thread pool threads are blocked (8 of them in .Result at Orders.cs:17), and 100 work items are waiting in the queue.", f.Summary);
    }

    [Fact]
    public void Consumers_next_to_Result_waits_count_as_blocked_with_nothing_queued()
    {
        // The continuations the .Result waits need a thread, and the consumers hold the others.
        var results = Enumerable.Range(20, 8).Select(id => Worker(id, [.. ResultWait, App("MyApp.Orders", "Load", "Orders.cs", 17)]));
        var snapshot = Process([.. Enumerable.Range(10, 6).Select(Consumer), .. results]) with { ThreadPool = EmptyQueue };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread pool starvation: 14 of 14 thread pool threads are blocked (8 of them in .Result at Orders.cs:17).", f.Summary);
    }

    [Fact]
    public void A_wait_with_no_app_code_on_the_stack_is_named_by_the_wait()
    {
        var snapshot = Process([.. Enumerable.Range(10, 6).Select(id => Worker(id, [.. ResultWait, Fx("System.Threading.Thread", "StartCallback")]))]) with { ThreadPool = FullPool };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith("Thread pool starvation: 6 of 6 thread pool threads are blocked (all in .Result at ", f.Summary);
        Assert.Contains("Threads blocked on .Result/.Wait() are usually waiting for a task whose continuation needs a free thread pool thread to finish, which is exactly what's missing.", f.Details);
        Assert.StartsWith("Stop blocking on async code in thread pool work: await instead of .Result in ", f.Advice);
    }

    [Fact]
    public void A_task_wait_inside_a_library_call_keeps_the_Result_explanation()
    {
        var snapshot = Process([.. Enumerable.Range(10, 6).Select(id => Worker(id,
            [.. ResultWait, Fx("Microsoft.Extensions.DependencyInjection.ServiceProvider", "GetService"), App("MyApp.Orders", "Resolve", "Orders.cs", 9)])
            with { Frames = [.. ResultWait, new StackFrameInfo { TypeName = "Microsoft.Extensions.DependencyInjection.ServiceProvider", MethodName = "GetService", ModuleName = "/app/Microsoft.Extensions.DependencyInjection.dll" },
                             App("MyApp.Orders", "Resolve", "Orders.cs", 9), .. PoolBottom] })]) with { ThreadPool = FullPool };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 6 of 6 thread pool threads are blocked (all in ServiceProvider.GetService at Orders.cs:9), and 32 work items are waiting in the queue.", f.Summary);
        Assert.Contains("Threads blocked on .Result/.Wait() are usually waiting for a task whose continuation needs a free thread pool thread to finish, which is exactly what's missing.", f.Details);
    }

    [Fact]
    public void A_database_wait_under_a_query_helper_is_named_by_the_driver_call()
    {
        StackFrameInfo[] query = [Fx("Npgsql.Internal.NpgsqlConnector", "ReadMessage"), Fx("Npgsql.NpgsqlDataReader", "Read"),
            Fx("System.Linq.Enumerable", "ToList"), App("MyApp.Orders", "Open", "Orders.cs", 12)];
        var snapshot = Process([.. Enumerable.Range(10, 6).Select(id => Worker(id, query))]) with { ThreadPool = FullPool };

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 6 of 6 thread pool threads are blocked (all in NpgsqlDataReader.Read at Orders.cs:12), and 32 work items are waiting in the queue.", f.Summary);
    }
}
