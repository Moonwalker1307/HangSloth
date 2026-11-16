namespace DeadlockLab.Code;

/// A small lock class around a SemaphoreSlim(1, 1), as many code bases have one. Enter() and Exit() are so small that
/// optimized code inlines them into the caller, and the frame of the code that waits then shows no Wait() at all.
public sealed class SlotLock
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    public void Enter() => _slot.Wait();

    public void Exit() => _slot.Release();
}

/// The dispatch desk's two locks. Picking takes the stock, then the label printer; packing takes them the other way round.
public static class DispatchDesk
{
    internal static readonly SlotLock Stock = new();
    internal static readonly SlotLock LabelPrinter = new();

    public static void Pick(Barrier bothHoldTheirFirst)
    {
        Stock.Enter();
        try
        {
            bothHoldTheirFirst.SignalAndWait();
            LabelPrinter.Enter();
            try { Console.WriteLine("pick list printed"); } finally { LabelPrinter.Exit(); }
        }
        finally { Stock.Exit(); }
    }

    public static void Pack(Barrier bothHoldTheirFirst)
    {
        LabelPrinter.Enter();
        try
        {
            bothHoldTheirFirst.SignalAndWait();
            Stock.Enter();
            try { Console.WriteLine("stock booked back"); } finally { Stock.Exit(); }
        }
        finally { LabelPrinter.Exit(); }
    }
}

internal static class LockClassScenario
{
    public static void Run()
    {
        using var barrier = new Barrier(2);
        new Thread(() => DispatchDesk.Pick(barrier)) { Name = "picking" }.Start();
        new Thread(() => DispatchDesk.Pack(barrier)) { Name = "packing" }.Start();
        Ready.SignalLater();
        Ready.Forever();
    }
}
