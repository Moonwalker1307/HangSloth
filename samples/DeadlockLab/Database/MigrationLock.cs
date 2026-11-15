namespace DeadlockLab.Database;

public static class Migrator
{
    /// A schema migration that runs in a transaction and then "waits for the other nodes" before
    /// committing. ALTER TABLE takes an ACCESS EXCLUSIVE lock: even plain SELECTs queue behind it.
    public static void AddColumn(ManualResetEventSlim otherNodesDone, ManualResetEventSlim altered)
    {
        using var connection = Db.Open();
        using var tx = connection.BeginTransaction();
        Db.Execute(connection, "ALTER TABLE lab_catalog ADD COLUMN discontinued boolean DEFAULT false", tx);
        altered.Set();
        otherNodesDone.Wait();
        tx.Commit();
    }
}

public static class CatalogQueries
{
    public static void CountProducts()
    {
        using var connection = Db.Open();
        Db.Execute(connection, "SELECT count(*) FROM lab_catalog");
    }
}

internal static class MigrationLockScenario
{
    public static void Run()
    {
        Db.Reset("lab_catalog", "id int primary key, name text", "(1, 'sloth plush')");
        var otherNodes = new ManualResetEventSlim();
        var altered = new ManualResetEventSlim();
        new Thread(() => Migrator.AddColumn(otherNodes, altered)) { Name = "migration", IsBackground = true }.Start();
        altered.Wait();
        for (var i = 0; i < 3; i++)
            new Thread(CatalogQueries.CountProducts) { Name = $"catalog-request-{i}", IsBackground = true }.Start();
        Ready.SignalLater(1000);
        Ready.Forever();
    }
}
