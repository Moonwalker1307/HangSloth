namespace DeadlockLab.Code;

// A price catalog behind a ReaderWriterLockSlim: reports read it for a long time, a refresh
// upgrades to write, an update writes. Slow readers make the others wait, but nothing is
// deadlocked: once the reports finish, the writers go ahead.

public sealed class PriceCatalog
{
    private readonly ReaderWriterLockSlim _rw = new();
    private readonly Dictionary<string, decimal> _prices = new() { ["tea"] = 3.5m, ["coffee"] = 4.2m };
    private DateTime _refreshedAt = DateTime.MinValue;

    /// A report that renders while holding the read lock: slow, but it finishes.
    public decimal ExportReport(TimeSpan renderTime)
    {
        _rw.EnterReadLock();
        try
        {
            var total = _prices.Values.Sum();
            Thread.Sleep(renderTime); // rendering the PDF
            return total;
        }
        finally { _rw.ExitReadLock(); }
    }

    /// The upgradeable pattern: read, and upgrade to write only when the prices are stale.
    /// The upgrade has to wait until every report has left the read lock.
    public void RefreshIfStale()
    {
        _rw.EnterUpgradeableReadLock();
        try
        {
            if (DateTime.UtcNow - _refreshedAt < TimeSpan.FromMinutes(5)) return;
            _rw.EnterWriteLock();
            try
            {
                _prices["tea"] = 3.6m;
                _refreshedAt = DateTime.UtcNow;
            }
            finally { _rw.ExitWriteLock(); }
        }
        finally { _rw.ExitUpgradeableReadLock(); }
    }

    public void SetPrice(string item, decimal price)
    {
        _rw.EnterWriteLock();
        try { _prices[item] = price; }
        finally { _rw.ExitWriteLock(); }
    }
}

internal static class PriceCatalogScenario
{
    /// Long enough to take a dump, short enough to show that it ends.
    private static readonly TimeSpan RenderTime = TimeSpan.FromMinutes(2);

    public static void RunUpgrade()
    {
        var catalog = new PriceCatalog();
        using var reading = new CountdownEvent(1);
        new Thread(() => { reading.Signal(); catalog.ExportReport(RenderTime); }) { IsBackground = true, Name = "report-export" }.Start();
        reading.Wait();
        Thread.Sleep(200);
        new Thread(catalog.RefreshIfStale) { IsBackground = true, Name = "price-refresh" }.Start();
        Ready.SignalLater(1000);
        Ready.Forever();
    }

    public static void RunWriterBehindReaders()
    {
        var catalog = new PriceCatalog();
        using var reading = new CountdownEvent(3);
        for (var i = 1; i <= 3; i++)
            new Thread(() => { reading.Signal(); catalog.ExportReport(RenderTime); }) { IsBackground = true, Name = $"report-export-{i}" }.Start();
        reading.Wait();
        Thread.Sleep(200);
        new Thread(() => catalog.SetPrice("coffee", 4.4m)) { IsBackground = true, Name = "price-update" }.Start();
        Ready.SignalLater(1000);
        Ready.Forever();
    }
}
