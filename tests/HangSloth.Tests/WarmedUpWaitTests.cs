using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using HangSloth.Reporting;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Stacks of a process that ran for a while with tiered PGO. Guarded devirtualization inlines the pool's most frequent
/// task delegate, lambda and all, into the dispatch loop, so the thread running it shows no frame of the app; and
/// ManualResetEventSlim.Wait inlined into its caller leaves the caller in Monitor.Wait on the event's own lock. The
/// collection code puts the inlined frame back (InlinedInto), and from there the verdict is the one cold code gets.
public class WarmedUpWaitTests
{
    private const ulong Gate = 0xA0, Task = 0x10;

    /// A pool thread's stack in a real dump once Task.InnerInvoke, the delegate call and the lambda were inlined into
    /// ExecutionContext.RunFromThreadPoolDispatchLoop: the lambda waits for a lock.
    private static readonly StackFrameInfo[] DispatchLoop =
    [
        Fx("System.Threading.Monitor", "Enter_Slowpath"),
        Fx("System.Threading.Monitor", "Enter"),
        Fx("System.Threading.ExecutionContext", "RunFromThreadPoolDispatchLoop"),
        Fx("System.Threading.Tasks.Task", "ExecuteWithThreadLocal"),
        Fx("System.Threading.ThreadPoolWorkQueue", "Dispatch"),
        Fx("System.Threading.PortableThreadPool+WorkerThread", "WorkerThreadStart"),
        Fx("System.Threading.Thread", "StartCallback"),
    ];

    /// The task's delegate, as the collection code puts it back above the frame it was inlined into.
    private static StackFrameInfo Lambda(string file = "Invoices.cs", int line = 21) =>
        App("MyApp.InvoiceBook+<>c__DisplayClass2_0", "<Close>b__0", file, line) with { InlinedInto = "RunFromThreadPoolDispatchLoop" };

    private static StackFrameInfo[] Restored(StackFrameInfo lambda) => [.. DispatchLoop[..2], lambda, .. DispatchLoop[2..]];

    private static readonly TaskInfo CloseTask = new()
    {
        Address = Task, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.Delegate, State = TaskState.Pending, Description = "Task.Run(InvoiceBook.Close (lambda))",
    };

    /// Thread 1 holds the gate and waits for the task, as in cold code: Task.Wait's frames survive warm-up.
    private static ThreadSnapshot Closer() =>
        Thread(1, [.. ResultWait.Take(4), Fx("System.Threading.Tasks.Task", "InternalWait"), App("MyApp.InvoiceBook", "Close", "Invoices.cs", 21) with { BlockingCall = ".Wait()" }, App("MyApp.Program", "Main", "Program.cs", 9)])
            with { BlockedOnTask = CloseTask };

    private static ThreadSnapshot PoolThread(IEnumerable<StackFrameInfo> frames, ulong? waitingFor = Gate) =>
        Thread(6, frames, ".NET TP Worker") with { IsThreadPoolWorker = true, RunningTask = Task, WaitingForLock = waitingFor };

    private static MonitorLock Lock(ulong address, string field, int owner, params int[] waiters) =>
        new() { Object = new ObjectRef(address, "System.Object"), FieldName = field, OwnerThreadId = owner, WaiterThreadIds = waiters };

    // ------------------------------------------------------------ where the delegate goes back

