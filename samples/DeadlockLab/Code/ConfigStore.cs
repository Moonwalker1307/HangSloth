namespace DeadlockLab.Code;

// ReaderWriterLockSlim on one side, a plain lock on the other. Each class only takes
// its own lock and calls the other; the cycle is between the two classes.

public sealed class ConfigStore
{
    private readonly ReaderWriterLockSlim _rw = new();
    private readonly Dictionary<string, string> _values = new() { ["currency"] = "EUR" };
    internal AuditLog? Audit;

    public string Get(string key)
    {
        _rw.EnterReadLock();
        try { return _values[key]; }
        finally { _rw.ExitReadLock(); }
    }

    public void Set(string key, string value)
    {
        _rw.EnterWriteLock();
        try
        {
            _values[key] = value;
            Audit?.Record($"{key} changed");
        }
        finally { _rw.ExitWriteLock(); }
    }
}

public sealed class AuditLog
{
    private readonly object _sync = new();
    private readonly List<string> _entries = [];
    internal ConfigStore? Config;
    internal Action? WhileLocked;

    public void Record(string what)
    {
        lock (_sync)
        {
            WhileLocked?.Invoke();
            // Stamp each entry with the configured currency, read under the audit lock.
            _entries.Add($"[{Config?.Get("currency")}] {what}");
        }
    }
}

internal static class ConfigStoreScenario
{
    public static void Run()
    {
        var config = new ConfigStore();
        var audit = new AuditLog { Config = config };
        config.Audit = audit;

        using var auditLocked = new ManualResetEventSlim();
        using var writing = new ManualResetEventSlim();
        audit.WhileLocked = () =>
        {
            if (Thread.CurrentThread.Name != "audit-flusher") return;
            auditLocked.Set();
            writing.Wait();
            Thread.Sleep(200);
        };

        new Thread(() => audit.Record("nightly flush")) { Name = "audit-flusher" }.Start();
        new Thread(() =>
        {
            auditLocked.Wait();
            writing.Set();
            config.Set("currency", "SEK");
        }) { Name = "admin-request" }.Start();

        Ready.SignalLater(1000);
        Ready.Forever();
    }
}
