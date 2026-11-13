using System.Collections.Concurrent;
using System.Diagnostics;
using HangSloth.Analysis;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// Runs the real HangApp sample in each hang mode, captures a dump of it, and checks
/// that HangSloth explains it correctly, including source lines from the PDBs.
[Trait("Category", "EndToEnd")]
public class EndToEndTests
{
    [Fact]
    public void Sync_over_async_on_a_single_threaded_context()
    {
        var report = SampleApp.Analyze("sync-over-async");
        var f = report.Findings[0];
        Assert.Equal("sync-over-async-deadlock", f.Id);
        Assert.Equal("Thread 1 is blocking with .Result on a task that's waiting for the same SynchronizationContext. Classic sync-over-async, in OrderController.cs:10.", f.Summary);
        Assert.Contains(f.Details, d => d.Contains("OrderService.GetOrderAsync (paused at OrderService.cs:9)"));
        Assert.Contains(f.Details, d => d.Contains("posted to the SingleThreadSynchronizationContext"));
    }

    [Fact]
    public void Sync_over_async_in_a_debug_build()
    {
        // Debug builds compile state machines as classes rather than structs.
        var report = SampleApp.Analyze("sync-over-async", "Debug");
        var f = report.Findings[0];
        Assert.Equal("sync-over-async-deadlock", f.Id);
        Assert.Equal("OrderController.cs", f.Location?.FileName);
        Assert.Contains(f.Details, d => d.Contains("paused at OrderService.cs:9"));
    }

    [Fact]
    public void Lock_ordering_deadlock()
    {
        var report = SampleApp.Analyze("deadlock");
        var f = Assert.Single(report.Findings, x => x.Severity == Severity.Critical);
        Assert.Equal("lock-deadlock", f.Id);
        Assert.Contains("Thread 4 (\"reserve-worker\") and Thread 5 (\"billing-worker\") are each waiting for a lock the other one holds", f.Summary);
        Assert.Contains(f.Details, d => d.Contains("holds the lock on LockDeadlockScenario.InventoryLock") && d.Contains("Scenarios.cs:39"));
        Assert.Contains(f.Details, d => d.Contains("holds the lock on LockDeadlockScenario.BillingLock") && d.Contains("Scenarios.cs:51"));
    }

    [Fact]
    public void Lock_contention_behind_a_sleeping_owner()
    {
        var report = SampleApp.Analyze("contention");
        var f = Assert.Single(report.Findings);
        Assert.Equal("lock-contention", f.Id);
        Assert.Equal("5 threads are stuck waiting for the lock on LockContentionScenario.CacheLock, held by Thread 4 (\"cache-rebuilder\"), which is sleeping (Thread.Sleep) in Scenarios.cs:84 while holding it.", f.Summary);
    }

    [Fact]
    public void Thread_pool_starvation()
    {
        var report = SampleApp.Analyze("starvation");
        var f = Assert.Single(report.Findings);
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.StartsWith("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in .Result at OrderController.cs:17)", f.Summary);
        Assert.NotNull(report.Snapshot.ThreadPool?.QueuedWorkItems);
        Assert.True(report.Snapshot.ThreadPool!.QueuedWorkItems > 0);
    }

    [Fact]
    public void Task_that_is_never_completed()
    {
        var report = SampleApp.Analyze("never-completes");
        var f = Assert.Single(report.Findings);
        Assert.Equal("blocked-on-task", f.Id);
        // The main thread's managed id isn't guaranteed to be 1 on every runtime/OS.
        Assert.Matches(@"^Thread \d+ is blocked in \.Wait\(\) at Scenarios\.cs:122, waiting for NeverCompletesScenario\.Importer\.ImportAsync \(paused at Scenarios\.cs:133\), which awaits a TaskCompletionSource<bool> task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Healthy_app_has_no_findings()
    {
        var report = SampleApp.Analyze("healthy");
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Deadlock_on_net9_lock_type()
    {
        var report = SampleApp.Analyze("lock-type-deadlock");
        var f = Assert.Single(report.Findings, x => x.Severity == Severity.Critical);
        Assert.Equal("lock-deadlock", f.Id);
        Assert.Contains(f.Details, d => d.Contains("holds the lock on LockTypeDeadlockScenario.Accounts"));
    }
}

internal static class SampleApp
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> Builds = new();
    public static AnalysisReport Analyze(string mode, string configuration = "Release")
    {
        var app = Builds.GetOrAdd(configuration, _ => new Lazy<string>(() => Build(configuration))).Value;
        var dump = Path.Combine(Path.GetTempPath(), $"hangsloth-test-{mode}-{Guid.NewGuid():N}.dmp");

        using var process = Process.Start(new ProcessStartInfo("dotnet", [app, mode])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            var ready = Task.Run(() =>
            {
                while (process.StandardOutput.ReadLine() is { } line)
                    if (line == "READY") return true;
                return false;
            });
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)) && ready.Result, $"Sample '{mode}' never became ready.");

            DumpLoader.CollectDump(process.Id, dump);
            var snapshot = DumpLoader.LoadDump(dump);
            return new HangAnalyzer().Analyze(snapshot);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { File.Delete(dump); } catch { }
        }
    }

    private static string Build(string configuration)
    {
        var project = Path.Combine(RepoRoot(), "samples", "HangApp", "HangApp.csproj");
        var output = Path.Combine(Path.GetTempPath(), "hangsloth-sample", configuration);
        Run("dotnet", $"build \"{project}\" -c {configuration} -o \"{output}\" -nologo -v q", expectSuccess: true);
        return Path.Combine(output, "HangApp.dll");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "HangSloth.sln"))) return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root.");
    }

    private static string Run(string file, string args, bool expectSuccess = false)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (expectSuccess && p.ExitCode != 0)
            throw new InvalidOperationException($"{file} {args} failed:\n{stdout.Result}\n{stderr.Result}");
        return stdout.Result;
    }
}
