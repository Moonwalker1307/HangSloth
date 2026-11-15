using Npgsql;

namespace DeadlockLab.Database;

/// A nightly export streams a big table and writes each row to a slow sink. Its SELECT holds a
/// light table lock (ACCESS SHARE) for as long as the reader is open. A deploy then runs
/// ALTER TABLE, which needs ACCESS EXCLUSIVE and queues behind the export, and every query that
/// arrives after the ALTER queues behind the ALTER. One slow read plus one migration freezes the table.
public static class EventExport
{
    public static void Export()
    {
        using var connection = Db.Open();
        using var command = new NpgsqlCommand("SELECT id, kind, payload FROM lab_events ORDER BY id", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            Thread.Sleep(50); // "upload the row": slow, and the reader (and its lock) stays open the whole time
    }
}

public static class EventMigration
{
    public static void AddColumn()
    {
        using var connection = Db.Open();
        Db.Execute(connection, "ALTER TABLE lab_events ADD COLUMN source text");
    }

    public static void Dashboard()
    {
        using var connection = Db.Open();
        Db.Execute(connection, "SELECT count(*) FROM lab_events WHERE kind = 'click'");
    }
}

internal static class SelectBlocksAlterScenario
{
    public static void Run()
    {
        Db.Reset("lab_events", "id int primary key, kind text, payload text", "(0, 'seed', '')");
        using (var c = Db.Open())
            Db.Execute(c, "INSERT INTO lab_events SELECT g, CASE WHEN g % 2 = 0 THEN 'click' ELSE 'view' END, repeat('x', 2000) FROM generate_series(1, 100000) g");
        new Thread(EventExport.Export) { Name = "nightly-export", IsBackground = true }.Start();
        Thread.Sleep(500);
        new Thread(EventMigration.AddColumn) { Name = "deploy-migration", IsBackground = true }.Start();
        Thread.Sleep(500);
        for (var i = 0; i < 3; i++)
            new Thread(EventMigration.Dashboard) { Name = $"dashboard-{i}", IsBackground = true }.Start();
        Ready.SignalLater(1500);
        Ready.Forever();
    }
}

/// A job claims a batch of work by locking a whole date range with SELECT ... FOR UPDATE, then
/// processes the rows one by one inside the transaction. Every row in the range stays locked until
/// the batch commits, so the checkout that updates one of those orders waits for the whole batch.
public static class InvoiceBatch
{
    public static void Run(ManualResetEventSlim locked)
    {
        using var connection = Db.Open();
        using var tx = connection.BeginTransaction();
        using var claim = new NpgsqlCommand("SELECT id FROM lab_invoices WHERE due < now() + interval '30 days' FOR UPDATE", connection, tx);
        var ids = new List<int>();
        using (var reader = claim.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetInt32(0));
        locked.Set();
        foreach (var id in ids)
            Thread.Sleep(1000); // "render and email the invoice", one per second, all rows still locked
        tx.Commit();
    }

    public static void PayOne()
    {
        using var connection = Db.Open();
        Db.Execute(connection, "UPDATE lab_invoices SET paid = true WHERE id = 4210");
    }
}

internal static class SelectForUpdateScenario
{
    public static void Run()
    {
        Db.Reset("lab_invoices", "id int primary key, due timestamptz, paid bool", "(0, now(), false)");
        using (var c = Db.Open())
            Db.Execute(c, "INSERT INTO lab_invoices SELECT g, now() + (g % 60) * interval '1 day', false FROM generate_series(1, 10000) g");
        var locked = new ManualResetEventSlim();
        new Thread(() => InvoiceBatch.Run(locked)) { Name = "invoice-batch", IsBackground = true }.Start();
        locked.Wait();
        new Thread(InvoiceBatch.PayOne) { Name = "checkout", IsBackground = true }.Start();
        Ready.SignalLater(1500);
        Ready.Forever();
    }
}