    [Fact]
    public void The_delegate_goes_back_above_the_frame_it_was_inlined_into()
    {
        Assert.Equal(2, SnapshotBuilder.InlinedDelegateIndex(DispatchLoop));

        // Without a captured ExecutionContext, Task.ExecuteWithThreadLocal calls the delegate itself.
        StackFrameInfo[] noContext = [Fx("System.Threading.Monitor", "Enter_Slowpath"), .. DispatchLoop[3..]];
        Assert.Equal(1, SnapshotBuilder.InlinedDelegateIndex(noContext));

        // A Task.Factory.StartNew(..., LongRunning) thread has no dispatch loop, only the task's own frames.
        StackFrameInfo[] longRunning =
        [
            Fx("System.Threading.Monitor", "Enter_Slowpath"),
            Fx("System.Threading.Tasks.Task", "ExecuteWithThreadLocal"),
            Fx("System.Threading.Tasks.Task", "ExecuteEntryUnsafe"),
            Fx("System.Threading.Tasks.ThreadPoolTaskScheduler+<>c", "<.cctor>b__10_0"),
            Fx("System.Threading.Thread", "StartCallback"),
        ];
        Assert.Equal(1, SnapshotBuilder.InlinedDelegateIndex(longRunning));
    }

    [Fact]
    public void A_stack_with_app_code_or_without_task_frames_gets_no_delegate()
    {
        Assert.Equal(-1, SnapshotBuilder.InlinedDelegateIndex(Restored(Lambda())));
        Assert.Equal(-1, SnapshotBuilder.InlinedDelegateIndex([Fx("System.Threading.Monitor", "Enter_Slowpath"), Fx("System.Threading.Thread", "StartCallback")]));
        // A timer callback runs in ExecutionContext.RunInternal too, but no task does: nothing names its code.
        Assert.Equal(-1, SnapshotBuilder.InlinedDelegateIndex(
            [Fx("System.Threading.Monitor", "Enter_Slowpath"), Fx("System.Threading.ExecutionContext", "RunInternal"), Fx("System.Threading.TimerQueue", "FireNextTimers"), .. DispatchLoop[4..]]));
    }

    // ------------------------------------------------------------ a task run inside another

    /// A pool task's lambda (still on the stack) waits for a task that hasn't started, so the runtime runs that task
    /// inline on the same thread, and PGO inlined the inner lambda into ExecutionContext.RunInternal.
    private static readonly StackFrameInfo[] InlineWait =
    [
        Fx("System.Threading.Monitor", "Enter_Slowpath"),
        Fx("System.Threading.Monitor", "Enter"),
        Fx("System.Threading.ExecutionContext", "RunInternal"),
        Fx("System.Threading.Tasks.Task", "ExecuteWithThreadLocal"),
        Fx("System.Threading.Tasks.Task", "WrappedTryRunInline"),
        Fx("System.Threading.Tasks.Task", "InternalWaitCore"),
        Fx("System.Threading.Tasks.Task", "InternalWait"),
        App("MyApp.InvoiceBook+<>c__DisplayClass4_0", "<CloseInline>b__0", "Invoices.cs", 30) with { BlockingCall = ".Wait()" },
        .. DispatchLoop[2..],
    ];

    /// The same with Task.RunSynchronously, and both lambdas inlined: the outer one into the dispatch loop.
    private static readonly StackFrameInfo[] Nested =
    [
        .. DispatchLoop[..2],
        Fx("System.Threading.ExecutionContext", "RunInternal"),
        Fx("System.Threading.Tasks.Task", "ExecuteWithThreadLocal"),
        Fx("System.Threading.Tasks.Task", "InternalRunSynchronously"),
        .. DispatchLoop[2..],
    ];

    private static StackFrameInfo PostLambda() =>
        App("MyApp.InvoiceBook+<>c__DisplayClass5_0", "<Post>b__0", "Invoices.cs", 36) with { InlinedInto = "RunInternal" };

