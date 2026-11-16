namespace DeadlockLab.Code;

/// Counting and packing each take one of the depot's two locks, do their part and give it back through an extension
/// method before they take the other one. Then the month-end closing takes both locks and waits for a sign-off that
/// never comes. Counting and packing wait for the lock the other one used first, but neither holds anything: a release
/// through a method of another class is still a release.
public sealed class StockDepot
{
    private readonly SemaphoreSlim _countLock = new(1, 1);
    private readonly SemaphoreSlim _packLock = new(1, 1);
    private int _counted;
    private int _packed;

    public void CountThenPack(Action betweenSteps)
    {
        _countLock.Wait();
        try { _counted++; }
        finally { _countLock.ReleaseQuietly(); }
        betweenSteps();
        _packLock.Wait();
        try { _packed++; }
        finally { _packLock.ReleaseQuietly(); }
    }

    public void PackThenCount(Action betweenSteps)
    {
        _packLock.Wait();
        try { _packed++; }
        finally { _packLock.ReleaseQuietly(); }
        betweenSteps();
        _countLock.Wait();
        try { _counted++; }
        finally { _countLock.ReleaseQuietly(); }
    }

    public void CloseMonth()
    {
        _countLock.Wait();
        _packLock.Wait(); // kept until the closing is signed off
    }
}

public static class SemaphoreExtensions
{
    /// Release() that puts up with a semaphore that is already free.
    public static void ReleaseQuietly(this SemaphoreSlim semaphore)
    {
        try { semaphore.Release(); }
        catch (SemaphoreFullException) { }
    }
}

internal static class DepotLocksScenario
{
    public static void Run()
    {
        var depot = new StockDepot();
        using var firstStepDone = new CountdownEvent(2);
        using var monthClosed = new ManualResetEventSlim();
        void BetweenSteps()
        {
            firstStepDone.Signal();
            monthClosed.Wait();
        }
        new Thread(() => depot.CountThenPack(BetweenSteps)) { IsBackground = true, Name = "counting" }.Start();
        new Thread(() => depot.PackThenCount(BetweenSteps)) { IsBackground = true, Name = "packing" }.Start();
        firstStepDone.Wait();
        depot.CloseMonth();
        monthClosed.Set();
        Ready.SignalLater();
        Ready.Forever();
    }
}
