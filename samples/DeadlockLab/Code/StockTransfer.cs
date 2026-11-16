namespace DeadlockLab.Code;

/// Two warehouses move stock to each other at the same time. A move takes its own warehouse's SemaphoreSlim(1, 1), then
/// the other warehouse's: lock ordering by object, the way `transfer` does it with lock, but with two objects of one
/// class, so the frame of MoveTo keeps two Warehouses alive and only the wait in progress tells which one is `this`.
public sealed class Warehouse(string name)
{
    public readonly SemaphoreSlim Lock = new(1, 1);
    private int _units = 100;

    public string Name => name;

    public void MoveTo(Warehouse other, int units, Action bothHoldTheirOwn)
    {
        Lock.Wait();
        try
        {
            bothHoldTheirOwn();
            other.Lock.Wait();
            try
            {
                _units -= units;
                other._units += units;
            }
            finally { other.Lock.Release(); }
        }
        finally { Lock.Release(); }
    }
}

internal static class StockTransferScenario
{
    public static void Run()
    {
        var north = new Warehouse("north");
        var south = new Warehouse("south");
        using var bothHoldTheirOwn = new Barrier(2);
        new Thread(() => north.MoveTo(south, 10, () => bothHoldTheirOwn.SignalAndWait())) { Name = "move north->south" }.Start();
        new Thread(() => south.MoveTo(north, 5, () => bothHoldTheirOwn.SignalAndWait())) { Name = "move south->north" }.Start();
        Ready.SignalLater();
        Ready.Forever();
    }
}
