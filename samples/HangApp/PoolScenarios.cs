using System.Collections.Concurrent;

namespace HangApp;

/// Control: every thread pool thread is parked in a long-lived loop (queue consumers, pollers, a shutdown
/// watcher) and nothing else is queued. Nothing is waiting for a thread, so this isn't starvation.
/// 14 of the 16 are parked in calls other than the consumers' wait, enough to be noted whatever spare
/// workers the pool keeps.
static class ParkedPoolScenario
{
    private static readonly BlockingCollection<string> MailQueue = new();
    private static readonly ManualResetEventSlim Shutdown = new();

    public static void Run()
    {
        ThreadPool.SetMinThreads(16, 16);
        for (var i = 0; i < 2; i++)
            _ = Task.Run(SendMail);
        for (var i = 0; i < 13; i++)
            _ = Task.Run(PollOutbox);
        _ = Task.Run(WatchShutdown);
        MailQueue.Add("welcome");
        Ready.SignalLater(1500);
        Thread.Sleep(Timeout.Infinite);
    }

    private static void SendMail()
    {
        foreach (var mail in MailQueue.GetConsumingEnumerable())
            Console.WriteLine($"sent {mail}");
    }

    private static void PollOutbox()
    {
        while (true)
            Thread.Sleep(30_000);
    }

    private static void WatchShutdown() => Shutdown.Wait();
}

/// Every thread pool thread needs the catalog lock, and a dedicated thread holds it while it sleeps:
/// the requests pile up behind the lock, not behind the pool. 16 requests: 8 run and wait for the lock,
/// 8 wait in the pool's queue, and each running request has started 2 audit tasks, which wait in the
/// local queue of its thread. 24 work items are waiting.
static class PoolLockConvoyScenario
{
    public const int Queued = 24;
    private const int Workers = 8;

    public static void Run()
    {
        ThreadPool.SetMinThreads(Workers, Workers);
        ThreadPool.SetMaxThreads(Workers, Workers);
        var catalog = new Catalog();
        new Thread(catalog.Rebuild) { Name = "catalog-rebuild", IsBackground = true }.Start();
        Thread.Sleep(200);
        for (var i = 0; i < 16; i++)
            ThreadPool.QueueUserWorkItem(_ => catalog.Count());
        Ready.SignalLater(1500);
        Thread.Sleep(Timeout.Infinite);
    }

    private sealed class Catalog
    {
        private readonly object _sync = new();
        private readonly CountdownEvent _allRunning = new(Workers);
        private int _items;
        private int _audits;
        private int _started;

        public void Rebuild()
        {
            lock (_sync)
            {
                _items = 0;
                Thread.Sleep(Timeout.Infinite); // the "rebuild" never finishes
            }
        }

        public int Count()
        {
            // Queue the audits only once every worker is in here: a worker still looking for work would
            // otherwise take them from another worker's local queue, and the count would vary.
            if (Interlocked.Increment(ref _started) <= Workers) _allRunning.Signal();
            _allRunning.Wait();
            // Started from a thread pool thread, so they go to this thread's local queue.
            for (var i = 0; i < 2; i++)
                _ = Task.Run(() => Interlocked.Increment(ref _audits));
            lock (_sync)
            {
                return _items;
            }
        }
    }
}

/// A thousand work items each wait for a price feed that never connects: eight run and block in
/// ManualResetEventSlim.Wait, and 992 wait in the queue behind them.
static class PoolBacklogScenario
{
    public const int Items = 1000;

    public static void Run()
    {
        ThreadPool.SetMinThreads(8, 8);
        ThreadPool.SetMaxThreads(8, 8);
        var feed = new PriceFeed();
        for (var i = 0; i < Items; i++)
            ThreadPool.QueueUserWorkItem(_ => feed.Publish());
        Ready.SignalLater(1500);
        Thread.Sleep(Timeout.Infinite);
    }

    private sealed class PriceFeed
    {
        private readonly ManualResetEventSlim _connected = new();

        public void Publish()
        {
            _connected.Wait();
            Console.WriteLine("published");
        }
    }
}
