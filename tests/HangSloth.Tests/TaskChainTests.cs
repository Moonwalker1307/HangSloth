using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Threads blocked on a task that only stands in for other work: Task.Run(async ...), WaitAsync, ContinueWith.
public class TaskChainTests
{
    private static readonly StackFrameInfo[] WaitCall =
        [Fx("System.Threading.Tasks.Task", "InternalWaitCore"), Fx("System.Threading.Tasks.Task", "Wait")];

    private static readonly TaskInfo NeverCompleted =
        new() { Address = 0x900, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.Promise, Description = "a TaskCompletionSource task" };

    private static TaskInfo TaskRunAsync(TaskInfo source) => new()
    {
        Address = 0x500, TypeName = "System.Threading.Tasks.UnwrapPromise<System.Threading.Tasks.VoidTaskResult>",
        Kind = TaskKind.Wrapper, Description = "Task.Run(async ...)", Awaiting = source,
    };

    private static TaskInfo AsyncMethod(string name, string file, int line, TaskInfo? awaiting) => new()
    {
        Address = 0x600, TypeName = $"AsyncStateMachineBox<MyApp.{name}>", Kind = TaskKind.AsyncMethod, Description = name,
        AwaitLocation = new SourceLocation($"/src/{file}", line), Awaiting = awaiting,
    };

