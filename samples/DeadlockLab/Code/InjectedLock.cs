namespace DeadlockLab.Code;

/// A lock class that is handed its SemaphoreSlim, as a container or a factory does it. The code that creates the
/// semaphore isn't in the class, so the dump doesn't show how many slots it was made with.
public sealed class SharedGate(SemaphoreSlim slot)
{
    public void Enter() => slot.Wait();

    public void Exit() => slot.Release();
}

/// Billing posts an invoice under the ledger gate, then takes the journal gate; the month-end close takes them the
/// other way round.
public sealed class Billing(SharedGate ledger, SharedGate journal)
{
    public void Post(Barrier bothHoldTheirFirst)
    {
        ledger.Enter();
        try
        {
            bothHoldTheirFirst.SignalAndWait();
            journal.Enter();
            try { Console.WriteLine("invoice posted"); } finally { journal.Exit(); }
        }
        finally { ledger.Exit(); }
    }

    public void Close(Barrier bothHoldTheirFirst)
    {
        journal.Enter();
        try
        {
            bothHoldTheirFirst.SignalAndWait();
            ledger.Enter();
            try { Console.WriteLine("month closed"); } finally { ledger.Exit(); }
        }
        finally { journal.Exit(); }
    }
}

internal static class InjectedLockScenario
{
    public static void Run()
    {
        var billing = new Billing(new SharedGate(new SemaphoreSlim(1, 1)), new SharedGate(new SemaphoreSlim(1, 1)));
        using var barrier = new Barrier(2);
        new Thread(() => billing.Post(barrier)) { Name = "post-invoice" }.Start();
        new Thread(() => billing.Close(barrier)) { Name = "close-month" }.Start();
        Ready.SignalLater();
        Ready.Forever();
    }
}
