using Npgsql;

namespace DeadlockLab.Database;

public static class LedgerService
{
    /// Debits one account and credits the other, in that order. Two opposite transfers at
    /// once are a textbook database deadlock: PostgreSQL detects it after deadlock_timeout
    /// (1 s by default) and aborts one of them. This code retries the victim the same way.
    public static void Transfer(int from, int to, Barrier bothDebited)
    {
        while (true)
        {
            using var connection = Db.Open();
            using var tx = connection.BeginTransaction();
            try
            {
                Db.Execute(connection, $"UPDATE lab_ledger SET balance = balance - 1 WHERE id = {from}", tx);
                bothDebited.SignalAndWait();
                Db.Execute(connection, $"UPDATE lab_ledger SET balance = balance + 1 WHERE id = {to}", tx);
                tx.Commit();
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                // "Just retry" -- with the same lock order, it deadlocks again.
            }
        }
    }
}

internal static class RetryingDeadlockScenario
{
    public static void Run()
    {
        Db.Reset("lab_ledger", "id int primary key, balance numeric", "(1, 1000), (2, 1000)");
        var barrier = new Barrier(2);
        new Thread(() => LedgerService.Transfer(1, 2, barrier)) { Name = "transfer 1->2", IsBackground = true }.Start();
        new Thread(() => LedgerService.Transfer(2, 1, barrier)) { Name = "transfer 2->1", IsBackground = true }.Start();
        Ready.SignalLater(1300);
        Ready.Forever();
    }
}
