using DeadlockLab.Code;
using DeadlockLab.Database;

namespace DeadlockLab;

public enum Area { Code, Database }

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
public sealed record Scenario(string Name, Area Area, Difficulty Difficulty, string Story, string[] Expected, Action Run);

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

        new("healthy", Area.Code, Difficulty.Obvious,
            "Control: background workers ticking along with short locks and a timer. Nothing is wrong.",
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

        new("db-healthy", Area.Database, Difficulty.Obvious,
            "Control: an idle app with a warm connection pool. Nothing is wrong.",
            [], HealthyDbScenario.Run),
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
