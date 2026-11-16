using Microsoft.Data.SqlClient;

namespace DeadlockLab.SqlServer;

/// The nested-connection pool deadlock with SQL Server: each shipment request keeps its connection open
/// and its audit helper opens a second one. With more requests than pool slots, every request waits for
/// a connection that only another waiting request can return.
public static class ShipmentHandler
{
    // A small pool and Connect Timeout=0 (wait for a pooled connection forever) make the hang permanent;
    // with the defaults (100 connections, 15 s) the same bug shows up as bursts of timeouts under load.
    internal static string ConnectionString =>
        new SqlConnectionStringBuilder(Sql.ConnectionString) { MaxPoolSize = 4, ConnectTimeout = 0 }.ConnectionString;

    public static void Ship(int shipmentId)
    {
        using var connection = Open();
        Sql.Execute(connection, $"UPDATE lab_shipments SET status = 'shipped' WHERE id = {shipmentId}");
        Thread.Sleep(300); // "printing the label"
        ShipmentAudit.Write($"shipment {shipmentId} shipped");
    }

    internal static SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }
}

public static class ShipmentAudit
{
    /// Written as a self-contained helper: opens its own connection. Called while the caller still
    /// holds one, it needs two pool slots per request.
    public static void Write(string message)
    {
        using var connection = ShipmentHandler.Open();
        Sql.Execute(connection, $"INSERT INTO lab_shipment_audit VALUES ('{message}')");
    }
}

internal static class SqlPoolExhaustionScenario
{
    public static void Run()
    {
        Sql.Reset("lab_shipments", "id int primary key, status nvarchar(20)",
            "INSERT INTO lab_shipments VALUES " + string.Join(",", Enumerable.Range(1, 8).Select(i => $"({i}, 'new')")) + ";");
        Sql.Reset("lab_shipment_audit", "message nvarchar(100)", "");
        SqlConnection.ClearAllPools();
        for (var i = 1; i <= 8; i++)
        {
            var id = i;
            new Thread(() => ShipmentHandler.Ship(id)) { Name = $"shipment-{id}", IsBackground = true }.Start();
        }
        Ready.SignalLater(1500);
        Ready.Forever();
    }
}
