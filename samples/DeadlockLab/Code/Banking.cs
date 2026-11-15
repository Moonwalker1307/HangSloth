namespace DeadlockLab.Code;

public sealed class Account(string id, decimal balance)
{
    public string Id { get; } = id;
    public decimal Balance { get; private set; } = balance;

    // Each account guards its own balance.
    internal readonly object Sync = new();

    internal void Add(decimal amount) => Balance += amount;
}

public static class Bank
{
    /// Locks the source, then the destination. Fine on its own; deadlocks as soon as
    /// someone transfers the other way at the same time.
    public static void Transfer(Account from, Account to, decimal amount, Action? whileHoldingSource = null)
    {
        lock (from.Sync)
        {
            whileHoldingSource?.Invoke();
            lock (to.Sync)
            {
                from.Add(-amount);
                to.Add(amount);
            }
        }
    }
}

internal static class Banking
{
    public static void Run()
    {
        var alice = new Account("alice", 100);
        var bob = new Account("bob", 100);
        using var bothHoldTheirFirstLock = new Barrier(2);

        new Thread(() => Bank.Transfer(alice, bob, 10, () => bothHoldTheirFirstLock.SignalAndWait())) { Name = "transfer alice->bob" }.Start();
        new Thread(() => Bank.Transfer(bob, alice, 5, () => bothHoldTheirFirstLock.SignalAndWait())) { Name = "transfer bob->alice" }.Start();

        Ready.SignalLater();
        Ready.Forever();
    }
}
