using System.Runtime.CompilerServices;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Optimized (tier-1) code inlines Task<T>.Result into the app's method: its frame is gone, and the return
/// address can map to an earlier statement than the call.
public class OptimizedTaskWaitTests
{
    // What's left of a .Result once get_Result and GetResultCore are inlined.
    private static readonly StackFrameInfo[] InlinedResultWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Threading.ManualResetEventSlim", "Wait"),
        Fx("System.Threading.Tasks.Task", "SpinThenBlockingWait"),
        Fx("System.Threading.Tasks.Task", "InternalWaitCore"),
        Fx("System.Threading.Tasks.Task", "InternalWait"),
    ];

    private static ThreadSnapshot Worker(int id, StackFrameInfo[] wait, StackFrameInfo app) =>
        Thread(id, [.. wait, app, App("MyApp.Batch", "RunAll", "Batch.cs", 12), .. PoolBottom]) with { IsThreadPoolWorker = true };

    [Fact]
    public void A_wait_without_its_result_frame_is_not_called_Wait()
    {
        StackFrameInfo[] frames = [.. InlinedResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 17)];
        Assert.Equal(".Result/.Wait()", FrameClassifier.DescribeTaskWait(frames));
    }

    [Fact]
    public void The_call_read_from_the_apps_IL_wins()
    {
        StackFrameInfo[] frames = [.. InlinedResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 23) with { BlockingCall = ".Result" }];
        Assert.Equal(".Result", FrameClassifier.DescribeTaskWait(frames));
    }

    [Fact]
    public void Task_Wait_is_still_Wait()
    {
        StackFrameInfo[] frames = [Fx("System.Threading.Tasks.Task", "InternalWaitCore"), Fx("System.Threading.Tasks.Task", "Wait"), App("MyApp.Checker", "Validate", "Checker.cs", 5)];
        Assert.Equal(".Wait()", FrameClassifier.DescribeTaskWait(frames));
    }

    [Fact]
    public void Starvation_groups_in_the_same_method_merge_and_trust_the_confirmed_line()
    {
        var confirmed = App("MyApp.Checker", "Validate", "Checker.cs", 23) with { BlockingCall = ".Result" };
        var threads = Enumerable.Range(10, 5).Select(id => Worker(id, ResultWait, confirmed))
            .Concat(Enumerable.Range(20, 6).Select(id => Worker(id, InlinedResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 17))))
            .Concat(Enumerable.Range(30, 3).Select(id => Worker(id, ResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 17))))
            .ToArray();

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(Process(threads), new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 14 of 14 thread pool threads are blocked (all in .Result at Checker.cs:23).", f.Summary);
        Assert.Contains("14 threads: .Result at Checker.Validate (Checker.cs:23).", f.Details);
        Assert.DoesNotContain(f.Details, d => d.Contains("Checker.cs:17") || d.Contains(": .Wait()"));
        Assert.Contains("await instead of .Result in Checker.cs:23", f.Advice);
        Assert.Equal(23, f.Location?.Line);
    }

    [Fact]
    public void Starvation_names_the_method_when_lines_disagree_and_nothing_confirms_one()
    {
        var threads = Enumerable.Range(10, 5).Select(id => Worker(id, ResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 23)))
            .Concat(Enumerable.Range(20, 6).Select(id => Worker(id, InlinedResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 17))))
            .ToArray();

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(Process(threads), new HashSet<int>()));
        Assert.Equal("Thread pool starvation: 11 of 11 thread pool threads are blocked (all in .Result at Checker.Validate).", f.Summary);
        Assert.DoesNotContain("Checker.cs:17", f.Advice);
        Assert.Null(f.Location);
    }

    [Fact]
    public void Different_methods_stay_separate_groups()
    {
        var threads = Enumerable.Range(10, 5).Select(id => Worker(id, ResultWait, App("MyApp.Checker", "Validate", "Checker.cs", 23)))
            .Concat(Enumerable.Range(20, 3).Select(id => Worker(id, ResultWait, App("MyApp.Prices", "Load", "Prices.cs", 8))))
            .ToArray();

        var f = Assert.Single(new ThreadPoolStarvationDetector().Detect(Process(threads), new HashSet<int>()));
        Assert.Contains("5 threads: .Result at Checker.Validate (Checker.cs:23).", f.Details);
        Assert.Contains("3 threads: .Result at Prices.Load (Prices.cs:8).", f.Details);
    }
}

/// Reads blocking calls from the IL of real methods (this test assembly's own, below).
public class BlockingCallResolverTests
{
    private static BlockingCall? Resolve(string method)
    {
        var info = typeof(BlockingCallSamples).GetMethod(method)!;
        using var resolver = new SourceResolver();
        // Offset 0 stands in for a return address that optimized code mapped to the method's first statement.
        return resolver.ResolveBlockingCall(typeof(BlockingCallSamples).Assembly.Location, info.MetadataToken, 0);
    }

    [Fact]
    public void Finds_Result_after_a_statement_that_spans_several_lines()
    {
        var call = Resolve(nameof(BlockingCallSamples.ResultAfterLongStatement));
        Assert.Equal(".Result", call?.Call);
        Assert.Equal(BlockingCallSamples.ResultLine, call?.Location?.Line);
        Assert.Equal("OptimizedCodeTests.cs", call?.Location?.FileName);
    }

