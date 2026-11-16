using HangSloth.Analysis;

namespace HangSloth.Tests;

/// Runs HangApp's task-chain modes: a thread blocked on Task.Run(async ...), Unwrap(), WaitAsync or
/// ContinueWith must be explained by the work behind that task, not by the wrapper it happens to wait on.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class TaskChainEndToEndTests
{
    private const string NobodyCompletes = "Waiting for a task nobody completes";

    /// The scenarios need a moment to reach their final wait; dump again until the chain ends where it should.
    private static Finding BlockedOnTask(string mode)
    {
        var report = SampleApp.Analyze(mode, until: r => r.Findings.Any(f => f.Severity == Severity.Warning));
        var f = Assert.Single(report.Findings);
        Assert.Equal("blocked-on-task", f.Id);
        return f;
    }

    [Fact]
    public void Wait_on_Task_Run_with_an_async_lambda_names_what_the_lambda_waits_for()
    {
        var f = BlockedOnTask("task-run-async");
        Assert.Equal(NobodyCompletes, f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Matches(@"^Thread \d+ is blocked in \.Wait\(\) at TaskChainScenarios\.cs:12, waiting for Task\.Run\(async \.\.\.\), " +
                       @"which waits for TaskRunAsyncScenario\.Run \(lambda\) \(paused at TaskChainScenarios\.cs:12\), " +
                       @"which awaits TaskRunAsyncScenario\.MailOutbox\.FlushAsync \(paused at TaskChainScenarios\.cs:23\), " +
                       @"which awaits a TaskCompletionSource task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Result_of_Task_Run_with_an_async_lambda_names_what_the_lambda_waits_for()
    {
        var f = BlockedOnTask("task-run-async-result");
        Assert.Equal(NobodyCompletes, f.Title);
        Assert.Matches(@"^Thread \d+ is blocked in \.Result at TaskChainScenarios\.cs:34, waiting for Task\.Run\(async \.\.\.\), " +
                       @"which waits for TaskRunAsyncResultScenario\.Run \(lambda\) \(paused at TaskChainScenarios\.cs:34\), " +
                       @"which awaits a TaskCompletionSource<decimal> task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Wait_on_WaitAsync_names_the_task_behind_it()
    {
        // The task has a second continuation, so the WaitAsync promise sits in a continuation list.
        var f = BlockedOnTask("wait-async");
        Assert.Equal(NobodyCompletes, f.Title);
        Assert.Matches(@"^Thread \d+ is blocked in \.Wait\(\) at TaskChainScenarios\.cs:47, waiting for WaitAsync\(\.\.\.\), " +
                       @"which waits for a TaskCompletionSource<bool> task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Result_of_StartNew_Unwrap_names_what_the_lambda_waits_for()
    {
        var f = BlockedOnTask("unwrap");
        Assert.Equal(NobodyCompletes, f.Title);
        Assert.Matches(@"^Thread \d+ is blocked in \.Result at TaskChainScenarios\.cs:57, waiting for Unwrap\(\), " +
                       @"which waits for UnwrapScenario\.Run \(lambda\) \(paused at TaskChainScenarios\.cs:61\), " +
                       @"which awaits a TaskCompletionSource<int> task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Result_of_ContinueWith_names_the_task_it_runs_after()
    {
        var f = BlockedOnTask("continue-with");
        Assert.Equal(NobodyCompletes, f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Matches(@"^Thread \d+ is blocked in \.Result at TaskChainScenarios\.cs:73, waiting for a ContinueWith\(\.\.\.\) task, " +
                       @"which runs after a TaskCompletionSource task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Lock_held_while_waiting_on_Task_Run_whose_async_lambda_needs_it_after_an_await()
    {
        // The lambda was resumed on a pool thread after its await: no Task.Run body is on that thread's stack.
        var f = WaitCycle("lock-task-run-async", line: 99);
        Assert.Contains(f.Details, d => System.Text.RegularExpressions.Regex.IsMatch(d,
            @"^Thread \d+ holds the lock on LockTaskRunAsyncScenario\.StockReport\._sync and is blocked in \.Wait\(\) at LockTaskRunAsyncScenario\.StockReport\.Generate \(TaskChainScenarios\.cs:99\), " +
            @"waiting for LockTaskRunAsyncScenario\.StockReport\.Generate \(lambda\), which is running on Thread \d+( \(""[^""]*""\))?\.$"));
    }

    [Fact]
    public void Lock_held_while_waiting_on_Task_Run_whose_async_lambda_needs_it_before_its_first_await()
    {
        var f = WaitCycle("lock-task-run-async-start", line: 97);
        Assert.Contains(f.Details, d => System.Text.RegularExpressions.Regex.IsMatch(d,
            @"^Thread \d+ holds the lock on LockTaskRunAsyncScenario\.StockReport\._sync and is blocked in \.Wait\(\) at LockTaskRunAsyncScenario\.StockReport\.Generate \(TaskChainScenarios\.cs:97\), " +
            @"waiting for Task\.Run\(LockTaskRunAsyncScenario\.StockReport\.Generate \(lambda\)\), which is running on Thread \d+( \(""[^""]*""\))?\.$"));
    }

    [Fact]
    public void Semaphore_WaitAsync_with_a_token_is_explained_as_a_semaphore_wait()
    {
        var report = SampleApp.Analyze("semaphore-token", until: r => r.Findings.Any(f => f.Id == "semaphore-wait"));
        var f = Assert.Single(report.Findings);
        Assert.Equal("semaphore-wait", f.Id);
        // The method that waits runs on the object that owns the semaphore; that alone is not a self-deadlock.
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Matches(@"^Thread \d+ is blocked in \.Wait\(\) at TaskChainScenarios\.cs:117, waiting for SemaphoreTokenScenario\.LabelPrinter\.PrintAsync \(paused at TaskChainScenarios\.cs:132\), " +
                       @"which is waiting to enter SemaphoreTokenScenario\.LabelPrinter\._device \(", f.Summary);
        Assert.DoesNotContain("TaskCompletionSource", f.Summary);
    }

    [Fact]
    public void Wait_on_WhenAny_of_three_tasks_names_WhenAny_and_a_task_behind_it()
    {
        var f = BlockedOnTask("when-any");
        Assert.Equal(NobodyCompletes, f.Title);
        Assert.Matches(@"^Thread \d+ is blocked in \.Wait\(\) at TaskChainScenarios\.cs:147, waiting for Task\.WhenAny\(\.\.\.\), " +
                       @"which waits for the first of its 3 tasks: a TaskCompletionSource task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Wait_on_WhenAny_with_a_timeout_among_its_tasks_is_not_a_task_nobody_completes()
    {
        var f = OnlyInformation("when-any-timeout");
        Assert.Matches(@"^Thread \d+ is blocked in \.Wait\(\) at TaskChainScenarios\.cs:159, waiting for Task\.WhenAny\(\.\.\.\)\.$", f.Summary);
    }

    [Fact]
    public void WaitAny_with_a_timeout_among_its_tasks_is_not_a_task_nobody_completes()
    {
        var f = OnlyInformation("wait-any-timeout");
        Assert.Matches(@"^Thread \d+ is blocked in Task\.WaitAny\(\.\.\.\) at TaskChainScenarios\.cs:169, waiting for one of its 2 tasks\.$", f.Summary);
    }

    [Fact]
    public void Task_Run_with_a_lambda_that_returns_a_task_is_not_called_async()
    {
        var f = BlockedOnTask("task-run-returns-task");
        Assert.Equal(NobodyCompletes, f.Title);
        Assert.Matches(@"^Thread \d+ is blocked in \.Wait\(\) at TaskChainScenarios\.cs:180, waiting for Task\.Run\(\.\.\.\), " +
                       @"which waits for a TaskCompletionSource<int> task that nothing has completed yet\.$", f.Summary);
    }

    [Fact]
    public void Thread_blocked_on_an_async_method_in_Monitor_Wait_on_a_pool_thread_waits_for_that_thread()
    {
        // The method went on after its await on a pool thread: no Task.Run body is on that thread's stack.
        var report = SampleApp.Analyze("resumed-monitor-wait", until: r => r.Findings.Any(f => f.Id == "monitor-wait"));
        var f = Assert.Single(report.Findings);
        Assert.Equal("monitor-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Matches(@"^Thread \d+( \(""[^""]*""\))? is waiting in Monitor\.Wait on ResumedMonitorWaitScenario\.PrintSpooler\._sync, .*Thread \d+ is waiting for it to finish\. Start at TaskChainScenarios\.cs:200\.$", f.Summary);
        Assert.Contains(f.Details, d => System.Text.RegularExpressions.Regex.IsMatch(d,
            @"^Thread \d+ is waiting for Thread \d+( \(""[^""]*""\))? \(blocked on ResumedMonitorWaitScenario\.PrintSpooler\.RunAsync\) in ResumedMonitorWaitScenario\.Run \(TaskChainScenarios\.cs:189\)\.$"));
    }

    /// A wait that something will end, if slowly: told as information only.
    private static Finding OnlyInformation(string mode)
    {
        var report = SampleApp.Analyze(mode, until: r => r.Findings.Any(f => f.Id == "blocked-on-task"));
        var f = Assert.Single(report.Findings);
        Assert.Equal("blocked-on-task", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread blocked on a task", f.Title);
        return f;
    }

    private static Finding WaitCycle(string mode, int line)
    {
        var report = SampleApp.Analyze(mode, until: r => r.Findings.Any(f => f.Id == "wait-cycle"));
        var f = Assert.Single(report.Findings);
        Assert.Equal("wait-cycle", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: threads waiting on each other", f.Title);
        Assert.Matches($@"^Thread \d+ and Thread \d+( \(""[^""]*""\))? are waiting for each other, so neither can ever continue\. Start at TaskChainScenarios\.cs:{line}\.$", f.Summary);
        Assert.Contains(f.Details, d => d.Contains("CollectRows (TaskChainScenarios.cs:105) for the lock on LockTaskRunAsyncScenario.StockReport._sync"));
        return f;
    }
}
