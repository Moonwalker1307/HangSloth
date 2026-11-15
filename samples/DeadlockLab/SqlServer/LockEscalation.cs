using Microsoft.Data.SqlClient;

namespace DeadlockLab.SqlServer;

/// A month-end report reads a big range under REPEATABLE READ "so the totals add up", then renders
/// the PDF before committing. Holding more than ~5,000 row locks makes SQL Server escalate them to
/// one shared lock on the whole table, so every writer on that table waits, not just the ones
/// touching the reported rows.
public static class MonthEndReport
{
    public static void Run(ManualResetEventSlim read)
    {
        using var connection = Sql.Open();
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
        using var totals = new SqlCommand("SELECT customer, SUM(amount) FROM lab_orders WHERE id <= 20000 GROUP BY customer", connection, tx) { CommandTimeout = 0 };
        using (var reader = totals.ExecuteReader())
            while (reader.Read()) { }
        read.Set();
        Thread.Sleep(Timeout.Infinite); // "render the PDF", still inside the transaction
        tx.Commit();
    }

    public static void TakeOrder()
    {
        using var connection = Sql.Open();
        // Order 25000 isn't in the report's range, but the table lock blocks it anyway.
        Sql.Execute(connection, "UPDATE lab_orders SET amount = amount + 1 WHERE id = 25000");
    }
}

internal static class LockEscalationScenario
{
    public static void Run()
    {
        Sql.Reset("lab_orders", "id int primary key, customer int, amount money",
            "INSERT INTO lab_orders SELECT TOP 30000 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), ABS(CHECKSUM(NEWID())) % 100, 10 FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
        var read = new ManualResetEventSlim();
        new Thread(() => MonthEndReport.Run(read)) { Name = "month-end-report", IsBackground = true }.Start();
        read.Wait();
        new Thread(MonthEndReport.TakeOrder) { Name = "checkout", IsBackground = true }.Start();
        Ready.SignalLater(1500);
        Ready.Forever();
    }
}

/// Unlike PostgreSQL, SQL Server's default READ COMMITTED (without READ_COMMITTED_SNAPSHOT) makes a
/// plain SELECT wait for rows another transaction has changed but not committed.
public static class StockAdjustment
{
    public static void Adjust(ManualResetEventSlim updated)
    {
        using var connection = Sql.Open();
        using var tx = connection.BeginTransaction();
        Sql.Execute(connection, "UPDATE lab_stock SET quantity = quantity - 1 WHERE sku = 'A-1'", tx);
        updated.Set();
        Thread.Sleep(Timeout.Infinite); // waits for a warehouse scanner, transaction still open
        tx.Commit();
    }

    public static void ProductPage()
    {
        using var connection = Sql.Open();
        using var command = new SqlCommand("SELECT quantity FROM lab_stock WHERE sku = 'A-1'", connection) { CommandTimeout = 0 };
        command.ExecuteScalar(); // a read, blocked by the uncommitted update
    }
}

internal static class ReadBlockedScenario
{
    public static void Run()
    {
        Sql.Reset("lab_stock", "sku varchar(10) primary key, quantity int", "INSERT INTO lab_stock VALUES ('A-1', 10), ('B-2', 5);");
        var updated = new ManualResetEventSlim();
        new Thread(() => StockAdjustment.Adjust(updated)) { Name = "stock-adjustment", IsBackground = true }.Start();
        updated.Wait();
        new Thread(StockAdjustment.ProductPage) { Name = "product-page", IsBackground = true }.Start();
        Ready.SignalLater(1500);
        Ready.Forever();
    }
}
