using System.Collections.Concurrent;
using System.Diagnostics;
using HangSloth.Analysis;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// Runs the real HangApp sample in each hang mode, captures a dump of it, and checks
/// that HangSloth explains it correctly, including source lines from the PDBs.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class EndToEndTests
{
    [Fact]
    public void Sync_over_async_on_a_single_threaded_context()
    {
        var report = SampleApp.Analyze("sync-over-async");
        var f = report.Findings[0];
        Assert.Equal("sync-over-async-deadlock", f.Id);
        // The main thread's managed id isn't always 1 (it's 2 on Windows with .NET 10).
        Assert.Matches(@"^Thread \d+ is blocking with \.Result on a task that's waiting for the same SynchronizationContext\. Classic sync-over-async, in OrderController\.cs:10\.$", f.Summary);
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
    public void Starvation_in_optimized_code_reports_the_line_of_the_Result()
    {
        // 200 pool threads blocked on the same .Result, most of them in tier-1 code where Task<T>.Result is
        // inlined: they must all be reported at the .Result line, as .Result, in a single group.
        var report = SampleApp.Analyze("tiered-starvation");
        // The scenario only tests something if some threads really run the inlined code.
        var stacks = string.Join("\n", report.Snapshot.Threads.Where(t => t.IsThreadPoolWorker)
            .Select(t => string.Join(" > ", t.Frames.Take(9).Select(x => x.ToString()))).Distinct());
        Assert.True(report.Snapshot.Threads.Any(t => t.Frames.Any(x => x.MethodName == "Validate")
                                                     && !t.Frames.Any(x => x.MethodName is "GetResultCore" or "get_Result")),
            $"No thread ran tier-1 code with Task<T>.Result inlined. Pool thread stacks:\n{stacks}");
        var f = Assert.Single(report.Findings);
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.StartsWith("Thread pool starvation: 200 of ", f.Summary);
        Assert.Contains("(all in .Result at InvoiceCheck.cs:23)", f.Summary);
        Assert.Contains("200 threads: .Result at InvoiceCheck.Validate (InvoiceCheck.cs:23).", f.Details);
        Assert.Contains("await instead of .Result in InvoiceCheck.cs:23", f.Advice);
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
    /// <param name="until">For timing-dependent scenarios: dump again (up to 3 times) until the report satisfies this.</param>
    public static AnalysisReport Analyze(string mode, string configuration = "Release", string project = "HangApp", Func<AnalysisReport, bool>? until = null)
    {
        var app = Build(project, configuration);
        var dump = Path.Combine(Path.GetTempPath(), $"hangsloth-test-{mode}-{Guid.NewGuid():N}.dmp");

        using var process = Process.Start(new ProcessStartInfo("dotnet", [app, mode])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = Task.Run(() =>
            {
                while (process.StandardOutput.ReadLine() is { } line)
                    if (line == "READY") return true;
                return false;
            });
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)) && ready.Result, $"Sample '{mode}' never became ready.{(process.HasExited ? " " + stderr.Result : "")}");

            for (var attempt = 1; ; attempt++)
            {
                DumpLoader.CollectDump(process.Id, dump);
                var report = new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump));
                File.Delete(dump);
                if (until is null || until(report) || attempt == 3) return report;
                Thread.Sleep(700);
            }
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { File.Delete(dump); } catch { }
        }
    }

    /// Builds a sample once per test run and returns the path of its .dll.
    public static string Build(string project, string configuration = "Release") =>
        Builds.GetOrAdd($"{project}|{configuration}", _ => new Lazy<string>(() =>
        {
            var csproj = Path.Combine(RepoRoot(), "samples", project, $"{project}.csproj");
            var output = Path.Combine(Path.GetTempPath(), "hangsloth-sample", project, configuration);
            Run("dotnet", $"build \"{csproj}\" -c {configuration} -o \"{output}\" -nologo -v q", expectSuccess: true);
            return Path.Combine(output, $"{project}.dll");
        })).Value;

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "HangSloth.sln"))) return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root.");
    }

    public static string Run(string file, string args, bool expectSuccess = false)
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
