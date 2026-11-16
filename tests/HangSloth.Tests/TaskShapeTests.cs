using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Delegate tasks that aren't running: one created with new Task(...) and never started, and one that was started
/// but still waits in its scheduler's queue for a thread. Neither is "still running".
public class TaskShapeTests
{
    private static Finding Blocked(TaskInfo waited) =>
        Assert.Single(new BlockedOnTaskDetector().Detect(
            Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = waited }), new HashSet<int>()));

    [Theory]
    [InlineData(0x0, TaskKind.NotStarted)]          // new Task(...)
    [InlineData(0x12008, TaskKind.Queued)]          // Task.Run behind busy threads: started, delegate not invoked
    [InlineData(0x32008, TaskKind.Delegate)]        // running
    [InlineData(0x2, TaskKind.NotStarted)]          // new Task(..., TaskCreationOptions.LongRunning)
    public void Delegate_task_state_comes_from_its_flags(int flags, TaskKind expected) =>
        Assert.Equal(expected, SnapshotBuilder.DelegateTaskKind(flags));

    [Fact]
    public void Unreadable_flags_say_nothing_about_the_task() =>
        Assert.Equal(TaskKind.Delegate, SnapshotBuilder.DelegateTaskKind(null));

    [Fact]
    public void Waiting_for_a_task_that_was_never_started_is_a_warning()
    {
        var f = Blocked(new TaskInfo { Address = 0x500, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.NotStarted, Description = "new Task(InvoiceExport.WriteFile)" });

        Assert.Equal("Waiting for a task that was never started", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Result at Program.cs:5, waiting for new Task(InvoiceExport.WriteFile), which was never started.", f.Summary);
        Assert.Equal(["A task created with new Task(...) runs only once something calls Start() on it. Until then, every wait for it waits forever."], f.Details);
        Assert.Equal("Call Start() on the task, or create it with Task.Run, which starts it right away.", f.Advice);
    }

    [Fact]
    public void Waiting_for_a_queued_task_does_not_call_it_running()
    {
        var f = Blocked(new TaskInfo { Address = 0x500, TypeName = "System.Threading.Tasks.Task<System.Int32>", Kind = TaskKind.Queued, Description = "Task.Run(Pricing.Compute)" });

        Assert.Equal("Thread blocked on a task", f.Title);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Result at Program.cs:5, waiting for Task.Run(Pricing.Compute), which is queued and hasn't started running: no thread has picked it up yet.", f.Summary);
    }

    [Fact]
    public void Task_Run_with_an_async_lambda_that_is_still_queued_is_named_once()
    {
        var outer = new TaskInfo { Address = 0x700, TypeName = "System.Threading.Tasks.Task<System.Threading.Tasks.Task>", Kind = TaskKind.Queued, Description = "Task.Run(Mailer.Flush (lambda))" };
        var wrapper = new TaskInfo
        {
            Address = 0x500, TypeName = "System.Threading.Tasks.UnwrapPromise<System.Threading.Tasks.VoidTaskResult>",
            Kind = TaskKind.Wrapper, Description = "Task.Run(...)", Awaiting = outer,
        };

        Assert.Equal("Thread 1 is blocked in .Result at Program.cs:5, waiting for Task.Run(Mailer.Flush (lambda)), which is queued and hasn't started running: no thread has picked it up yet.",
            Blocked(wrapper).Summary);
    }

    [Fact]
    public void A_never_started_task_behind_a_continuation_is_still_a_warning()
    {
        var never = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.NotStarted, Description = "new Task(Report.Build)" };
        var continuation = new TaskInfo
        {
            Address = 0x500, TypeName = "System.Threading.Tasks.ContinuationTaskFromTask", Kind = TaskKind.Continuation,
            Description = "a ContinueWith(...) task", Awaiting = never,
        };

        var f = Blocked(continuation);

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Result at Program.cs:5, waiting for a ContinueWith(...) task, which runs after new Task(Report.Build), which was never started.", f.Summary);
    }
}
