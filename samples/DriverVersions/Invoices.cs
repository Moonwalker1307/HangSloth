using Npgsql;

namespace DriverVersions;

/// An invoice run that opens a second connection for its log while it still holds the first one, so a
/// pool of 3 is used up by three runs that each wait for a fourth connection. Also a plain row lock.
public static class InvoiceRun
{
    /// A small pool and no timeout make the hang permanent.
    internal const string PoolSettings = "Maximum Pool Size=3;Timeout=0";

    public static void Issue(int invoiceId)
    {
        using var connection = Pg.Open(PoolSettings);
        Pg.Execute(connection, $"UPDATE compat_invoices SET status = 'issued' WHERE id = {invoiceId}");
        Thread.Sleep(300); // "rendering the invoice"
        InvoiceLog.Write($"invoice {invoiceId} issued");
    }

    public static async Task IssueAsync(int invoiceId)
    {
        await using var connection = new NpgsqlConnection(Pg.ConnectionString(PoolSettings));
        await connection.OpenAsync();
        await using (var update = new NpgsqlCommand($"UPDATE compat_async_invoices SET status = 'issued' WHERE id = {invoiceId}", connection))
            await update.ExecuteNonQueryAsync();
        await Task.Delay(300);
        await InvoiceLog.WriteAsync($"invoice {invoiceId} issued");
    }
}

public static class InvoiceLog
{
    /// Opens its own connection: called while the caller holds one, each run needs two pool slots.
    public static void Write(string message)
    {
        using var connection = Pg.Open(InvoiceRun.PoolSettings);
        Pg.Execute(connection, $"INSERT INTO compat_invoice_log VALUES ('{message}')");
    }

    public static async Task WriteAsync(string message)
    {
        await using var connection = new NpgsqlConnection(Pg.ConnectionString(InvoiceRun.PoolSettings));
        await connection.OpenAsync(); // BUG: a second connection while the caller holds the first
        await using var insert = new NpgsqlCommand($"INSERT INTO compat_async_invoice_log VALUES ('{message}')", connection);
        await insert.ExecuteNonQueryAsync();
    }
}

public static class InvoiceApproval
{
    /// Locks the invoice row and waits for an approval that never comes, inside the transaction.
    public static void Hold(ManualResetEventSlim locked)
    {
        using var connection = Pg.Open();
        using var tx = connection.BeginTransaction();
        Pg.Execute(connection, "UPDATE compat_locked_invoices SET total = total + 1 WHERE id = 1", tx);
        locked.Set();
        Thread.Sleep(Timeout.Infinite);
        tx.Commit();
    }

    public static void Correct()
    {
        using var connection = Pg.Open();
        Pg.Execute(connection, "UPDATE compat_locked_invoices SET total = total - 1 WHERE id = 1");
    }
}

internal static class InvoiceScenarios
{
    public static void PoolExhaustion()
    {
        Pg.Reset("compat_invoices", "id int primary key, status text", string.Join(",", Enumerable.Range(1, 6).Select(i => $"({i}, 'new')")));
        Pg.Reset("compat_invoice_log", "message text", "('start')");
        for (var i = 1; i <= 6; i++)
        {
            var id = i;
            new Thread(() => InvoiceRun.Issue(id)) { Name = $"invoice-{id}", IsBackground = true }.Start();
        }
        Ready.After(1500);
    }

    public static void AsyncPoolExhaustion()
    {
        Pg.Reset("compat_async_invoices", "id int primary key, status text", string.Join(",", Enumerable.Range(1, 6).Select(i => $"({i}, 'new')")));
        Pg.Reset("compat_async_invoice_log", "message text", "('start')");
        var runs = Enumerable.Range(1, 6).Select(InvoiceRun.IssueAsync).ToArray();
        new Thread(() => Task.WaitAll(runs)) { Name = "invoice-batch", IsBackground = true }.Start();
        Ready.After(1500);
    }

    public static void RowLock()
    {
        Pg.Reset("compat_locked_invoices", "id int primary key, total numeric", "(1, 100)");
        var locked = new ManualResetEventSlim();
        new Thread(() => InvoiceApproval.Hold(locked)) { Name = "approval", IsBackground = true }.Start();
        locked.Wait();
        new Thread(InvoiceApproval.Correct) { Name = "correction", IsBackground = true }.Start();
        Ready.After(1000);
    }
}

internal static class Pg
{
    public static string ConnectionString(string? extra = null)
    {
        var cs = Environment.GetEnvironmentVariable("DEADLOCKLAB_PG")
                 ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
        var builder = new NpgsqlConnectionStringBuilder(cs) { ApplicationName = "DriverVersions" };
        foreach (var part in (extra ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            builder[kv[0].Trim()] = kv[1].Trim();
        }
        return builder.ConnectionString;
    }

    public static NpgsqlConnection Open(string? extra = null)
    {
        var connection = new NpgsqlConnection(ConnectionString(extra));
        connection.Open();
        return connection;
    }

    public static int Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? tx = null)
    {
        using var command = new NpgsqlCommand(sql, connection, tx);
        return command.ExecuteNonQuery();
    }

    /// Recreate a mode's table. Each mode has its own table, so modes can run side by side.
    public static void Reset(string table, string columns, string rows)
    {
        using var c = Open();
        Execute(c, $"DROP TABLE IF EXISTS {table}; CREATE TABLE {table} ({columns}); INSERT INTO {table} VALUES {rows};");
    }
}
