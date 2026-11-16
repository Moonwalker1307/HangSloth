using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// A test that blocks on async code under a test runner's SynchronizationContext. xunit's contexts post
/// continuations to other threads, so that is a wait, not a sync-over-async deadlock, unless a continuation is
/// already stuck in the context's queue.
public class TestRunnerContextTests
{
    private static readonly ObjectRef TestContext = new(0x1000, "Xunit.Sdk.AsyncTestSyncContext");

    private static readonly TaskInfo SlowFeed = new()
    {
        Address = 0x2000, TypeName = "AsyncStateMachineBox<MyApp.Tests.FeedReader+<ReadAllAsync>d__0>", Kind = TaskKind.AsyncMethod,
        Description = "FeedReader.ReadAllAsync", AwaitLocation = new SourceLocation("/src/FeedReader.cs", 9),
        Awaiting = new TaskInfo { Address = 0x3000, TypeName = "System.Threading.Tasks.Task+DelayPromise", Kind = TaskKind.Delay, Description = "Task.Delay(...)" },
    };

    private static ProcessSnapshot TestBlocking(TaskInfo task, ObjectRef context) => Process(
        Thread(13, [.. ResultWait, App("MyApp.Tests.FeedTests", "Reads_every_item", "FeedTests.cs", 35)], ".NET TP Worker") with
        {
            SynchronizationContext = context,
            BlockedOnTask = task,
            IsThreadPoolWorker = true,
        });

    private static List<Finding> Analyze(ProcessSnapshot snapshot) => new HangAnalyzer().Analyze(snapshot).Findings.ToList();

    [Fact]
    public void Blocking_on_a_method_that_resumes_on_the_test_context_is_not_a_deadlock()
    {
        var findings = Analyze(TestBlocking(SlowFeed with { Resume = new ResumeTarget(TestContext, ResumeStatus.Registered) }, TestContext));

        var f = Assert.Single(findings);
        Assert.Equal("blocked-on-task", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 13 (\".NET TP Worker\") is blocked in .Result at FeedTests.cs:35, waiting for FeedReader.ReadAllAsync (paused at FeedReader.cs:9), which awaits a Task.Delay that hasn't elapsed yet.", f.Summary);
    }

    [Fact]
    public void Blocking_on_a_method_that_doesnt_capture_the_test_context_is_not_a_likely_deadlock()
    {
        // ConfigureAwait(false) inside: nothing resumes on the context, which once gave "Likely sync-over-async".
        var findings = Analyze(TestBlocking(SlowFeed, TestContext));

        Assert.DoesNotContain(findings, f => f.Id.StartsWith("sync-over-async", StringComparison.Ordinal));
        Assert.Equal(Severity.Info, Assert.Single(findings).Severity);
    }

    [Theory]
    [InlineData("Xunit.Sdk.AsyncTestSyncContext")]
    [InlineData("Xunit.Sdk.MaxConcurrencySyncContext")]
    [InlineData("Xunit.v3.AsyncTestSyncContext")]
    [InlineData("Xunit.v3.MaxConcurrencySyncContext")]
    public void Test_runner_contexts_run_continuations_on_other_threads(string type) =>
        Assert.True(SyncOverAsyncDetector.RunsContinuationsElsewhere(new ObjectRef(0x1000, type)));

    [Theory]
    [InlineData("MyApp.UiSynchronizationContext")]
    [InlineData("HangApp.SingleThreadSynchronizationContext")]
    [InlineData("System.Windows.Forms.WindowsFormsSynchronizationContext")]
    [InlineData("System.Windows.Threading.DispatcherSynchronizationContext")]
    [InlineData("System.Web.AspNetSynchronizationContext")]
    [InlineData("MyApp.NotAsyncTestSyncContext")]
    public void Other_contexts_may_own_a_single_thread(string type) =>
        Assert.False(SyncOverAsyncDetector.RunsContinuationsElsewhere(new ObjectRef(0x1000, type)));

    [Fact]
    public void A_continuation_already_queued_on_the_test_context_is_still_a_deadlock()
    {
        // MaxConcurrencySyncContext with every one of its threads blocked: the continuation sits in its queue.
        var context = new ObjectRef(0x1000, "Xunit.Sdk.MaxConcurrencySyncContext");
        var finished = SlowFeed with
        {
            Resume = new ResumeTarget(context, ResumeStatus.Queued),
            Awaiting = SlowFeed.Awaiting! with { State = TaskState.RanToCompletion },
        };

        var f = Assert.Single(new SyncOverAsyncDetector().Detect(TestBlocking(finished, context), new HashSet<int>()));

        Assert.Equal("sync-over-async-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public void A_single_threaded_context_with_a_registered_continuation_is_still_a_deadlock()
    {
        var ui = new ObjectRef(0x1000, "System.Windows.Forms.WindowsFormsSynchronizationContext");

        var f = Assert.Single(new SyncOverAsyncDetector().Detect(TestBlocking(SlowFeed with { Resume = new ResumeTarget(ui, ResumeStatus.Registered) }, ui), new HashSet<int>()));

        Assert.Equal("sync-over-async-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
    }
}
