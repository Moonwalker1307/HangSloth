using System.Reflection;
using System.Runtime.CompilerServices;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Where a wait in optimized code is said to be when its IL leaves the line open, and which framework calls can lead to
/// the frame above once the JIT has inlined them.
public class OptimizedWaitPlaceTests
{
    private static readonly string Module = typeof(PlaceSamples).Assembly.Location;

    private static StackFrameInfo LeftOpen(string type, string method, params int[] lines) =>
        new() { TypeName = type, MethodName = method, ModuleName = "/app/MyApp.dll", CandidateLines = [.. lines.Select(l => new SourceLocation("/src/Billing.cs", l))] };

    private static int[] TakesLock(string method)
    {
        using var resolver = new SourceResolver();
        var token = typeof(PlaceSamples).GetMethod(method)!.MetadataToken;
        var sites = resolver.ResolveCall(Module, token, typeof(Monitor).FullName!, "Enter_Slowpath", typeof(Monitor).Assembly.Location);
        return sites?.Lines.Select(l => l.Line).ToArray() ?? [];
    }

    [Fact]
    public void A_frame_whose_line_is_left_open_is_the_place_not_its_caller()
    {
        var stamp = LeftOpen("MyApp.Billing", "Stamp", 40, 41);
        var frames = new[] { Fx("System.Threading.Monitor", "Enter_Slowpath"), stamp, App("MyApp.Caller", "Go", "Caller.cs", 22) };
        Assert.Same(stamp, FrameClassifier.BestUserFrame(frames));
    }

    [Fact]
    public void A_frame_without_a_line_for_another_reason_still_gives_way_to_its_caller()
    {
        var noPdb = new StackFrameInfo { TypeName = "MyApp.Billing", MethodName = "Stamp", ModuleName = "/app/MyApp.dll" };
        var caller = App("MyApp.Caller", "Go", "Caller.cs", 22);
        Assert.Same(caller, FrameClassifier.BestUserFrame([Fx("System.Threading.Monitor", "Enter_Slowpath"), noPdb, caller]));
    }

