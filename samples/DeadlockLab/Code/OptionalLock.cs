namespace DeadlockLab.Code;

/// An order store with a table lock that only an exclusive save takes, and a row lock that every save takes. A rebuild
/// takes the row lock, then the table lock. A truncate that found nothing to do returned without releasing the table
/// lock, so the rebuild waits for it forever, and a plain save waits for the rebuild's row lock. Nobody is in a circle:
/// the plain save took no table lock, because its `if (exclusive)` didn't run.
public sealed class OrderStore
{
    private readonly SemaphoreSlim _tableLock = new(1, 1);
    private readonly SemaphoreSlim _rowLock = new(1, 1);
    private int _rows;

    public void Truncate()
    {
        _tableLock.Wait();
        if (_rows == 0) return; // the bug: nothing to truncate, and the table lock is never released
        _rows = 0;
        _tableLock.Release();
    }

    public void Save(bool exclusive)
    {
        if (exclusive) _tableLock.Wait();
        try
        {
            _rowLock.Wait();
            try { _rows++; } finally { _rowLock.Release(); }
        }
        finally
        {
            if (exclusive) _tableLock.Release();
        }
    }

    public void Rebuild(ManualResetEventSlim holdsTheRows)
    {
        _rowLock.Wait();
        try
        {
            holdsTheRows.Set();
            _tableLock.Wait();
            try { _rows = 0; } finally { _tableLock.Release(); }
        }
        finally { _rowLock.Release(); }
    }
}

internal static class OptionalLockScenario
{
    public static void Run()
    {
        var store = new OrderStore();
        store.Truncate();
        using var holdsTheRows = new ManualResetEventSlim();
        new Thread(() => store.Rebuild(holdsTheRows)) { Name = "rebuild" }.Start();
        holdsTheRows.Wait();
        new Thread(() => store.Save(exclusive: false)) { Name = "save" }.Start();
        Ready.SignalLater();
        Ready.Forever();
    }
}
