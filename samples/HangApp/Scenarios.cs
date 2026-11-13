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
