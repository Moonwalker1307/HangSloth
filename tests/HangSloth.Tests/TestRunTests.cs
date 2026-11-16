using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HangSloth.Tests;

/// A test run cleans up after itself even when it is killed, and the run after it removes what is left.
public class TestRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hangsloth-testrun-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// A process that runs until it is killed, with `marker` in its command line.
    private static Process LongRunning(string marker)
    {
        if (OperatingSystem.IsWindows())
            return Process.Start(new ProcessStartInfo("ping", ["-n", "120", "127.0.0.1"]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        // A copy of sleep inside the folder, so that its path is in the command line.
        var sleep = Path.Combine(marker, "sleep");
        File.Copy(File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep", sleep);
        return Process.Start(new ProcessStartInfo(sleep, ["120"]) { UseShellExecute = false })!;
    }

    private static long StartTicks(Process p) => p.StartTime.ToUniversalTime().Ticks;

    [SkippableFact]
    public void The_watchdog_kills_the_samples_and_removes_the_folder_when_the_test_process_is_killed()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows uses a job object instead (see the next test).");
        var folder = Directory.CreateDirectory(Path.Combine(_root, TestRun.FolderPrefix + "watched")).FullName;
        using var owner = Process.Start(new ProcessStartInfo("sleep", ["120"]) { UseShellExecute = false })!;
        using var sample = LongRunning(folder);
        using var watchdog = TestRun.StartWatchdog(owner.Id, folder);
        Assert.NotNull(watchdog);
        try
        {
            Thread.Sleep(1500);
            Assert.False(sample.HasExited);

            owner.Kill();
            owner.WaitForExit();
            Assert.True(sample.WaitForExit(10_000), "The watchdog didn't kill the sample after its test process was gone.");
            Assert.True(watchdog!.WaitForExit(10_000), "The watchdog didn't end.");
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            try { sample.Kill(); } catch { }
            try { owner.Kill(); } catch { }
            try { watchdog?.Kill(); } catch { }
        }
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/tmp")]
    public void The_watchdog_does_nothing_without_a_run_folder(string folder)
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows uses a job object instead.");
        Assert.Contains($"*/{TestRun.FolderPrefix}*", TestRun.WatchdogScript);
        using var owner = Process.Start(new ProcessStartInfo("sleep", ["120"]) { UseShellExecute = false })!;
        using var watchdog = TestRun.StartWatchdog(owner.Id, folder);
        Assert.NotNull(watchdog);
        try
        {
            // It must give up at once, while the process it would wait for is still there: a watchdog that waited
            // and then matched "/" would kill everything it may signal.
            Assert.True(watchdog!.WaitForExit(10_000), "The watchdog kept waiting although it has no run folder.");
            Assert.False(owner.HasExited);
        }
        finally
        {
            // The watchdog first, so that it can never see its process go.
            try { watchdog?.Kill(); watchdog?.WaitForExit(5000); } catch { }
            try { owner.Kill(); } catch { }
        }
    }

    [SkippableFact]
    public void The_job_object_kills_the_samples_when_it_is_closed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Job objects are Windows only; elsewhere the watchdog does this.");
        var job = ProcessJob.Create();
        Assert.NotEqual(IntPtr.Zero, job);
        using var sample = LongRunning(_root);
        try
        {
            Assert.True(ProcessJob.Add(job, sample));
            ProcessJob.Close(job);
            Assert.True(sample.WaitForExit(10_000), "Closing the job didn't kill the process in it.");
        }
        finally
        {
            try { sample.Kill(); } catch { }
        }
    }

    [Fact]
    public void The_next_run_stops_the_samples_of_a_run_that_is_gone_and_keeps_those_of_a_live_one()
    {
        var gone = Directory.CreateDirectory(Path.Combine(_root, TestRun.FolderPrefix + "gone")).FullName;
        var live = Directory.CreateDirectory(Path.Combine(_root, TestRun.FolderPrefix + "live")).FullName;
        using var leftover = LongRunning(gone);
        using var running = LongRunning(live);
        try
        {
            using var me = Process.GetCurrentProcess();
            // No process has id int.MaxValue: the owner of 'gone' is not running.
            File.WriteAllText(Path.Combine(gone, "owner"), $"{int.MaxValue} {StartTicks(me)}");
            File.WriteAllText(Path.Combine(live, "owner"), $"{me.Id} {StartTicks(me)}");
            foreach (var (folder, sample) in new[] { (gone, leftover), (live, running) })
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "pids")).FullName, sample.Id.ToString()), StartTicks(sample).ToString());

            TestRun.Sweep(_root);

            Assert.True(leftover.WaitForExit(10_000), "The sample of the run that is gone is still running.");
            Assert.False(Directory.Exists(gone));
            Assert.False(running.HasExited);
            Assert.True(Directory.Exists(live));
        }
        finally
        {
            try { leftover.Kill(); } catch { }
            try { running.Kill(); } catch { }
        }
    }
}

