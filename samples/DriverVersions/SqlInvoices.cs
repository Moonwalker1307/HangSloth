using Microsoft.Data.SqlClient;

namespace DriverVersions;

/// The nested-connection pool deadlock with Microsoft.Data.SqlClient: Max Pool Size=3 and three invoice
/// runs that each hold a connection while their log opens another one.
public static class SqlInvoiceRun
{
    /// Connect Timeout=0 waits for a pooled connection forever.
    private static string ConnectionString =>
        new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DEADLOCKLAB_SQLSERVER")
            ?? "Server=localhost;User Id=sa;Password=Lab_Passw0rd!;TrustServerCertificate=true")
        { ApplicationName = "DriverVersions", MaxPoolSize = 3, ConnectTimeout = 0 }.ConnectionString;

    public static void Issue(int invoiceId)
    {
        using var connection = Open();
        Execute(connection, $"UPDATE compat_invoices SET status = 'issued' WHERE id = {invoiceId}");
        Thread.Sleep(300);
        WriteLog($"invoice {invoiceId} issued");
    }

    /// Opens its own connection while the caller still holds one.
    public static void WriteLog(string message)
    {
        using var connection = Open();
        Execute(connection, $"INSERT INTO compat_invoice_log VALUES ('{message}')");
    }

    internal static SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    internal static void Execute(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.ExecuteNonQuery();
    }
}

internal static class SqlInvoiceScenarios
{
    public static void PoolExhaustion()
    {
        using (var c = SqlInvoiceRun.Open())
            SqlInvoiceRun.Execute(c, "DROP TABLE IF EXISTS compat_invoices; CREATE TABLE compat_invoices (id int primary key, status nvarchar(20));"
                + "INSERT INTO compat_invoices VALUES " + string.Join(",", Enumerable.Range(1, 6).Select(i => $"({i}, 'new')")) + ";"
                + "DROP TABLE IF EXISTS compat_invoice_log; CREATE TABLE compat_invoice_log (message nvarchar(100));");
        SqlConnection.ClearAllPools();
        for (var i = 1; i <= 6; i++)
        {
            var id = i;
            new Thread(() => SqlInvoiceRun.Issue(id)) { Name = $"invoice-{id}", IsBackground = true }.Start();
        }
        Ready.After(1500);
    }
}
