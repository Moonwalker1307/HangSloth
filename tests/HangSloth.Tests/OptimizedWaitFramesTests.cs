using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Optimized code inlines a framework wait's small public method into the app's method: the stack then shows the
/// generated P/Invoke stub or an internal method instead. Each row is the stack of a real dump, in code that ran
/// once (cold) and with DOTNET_TieredCompilation=0 (optimized), and both must be the same wait.
public class OptimizedWaitFramesTests
{
    /// A runtime frame (the InlinedCallFrame of a P/Invoke), as ClrMD shows a stub that was inlined.
    private static StackFrameInfo Stub(string type, string method) => Fx(type, method) with { IsRuntimeFrame = true };

    private static readonly StackFrameInfo Caller = App("MyApp.EmailWorker", "Stop", "Worker.cs", 22);

    public static TheoryData<string, StackFrameInfo[], StackFrameInfo[], BlockingKind> Rows => new()
    {
        {
            "thread.Join()",
            [Fx("System.Threading.Thread", "Join"), Fx("System.Threading.Thread", "Join")],
            [Stub("System.Threading.Thread", "<Join>g____PInvoke|56_0"), Stub("System.Threading.Thread", "<Join>g____PInvoke|56_0")],
            BlockingKind.Join
        },
        {
            // The stub's number differs on Windows (and between builds).
            "thread.Join() on Windows",
            [Fx("System.Threading.Thread", "Join"), Fx("System.Threading.Thread", "Join")],
            [Stub("System.Threading.Thread", "<Join>g____PInvoke|59_0")],
            BlockingKind.Join
        },
        {
            "Monitor.Wait(obj)",
            [Fx("System.Threading.Monitor", "Wait")],
            [Stub("System.Threading.Monitor", "<Wait>g____PInvoke|24_0"), Stub("System.Threading.Monitor", "<Wait>g____PInvoke|24_0")],
            BlockingKind.MonitorWait
        },
        {
            // In an async method's MoveNext the native call goes through the runtime's marshalling stub.
            "Monitor.Wait(obj) after an await",
            [Fx("System.Threading.Monitor", "Wait")],
            [Stub("System.Threading.Monitor", "<Wait>g____PInvoke|24_0"), Stub("System.Threading.Monitor", "<Wait>g____PInvoke|24_0"), Fx("(dynamicClass)", "IL_STUB_PInvoke")],
            BlockingKind.MonitorWait
        },
        {
            "GC.WaitForPendingFinalizers()",
            [Fx("System.GC", "WaitForPendingFinalizers")],
            [Stub("System.GC", "_WaitForPendingFinalizers"), Stub("System.GC", "_WaitForPendingFinalizers")],
            BlockingKind.Finalizers
        },
        {
            "process.WaitForExit()",
            [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Diagnostics.ProcessWaitState", "WaitForExit"), Fx("System.Diagnostics.Process", "WaitForExitCore"), Fx("System.Diagnostics.Process", "WaitForExit")],
            [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Diagnostics.ProcessWaitState", "WaitForExit")],
            BlockingKind.ProcessExit
        },
        {
            // Windows: WaitForExitCore is too big to inline, so its frame stays.
            "process.WaitForExit() on Windows",
            [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Diagnostics.Process", "WaitForExitCore"), Fx("System.Diagnostics.Process", "WaitForExit")],
            [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Diagnostics.Process", "WaitForExitCore")],
            BlockingKind.ProcessExit
        },
        {
            "lock (obj)",
            [Fx("System.Threading.Monitor", "Enter_Slowpath"), Fx("System.Threading.Monitor", "Enter")],
            [Fx("System.Threading.Monitor", "Enter_Slowpath")],
            BlockingKind.MonitorEnter
        },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Cold_and_optimized_frames_are_the_same_wait(string call, StackFrameInfo[] cold, StackFrameInfo[] optimized, BlockingKind kind)
    {
        Assert.True(kind == FrameClassifier.Classify([.. cold, Caller]), $"{call}, cold");
        Assert.True(kind == FrameClassifier.Classify([.. optimized, Caller]), $"{call}, optimized");
    }

    /// Optimized stacks of the other waits, from real dumps: they were already recognised and stay so.
    public static TheoryData<string, StackFrameInfo[], BlockingKind> AlreadyFound => new()
    {
        { "Thread.Sleep", [Fx("System.Threading.Thread", "Sleep")], BlockingKind.Sleep },
        { "WaitHandle.WaitOne", [Fx("System.Threading.WaitHandle", "WaitOneNoCheck")], BlockingKind.WaitHandle },
        { "WaitHandle.WaitAny", [Fx("System.Threading.WaitHandle", "WaitMultiple")], BlockingKind.WaitHandle },
        { "ManualResetEventSlim.Wait", [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.ManualResetEventSlim", "Wait")], BlockingKind.MonitorWait },
        { "SemaphoreSlim.Wait", [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "WaitCore")], BlockingKind.SemaphoreWait },
        { "task.Result", [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.ManualResetEventSlim", "Wait"), Fx("System.Threading.Tasks.Task", "SpinThenBlockingWait"), Fx("System.Threading.Tasks.Task", "InternalWaitCore"), Fx("System.Threading.Tasks.Task", "InternalWait")], BlockingKind.TaskWait },
        { "Lock.Enter", [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Threading.Lock", "TryEnterSlow"), Fx("System.Threading.Lock", "EnterAndGetCurrentThreadId")], BlockingKind.MonitorEnter },
        { "ReaderWriterLockSlim.EnterWriteLock", [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Threading.ReaderWriterLockSlim", "WaitOnEvent"), Fx("System.Threading.ReaderWriterLockSlim", "TryEnterWriteLockCore")], BlockingKind.ReaderWriterLock },
        { "CancellationTokenRegistration.Dispose", [Fx("System.Threading.SpinWait", "SpinOnceCore"), Fx("System.Threading.CancellationTokenSource+Registrations", "WaitForCallbackToComplete")], BlockingKind.CancellationCallback },
        { "a static constructor", [Fx("System.Runtime.CompilerServices.InitHelpers", "InitClassSlow"), Fx("System.Runtime.CompilerServices.StaticsHelpers", "GetNonGCStaticBaseSlow")], BlockingKind.TypeInitializer },
    };

    [Theory]
    [MemberData(nameof(AlreadyFound))]
    public void Other_optimized_waits_keep_their_kind(string call, StackFrameInfo[] optimized, BlockingKind kind) =>
        Assert.True(kind == FrameClassifier.Classify([.. optimized, Caller]), call);

    /// A stub belongs to a wait by its name and its type, not because the name contains "Join" or "Wait".
    [Theory]
    [InlineData("System.Threading.Thread", "<Joiner>g____PInvoke|56_0")]
    [InlineData("System.Threading.Thread", "<Join>g__Helper|56_0")]
    [InlineData("System.Threading.Thread", "<Sleep>g____PInvoke|57_0")]
    [InlineData("System.Threading.Thread", "JoinInternal")]
    [InlineData("System.Threading.Monitor", "<WaitOne>g____PInvoke|24_0")]
    [InlineData("System.Threading.Monitor", "<Pulse>g____PInvoke|25_0")]
    [InlineData("System.Threading.WaitHandle", "<Wait>g____PInvoke|24_0")]
    [InlineData("System.Threading.Tasks.Task", "<Join>g____PInvoke|1_0")]
    [InlineData("System.GC", "_WaitForPendingFinalizersSoon")]
    [InlineData("System.GC", "RunFinalizers")]
    [InlineData("System.Diagnostics.ProcessWaitState", "WaitForExitAsync")]
    [InlineData("System.Diagnostics.ProcessWaitState", "GetExited")]
    public void Look_alike_frames_are_no_framework_wait(string type, string method)
    {
        Assert.Equal(BlockingKind.None, FrameClassifier.FrameworkWait(type, method));
        Assert.Equal(BlockingKind.None, FrameClassifier.Classify([Stub(type, method), Caller]));
    }

    [Fact]
    public void WaitOne_inlined_down_to_its_stub_is_still_a_wait_on_a_WaitHandle()
    {
        // Windows after warm-up (tiered PGO), the stack of a dump from CI: WaitOne, WaitOneNoCheck and WaitOneCore are
        // inlined into the app's method and only the generated stub is left, twice.
        var stub = Fx("System.Threading.WaitHandle", "<WaitOneCore>g____PInvoke|0_0");
        Assert.Equal(BlockingKind.WaitHandle, FrameClassifier.Classify([stub, stub, Caller]));
        // WaitAny and WaitAll end in the type's other stub.
        Assert.Equal(BlockingKind.WaitHandle, FrameClassifier.Classify([Fx("System.Threading.WaitHandle", "<WaitMultipleIgnoringSyncContext>g____PInvoke|2_0"), Caller]));
    }

    [Fact]
    public void A_thread_running_the_apps_code_is_not_blocked_by_a_stub_further_down()
    {
        // The app's own frame is on top: it runs. The stub below it belongs to code that hasn't returned yet.
        StackFrameInfo[] frames = [App("MyApp.Report", "Render", "Report.cs", 8), Stub("System.Threading.Thread", "<Join>g____PInvoke|56_0"), Caller];
        Assert.Equal(BlockingKind.None, FrameClassifier.Classify(frames));
    }

    [Theory]
    [InlineData("(dynamicClass)", "IL_STUB_PInvoke", true)]
    [InlineData("", "IL_STUB_PInvoke", true)]
    [InlineData("(dynamicClass)", "IL_STUB_ReversePInvoke", false)]
    [InlineData("MyApp.Native", "IL_STUB_PInvoke", false)]
    public void Only_the_runtimes_marshalling_stub_is_one(string type, string method, bool expected) =>
        Assert.Equal(expected, FrameClassifier.IsMarshallingStub(type, method));

    [Theory]
    [InlineData("<Join>g____PInvoke|56_0", "Join")]
    [InlineData("<Wait>g____PInvoke|24_0", "Wait")]
    [InlineData("<Join>g____PInvoke|59_0", "Join")]
    [InlineData("<Join>g__Helper|56_0", null)]
    [InlineData("Join", null)]
    [InlineData("_WaitForPendingFinalizers", null)]
    public void PInvoke_stubs_are_named_after_their_method(string method, string? expected) =>
        Assert.Equal(expected, Names.PInvokeStubOf(method));

    [Fact]
    public void Waiting_for_finalizers_in_optimized_code_is_the_same_deadlock()
    {
        var snapshot = Process(
            Thread(1, [Stub("System.GC", "_WaitForPendingFinalizers"), Stub("System.GC", "_WaitForPendingFinalizers"), App("MyApp.Cleanup", "Run", "Cleanup.cs", 29)]),
            Thread(2, [Fx("System.Threading.Monitor", "Enter_Slowpath"), App("MyApp.NativeBuffer", "Finalize", "NativeBuffer.cs", 15), Fx("System.GC", "RunFinalizers")]) with { IsFinalizer = true, WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "HandleLog.Sync", OwnerThreadId = 1, WaiterThreadIds = [2] }],
        };

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Deadlock: waiting for finalizers while holding a lock a finalizer needs", f.Title);
        Assert.StartsWith("Thread 1 and Thread 2 (finalizer) are waiting for each other", f.Summary);
        Assert.Contains("Thread 1 holds the lock on HandleLog.Sync and is waiting in Cleanup.Run (Cleanup.cs:29) for Thread 2 (finalizer) to finish running finalizers (GC.WaitForPendingFinalizers).", f.Details);
    }

    [Fact]
    public void Joining_in_optimized_code_reads_like_cold_code()
    {
        ProcessSnapshot Snapshot(StackFrameInfo[] join) => Process(
            Thread(1, [.. join, App("MyApp.Worker", "Stop", "Worker.cs", 22)]) with { JoiningThreadId = 4 },
            Thread(4, [Fx("System.Threading.Monitor", "Enter_Slowpath"), App("MyApp.Worker", "OnStopped", "Worker.cs", 34)], "email-worker") with { WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Worker._state", OwnerThreadId = 1, WaiterThreadIds = [4] }],
        };

        var cold = Assert.Single(new WaitCycleDetector().Detect(Snapshot([Fx("System.Threading.Thread", "Join")]), new HashSet<int>()));
        var optimized = Assert.Single(new WaitCycleDetector().Detect(Snapshot([Stub("System.Threading.Thread", "<Join>g____PInvoke|56_0")]), new HashSet<int>()));
        Assert.Equal(cold.Summary, optimized.Summary);
        Assert.Equal(cold.Details, optimized.Details);
        Assert.Contains(optimized.Details, d => d == "Thread 1 holds the lock on Worker._state and is waiting in Worker.Stop (Worker.cs:22) for Thread 4 (\"email-worker\") to finish (Thread.Join).");
    }

    [Fact]
    public void Waiting_in_Monitor_Wait_in_optimized_code_reads_like_cold_code()
    {
        var wait = new PulseWait { Object = new ObjectRef(0x100, "System.Object"), FieldName = "WorkQueue._sync", PendingWork = ["WorkQueue._items (1 item)"] };
        Finding Detect(StackFrameInfo[] frames) =>
            Assert.Single(new MonitorWaitDetector().Detect(Process(Thread(4, [.. frames, App("MyApp.WorkQueue", "Dequeue", "WorkQueue.cs", 24)], "queue-worker") with { WaitingForPulse = wait }), new HashSet<int>()));

        var cold = Detect([Fx("System.Threading.Monitor", "Wait")]);
        var optimized = Detect([Stub("System.Threading.Monitor", "<Wait>g____PInvoke|24_0"), Stub("System.Threading.Monitor", "<Wait>g____PInvoke|24_0")]);
        Assert.Equal(Severity.Critical, optimized.Severity);
        Assert.Equal(cold.Title, optimized.Title);
        Assert.Equal(cold.Summary, optimized.Summary);
        Assert.Equal(cold.Details, optimized.Details);
    }
}