    [Fact]
    public void A_lock_wait_whose_line_is_left_open_is_named_by_its_method()
    {
        var snapshot = Process(
            Thread(7, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Billing", "Close", "Billing.cs", 70)], "billing-close"),
            Thread(8, [Fx("System.Threading.Monitor", "Enter_Slowpath"), LeftOpen("MyApp.Billing", "Stamp", 40, 41), App("MyApp.Caller", "Go", "Caller.cs", 22)]) with { WaitingForLock = 0xA }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0xA, "System.Object"), FieldName = "Billing._gate", OwnerThreadId = 7, WaiterThreadIds = [8] }],
        };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Waiting at: Billing.Stamp.", f.Details[0]);
    }

    [Fact]
    public void A_task_wait_whose_line_is_left_open_is_named_by_its_method()
    {
        var total = LeftOpen("MyApp.Billing", "Total", 39, 40) with { BlockingCall = ".Result" };
        var snapshot = Process(Thread(1, [.. ResultWait, total, App("MyApp.Caller", "Go", "Caller.cs", 22)], "totals") with
        {
            BlockedOnTask = new TaskInfo { Address = 0x10, TypeName = "System.Threading.Tasks.Task<System.Int32>", Kind = TaskKind.Promise, State = TaskState.Pending, Description = "a TaskCompletionSource<int> task" },
        });

        var f = Assert.Single(new BlockedOnTaskDetector().Detect(snapshot, new HashSet<int>()));
        Assert.StartsWith("Thread 1 (\"totals\") is blocked in .Result at Billing.Total, waiting for", f.Summary);
        Assert.Null(f.Location);
    }

    [Fact]
    public void Logging_and_a_list_before_a_lock_are_not_places_that_take_it()
    {
        Assert.Equal([PlaceSamples.LogThenLockLine], TakesLock(nameof(PlaceSamples.LogThenLock)));
        Assert.Equal([PlaceSamples.WriterThenLockLine], TakesLock(nameof(PlaceSamples.WriterThenLock)));
    }

    [Fact]
    public void A_helper_that_takes_the_lock_too_still_counts() =>
        Assert.Equal([PlaceSamples.LockBeforeHelperLine, PlaceSamples.HelperCallLine], TakesLock(nameof(PlaceSamples.LockThenHelper)));

    [Fact]
    public void Blocking_callers_are_read_once_per_state_machine()
    {
        var stateMachine = typeof(PlaceSamples).GetMethod("QuoteAsync", BindingFlags.NonPublic | BindingFlags.Static)!.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.MetadataToken;
        using var resolver = new SourceResolver();
        var first = resolver.ResolveBlockingCallers(Module, moveNext, [(Module, null)]);
        Assert.Equal(nameof(PlaceSamples.Quote), Assert.Single(first!.Methods).MethodName);
        Assert.Same(first, resolver.ResolveBlockingCallers(Module, moveNext, [(Module, null)]));
    }

    [Fact]
    public void Synchronous_IO_that_blocks_on_a_task_is_left_for_the_detectors_to_name()
    {
        var send = Fx("System.Net.Http.HttpClient", "Send");
        StackFrameInfo[] http = [Fx("System.Runtime.CompilerServices.TaskAwaiter`1", "GetResult"), Fx("System.Net.Http.HttpMessageInvoker", "Send"), send, App("MyApp.PriceClient", "Fetch", "PriceClient.cs", 34)];
        Assert.Null(SnapshotBuilder.CalleeCall(http, send));

        var forEach = Fx("System.Threading.Tasks.Parallel", "ForEach");
        StackFrameInfo[] parallel = [.. ResultWait.Take(4), Fx("System.Threading.Tasks.Task", "Wait"), forEach, App("MyApp.Repricer", "Run", "Repricer.cs", 11)];
        Assert.Equal("Parallel.ForEach", SnapshotBuilder.CalleeCall(parallel, forEach));

        // Fully optimized code inlines Parallel.ForEach, and for an array or a list the worker it calls is ForWorker too.
        var worker = Fx("System.Threading.Tasks.Parallel", "ForWorker[[System.__Canon, System.Private.CoreLib]]");
        Assert.Equal("Parallel.For/ForEach", SnapshotBuilder.CalleeCall([.. parallel.SkipLast(2), worker, parallel[^1]], worker));
    }

    [Theory]
    [InlineData("System.Threading.Monitor", "Enter_Slowpath", "takes a lock")]
    [InlineData("System.Threading.Monitor", "ReliableEnter", "takes a lock")]
    [InlineData("System.Threading.Lock", "EnterAndGetCurrentThreadId", "takes a lock")]
    [InlineData("System.Threading.SemaphoreSlim", "WaitCore", "calls SemaphoreSlim.Wait()")]
    [InlineData("System.Threading.Thread", "Sleep", "calls Thread.Sleep")]
    [InlineData("System.Threading.ManualResetEventSlim", "Wait", "calls ManualResetEventSlim.Wait")]
    [InlineData("MyApp.RateCache", "get_Current", "calls RateCache.Current")]
    public void Notes_name_the_call_the_app_made(string type, string method, string said) =>
        Assert.Equal(said, SnapshotBuilder.PublicCall(Fx(type, method)));
}

/// Methods whose lines the tests above look for. The line constants say where each call is: add new code at the end.
public static class PlaceSamples
{
    public const int LogThenLockLine = 135;
    public const int WriterThenLockLine = 143;
    public const int LockBeforeHelperLine = 148;
    public const int HelperCallLine = 149;

    private static readonly object Gate = new();
    private static readonly TextWriter Log = TextWriter.Null;

    public static int LogThenLock(int x)
    {
        Console.WriteLine($"adjusting {x}");
        lock (Gate) { return x + 1; }
    }

    public static int WriterThenLock(int x)
    {
        Log.WriteLine($"posting {x}");
        var lines = new List<int> { x };
        var totals = new Dictionary<int, int> { [x] = lines.Count };
        lock (Gate) { return totals.Count; }
    }

    public static int LockThenHelper(int x)
    {
        lock (Gate) { x++; }
        return Helper(x);
    }

    private static int Helper(int x)
    {
        lock (Gate) { return x; }
    }

    private static async Task<decimal> QuoteAsync(int id)
    {
        await Task.Yield();
        return id * 1.5m;
    }

    public static decimal Quote(int id) => QuoteAsync(id).GetAwaiter().GetResult();
}
