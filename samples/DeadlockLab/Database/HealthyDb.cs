namespace DeadlockLab.Database;

internal static class HealthyDbScenario
{
    public static void Run()
    {
        Db.Reset("lab_healthy", "id int primary key", "(1)");
        for (var i = 0; i < 3; i++)
        {
            using var c = Db.Open();
            Db.Execute(c, "SELECT count(*) FROM lab_healthy");
        }
        using var held = Db.Open(); // an idle open connection, outside any transaction
        Ready.SignalLater();
        Ready.Forever();
    }
}