    [Fact]
    public void The_delegate_of_a_task_run_inside_another_goes_back_above_the_inner_task_s_frames()
    {
        Assert.Equal(2, SnapshotBuilder.InlinedDelegateIndex(Nested));
        Assert.Equal(2, SnapshotBuilder.InlinedDelegateIndex(InlineWait));
        Assert.Equal(2, SnapshotBuilder.TaskRuns(Nested));
        Assert.Equal(2, SnapshotBuilder.TaskRuns(InlineWait));
        // ExecuteFromThreadPool, ExecuteEntryUnsafe and ExecuteWithThreadLocal one above the other are one task.
        Assert.Equal(1, SnapshotBuilder.TaskRuns([.. DispatchLoop[..4], Fx("System.Threading.Tasks.Task", "ExecuteEntryUnsafe"), Fx("System.Threading.Tasks.Task", "ExecuteFromThreadPool"), .. DispatchLoop[4..]]));
        Assert.Equal(1, SnapshotBuilder.TaskRuns(DispatchLoop));
    }

    [Fact]
    public void Only_the_runtime_s_record_of_the_innermost_task_names_the_delegate_of_a_nested_stack()
    {
        const ulong Outer = 0x10, Inner = 0x20;
        // The inner task's frames keep the outer task alive as well, and the frame lookup can give that one first.
        Assert.Null(SnapshotBuilder.DelegateTask(Nested, current: null, currentKept: false, running: Outer));
        Assert.Null(SnapshotBuilder.DelegateTask(InlineWait, current: null, currentKept: false, running: Outer));
        Assert.Equal(Inner, SnapshotBuilder.DelegateTask(Nested, current: Inner, currentKept: true, running: Outer));
        // A record that no frame running tasks keeps alive isn't the task those frames run.
        Assert.Null(SnapshotBuilder.DelegateTask(Nested, current: Inner, currentKept: false, running: Outer));
        // With one task on the stack, the task its frames keep alive is that one.
        Assert.Equal(Outer, SnapshotBuilder.DelegateTask(DispatchLoop, current: null, currentKept: false, running: Outer));
        Assert.Equal(Inner, SnapshotBuilder.DelegateTask(DispatchLoop, current: Inner, currentKept: true, running: Outer));
    }

    [Fact]
    public void A_task_run_inline_looks_like_a_wait_for_itself_without_its_delegate_and_waits_for_the_lock_with_it()
    {
        StackFrameInfo[] restored = [.. InlineWait[..2], PostLambda(), .. InlineWait[2..]];
        Assert.Equal(BlockingKind.TaskWait, FrameClassifier.Classify(InlineWait));
        Assert.Equal(BlockingKind.MonitorEnter, FrameClassifier.Classify(restored));

        // The thread runs the outer task (the frame lookup's answer), and inside it the inner one that waits for the lock.
        var closer = Closer() with { BlockedOnTask = CloseTask with { Description = "Task.Run(InvoiceBook.CloseInline (lambda))" } };
        var snapshot = Process(closer, PoolThread(restored)) with { Locks = [Lock(Gate, "InvoiceBook._sync", 1, 6)] };

        var report = new HangAnalyzer().Analyze(snapshot);

        Verdict.Expect(report, "critical wait-cycle");
        Assert.Contains("Thread 6 (\".NET TP Worker\") is waiting in InvoiceBook.Post (lambda) (Invoices.cs:36) for the lock on InvoiceBook._sync, which Thread 1 holds.", report.Findings[0].Details);
    }

    [Fact]
    public void The_call_a_restored_delegate_makes_is_the_frame_above_it_past_the_marshalling_stub()
    {
        StackFrameInfo stub = Fx("System.Threading.Thread", "<Join>g____PInvoke|56_0") with { IsRuntimeFrame = true };
        StackFrameInfo[] join = [stub, stub, Fx("(dynamicClass)", "IL_STUB_PInvoke"), Lambda() with { Source = null }, .. DispatchLoop[2..]];
        Assert.Same(stub, SnapshotBuilder.DelegateCallee(join, 3));

        // A ManualResetEventSlim.Wait put back above the delegate is the call the delegate makes.
        StackFrameInfo wait = Fx("System.Threading.ManualResetEventSlim", "Wait") with { InlinedInto = "<Close>b__0" };
        Assert.Same(wait, SnapshotBuilder.DelegateCallee([Fx("System.Threading.Monitor", "Wait"), wait, Lambda() with { Source = null }], 2));

        Assert.Null(SnapshotBuilder.DelegateCallee([Lambda() with { Source = null }, .. DispatchLoop[2..]], 0));
    }

