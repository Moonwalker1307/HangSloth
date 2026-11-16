using HangSloth.Analysis;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// Waits in optimized code whose target is easy to get wrong once the framework's wait method is inlined into
/// the app's method, run from tests/OptimizedWaitProbe: the target must be found when it is clear, and stay
/// unknown when it isn't.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class OptimizedWaitTargetEndToEndTests
{
    private static readonly Dictionary<string, string?> Optimized = new() { ["DOTNET_TieredCompilation"] = "0" };

    [Fact]
    public void A_child_process_wait_is_found_after_warm_up_inlined_it_down_to_the_WaitHandle()
    {
        // Default tiering with PGO: after warm-up only WaitHandle.WaitOneNoCheck is left above Exporter.Wait.
        var report = Analyze("export-warm", environment: null, until: r => r.Findings.Any(f => f.Id == "process-pipe-deadlock"));
        Verdict.Expect(report, "warning process-pipe-deadlock");
        Assert.Contains("for `dotnet OptimizedWaitProbe.dll --spew` (pid ", report.Findings[0].Summary);
        Assert.Contains("its standard output is redirected and nothing has read it yet", report.Findings[0].Summary);
    }

    [Fact]
    public void Holding_a_chatty_child_while_waiting_on_another_event_is_no_child_process_wait()
    {
        var report = Analyze("export-other-event", Optimized);
        Verdict.Expect(report, "");
    }

    [Fact]
    public void A_once_contended_lock_in_the_waiting_method_does_not_hide_the_object_waited_on()
    {
        var report = Analyze("queue-with-stats-lock", Optimized, until: r => r.Findings.Any(f => f.Id == "monitor-wait"));
        Verdict.Expect(report, "critical monitor-wait");
        Assert.Contains("is waiting in Monitor.Wait on ResizeQueue._sync although ResizeQueue._jobs (1 item) isn't empty", report.Findings[0].Summary);
    }

    [Fact]
    public void Sending_to_a_context_nobody_installed_is_not_a_deadlock_with_the_thread_that_has_one_of_that_type()
    {
        // Two PumpContexts: the UI thread installed one and joins the sender, which sends to the other one.
        var report = Analyze("send-to-unpumped-context", Optimized);
        Verdict.Expect(report, "");
        Assert.All(report.Snapshot.Threads, t => Assert.Null(t.SendingTo));
    }

    private static readonly Lazy<string> Probe = new(() =>
    {
        // Copied into the run's own folder and built there, so that runs from one checkout never share its obj folder.
        var source = Path.Combine(SampleApp.RepoRoot(), "tests", "OptimizedWaitProbe");
        var copy = Directory.CreateDirectory(Path.Combine(TestRun.Directory, "probe-source", "OptimizedWaitProbe")).FullName;
        foreach (var file in new[] { "OptimizedWaitProbe.csproj", "Program.cs" })
            File.Copy(Path.Combine(source, file), Path.Combine(copy, file), overwrite: true);
        var output = Path.Combine(TestRun.Directory, "samples", "OptimizedWaitProbe");
        SampleApp.Run("dotnet", $"build \"{Path.Combine(copy, "OptimizedWaitProbe.csproj")}\" -c Release -o \"{output}\" -nologo -v q -nodeReuse:false", expectSuccess: true);
        return Path.Combine(output, "OptimizedWaitProbe.dll");
    });

    private static AnalysisReport Analyze(string mode, IReadOnlyDictionary<string, string?>? environment, Func<AnalysisReport, bool>? until = null)
    {
        var dump = TestRun.NewDumpPath(mode);
        using var sample = Sample.Start(Probe.Value, [mode], environment);
        try
        {
            sample.WaitUntilReady(TimeSpan.FromSeconds(60));
            for (var attempt = 1; ; attempt++)
            {
                DumpLoader.CollectDump(sample.Process.Id, dump);
                var report = new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump));
                File.Delete(dump);
                if (until is null || until(report) || attempt == 3) return report;
                Thread.Sleep(700);
            }
        }
        finally
        {
            try { File.Delete(dump); } catch { }
        }
    }
}
