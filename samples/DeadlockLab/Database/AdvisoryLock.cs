using Npgsql;

namespace DeadlockLab.Database;

/// "Only one node runs the nightly job": a session-level advisory lock. It isn't tied to a
/// transaction, so it's held for as long as the connection stays open... and this connection
/// stays open, back in a long-lived field, after the job is done.
public sealed class NightlyJob
{
    private NpgsqlConnection? _leaderConnection;

    public void RunAsLeader()
    {
        _leaderConnection = Db.Open();
        Db.Execute(_leaderConnection, "SELECT pg_advisory_lock(4242)");
        Console.WriteLine("job done");
        // BUG: no pg_advisory_unlock, and the connection is kept "for next time".
    }

    public static void RunOnOtherNode()
    {
        using var connection = Db.Open();
        Db.Execute(connection, "SELECT pg_advisory_lock(4242)"); // waits forever
        Db.Execute(connection, "SELECT pg_advisory_unlock(4242)");
    }

    public void IdleUntilNextRun() => Thread.Sleep(Timeout.Infinite);
}

internal static class AdvisoryLockScenario
{
    public static void Run()
    {
        var job = new NightlyJob();
        var leader = new Thread(() => { job.RunAsLeader(); job.IdleUntilNextRun(); }) { Name = "scheduler", IsBackground = true };
        leader.Start();
        Thread.Sleep(500);
        new Thread(NightlyJob.RunOnOtherNode) { Name = "second-scheduler", IsBackground = true }.Start();
        Ready.SignalLater(1000);
        Ready.Forever();
    }
}