    // ------------------------------------------------------------ what the restored frame changes

    [Fact]
    public void Without_the_delegate_a_pool_thread_waiting_for_a_lock_looks_idle_and_with_it_waits_for_the_lock()
    {
        Assert.Equal(BlockingKind.Idle, FrameClassifier.Classify(DispatchLoop));
        Assert.Equal(BlockingKind.MonitorEnter, FrameClassifier.Classify(Restored(Lambda())));
    }

    [Fact]
    public void A_lock_holder_waiting_for_a_task_whose_inlined_delegate_needs_the_lock_is_a_deadlock()
    {
        var snapshot = Process(Closer(), PoolThread(Restored(Lambda()))) with { Locks = [Lock(Gate, "InvoiceBook._sync", 1, 6)] };

        var report = new HangAnalyzer().Analyze(snapshot);

        Verdict.Expect(report, "critical wait-cycle");
        var f = report.Findings[0];
        Assert.Equal("Deadlock: threads waiting on each other", f.Title);
        Assert.Equal("Thread 1 and Thread 6 (\".NET TP Worker\") are waiting for each other, so neither can ever continue. Start at Invoices.cs:21.", f.Summary);
        Assert.Equal(
        [
            "Thread 1 holds the lock on InvoiceBook._sync and is blocked in .Wait() at InvoiceBook.Close (Invoices.cs:21), waiting for Task.Run(InvoiceBook.Close (lambda)), which is running on Thread 6 (\".NET TP Worker\").",
            "Thread 6 (\".NET TP Worker\") is waiting in InvoiceBook.Close (lambda) (Invoices.cs:21) for the lock on InvoiceBook._sync, which Thread 1 holds.",
        ], f.Details);
    }

    [Fact]
    public void A_delegate_whose_line_is_left_open_is_named_by_its_method()
    {
        var lambda = Lambda() with { Source = null, CandidateLines = [new SourceLocation("/src/Invoices.cs", 21), new SourceLocation("/src/Invoices.cs", 23)] };
        var snapshot = Process(Closer(), PoolThread(Restored(lambda))) with { Locks = [Lock(Gate, "InvoiceBook._sync", 1, 6)] };

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);

