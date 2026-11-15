using Npgsql;

namespace DeadlockLab.Database;

public static class CheckoutHandler
{
    // A small pool and no timeout make the hang permanent; with the defaults (100
    // connections, 15 s timeout) the same bug shows up as bursts of timeouts under load.
    internal const string PoolSettings = "Maximum Pool Size=4;Timeout=0";

    public static void Handle(int orderId)
    {
        using var connection = Db.Open(PoolSettings);
        Db.Execute(connection, $"UPDATE lab_orders SET status = 'paid' WHERE id = {orderId}");
        Thread.Sleep(300); // "calling the payment provider"
        AuditTrail.Write($"order {orderId} paid");
    }
}

public static class AuditTrail
{
    /// Written as a self-contained helper: opens its own connection. Called while the
    /// caller still holds one, it needs two pool slots per request.
    public static void Write(string message)
    {
        using var connection = Db.Open(CheckoutHandler.PoolSettings);
        Db.Execute(connection, $"INSERT INTO lab_audit VALUES ('{message}')");
    }
}

internal static class PoolExhaustionScenario
{
    public static void Run()
    {
        Db.Reset("lab_orders", "id int primary key, status text", string.Join(",", Enumerable.Range(1, 8).Select(i => $"({i}, 'new')")));
        Db.Reset("lab_audit", "message text", "('start')");
        NpgsqlConnection.ClearAllPools();
        for (var i = 1; i <= 8; i++)
        {
            var id = i;
            new Thread(() => CheckoutHandler.Handle(id)) { Name = $"request-{id}", IsBackground = true }.Start();
        }
        Ready.SignalLater(1500);
        Ready.Forever();
    }
}
