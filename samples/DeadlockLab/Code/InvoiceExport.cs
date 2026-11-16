namespace DeadlockLab.Code;

/// Invoice exports take a SemaphoreSlim(1, 1) so only one runs at a time. An export with nothing to send returns
/// early, before the Release() at the end, so the lock is never given back and every later export waits for it.
public sealed class InvoiceExport
{
    private readonly SemaphoreSlim _exportLock = new(1, 1);
    private readonly List<string> _invoices = [];

    public void Queue(string invoice)
    {
        lock (_invoices) _invoices.Add(invoice);
    }

    public void Run(string batch)
    {
        _exportLock.Wait();
        string[] pending;
        lock (_invoices) pending = [.. _invoices];
        if (pending.Length == 0) return; // BUG: returns without Release()
        Console.WriteLine($"{batch}: exported {pending.Length} invoices");
        _exportLock.Release();
    }
}

internal static class InvoiceExportScenario
{
    public static void Run()
    {
        var export = new InvoiceExport();
        export.Run("nightly"); // nothing queued yet: takes the lock and keeps it
        export.Queue("INV-1001");
        for (var i = 1; i <= 2; i++)
        {
            var batch = $"hourly-{i}";
            new Thread(() => export.Run(batch)) { IsBackground = true, Name = $"export-{i}" }.Start();
        }
        Ready.SignalLater();
        Ready.Forever();
    }
}
