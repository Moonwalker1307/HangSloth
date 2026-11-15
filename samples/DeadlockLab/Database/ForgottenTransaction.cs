using Npgsql;

namespace DeadlockLab.Database;

public sealed class RefundService
{
    /// Takes a row lock, then waits for a human approval before committing. If the
    /// approval never comes, the row stays locked for as long as the transaction is open.
    public static void ChargeWithApproval(ManualResetEventSlim approval, ManualResetEventSlim rowLocked)
    {
        using var connection = Db.Open();
        using var tx = connection.BeginTransaction();
        Db.Execute(connection, "UPDATE lab_wallets SET balance = balance - 10 WHERE id = 1", tx);
        rowLocked.Set();
        approval.Wait(); // BUG: waiting for something slow inside an open transaction
        tx.Commit();
    }

    public static void Refund()
    {
        using var connection = Db.Open();
        Db.Execute(connection, "UPDATE lab_wallets SET balance = balance + 5 WHERE id = 1");
    }
}

internal static class ForgottenTransactionScenario
{
    public static void Run()
    {
        Db.Reset("lab_wallets", "id int primary key, balance numeric", "(1, 100), (2, 100)");
        var approval = new ManualResetEventSlim();
        var rowLocked = new ManualResetEventSlim();
        new Thread(() => RefundService.ChargeWithApproval(approval, rowLocked)) { Name = "charge" }.Start();
        rowLocked.Wait();
        new Thread(RefundService.Refund) { Name = "refund" }.Start();
        Ready.SignalLater(1000);
        Ready.Forever();
    }
}

public sealed class AsyncRefundService
{
    public static async Task RefundAsync()
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE lab_async_wallets SET balance = balance + 5 WHERE id = 1", connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal static class AsyncRowLockScenario
{
    public static void Run()
    {
        Db.Reset("lab_async_wallets", "id int primary key, balance numeric", "(1, 100)");
        var rowLocked = new ManualResetEventSlim();
        new Thread(() =>
        {
            using var connection = Db.Open();
            using var tx = connection.BeginTransaction();
            Db.Execute(connection, "UPDATE lab_async_wallets SET balance = 0 WHERE id = 1", tx);
            rowLocked.Set();
            Thread.Sleep(Timeout.Infinite); // a long "report" running inside the transaction
        }) { Name = "month-end-close", IsBackground = true }.Start();
        rowLocked.Wait();
        Ready.SignalLater(1000);
        AsyncRefundService.RefundAsync().GetAwaiter().GetResult();
    }
}
