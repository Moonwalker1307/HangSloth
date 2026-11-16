using DeadlockLab.Code;
using DeadlockLab.Database;
using DeadlockLab.Http;
using DeadlockLab.SqlServer;

namespace DeadlockLab;

/// Database scenarios need PostgreSQL; SqlServer scenarios need SQL Server.
public enum Area { Code, Database, SqlServer }

public enum Difficulty
{
    /// The kind you'd find in a textbook: the bug is visible in one method.
    Obvious,
    /// Spread over several classes, hidden in a library, a callback, an initializer or the database.
    Hidden,
}

/// <param name="Expected">
/// HangSloth finding ids this scenario should produce (Warning or Critical). Empty means the
/// app is healthy and HangSloth should stay quiet. The end-to-end tests check these.
/// </param>
/// <param name="MustNotReport">
/// Finding ids HangSloth must not produce here at any severity, for a scenario that looks like a
/// worse problem than it is (a slow reader isn't a deadlock).
/// </param>
public sealed record Scenario(string Name, Area Area, Difficulty Difficulty, string Story, string[] Expected, Action Run, string[]? MustNotReport = null);

public static class Scenarios
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        // ---------------------------------------------------------------- code
        new("transfer", Area.Code, Difficulty.Obvious,
            "Two transfers between the same accounts lock them in opposite order.",
            ["lock-deadlock"], Banking.Run),

        new("dining-philosophers", Area.Code, Difficulty.Obvious,
            "Three workers each hold one fork and wait for the next one, in a circle.",
            ["lock-deadlock"], Philosophers.Run),

        new("event-callback", Area.Code, Difficulty.Hidden,
            "OrderBook locks itself and calls Inventory; Inventory raises StockChanged under its own lock, and OrderBook's handler locks the OrderBook.",
            ["lock-deadlock"], OrderBookScenario.Run),

        new("library-sync-over-async", Area.Code, Difficulty.Hidden,
            "A sync wrapper in a 'library' blocks on async code; ConfigureAwait(false) is there, but only on the last await, three calls deep.",
            ["sync-over-async-deadlock"], LegacySettingsScenario.Run),

        new("semaphore-order", Area.Code, Difficulty.Hidden,
            "Two SemaphoreSlim(1,1) used as mutexes, taken in opposite order. No lock statement in sight.",
            ["semaphore-deadlock"], SemaphoreOrderScenario.Run),

        new("async-semaphore-reentry", Area.Code, Difficulty.Hidden,
            "An async cache takes its SemaphoreSlim with WaitAsync and then calls a refresh method that waits on the same semaphore. SemaphoreSlim isn't reentrant.",
            ["semaphore-wait"], AsyncCacheScenario.Run),

        new("rwlock-vs-lock", Area.Code, Difficulty.Hidden,
            "ConfigStore holds its ReaderWriterLockSlim write lock while logging to AuditLog; AuditLog holds its lock while reading the config.",
            ["lock-deadlock"], ConfigStoreScenario.Run),

        new("rwlock-upgrade-slow-reader", Area.Code, Difficulty.Hidden,
            "A price refresh holds the ReaderWriterLockSlim in upgradeable mode and waits to upgrade while a slow report still reads. The report finishes after two minutes and the refresh goes ahead: slow, not deadlocked.",
            ["lock-contention"], PriceCatalogScenario.RunUpgrade, MustNotReport: ["lock-deadlock", "wait-cycle"]),

        new("rwlock-readers-block-writer", Area.Code, Difficulty.Obvious,
            "Three slow reports hold a ReaderWriterLockSlim for reading; a price update waits for the write lock until they finish. Nothing is orphaned or deadlocked.",
            ["lock-contention"], PriceCatalogScenario.RunWriterBehindReaders, MustNotReport: ["lock-deadlock", "wait-cycle"]),

        new("lazy-init", Area.Code, Difficulty.Hidden,
            "A Lazy<T> factory waits for a Task.Run that reads the same Lazy<T>.Value.",
            ["wait-cycle"], CatalogScenario.Run),

        new("static-constructor", Area.Code, Difficulty.Hidden,
            "A static constructor waits for a Task.Run that touches the same class, which is still being initialized.",
            ["type-initializer-deadlock"], PriceTableScenario.Run),

        new("lock-then-wait", Area.Code, Difficulty.Obvious,
            "A report holds a lock and waits for a Task.Run that needs the same lock.",
            ["wait-cycle"], ReportScenario.Run),

        new("thread-join-under-lock", Area.Code, Difficulty.Hidden,
            "Stop() joins the worker thread while holding a lock; the worker's last step reports 'stopped' under that same lock.",
            ["wait-cycle"], WorkerScenario.Run),

        new("timer-dispose", Area.Code, Difficulty.Hidden,
            "Stop() holds the buffer lock and waits for the timer to drain its callbacks, but the running callback is waiting for the buffer lock.",
            ["lock-contention"], MetricsScenario.Run),

        new("parallel-foreach-under-lock", Area.Code, Difficulty.Hidden,
            "A thread holds a lock and runs Parallel.ForEach; the loop body, running on other threads, takes the same lock.",
            ["wait-cycle"], ParallelScenario.Run),

        new("ui-invoke", Area.Code, Difficulty.Hidden,
            "The UI thread waits for a background export, which synchronously Sends a status update to the UI thread (Control.Invoke style).",
            ["wait-cycle"], UiInvokeScenario.Run),

        new("inline-continuation", Area.Code, Difficulty.Hidden,
            "TaskCompletionSource.SetResult under a lock runs the awaiting method inline; that continuation needs a lock another thread holds while it waits for the first lock.",
            ["lock-deadlock"], InlineContinuationScenario.Run),

        new("throttle-reentry", Area.Code, Difficulty.Hidden,
            "A SemaphoreSlim(3) throttle: each call holds a slot and makes a nested call through the same throttle. Three concurrent calls use every slot.",
            ["semaphore-wait"], ThrottleScenario.Run),

        new("bounded-queue", Area.Code, Difficulty.Hidden,
            "A publisher holds a lock while adding to a full BlockingCollection; the only consumer needs that lock before it takes the next item.",
            ["bounded-queue-deadlock"], BoundedQueueScenario.Run),

        new("lost-pulse", Area.Code, Difficulty.Hidden,
            "A hand-rolled queue pulses only when the consumer is already waiting; the item arrived first, so the consumer waits forever.",
            ["monitor-wait"], LostPulseScenario.Run),

        new("idle-monitor-consumer", Area.Code, Difficulty.Obvious,
            "Control: a mail sender waits in Monitor.Wait for its empty outbox, in the textbook loop, and a receipt printer waits while its counter of due receipts is 0; their logs of sent mails and printed receipts aren't empty, and aren't work. Nothing is wrong.",
            [], MailOutboxScenario.Run, MustNotReport: ["monitor-wait"]),

        new("http-connection-limit", Area.Code, Difficulty.Hidden,
            "An HttpClient allows 2 connections per server. Each page request keeps its first response unread while it makes a second request to the same server.",
            ["http-connection-limit"], HttpConnectionLimitScenario.Run),

        new("http-no-response", Area.Code, Difficulty.Obvious,
            "Checkout calls a payment gateway with .Result and no timeout; the gateway accepted the connection and never answers.",
            ["http-wait"], HttpNoResponseScenario.Run),

        new("timer-overlap", Area.Code, Difficulty.Hidden,
            "A timer fires every 100 ms but its callback holds a lock for seconds. Callbacks don't wait for each other, so they pile up behind the lock until the thread pool is full.",
            ["threadpool-starvation"], TimerOverlapScenario.Run),

        new("cancellation-dispose", Area.Code, Difficulty.Hidden,
            "Disposing a CancellationTokenRegistration waits for its callback if it's running. The callback needs a lock that the disposing code holds.",
            ["wait-cycle"], CancellationDisposeScenario.Run),

        new("finalizer-wait", Area.Code, Difficulty.Hidden,
            "A cleanup job holds a logger's lock and calls GC.WaitForPendingFinalizers; a finalizer logs through the same logger.",
            ["wait-cycle"], FinalizerWaitScenario.Run),

        new("child-process-pipe", Area.Code, Difficulty.Hidden,
            "The app redirects a child process's output and waits for it to exit before reading it. The child fills the pipe and waits for the parent to read.",
            ["process-pipe-deadlock"], ChildProcessScenario.Run),

        new("healthy", Area.Code, Difficulty.Obvious,
            "Control: background workers ticking along with short locks and a timer, and idle queue consumers waiting for work. Nothing is wrong.",
            [], HealthyScenario.Run),

        // ---------------------------------------------------------------- database
        new("db-forgotten-transaction", Area.Database, Difficulty.Obvious,
            "One thread updates a row in a transaction, then waits for an approval that never comes, without committing. Another thread updates the same row.",
            ["database-wait"], ForgottenTransactionScenario.Run),

        new("db-deadlock-retry", Area.Database, Difficulty.Obvious,
            "Two transfers update the same two rows in opposite order. PostgreSQL kills one as a deadlock victim, the app retries the same way, forever.",
            ["database-wait"], RetryingDeadlockScenario.Run),

        new("db-app-lock-cycle", Area.Database, Difficulty.Hidden,
            "Thread A holds an app lock and waits for a row lock in the database; thread B holds that row lock in a transaction and waits for the app lock. Neither .NET nor PostgreSQL can see the whole cycle.",
            ["database-deadlock"], CacheAndRowLockScenario.Run),

        new("db-pool-exhaustion", Area.Database, Difficulty.Hidden,
            "Each request keeps its connection open and opens a second one for audit logging. With more requests than pool slots, every request waits for a connection that only another waiting request can return.",
            ["connection-pool-exhausted"], PoolExhaustionScenario.Run),

        new("db-async-row-lock", Area.Database, Difficulty.Hidden,
            "Like the forgotten transaction, but the blocked update is async and the caller blocks on it with .GetAwaiter().GetResult().",
            ["database-wait"], AsyncRowLockScenario.Run),

        new("db-migration-lock", Area.Database, Difficulty.Hidden,
            "A schema migration runs ALTER TABLE in a transaction and waits before committing; plain SELECTs on that table queue behind its lock.",
            ["database-wait"], MigrationLockScenario.Run),

        new("db-advisory-lock", Area.Database, Difficulty.Hidden,
            "A job takes a session-level pg_advisory_lock and keeps the connection open without unlocking; the next scheduler waits for it forever. No transaction is involved.",
            ["database-wait"], AdvisoryLockScenario.Run),

        new("db-starvation", Area.Database, Difficulty.Obvious,
            "Controller actions block on async database calls with .Result; requests hold every thread pool thread while they wait.",
            ["threadpool-starvation"], DbStarvationScenario.Run),

        new("db-async-pool-exhaustion", Area.Database, Difficulty.Hidden,
            "The async version of the nested-connection pool deadlock: every request awaits a second connection while holding one. No thread is blocked except the one waiting for all of them.",
            ["connection-pool-exhausted"], AsyncPoolExhaustionScenario.Run),

        new("ef-row-lock", Area.Database, Difficulty.Hidden,
            "EF Core: approving an order saves it inside a transaction and then waits for a fraud check before committing; the warehouse's SaveChanges on the same order waits for the row lock.",
            ["database-wait"], EfRowLockScenario.Run),

        new("ef-nested-context", Area.Database, Difficulty.Hidden,
            "EF Core: a checkout holds a DbContext transaction and logs through an audit service that creates its own DbContext. Three checkouts, two pooled connections.",
            ["connection-pool-exhausted"], EfNestedContextScenario.Run),

        new("db-large-select", Area.Database, Difficulty.Obvious,
            "A cache refresh loads a whole table (millions of rows) into a list while holding the cache lock; every request queues behind it. Slow, not deadlocked.",
            ["lock-contention"], LargeSelectScenario.Run),

        new("db-select-blocks-alter", Area.Database, Difficulty.Hidden,
            "A slow export keeps a big SELECT open (ACCESS SHARE); a migration's ALTER TABLE queues behind it, and every later query on the table queues behind the ALTER.",
            ["database-wait"], SelectBlocksAlterScenario.Run),

        new("db-select-for-update", Area.Database, Difficulty.Hidden,
            "A batch job locks a whole date range with SELECT ... FOR UPDATE and processes the rows one per second in the transaction; a checkout updating one of them waits for the whole batch.",
            ["database-wait"], SelectForUpdateScenario.Run),

        new("db-healthy", Area.Database, Difficulty.Obvious,
            "Control: an idle app with a warm connection pool. Nothing is wrong.",
            [], HealthyDbScenario.Run),

        // ---------------------------------------------------------------- SQL Server
        new("sqlserver-lock-escalation", Area.SqlServer, Difficulty.Hidden,
            "A report reads 20,000 rows under REPEATABLE READ and renders a PDF before committing; SQL Server escalates the row locks to a table lock, so an update of a row outside the report waits.",
            ["database-wait"], LockEscalationScenario.Run),

        new("sqlserver-read-blocked", Area.SqlServer, Difficulty.Obvious,
            "A stock adjustment updates a row and waits for a scanner without committing; under SQL Server's default READ COMMITTED, a plain SELECT of that row waits too.",
            ["database-wait"], ReadBlockedScenario.Run),
    ];
}

internal static class Ready
{
    public static void Signal()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();
    }

    /// Signal readiness from a background thread after the hang has had time to settle.
    public static void SignalLater(int ms = 750) =>
        new Thread(() => { Thread.Sleep(ms); Signal(); }) { IsBackground = true, Name = "ready-signal" }.Start();

    /// Park the calling thread forever, like a host waiting for shutdown.
    public static void Forever() => Thread.Sleep(Timeout.Infinite);
}
