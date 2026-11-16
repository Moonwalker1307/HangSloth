using System.Text.RegularExpressions;
using HangSloth.Analysis;

namespace HangSloth.Tests;

/// Real dumps of task shapes that used to be misdescribed: an app's async Main, a test under xunit's
/// AsyncTestSyncContext, a generic host stuck in StartAsync, a task that was never started or is still queued,
/// and Task.WhenAny of two tasks. The healthy ones must leave the CLI's exit code at 0.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class TaskDescriptionEndToEndTests
{
    /// Thread numbers differ between Linux and Windows, and Program.cs's lines move whenever HangApp gets a mode.
    private static string Stable(string text) => Regex.Replace(Regex.Replace(text, @"Thread \d+", "Thread N"), @"Program\.cs:\d+", "Program.cs:N");

    /// The exit code the CLI gives for this report's snapshot.
    private static int ExitCode(AnalysisReport report) => CliTests.Run(["sample.dmp", "--no-color"], (_, _) => report.Snapshot).Exit;

    private static Finding Single(AnalysisReport report, string id)
    {
        var f = Assert.Single(report.Findings);
        Assert.Equal(id, f.Id);
        return f;
    }

    [Fact]
    public void Async_Main_waiting_for_its_stop_signal_is_not_a_hang()
    {
        var report = SampleApp.Analyze("async-main-shutdown");

        Assert.Empty(report.Findings);
        Assert.Equal(0, ExitCode(report));
        var output = Stable(CliTests.Run(["sample.dmp", "--no-color"], (_, _) => report.Snapshot).Out);
        Assert.Contains("Thread N is waiting for the app's async Main, which is paused at Program.cs:N.", output);
    }

    [Fact]
    public void Async_Main_waiting_for_a_task_nobody_completes_further_down_is_reported()
    {
        // The same hang as never-completes, entered through an async Main: same verdict, and a note on stop signals.
        var report = SampleApp.Analyze("async-main-import", until: r => r.Findings.Count > 0);

        var f = Single(report, "blocked-on-task");
        Assert.Equal("Waiting for a task nobody completes", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread N is waiting for the app's async Main, which is paused at Program.cs:N, awaiting AsyncMainImportScenario.RunAsync (paused at AsyncMainScenarios.cs:28), " +
                     "which awaits AsyncMainImportScenario.NightlyImport.RunAsync (paused at AsyncMainScenarios.cs:38), which awaits a TaskCompletionSource<bool> task that nothing has completed yet.",
            Stable(f.Summary));
        Assert.Contains("If this is how the app waits to be told to stop, waiting here for as long as it runs is normal. If the app should have moved on by now, this is where it is stuck.", f.Details);
        Assert.Equal(2, ExitCode(report));
    }

    [Fact]
    public void A_test_blocking_on_async_code_under_xunits_context_is_not_a_deadlock()
    {
        var report = SampleApp.Analyze("test-blocks-on-async", project: "DeadlockLab", until: r => r.Findings.Count == 2);

        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("sync-over-async", StringComparison.Ordinal));
        Assert.All(report.Findings, f => Assert.Equal(Severity.Info, f.Severity));
        Assert.Contains(report.Findings, f => Stable(f.Summary) ==
            "Thread N (\"stock-feed-test\") is blocked in .Result at TestRun.cs:13, waiting for TestRunScenario.StockFeed.ReadAllAsync (paused at TestRun.cs:31), which awaits a Task.Delay that hasn't elapsed yet.");
        Assert.Contains(report.Findings, f => Stable(f.Summary) ==
            "Thread N (\"stock-feed-no-context-test\") is blocked in .Result at TestRun.cs:14, waiting for TestRunScenario.StockFeed.ReadAllWithoutContextAsync (paused at TestRun.cs:37), which awaits a Task.Delay that hasn't elapsed yet.");
        Assert.Equal(0, ExitCode(report));
    }

    private const string StuckStart =
        "HostingAbstractionsHostExtensions.RunAsync, which awaits Host.StartAsync, which awaits Host.ForeachService, which awaits Host.StartAsync (lambda), " +
        "which awaits GenericHostScenario.BrokerConnection.StartAsync (paused at GenericHostScenarios.cs:36), " +
        "which awaits GenericHostScenario.BrokerConnection.ConnectAsync (paused at GenericHostScenarios.cs:41), " +
        "which awaits WaitAsync(...), which waits for a TaskCompletionSource task that nothing has completed yet.";

    private const string HostStarting =
        "The generic host is still starting: Host.StartAsync waits for every hosted service's StartAsync to return, and until they all have, the app has not finished starting.";

    [Fact]
    public void Host_Run_stuck_in_StartAsync_is_reported()
    {
        var report = SampleApp.Analyze("host-start-stuck", until: r => r.Findings.Count > 0);

        var f = Single(report, "blocked-on-task");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal($"Thread N is blocked in .Run() at GenericHostScenarios.cs:13, waiting for {StuckStart}", Stable(f.Summary));
        Assert.Contains(HostStarting, f.Details);
        Assert.Equal(2, ExitCode(report));
    }

    [Fact]
    public void Awaited_RunAsync_stuck_in_StartAsync_is_reported()
    {
        var report = SampleApp.Analyze("host-start-stuck-async", until: r => r.Findings.Count > 0);

        var f = Single(report, "blocked-on-task");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal($"Thread N is waiting for the app's async Main, which is paused at Program.cs:N, awaiting {StuckStart}", Stable(f.Summary));
        Assert.Contains(HostStarting, f.Details);
    }

    [Fact]
    public void A_running_host_is_not_a_hang()
    {
        var report = SampleApp.Analyze("host-running");

        Assert.Empty(report.Findings);
        Assert.Equal(0, ExitCode(report));
    }

    [Fact]
    public void Waiting_for_a_task_that_was_never_started()
    {
        var f = Single(SampleApp.Analyze("never-started"), "blocked-on-task");

        Assert.Equal("Waiting for a task that was never started", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread N is blocked in .Wait() at TaskShapeScenarios.cs:13, waiting for new Task(NeverStartedTaskScenario.InvoiceExport.WriteFile), which was never started.", Stable(f.Summary));
    }

    [Fact]
    public void Waiting_for_a_task_still_queued_for_a_thread()
    {
        var f = Single(SampleApp.Analyze("queued-task"), "blocked-on-task");

        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread N is blocked in .Wait() at TaskShapeScenarios.cs:31, waiting for Task.Run(QueuedTaskScenario.LabelPrinterScheduler.PrintLabel), " +
                     "which is queued and hasn't started running: no thread has picked it up yet.", Stable(f.Summary));
    }

    [Fact]
    public void Task_WhenAny_of_two_tasks_nobody_completes_is_followed()
    {
        var f = Single(SampleApp.Analyze("when-any-two"), "blocked-on-task");

        Assert.Equal("Waiting for a task nobody completes", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread N is blocked in .Wait() at TaskShapeScenarios.cs:62, waiting for Task.WhenAny(...), which waits for the first of its 2 tasks: a TaskCompletionSource task that nothing has completed yet.", Stable(f.Summary));
    }

    [Fact]
    public void Task_WhenAny_of_two_tasks_with_a_timeout_is_only_information()
    {
        var report = SampleApp.Analyze("when-any-two-timeout");

        var f = Single(report, "blocked-on-task");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread N is blocked in .Wait() at TaskShapeScenarios.cs:73, waiting for Task.WhenAny(...).", Stable(f.Summary));
        Assert.Equal(0, ExitCode(report));
    }
}
