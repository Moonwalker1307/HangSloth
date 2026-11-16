namespace HangApp;

static class SyncOverAsyncScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var context = new SingleThreadSynchronizationContext();
        context.RunOnCurrentThread(() =>
        {
            var controller = new OrderController();
            Console.WriteLine(controller.Get(42));
        });
    }
}

static class LockDeadlockScenario
{
    private static readonly object InventoryLock = new InventoryGate();
    private static readonly object BillingLock = new BillingGate();

    public static void Run()
    {
        using var barrier = new Barrier(2);
        var a = new Thread(() => ReserveThenCharge(barrier)) { Name = "reserve-worker" };
        var b = new Thread(() => ChargeThenReserve(barrier)) { Name = "billing-worker" };
        a.Start();
        b.Start();
        Ready.SignalLater();
        a.Join();
        b.Join();
    }

    private static void ReserveThenCharge(Barrier barrier)
    {
        lock (InventoryLock)
        {
            barrier.SignalAndWait();
            lock (BillingLock)
            {
                Console.WriteLine("charged");
            }
        }
    }

    private static void ChargeThenReserve(Barrier barrier)
    {
        lock (BillingLock)
        {
            barrier.SignalAndWait();
            lock (InventoryLock)
            {
                Console.WriteLine("reserved");
            }
        }
    }

    private sealed class InventoryGate { }
    private sealed class BillingGate { }
}

static class LockContentionScenario
{
    private static readonly object CacheLock = new CacheGate();

    public static void Run()
    {
        var holder = new Thread(RebuildCache) { Name = "cache-rebuilder", IsBackground = true };
        holder.Start();
        Thread.Sleep(100);
        var readers = Enumerable.Range(0, 5)
            .Select(i => new Thread(ReadCache) { Name = $"reader-{i}", IsBackground = true })
            .ToList();
        readers.ForEach(t => t.Start());
        Ready.SignalLater();
        holder.Join();
    }

    private static void RebuildCache()
    {
        lock (CacheLock)
        {
            // Simulates a very slow call made while holding the lock.
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static void ReadCache()
    {
        lock (CacheLock)
        {
            Console.WriteLine("read");
        }
    }

    private sealed class CacheGate { }
}

static class StarvationScenario
{
    public static void Run()
    {
        ThreadPool.SetMinThreads(4, 4);
        ThreadPool.SetMaxThreads(8, 8);
        var controller = new OrderController();
        for (var i = 0; i < 64; i++)
        {
            var id = i;
            ThreadPool.QueueUserWorkItem(_ => controller.GetPrice(id));
        }
        Ready.SignalLater(1500);
        Thread.Sleep(Timeout.Infinite);
    }
}

static class NeverCompletesScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var importer = new Importer();
        importer.ImportAsync().Wait();
    }

    private sealed class Importer
    {
        private readonly TaskCompletionSource<bool> _ack = new();

        public async Task ImportAsync()
        {
            await Task.Delay(10);
            // Waits for an acknowledgement that nothing ever sends.
            await _ack.Task;
        }
    }
}

static class HealthyScenario
{
    public static void Run()
    {
        Ready.SignalLater(300);
        Thread.Sleep(Timeout.Infinite);
    }
}

static class LockTypeDeadlockScenario
{
    private static readonly Lock Accounts = new();
    private static readonly Lock Ledger = new();

    public static void Run()
    {
        using var barrier = new Barrier(2);
        var a = new Thread(() => Transfer(barrier)) { Name = "transfer" };
        var b = new Thread(() => Audit(barrier)) { Name = "audit" };
        a.Start();
        b.Start();
        Ready.SignalLater();
        a.Join();
    }

    private static void Transfer(Barrier barrier)
    {
        lock (Accounts)
        {
            barrier.SignalAndWait();
            lock (Ledger) { Console.WriteLine("transferred"); }
        }
    }

    private static void Audit(Barrier barrier)
    {
        lock (Ledger)
        {
            barrier.SignalAndWait();
            lock (Accounts) { Console.WriteLine("audited"); }
        }
    }
}

static class TieredStarvationScenario
{
    /// 200 pool threads blocked on the same .Result: some still in tier-0 code, the rest in optimized
    /// tier-1 code where Task<T>.Result is inlined into the app's method.
    public static void Run()
    {
        ThreadPool.SetMinThreads(220, 220);
        var directory = new CustomerDirectory();
        var check = new InvoiceCheck(directory);
        var ids = Enumerable.Range(1, 1).ToArray();

        using (var jit = new OptimizedCodeListener())
        {
            directory.Upstream = true;
            for (var i = 0; i < 60; i++) _ = Task.Run(() => check.CheckAll(ids));
            Thread.Sleep(300);

            // Keep calling it, with time in between, until the runtime says it has recompiled Validate at
            // tier 1. How long that takes depends on the machine (about ten times longer on a single CPU than
            // on four), so a fixed number of rounds isn't enough.
            directory.Upstream = false;
            var warmUp = System.Diagnostics.Stopwatch.StartNew();
            while (!jit.ValidateIsOptimized)
            {
                if (warmUp.Elapsed > TimeSpan.FromSeconds(20))
                {
                    Console.Error.WriteLine("The runtime did not recompile InvoiceCheck.Validate with optimizations within 20 seconds, so this mode cannot show what it is meant to show.");
                    Environment.Exit(3);
                }
                CallOften();
            }
            CallOften(); // The event is sent just before the new code is switched in.
        }

        directory.Upstream = true;
        for (var i = 0; i < 140; i++) _ = Task.Run(() => check.CheckAll(ids));
        Ready.SignalLater(1500);
        Thread.Sleep(Timeout.Infinite);

        void CallOften()
        {
            for (var i = 0; i < 2000; i++) check.CheckAll(ids);
            Thread.Sleep(50);
        }
    }
}
