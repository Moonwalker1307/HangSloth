using Npgsql;

namespace DeadlockLab.Database;

/// An "ASP.NET Core" controller that blocks on async database calls. Each request holds a
/// thread pool thread while it waits, so the pool runs dry long before the database does.
public sealed class ReportsController
{
    public long GetCount()
    {
        return CountAsync().Result; // BUG: sync-over-async on a thread pool thread
    }

    private static async Task<long> CountAsync()
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM lab_reports, pg_sleep(30)", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

internal static class DbStarvationScenario
{
    public static void Run()
    {
        Db.Reset("lab_reports", "id int primary key", "(1)");
        ThreadPool.SetMinThreads(4, 4);
        ThreadPool.SetMaxThreads(8, 8);
        var controller = new ReportsController();
        for (var i = 0; i < 40; i++)
            ThreadPool.QueueUserWorkItem(_ => controller.GetCount());
        Ready.SignalLater(2500);
        Ready.Forever();
    }
}
