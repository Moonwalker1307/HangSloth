namespace DeadlockLab.Code;

/// A System.Threading.Timer fires every 100 ms, but its callback takes seconds (it holds a lock
/// around a slow "upload"). Timers don't wait for the previous callback, so callbacks pile up
/// behind the lock, one thread pool thread each, until the pool is full of them.
public sealed class InventorySync
{
    private readonly object _syncLock = new();
    private Timer? _timer;

    public void Start() => _timer = new Timer(_ => SyncNow(), null, 0, 100); // BUG: callback runs longer than the period

    private void SyncNow()
    {
        lock (_syncLock)
        {
            Thread.Sleep(30_000); // the "upload"
        }
    }
}

internal static class TimerOverlapScenario
{
    public static void Run()
    {
        ThreadPool.SetMinThreads(4, 4);
        ThreadPool.SetMaxThreads(8, 8);
        new InventorySync().Start();
        Ready.SignalLater(2500);
        Ready.Forever();
    }
}