        Assert.Contains("Thread 6 (\".NET TP Worker\") is waiting in InvoiceBook.Close (lambda) for the lock on InvoiceBook._sync, which Thread 1 holds.", f.Details);
    }

    [Fact]
    public void A_task_whose_delegate_waits_for_a_lock_of_a_third_thread_is_no_cycle()
    {
        const ulong Audit = 0xB0;
        var auditor = Thread(3, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Auditor", "Review", "Auditor.cs", 14)], "auditor");
        var snapshot = Process(Closer(), auditor, PoolThread(Restored(Lambda()), waitingFor: Audit)) with
        {
            Locks = [Lock(Gate, "InvoiceBook._sync", 1), Lock(Audit, "Auditor._audit", 3, 6)],
        };

        var report = new HangAnalyzer().Analyze(snapshot);

        Verdict.Expect(report, "warning lock-contention, info blocked-on-task");
        Assert.Equal("Thread 6 (\".NET TP Worker\") is stuck waiting for the lock on Auditor._audit, held by Thread 3 (\"auditor\"), which is sleeping (Thread.Sleep) in Auditor.cs:14 while holding it.",
            report.Findings.Single(f => f.Id == "lock-contention").Summary);
        Assert.Contains("Waiting at: Invoices.cs:21.", report.Findings.Single(f => f.Id == "lock-contention").Details);
    }

    [Fact]
    public void A_pool_thread_running_an_inlined_delegate_is_listed_with_the_threads_in_the_app_code()
    {
        // The delegate is busy in code of its own: the frame that runs it is the top of the stack.
        var busy = Thread(6, [Lambda() with { Source = null }, .. DispatchLoop[2..]], ".NET TP Worker") with { IsThreadPoolWorker = true, RunningTask = Task };
        var w = new StringWriter { NewLine = "\n" };

        TextReporter.Write(new HangAnalyzer().Analyze(Process(busy)), w);

        Assert.Contains("Threads in your code\n   Thread 6 (\".NET TP Worker\") is running InvoiceBook.Close (lambda).\n", w.ToString());
        Assert.Contains("      › InvoiceBook.Close (lambda)\n", w.ToString());
    }

    // ------------------------------------------------------------ ManualResetEventSlim.Wait inlined into its caller

    private static readonly StackFrameInfo Holder = App("MyApp.Ledger", "WaitFor", "Ledger.cs", 12);

    private static StackFrameInfo[] EventWait(bool inlined) =>
        [Fx("System.Threading.Monitor", "Wait"), .. inlined ? new[] { Fx("System.Threading.ManualResetEventSlim", "Wait") with { InlinedInto = "WaitFor" } } : [], Holder, App("MyApp.Program", "Main", "Program.cs", 9)];

    [Fact]
    public void Monitor_Wait_frames_count_its_stubs()
    {
        StackFrameInfo stub = Fx("System.Threading.Monitor", "<Wait>g____PInvoke|24_0") with { IsRuntimeFrame = true };
        Assert.Equal(3, SnapshotBuilder.MonitorWaitDepth([stub, stub, Fx("(dynamicClass)", "IL_STUB_PInvoke"), Holder]));
        Assert.Equal(1, SnapshotBuilder.MonitorWaitDepth(EventWait(inlined: true)));
        Assert.Equal(0, SnapshotBuilder.MonitorWaitDepth([Holder]));
    }

    [Fact]
    public void An_event_wait_whose_Wait_was_put_back_reads_as_the_cold_one()
    {
        // The cold stack has ManualResetEventSlim.Wait's own frame; the restored one has it with InlinedInto.
        StackFrameInfo[] cold = [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.ManualResetEventSlim", "Wait"), Holder, App("MyApp.Program", "Main", "Program.cs", 9)];
        foreach (var frames in new[] { cold, EventWait(inlined: true) })
        {
            var holder = Thread(4, frames, "ledger-close");
            var approvers = new[] { 5, 6 }.Select(id => Thread(id, [.. MonitorEnter, App("MyApp.Ledger", "Approve", "Ledger.cs", 17)], $"approver-{id}") with { WaitingForLock = Gate });
            var snapshot = Process([holder, .. approvers]) with { Locks = [Lock(Gate, "Ledger._sync", 4, 5, 6)] };

            Assert.Equal(BlockingKind.MonitorWait, FrameClassifier.Classify(frames));
            var report = new HangAnalyzer().Analyze(snapshot);
            Verdict.Expect(report, "warning lock-contention");
            Assert.Equal("2 threads are stuck waiting for the lock on Ledger._sync, held by Thread 4 (\"ledger-close\"), which is waiting for a ManualResetEventSlim to be set in Ledger.cs:12 while holding it.",
                report.Findings[0].Summary);
        }
    }

    [Fact]
    public void Without_the_event_frame_the_same_stack_reads_as_a_wait_for_a_pulse()
    {
        // What the collection code avoids: Monitor.Wait right above the app's code is the app's own Monitor.Wait.
        var holder = Thread(4, EventWait(inlined: false), "ledger-close");
        Assert.Equal("waiting in Monitor.Wait in Ledger.cs:12", Describe.Activity(holder));
        Assert.Equal("waiting for a ManualResetEventSlim to be set in Ledger.cs:12", Describe.Activity(holder with { Frames = EventWait(inlined: true) }));
    }
}
