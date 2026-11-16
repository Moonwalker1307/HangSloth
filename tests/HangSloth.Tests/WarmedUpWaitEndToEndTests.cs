using System.Text.RegularExpressions;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;

namespace HangSloth.Tests;

/// Waits in a process that ran for a while, run from tests/OptimizedWaitProbe with default tiering: the warm-up makes
/// tiered PGO inline a task's delegate into the thread pool's dispatch loop (or, for a task run inside another, into
/// ExecutionContext.RunInternal), or ManualResetEventSlim.Wait into its caller. Each verdict is the one the same wait
/// gets in code that wasn't optimized, whether the JIT inlined or not. A few run with DOTNET_TieredCompilation=0.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class WarmedUpWaitEndToEndTests
{
    [Fact]
    public void A_task_whose_delegate_was_inlined_into_the_dispatch_loop_still_closes_the_cycle_with_its_waiter()
    {
        var (report, dump) = Analyze("invoice-close-warm", r => r.Findings.Any(f => f.Id == "wait-cycle"));
        try
        {
            ExpectCycle(report);

            // Without the frame that keeps the task alive, the runtime's own record of the task a thread runs is enough.
            var unread = new HangAnalyzer().Analyze(LoadWithFault(dump, "running task"));
            ExpectCycle(unread);

            // And a record that can't be read costs nothing that the stack has.
            var unrecorded = new HangAnalyzer().Analyze(LoadWithFault(dump, "current task"));
            ExpectCycle(unrecorded);
            Assert.Contains(unrecorded.Snapshot.Warnings, w => w.Contains(" could not be read completely from the dump (", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(dump);
        }
    }

    [Fact]
    public void A_task_whose_inlined_delegate_waits_for_a_lock_of_a_third_thread_is_no_cycle()
    {
        var (report, dump) = Analyze("invoice-close-audited", r => r.Findings.Any(f => f.Id == "lock-contention"));
        File.Delete(dump);

        Verdict.Expect(report, "warning lock-contention, info blocked-on-task");
        Assert.Matches(@"^Thread \d+ \(""\.NET TP Worker""\) is stuck waiting for the lock on InvoiceBook\.Audit, held by Thread \d+ \(""auditor""\), which is sleeping \(Thread\.Sleep\) in Program\.cs:\d+ while holding it\.$",
            report.Findings.Single(f => f.Id == "lock-contention").Summary);
    }

    [Fact]
    public void An_event_wait_inlined_into_the_lock_holder_is_an_event_wait_and_not_a_wait_for_a_pulse()
    {
        var (report, dump) = Analyze("ledger-close-warm", r => r.Findings.Any(f => f.Id == "lock-contention"));
        File.Delete(dump);

        Verdict.Expect(report, "warning lock-contention");
        Assert.Matches(@"^3 threads are stuck waiting for the lock on Ledger\._sync, held by Thread \d+ \(""ledger-close""\), which is waiting for a ManualResetEventSlim to be set in Program\.cs:\d+ while holding it\.$",
            report.Findings[0].Summary);
        var holder = report.Snapshot.Threads.Single(t => t.Name == "ledger-close");
        Assert.Null(holder.WaitingForPulse);
        Assert.Contains(holder.Frames, f => f.TypeName == "System.Threading.ManualResetEventSlim" && f.MethodName == "Wait");
    }

    [Fact]
    public void A_Monitor_Wait_on_the_app_s_own_object_next_to_an_event_it_keeps_stays_a_Monitor_Wait()
    {
        var (report, dump) = Analyze("queue-after-started", r => r.Findings.Any(f => f.Id == "monitor-wait"), Optimized);
        File.Delete(dump);

        Verdict.Expect(report, "critical monitor-wait");
        Assert.Contains("is waiting in Monitor.Wait on ResizeQueue._sync although ResizeQueue._jobs (1 item) isn't empty", report.Findings[0].Summary);
        var worker = report.Snapshot.Threads.Single(t => t.Name == "resize-worker");
        Assert.DoesNotContain(worker.Frames, f => f.TypeName == "System.Threading.ManualResetEventSlim");
    }

    [Fact]
    public void A_task_that_runs_inline_inside_another_is_shown_in_its_own_lambda_waiting_for_the_lock()
    {
        // With the framework's code JIT-compiled too, PGO can inline the inner lambda into ExecutionContext.RunInternal.
        var (report, dump) = Analyze("invoice-close-inline", r => r.Findings.Any(f => f.Severity >= Severity.Warning), Jitted);
        File.Delete(dump);

        ExpectInnerLambdaWaits(report, inner: "<Post>b__0", outer: "<CloseInline>b__0");
    }

    [Fact]
    public void A_task_run_synchronously_inside_another_is_shown_in_its_own_lambda_and_not_the_outer_one()
    {
        var (report, dump) = Analyze("invoice-close-nested", r => r.Findings.Any(f => f.Severity >= Severity.Warning));
        File.Delete(dump);

        ExpectInnerLambdaWaits(report, inner: "<Posting>b__0", outer: "<CloseNested>b__0");
    }

    [Fact]
    public void An_inlined_delegate_that_joins_a_thread_gets_the_line_of_its_Join()
    {
        var (report, dump) = Analyze("invoice-close-printed", r => r.Findings.Any(f => f.Id == "wait-cycle"));
        File.Delete(dump);

        Verdict.Expect(report, "critical wait-cycle");
        // Only Thread.Join's P/Invoke stub and the marshalling stub are left above the lambda once it is inlined.
        Assert.True(report.Findings[0].Details.Any(d => Regex.IsMatch(d,
                @"^Thread \d+ \(""\.NET TP Worker""\) is waiting in InvoiceBook\.ClosePrinted \(lambda\) \(Program\.cs:\d+\) for Thread \d+ \(""printer""\) to finish \(Thread\.Join\)\.$")),
            $"{string.Join("\n", report.Findings[0].Details)}\n{Verdict.Stacks(report)}");
        Assert.DoesNotContain(report.Snapshot.Warnings, w => w.Contains("IL_STUB", StringComparison.Ordinal));
    }

    [Fact]
    public void An_inlined_delegate_that_blocks_on_a_task_gets_the_line_and_the_call_of_its_wait()
    {
        var (report, dump) = Analyze("invoice-close-pending", r => r.Findings.Any(f => f.Severity == Severity.Warning));
        File.Delete(dump);

        Verdict.Expect(report, "warning blocked-on-task, info blocked-on-task");
        Assert.Matches(@"^Thread \d+ \(""\.NET TP Worker""\) is blocked in \.Wait\(\) at Program\.cs:\d+, waiting for a TaskCompletionSource task that nothing has completed yet\.$",
            report.Findings.Single(f => f.Severity == Severity.Warning).Summary);
    }

    [Fact]
    public void A_task_delegate_that_lost_its_frame_in_optimized_code_is_where_its_thread_is_shown_waiting()
    {
        // HangApp's label printer runs a job that only calls Thread.Sleep: optimized code leaves it no frame of its own.
        var report = SampleApp.Analyze("queued-task", environment: Optimized);

        Verdict.Expect(report, "info blocked-on-task");
        var printer = report.Snapshot.Threads.Single(t => t.Name == "label-printer");
        Assert.True(FrameClassifier.FirstUserFrame(printer.Frames) is { TypeName: "HangApp.QueuedTaskScenario+LabelPrinterScheduler", MethodName: "Jam" },
            Verdict.Stacks(report));
    }

    private static readonly Dictionary<string, string?> Optimized = new() { ["DOTNET_TieredCompilation"] = "0" };
    private static readonly Dictionary<string, string?> Jitted = new() { ["DOTNET_ReadyToRun"] = "0" };

    /// The pool thread runs the inner task inside the outer one, and the inner task's lambda waits for the book's lock:
    /// the lock wait is read, and the wait is shown in the inner lambda, never in the outer one. The waiter closes a
    /// cycle with that thread when the frame lookup gives it the outer task (which it runs as well), else it waits behind
    /// the lock: both are true, and which one depends on the order the dump lists the frame's objects in.
    private static void ExpectInnerLambdaWaits(AnalysisReport report, string inner, string outer)
    {
        var shown = $"got \"{Verdict.Of(report)}\"\n{Verdict.Stacks(report)}";
        Assert.True(Verdict.Matches(report, "critical wait-cycle") || Verdict.Matches(report, "warning lock-contention"), shown);
        var waiter = Assert.Single(report.Snapshot.Threads, t => t.IsThreadPoolWorker && t.WaitingForLock is not null);
        Assert.True(FrameClassifier.FirstUserFrame(waiter.Frames)?.MethodName == inner, shown);
        Assert.DoesNotContain(waiter.Frames, f => f.InlinedInto is not null && f.MethodName == outer);
    }

    private static void ExpectCycle(AnalysisReport report)
    {
        Verdict.Expect(report, "critical wait-cycle");
        var f = report.Findings[0];
        Assert.Matches(@"^Thread \d+ and Thread \d+ \(""\.NET TP Worker""\) are waiting for each other, so neither can ever continue\. Start at Program\.cs:\d+\.$", f.Summary);
        Assert.Equal(2, f.Details.Count);
        Assert.Matches(@"^Thread \d+ holds the lock on InvoiceBook\._sync and is blocked in \.Wait\(\) at InvoiceBook\.Close \(Program\.cs:\d+\), waiting for Task\.Run\(InvoiceBook\.Close \(lambda\)\), which is running on Thread \d+ \(""\.NET TP Worker""\)\.$", f.Details[0]);
        Assert.Matches(@"^Thread \d+ \(""\.NET TP Worker""\) is waiting in InvoiceBook\.Close \(lambda\) \(Program\.cs:\d+\) for the lock on InvoiceBook\._sync, which Thread \d+ holds\.$", f.Details[1]);
    }

    /// One dump of the mode, analysed; kept for further reads of it. Taken again (up to three times) until <paramref name="until"/>.
    private static (AnalysisReport Report, string Dump) Analyze(string mode, Func<AnalysisReport, bool> until, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var dump = TestRun.NewDumpPath(mode);
        using var sample = Sample.Start(OptimizedWaitTargetEndToEndTests.Probe.Value, [mode], environment);
        try
        {
            // The warm-up takes a few seconds before the wait.
            sample.WaitUntilReady(TimeSpan.FromSeconds(120));
            for (var attempt = 1; ; attempt++)
            {
                DumpLoader.CollectDump(sample.Process.Id, dump);
                var report = new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump));
                if (until(report) || attempt == 3) return (report, dump);
                File.Delete(dump);
                Thread.Sleep(700);
            }
        }
        catch
        {
            try { File.Delete(dump); } catch { }
            throw;
        }
    }

    /// The dump read again with the named per-thread read failing on every thread.
    private static ProcessSnapshot LoadWithFault(string dump, string step)
    {
        SnapshotBuilder.ThreadReadFault = (_, at) =>
        {
            if (at == step) throw new InvalidOperationException("unreadable");
        };
        try
        {
            return DumpLoader.LoadDump(dump);
        }
        finally
        {
            SnapshotBuilder.ThreadReadFault = null;
        }
    }
}