/// When a sample doesn't get ready, the failure says why.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class SampleStartEndToEndTests
{
    [Fact]
    public void A_sample_that_dies_at_startup_reports_its_exit_code_and_error_every_time()
    {
        var app = SampleApp.Build("DeadlockLab");
        for (var i = 0; i < 5; i++)
        {
            using var sample = Sample.Start(app, ["db-forgotten-transaction"],
                new Dictionary<string, string?> { ["DEADLOCKLAB_PG"] = "Host=127.0.0.1;Port=9;Username=nobody;Password=nobody;Timeout=5" });
            var ex = Assert.ThrowsAny<Exception>(() => sample.WaitUntilReady(TimeSpan.FromSeconds(30)));
            Assert.StartsWith("Sample 'db-forgotten-transaction' never became ready: it exited with code ", ex.Message);
            Assert.Contains("Npgsql.NpgsqlException", ex.Message);
        }
    }

    [Fact]
    public void A_sample_that_is_still_running_reports_what_HangSloth_sees_in_it()
    {
        // A "database" that takes the connection and never answers: the scenario hangs before READY, in its set-up.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var sample = Sample.Start(SampleApp.Build("DeadlockLab"), ["db-forgotten-transaction"],
            new Dictionary<string, string?> { ["DEADLOCKLAB_PG"] = $"Host=127.0.0.1;Port={port};Username=orders;Password=orders;Timeout=0" });
        // A sample that never connects fails here with its exit code and output, instead of hanging the test.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!listener.Pending() && DateTime.UtcNow < deadline) Thread.Sleep(100);
        if (!listener.Pending()) sample.WaitUntilReady(TimeSpan.Zero);
        Assert.True(listener.Pending(), "The sample got ready without connecting to the database.");
        using var accepted = listener.AcceptTcpClient();

        var ex = Assert.ThrowsAny<Exception>(() => sample.WaitUntilReady(TimeSpan.FromSeconds(3)));
        Assert.StartsWith("Sample 'db-forgotten-transaction' never became ready: it is still running after 3 seconds. HangSloth says it is stuck here:\n", ex.Message);
        Assert.Contains("\n  [Warning] database-connect: Thread ", ex.Message);
        Assert.Contains($"is still connecting to PostgreSQL at 127.0.0.1:{port}", ex.Message);
    }

    [Fact]
    public void The_database_probe_is_a_login_not_an_open_port()
    {
        // Something that accepts connections and hangs up is not a database.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                using (var client = await listener.AcceptTcpClientAsync(stop.Token)) { }
        });
        try
        {
            var postgres = Lab.Admin("postgres", "DEADLOCKLAB_PG", $"Host=127.0.0.1;Port={port};Username=nobody;Password=nobody", "check");
            Assert.Equal(1, postgres.Exit);
            Assert.StartsWith("NpgsqlException: ", postgres.Error);
            var sqlServer = Lab.Admin("sqlserver", "DEADLOCKLAB_SQLSERVER", $"Server=127.0.0.1,{port};User Id=nobody;Password=nobody;TrustServerCertificate=true", "check");
            Assert.Equal(1, sqlServer.Exit);
            Assert.StartsWith("SqlException: ", sqlServer.Error);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