    [Fact]
    public void Finds_Wait() => Assert.Equal(".Wait()", Resolve(nameof(BlockingCallSamples.Waits))?.Call);

    [Fact]
    public void Finds_GetAwaiter_GetResult() => Assert.Equal(".GetAwaiter().GetResult()", Resolve(nameof(BlockingCallSamples.GetsResult))?.Call);

    [Fact]
    public void Finds_GetResult_through_ConfigureAwait() => Assert.Equal(".GetAwaiter().GetResult()", Resolve(nameof(BlockingCallSamples.GetsConfiguredResult))?.Call);

    [Fact]
    public void Finds_Task_WaitAll() => Assert.Equal("Task.WaitAll(...)", Resolve(nameof(BlockingCallSamples.WaitsForAll))?.Call);

    [Fact]
    public void A_method_without_a_blocking_call_gives_nothing() => Assert.Null(Resolve(nameof(BlockingCallSamples.DoesNotBlock)));
}

public static class BlockingCallSamples
{
    public const int ResultLine = 138;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ResultAfterLongStatement(Task<int> pending)
    {
        var parts = new List<string>(
            [
                "a",
                "b",
            ]);
        var value = pending.Result;
        return parts.Count + value;
    }

    public static void Waits(Task pending) => pending.Wait();
    public static int GetsResult(Task<int> pending) => pending.GetAwaiter().GetResult();
    public static int GetsConfiguredResult(Task<int> pending) => pending.ConfigureAwait(false).GetAwaiter().GetResult();
    public static void WaitsForAll(Task a, Task b) => Task.WaitAll(a, b);
    public static int DoesNotBlock(int x) => x * 2;
}

public class TestHostAndHostWaitTests
{
    [Fact]
    public void Testhost_main_waiting_for_its_engine_is_not_reported()
    {
        static StackFrameInfo TestHost(string type, string method) =>
            new() { TypeName = type, MethodName = method, ModuleName = "/app/bin/testhost.dll" };
        var engine = new TaskInfo { Address = 1, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.Delegate, Description = "Task.Run(DefaultEngineInvoker.StartProcessingAsync (lambda))" };
        var main = Thread(1,
        [
            .. InlinedWait(),
            Fx("System.Threading.Tasks.Task", "Wait"),
            TestHost("Microsoft.VisualStudio.TestPlatform.TestHost.DefaultEngineInvoker", "Invoke"),
            TestHost("Microsoft.VisualStudio.TestPlatform.TestHost.Program", "Run"),
            TestHost("Microsoft.VisualStudio.TestPlatform.TestHost.Program", "Main"),
        ]) with { BlockedOnTask = engine };

        Assert.Equal(BlockingKind.Idle, FrameClassifier.Classify(main.Frames));
        Assert.Empty(new HangAnalyzer().Analyze(Process(main)).Findings);
    }

    [Fact]
    public void Main_running_the_generic_host_is_not_reported()
    {
        var delay = new TaskInfo { Address = 3, TypeName = "System.Threading.Tasks.Task+DelayPromise", Kind = TaskKind.Delay, Description = "Task.Delay" };
        var waitForShutdown = new TaskInfo
        {
            Address = 2, Kind = TaskKind.AsyncMethod, Description = "HostingAbstractionsHostExtensions.WaitForShutdownAsync", Awaiting = delay,
            TypeName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox<Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions+<WaitForShutdownAsync>d__4>",
        };
        var run = new TaskInfo
        {
            Address = 1, Kind = TaskKind.AsyncMethod, Description = "HostingAbstractionsHostExtensions.RunAsync", Awaiting = waitForShutdown,
            TypeName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox<Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions+<RunAsync>d__1>",
        };
        var main = Thread(1, [.. InlinedWait(), Fx("System.Runtime.CompilerServices.TaskAwaiter", "GetResult"), App("MyApp.Program", "Main", "Program.cs", 6)]) with { BlockedOnTask = run };

        Assert.Empty(new BlockedOnTaskDetector().Detect(Process(main), new HashSet<int>()));
    }

    [Fact]
    public void A_test_blocking_on_a_task_is_still_reported()
    {
        var never = new TaskInfo { Address = 1, TypeName = "Task<bool>", Kind = TaskKind.Promise, Description = "a TaskCompletionSource<bool> task" };
        var test = Thread(5,
        [
            .. ResultWait,
            App("MyApp.Tests.ImportTests", "Imports", "ImportTests.cs", 14),
            new() { TypeName = "Microsoft.VisualStudio.TestPlatform.TestHost.Program", MethodName = "Main", ModuleName = "/app/bin/testhost.dll" },
        ]) with { BlockedOnTask = never };

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(Process(test), new HashSet<int>()));
        Assert.StartsWith("Thread 5 is blocked in .Result at ImportTests.cs:14", f.Summary);
    }

    private static StackFrameInfo[] InlinedWait() =>
    [
        Fx("System.Threading.ManualResetEventSlim", "Wait"),
        Fx("System.Threading.Tasks.Task", "SpinThenBlockingWait"),
        Fx("System.Threading.Tasks.Task", "InternalWaitCore"),
    ];
}