    private static Finding Blocked(TaskInfo waited) =>
        Assert.Single(new BlockedOnTaskDetector().Detect(
            Process(Thread(1, [.. WaitCall, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = waited }), new HashSet<int>()));

    [Fact]
    public void Task_Run_with_an_async_lambda_is_followed_to_what_the_lambda_waits_for()
    {
        var f = Blocked(TaskRunAsync(AsyncMethod("Mailer.Flush (lambda)", "Mailer.cs", 12, NeverCompleted)));

        Assert.Equal("Waiting for a task nobody completes", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for Task.Run(async ...), which waits for Mailer.Flush (lambda) (paused at Mailer.cs:12), " +
                     "which awaits a TaskCompletionSource task that nothing has completed yet.", f.Summary);
    }

    [Fact]
    public void WaitAsync_names_the_task_it_waits_for()
    {
        var waitAsync = new TaskInfo
        {
            Address = 0x500, TypeName = "System.Threading.Tasks.Task+CancellationPromise<System.Boolean>",
            Kind = TaskKind.Wrapper, Description = "WaitAsync(...)", Awaiting = NeverCompleted,
        };

        var f = Blocked(waitAsync);

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for WaitAsync(...), which waits for a TaskCompletionSource task that nothing has completed yet.", f.Summary);
    }

    [Fact]
    public void ContinueWith_runs_after_the_task_it_continues()
    {
        var continuation = new TaskInfo
        {
            Address = 0x500, TypeName = "System.Threading.Tasks.ContinuationResultTaskFromTask<System.Int32>",
            Kind = TaskKind.Continuation, Description = "a ContinueWith(...) task", Awaiting = NeverCompleted,
        };

        var f = Blocked(continuation);

        Assert.Equal("Waiting for a task nobody completes", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for a ContinueWith(...) task, which runs after a TaskCompletionSource task that nothing has completed yet.", f.Summary);
    }

    [Fact]
    public void ContinueWith_that_has_started_is_only_information()
    {
        var f = Blocked(new TaskInfo { Address = 0x500, TypeName = "System.Threading.Tasks.ContinuationTaskFromTask", Kind = TaskKind.Continuation, Description = "a ContinueWith(...) task" });

        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for a ContinueWith(...) task.", f.Summary);
    }

    [Fact]
    public void Task_Run_whose_async_lambda_has_not_reached_its_first_await_is_named_once()
    {
        var start = new TaskInfo { Address = 0x700, TypeName = "System.Threading.Tasks.Task<System.Threading.Tasks.Task>", Kind = TaskKind.Delegate, Description = "Task.Run(Worker.Start (lambda))" };

        var f = Blocked(TaskRunAsync(start));

        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for Task.Run(Worker.Start (lambda)), which is still running.", f.Summary);
    }

    [Fact]
    public void Wrapper_whose_source_is_unknown_is_not_called_a_TaskCompletionSource()
    {
        var f = Blocked(new TaskInfo { Address = 0x500, TypeName = "System.Threading.Tasks.UnwrapPromise<System.Int32>", Kind = TaskKind.Unknown, Description = "Task.Run(async ...)" });

        Assert.Equal("Thread blocked on a task", f.Title);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for Task.Run(async ...).", f.Summary);
        Assert.Empty(f.Details);
    }

    [Fact]
    public void Semaphore_wait_behind_Task_Run_keeps_the_whole_path()
    {
        var node = new TaskInfo { Address = 0x800, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()" };
        var inner = AsyncMethod("Stock.ReserveAsync", "Stock.cs", 31, node);
        var outer = AsyncMethod("Stock.Refresh (lambda)", "Stock.cs", 18, inner) with { Address = 0x601 };

        var f = Blocked(TaskRunAsync(outer));

        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for Task.Run(async ...), which waits for Stock.Refresh (lambda) (paused at Stock.cs:18), " +
                     "which awaits Stock.ReserveAsync (paused at Stock.cs:31), which is waiting to enter a SemaphoreSlim (SemaphoreSlim.WaitAsync()).", f.Summary);
    }

    [Fact]
    public void Semaphore_wait_with_a_token_reads_like_a_plain_WaitAsync()
    {
        // SemaphoreSlim.WaitAsync(token) waits through its own async method and a WaitAsync(...) wrapper around the
        // queued waiter. The method that called it runs on the object that owns the semaphore, which must not
        // be mistaken for an earlier method in the chain that already holds it.
        const ulong printer = 0x4000;
        var node = new TaskInfo { Address = 0x800, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = 0x100 };
        var wrapper = new TaskInfo
        {
            Address = 0x801, TypeName = "System.Threading.Tasks.Task+CancellationPromise<System.Boolean>",
            Kind = TaskKind.Wrapper, Description = "WaitAsync(...)", Awaiting = node,
        };
        var plumbing = new TaskInfo
        {
            Address = 0x802, Kind = TaskKind.AsyncMethod, Description = "SemaphoreSlim.WaitUntilCountOrTimeoutAsync", Awaiting = wrapper,
            TypeName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Boolean>+AsyncStateMachineBox<System.Threading.SemaphoreSlim+<WaitUntilCountOrTimeoutAsync>d__33>",
        };
        var print = AsyncMethod("LabelPrinter.PrintAsync", "Printer.cs", 24, plumbing) with { ThisAddress = printer };
        var snapshot = Process(Thread(1, [.. WaitCall, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = print }) with
        {
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x100, "System.Threading.SemaphoreSlim"), FieldName = "LabelPrinter._device", ContainerAddress = printer, CurrentCount = 0, MaxCount = 1, AsyncWaiters = 1 }],
        };

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(snapshot, new HashSet<int>()));

        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim nobody releases", f.Title);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for LabelPrinter.PrintAsync (paused at Printer.cs:24), " +
                     "which is waiting to enter LabelPrinter._device (SemaphoreSlim.WaitAsync(), 0 of 1 slots free).", f.Summary);
    }

    [Fact]
    public void Semaphore_wait_behind_ContinueWith_is_not_taken_for_a_second_wait_on_the_same_semaphore()
    {
        // PrintAsync awaits _device.WaitAsync().ContinueWith(...): PrintAsync is the caller of WaitAsync, not an
        // earlier method of the chain that already holds the semaphore.
        const ulong printer = 0x4000;
        var node = new TaskInfo { Address = 0x800, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = 0x100 };
        var continuation = new TaskInfo
        {
            Address = 0x801, TypeName = "System.Threading.Tasks.ContinuationResultTaskFromResultTask<System.Boolean, System.Boolean>",
            Kind = TaskKind.Continuation, Description = "a ContinueWith(...) task", Awaiting = node,
        };
        var print = AsyncMethod("LabelPrinter.PrintAsync", "Printer.cs", 24, continuation) with { ThisAddress = printer };
        var snapshot = Process(Thread(1, [.. WaitCall, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = print }) with
        {
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x100, "System.Threading.SemaphoreSlim"), FieldName = "LabelPrinter._device", ContainerAddress = printer, CurrentCount = 0, MaxCount = 1, AsyncWaiters = 1 }],
        };

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(snapshot, new HashSet<int>()));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim nobody releases", f.Title);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for LabelPrinter.PrintAsync (paused at Printer.cs:24), " +
                     "which is waiting to enter LabelPrinter._device (SemaphoreSlim.WaitAsync(), 0 of 1 slots free).", f.Summary);
    }

    [Fact]
    public void ContinueWith_after_ContinueWith_names_both()
    {
        var first = new TaskInfo
        {
            Address = 0x501, TypeName = "System.Threading.Tasks.ContinuationResultTaskFromResultTask<System.Int32, System.Int32>",
            Kind = TaskKind.Continuation, Description = "a ContinueWith(...) task", Awaiting = NeverCompleted,
        };
        var second = first with { Address = 0x502, Awaiting = first };

        var f = Blocked(second);

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Wait() at Program.cs:5, waiting for a ContinueWith(...) task, which runs after a ContinueWith(...) task, " +
                     "which runs after a TaskCompletionSource task that nothing has completed yet.", f.Summary);
    }

    [Fact]
    public void Lock_holder_blocked_on_Task_Run_whose_async_lambda_needs_the_lock_is_a_deadlock()
    {
        // The lambda was resumed after an await on pool thread 7, so thread 7 is running its async method (0x600).
        var lambda = AsyncMethod("StockReport.Generate (lambda)", "Reports.cs", 14, null) with { AwaitLocation = null };
        var snapshot = Process(
            Thread(1, [.. ResultWait, App("MyApp.StockReport", "Generate", "Reports.cs", 14)]) with { BlockedOnTask = TaskRunAsync(lambda) },
            Thread(7, [.. MonitorEnter, App("MyApp.StockReport", "CollectRows", "Reports.cs", 20)], ".NET TP Worker") with { WaitingForLock = 0x100, RunningTask = 0x600 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "StockReport._sync", OwnerThreadId = 1, WaiterThreadIds = [7] }],
        };

        var f = new HangAnalyzer().Analyze(snapshot).Findings[0];

        Assert.Equal("wait-cycle", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread 1 holds the lock on StockReport._sync and is blocked in .Result at StockReport.Generate (Reports.cs:14), " +
                     "waiting for StockReport.Generate (lambda), which is running on Thread 7 (\".NET TP Worker\").", f.Details[0]);
    }

    [Fact]
    public void Lock_holder_blocked_on_an_async_method_that_needs_the_lock_after_an_await_is_a_deadlock()
    {
        // No Task.Run: the method itself went on after its await on pool thread 7, which is running its box (0x600).
        var post = AsyncMethod("Ledger.PostAsync", "Ledger.cs", 18, null) with { AwaitLocation = null };
        var snapshot = Process(
            Thread(1, [.. WaitCall, App("MyApp.Ledger", "Run", "Ledger.cs", 10)]) with { BlockedOnTask = post },
            Thread(7, [.. MonitorEnter, App("MyApp.Ledger+<PostAsync>d__2", "MoveNext", "Ledger.cs", 18)], ".NET TP Worker") with { WaitingForLock = 0x100, RunningTask = 0x600 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Ledger._sync", OwnerThreadId = 1, WaiterThreadIds = [7] }],
        };

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);

        Assert.Equal("wait-cycle", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread 1 holds the lock on Ledger._sync and is blocked in .Wait() at Ledger.Run (Ledger.cs:10), " +
                     "waiting for Ledger.PostAsync, which is running on Thread 7 (\".NET TP Worker\").", f.Details[0]);
    }

    [Fact]
    public void Async_method_waiting_in_Monitor_Wait_with_a_thread_blocked_on_it_is_a_monitor_wait()
    {
        // The method went on after its await on pool thread 7 and sits in Monitor.Wait there; thread 1 waits for the method.
        var run = AsyncMethod("PrintSpooler.RunAsync", "Spooler.cs", 12, null) with { AwaitLocation = null };
        var wait = new PulseWait { Object = new ObjectRef(0x100, "System.Object"), FieldName = "PrintSpooler._sync" };
        var snapshot = Process(
            Thread(1, [.. WaitCall, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = run },
            Thread(7, [Fx("System.Threading.Monitor", "Wait"), App("MyApp.PrintSpooler+<RunAsync>d__1", "MoveNext", "Spooler.cs", 14)], ".NET TP Worker") with { WaitingForPulse = wait, RunningTask = 0x600 });

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);

        Assert.Equal("monitor-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread waiting for a Monitor.Pulse that may never come", f.Title);
        Assert.Contains("Thread 1 is waiting for it to finish.", f.Summary);
        Assert.Contains("Thread 1 is waiting for Thread 7 (\".NET TP Worker\") (blocked on PrintSpooler.RunAsync) in Program.Main (Program.cs:5).", f.Details);
        Assert.Equal([7, 1], f.ThreadIds);
    }

    [Theory]
    [InlineData("System.Threading.Tasks.UnwrapPromise<System.Threading.Tasks.VoidTaskResult>", true, TaskKind.AsyncMethod, "Task.Run(async ...)")]
    [InlineData("System.Threading.Tasks.UnwrapPromise<System.Int32>", true, TaskKind.Promise, "Task.Run(...)")]
    [InlineData("System.Threading.Tasks.UnwrapPromise<System.Int32>", true, TaskKind.SemaphoreWait, "Task.Run(...)")]
    [InlineData("System.Threading.Tasks.UnwrapPromise<System.Int32>", true, null, "Task.Run(...)")]
    [InlineData("System.Threading.Tasks.UnwrapPromise<System.Int32>", false, TaskKind.AsyncMethod, "Unwrap()")]
    [InlineData("System.Threading.Tasks.Task+CancellationPromise<System.Boolean>", true, TaskKind.Promise, "WaitAsync(...)")]
    [InlineData("System.Threading.Tasks.ValueTask<System.Int32>+ValueTaskSourceAsTask", true, null, "ValueTask.AsTask()")]
    [InlineData("System.Threading.Tasks.ValueTask+ValueTaskSourceAsTask", true, null, "ValueTask.AsTask()")]
    [InlineData("MyApp.OrderTask", true, null, null)]
    public void Wrappers_are_named_after_the_call_that_made_them(string typeName, bool fromTaskRun, TaskKind? source, string? name) =>
        Assert.Equal(name, SnapshotBuilder.WrapperName(typeName, fromTaskRun, source));

    [Theory]
    [InlineData("System.Threading.Tasks.Task", true)]
    [InlineData("System.Threading.Tasks.Task<System.Boolean>", true)]
    [InlineData("System.Threading.Tasks.Task<System.Threading.Tasks.Task<System.Int32>>", true)]
    [InlineData("System.Threading.Tasks.Task<System.Net.Http.HttpConnection>", true)]
    [InlineData("System.Threading.Tasks.UnwrapPromise<System.Int32>", false)]
    [InlineData("System.Threading.Tasks.Task+CancellationPromise<System.Boolean>", false)]
    [InlineData("System.Threading.Tasks.Task+WhenAllPromise", false)]
    [InlineData("System.Threading.Tasks.ValueTask<System.Int32>+ValueTaskSourceAsTask", false)]
    [InlineData("System.Threading.Tasks.Task<System.Int32>+Nested", false)]
    public void Only_exactly_Task_and_Task_of_T_count_as_a_TaskCompletionSource_task(string typeName, bool plain) =>
        Assert.Equal(plain, SnapshotBuilder.IsPlainTask(typeName));
}
