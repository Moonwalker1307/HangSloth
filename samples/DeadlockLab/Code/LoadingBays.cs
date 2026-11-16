namespace DeadlockLab.Code;

/// Loading bays move pallets into the two dispatch bays, which are closed for a stocktake. A move docks its own bay
/// (the gate stays taken until Undock()) and then waits for the dispatch bay's gate. Nobody is in a circle: four moves
/// queue behind two stocktakes. After its own take the code never uses `this` again, so in optimized code the frame of
/// the move keeps only the dispatch bay alive, whose gate is not the one the move took. Two of the moves name the
/// dispatch bay by its own class, which is derived from this one: nothing in the method's own class says that such a
/// parameter can hold a bay.
public class LoadingBay(string name)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pallets;

    public string Name => name;

    public int Pallets => _pallets;

    public void CloseForStocktake() => _gate.Wait();

    public void DockAndMoveTo(LoadingBay to)
    {
        _gate.Wait();
        to._gate.Wait();
        try { to._pallets++; }
        finally { to._gate.Release(); }
    }

    public void DockAndDispatchTo(DispatchBay to)
    {
        _gate.Wait();
        to._gate.Wait();
        try { to._pallets++; }
        finally { to._gate.Release(); }
    }

    public void Undock() => _gate.Release();
}

/// A bay that goods leave from.
public sealed class DispatchBay(string name) : LoadingBay(name);

internal static class LoadingBaysScenario
{
    public static void Run()
    {
        var north = new DispatchBay("dispatch north");
        var south = new DispatchBay("dispatch south");
        north.CloseForStocktake();
        south.CloseForStocktake();
        for (var i = 1; i <= 4; i++)
        {
            var bay = new LoadingBay($"bay {i}");
            ThreadStart move = i <= 2 ? () => bay.DockAndMoveTo(north) : () => bay.DockAndDispatchTo(south);
            new Thread(move) { IsBackground = true, Name = $"move-{i}" }.Start();
        }
        Ready.SignalLater();
        Ready.Forever();
    }
}
