using Npgsql;

namespace DeadlockLab.Database;

/// The async version of the nested-connection pool deadlock: nobody blocks a thread, every
/// request is "just awaiting", and still nothing ever finishes.
public static class AsyncCheckoutHandler
{
    private const string PoolSettings = "Maximum Pool Size=3;Timeout=0";

    public static async Task HandleAsync(int orderId)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString(PoolSettings));
        await connection.OpenAsync();
        await using (var update = new NpgsqlCommand($"UPDATE lab_async_orders SET status = 'paid' WHERE id = {orderId}", connection))
            await update.ExecuteNonQueryAsync();
        await Task.Delay(300);
        await WriteAuditAsync(orderId);
    }

    private static async Task WriteAuditAsync(int orderId)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString(PoolSettings));
        await connection.OpenAsync(); // BUG: a second connection while the caller holds the first
        await using var insert = new NpgsqlCommand($"INSERT INTO lab_async_audit VALUES ('order {orderId}')", connection);
        await insert.ExecuteNonQueryAsync();
    }
}

internal static class AsyncPoolExhaustionScenario
{
    public static void Run()
    {
        Db.Reset("lab_async_orders", "id int primary key, status text", string.Join(",", Enumerable.Range(1, 6).Select(i => $"({i}, 'new')")));
        Db.Reset("lab_async_audit", "message text", "('start')");
        var requests = Enumerable.Range(1, 6).Select(AsyncCheckoutHandler.HandleAsync).ToArray();
        Ready.SignalLater(1500);
        Task.WaitAll(requests);
    }
}
