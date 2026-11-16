using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using HangSloth.Collection;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Tests;

/// The lines of frames in optimized code, which the dump has no line map for: read from the IL, as the calls that can
/// lead to the frame above, or to the wait the thread is in.
public class OptimizedWaitResolverTests
{
    private static readonly string Module = typeof(WaitSamples).Assembly.Location;
    private static readonly (string Path, Func<Guid?>? DumpedBuild)[] AppModules = [(Module, null)];

    private static int Token(string method) =>
        typeof(WaitSamples).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.MetadataToken;

    private static int[] Lines(CallSites? sites) => sites?.Lines.Select(l => l.Line).ToArray() ?? [];

    private static CallSites? CallsTarget(string method)
    {
        using var resolver = new SourceResolver();
        return resolver.ResolveCall(Module, Token(method), typeof(WaitSamples).FullName!, nameof(WaitSamples.Target));
    }

    private static CallSites? FrameworkCall(string method, Type callee, string calleeMethod)
    {
        using var resolver = new SourceResolver();
        return resolver.ResolveCall(Module, Token(method), callee.FullName!, calleeMethod, callee.Assembly.Location);
    }

    private static CallSites? Waits(string method, string? call, string? resultType)
    {
        using var resolver = new SourceResolver();
        return resolver.ResolveBlockingCall(Module, Token(method), new TaskWaitHint(call, resultType));
    }

    private static BlockingCallers? Callers(string asyncMethod)
    {
        var stateMachine = typeof(WaitSamples).GetMethod(asyncMethod, BindingFlags.NonPublic | BindingFlags.Static)!.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var resolver = new SourceResolver();
        return resolver.ResolveBlockingCallers(Module, moveNext.MetadataToken, AppModules);
    }

    [Fact]
    public void The_only_call_of_the_frame_above_gives_the_line()
    {
        var sites = CallsTarget(nameof(WaitSamples.CallsOnce));
        Assert.Equal(WaitSamples.CallsOnceLine, sites?.Line?.Line);
        Assert.Equal("OptimizedWaitResolverTests.cs", sites?.Line?.FileName);
    }

    [Fact]
    public void Two_calls_give_both_lines_and_no_line()
    {
        var sites = CallsTarget(nameof(WaitSamples.CallsTwice));
        Assert.Null(sites?.Line);
        Assert.Equal([WaitSamples.CallsTwiceFirstLine, WaitSamples.CallsTwiceSecondLine], Lines(sites));
    }

    [Fact]
    public void A_call_of_a_helper_that_can_be_inlined_and_calls_it_counts_too() =>
        Assert.Equal([WaitSamples.ViaHelperDirectLine, WaitSamples.ViaHelperLine], Lines(CallsTarget(nameof(WaitSamples.CallsViaHelper))));

    [Fact]
    public void A_call_of_a_helper_that_is_never_inlined_does_not_count() =>
        Assert.Equal([WaitSamples.ViaNoInliningDirectLine], Lines(CallsTarget(nameof(WaitSamples.CallsViaNoInliningHelper))));

    [Fact]
    public void The_constructor_of_a_thrown_exception_does_not_count()
    {
        using var resolver = new SourceResolver();
        var caller = typeof(ThrowSamples).GetMethod(nameof(ThrowSamples.ChecksThenCalls))!;
        var sites = resolver.ResolveCall(Module, caller.MetadataToken, typeof(WaitSamples).FullName!, nameof(WaitSamples.Target));
        Assert.Equal([ThrowSamples.TargetLine], Lines(sites));
    }

    [Theory]
    [InlineData(typeof(StockSource), "Count")]
    [InlineData(typeof(CountedShelf), "HangSloth.Tests.IStockSource.Count")]
    public void A_call_through_an_interface_is_found_by_the_method_name(Type implementation, string method)
    {
        using var resolver = new SourceResolver();
        var caller = typeof(InterfaceSamples).GetMethod(nameof(InterfaceSamples.CallsThroughInterface))!;
        Assert.Equal(InterfaceSamples.CountLine, resolver.ResolveCall(Module, caller.MetadataToken, implementation.FullName!, method)?.Line?.Line);
    }

    [Fact]
    public void A_lock_is_found_from_the_Monitor_frame_inside_it() =>
        Assert.Equal(WaitSamples.LockLine, FrameworkCall(nameof(WaitSamples.TakesLock), typeof(Monitor), "Enter_Slowpath")?.Line?.Line);

    [Fact]
    public void An_inlined_SemaphoreSlim_Wait_is_found_from_the_frame_inside_it() =>
        Assert.Equal(WaitSamples.SemaphoreLine, FrameworkCall(nameof(WaitSamples.WaitsForSemaphore), typeof(SemaphoreSlim), "WaitCore")?.Line?.Line);

    [Fact]
    public void Parallel_ForEach_is_found_by_its_name_with_type_arguments() =>
        Assert.Equal(WaitSamples.ParallelLine, FrameworkCall(nameof(WaitSamples.RunsInParallel), typeof(Parallel), "ForEach[[System.__Canon, System.Private.CoreLib]]")?.Line?.Line);

    [Fact]
    public void The_result_type_of_the_waited_task_picks_the_Result()
    {
        Assert.Equal(WaitSamples.IntResultLine, Waits(nameof(WaitSamples.TwoResults), ".Result", "System.Int32")?.Line?.Line);
        Assert.Equal(WaitSamples.StringResultLine, Waits(nameof(WaitSamples.TwoResults), ".Result", "System.String")?.Line?.Line);
        Assert.Equal(WaitSamples.ListResultLine, Waits(nameof(WaitSamples.TwoResults), null, "System.Collections.Generic.List<System.String>")?.Line?.Line);
    }

    [Fact]
    public void Two_Results_that_nothing_tells_apart_give_both_lines_and_the_call()
    {
        var sites = Waits(nameof(WaitSamples.TwoResults), ".Result", null);
        Assert.Null(sites?.Line);
        Assert.Equal(".Result", sites?.Call);
        Assert.Equal([WaitSamples.StringResultLine, WaitSamples.IntResultLine, WaitSamples.ListResultLine], Lines(sites));
    }

    [Fact]
    public void A_task_without_a_result_is_not_waited_for_with_Result()
    {
        var sites = Waits(nameof(WaitSamples.ResultThenWait), null, "");
        Assert.Equal(WaitSamples.WaitLine, sites?.Line?.Line);
        Assert.Equal(".Wait()", sites?.Call);
    }

    [Fact]
    public void Different_calls_give_no_call()
    {
        var sites = Waits(nameof(WaitSamples.ResultThenWait), null, null);
        Assert.Null(sites?.Call);
        Assert.Equal([WaitSamples.ResultBeforeWaitLine, WaitSamples.WaitLine], Lines(sites));
    }

    [Fact]
    public void The_method_that_blocks_on_an_async_method_is_found_from_its_state_machine()
    {
        var callers = Callers("LookupAsync");
        Assert.True(callers?.Complete);
        var caller = Assert.Single(callers!.Methods);
        Assert.Equal(typeof(WaitSamples).FullName, caller.TypeName);
        Assert.Equal(nameof(WaitSamples.GetsLookup), caller.MethodName);
        Assert.Equal(".GetAwaiter().GetResult()", caller.Call.Call);
        Assert.Equal(WaitSamples.GetsLookupLine, caller.Call.Location?.Line);
    }

    [Fact]
    public void A_method_that_only_returns_the_task_is_followed_to_its_callers()
    {
        var callers = Callers("InnerAsync");
        Assert.True(callers?.Complete);
        var caller = Assert.Single(callers!.Methods);
        Assert.Equal(nameof(WaitSamples.GetsForwarded), caller.MethodName);
        Assert.Equal(".Result", caller.Call.Call);
        Assert.Equal(WaitSamples.GetsForwardedLine, caller.Call.Location?.Line);
    }

    [Fact]
    public void Two_methods_that_block_on_it_are_both_named()
    {
        var callers = Callers("SharedAsync");
        Assert.True(callers?.Complete);
        Assert.Equal([nameof(WaitSamples.FirstUser), nameof(WaitSamples.SecondUser)], callers!.Methods.Select(m => m.MethodName).Order());
    }

    [Fact]
    public void A_task_that_is_kept_somewhere_makes_the_list_incomplete()
    {
        var callers = Callers("KeptAsync");
        Assert.False(callers?.Complete);
        Assert.Equal(nameof(WaitSamples.GetsKept), Assert.Single(callers!.Methods).MethodName);
    }

    [Fact]
    public void A_method_that_is_never_inlined_is_not_a_candidate()
    {
        var callers = Callers("NeverInlinedAsync");
        Assert.True(callers?.Complete);
        Assert.Empty(callers!.Methods);
    }

    [Fact]
    public void Missing_modules_and_odd_tokens_give_nothing()
    {
        using var resolver = new SourceResolver();
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll");
        Assert.Null(resolver.ResolveCall(missing, Token(nameof(WaitSamples.CallsOnce)), "A", "B"));
        Assert.Null(resolver.ResolveCall(Module, 0x02000001, "A", "B"));
        Assert.Null(resolver.ResolveBlockingCall(Module, 0, new TaskWaitHint(".Result", null)));
        Assert.Null(resolver.ResolveBlockingCallers(missing, 0x06000001, AppModules));
        Assert.Null(resolver.ResolveBlockingCallers(Module, Token(nameof(WaitSamples.CallsOnce)), AppModules));
    }

    [Fact]
    public void An_address_outside_the_line_map_is_not_mapped()
    {
        ImmutableArray<ILToNativeMap> map =
        [
            new() { ILOffset = 0, StartAddress = 0x1000, EndAddress = 0x100F },
            new() { ILOffset = 6, StartAddress = 0x1010, EndAddress = 0x1040 },
        ];
        Assert.True(SnapshotBuilder.IsMapped(map, 0x1000));
        Assert.True(SnapshotBuilder.IsMapped(map, 0x1020));
        Assert.True(SnapshotBuilder.IsMapped(map, 0x1040));
        Assert.False(SnapshotBuilder.IsMapped(map, 0x0FFF));
        Assert.False(SnapshotBuilder.IsMapped(map, 0x2000));
        Assert.False(SnapshotBuilder.IsMapped([], 0x1000));
        Assert.False(SnapshotBuilder.IsMapped(default, 0x1000));
    }

    [Theory]
    [InlineData("System.Threading.Tasks.Task`1", "System.Threading.Tasks.Task")]
    [InlineData("System.Threading.Tasks.Task<System.Int32>", "System.Threading.Tasks.Task")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>", "System.Collections.Generic.Dictionary")]
    [InlineData("ForEach[[System.__Canon, System.Private.CoreLib]]", "ForEach")]
    [InlineData("Orders.Importer+<>c", "Orders.Importer+<>c")]
    [InlineData("<Run>b__0_0", "<Run>b__0_0")]
    [InlineData("Orders.Importer+<RunAsync>d__3", "Orders.Importer+<RunAsync>d__3")]
    public void Plain_names_drop_arity_and_type_arguments(string name, string plain) => Assert.Equal(plain, SourceResolver.PlainName(name));

    [Theory]
    [InlineData("System.Threading.Tasks.Task<System.Int32>", "System.Int32")]
    [InlineData("System.Threading.Tasks.Task<System.Collections.Generic.List<System.String>>", "System.Collections.Generic.List<System.String>")]
    [InlineData("System.Runtime.CompilerServices.ConfiguredTaskAwaitable<System.Collections.Generic.Dictionary<System.String, System.Int32>>+ConfiguredTaskAwaiter", "System.Collections.Generic.Dictionary<System.String, System.Int32>")]
    [InlineData("System.Collections.Generic.KeyValuePair<System.String, System.Int32>", "System.String")]
    [InlineData("System.Threading.Tasks.Task<System.__Canon>", null)]
    [InlineData("System.Threading.Tasks.Task", null)]
    [InlineData("Orders.Importer+<>c", null)]
    public void The_result_type_is_the_first_type_argument(string typeName, string? resultType) => Assert.Equal(resultType, SnapshotBuilder.ResultType(typeName));
}

/// Methods whose lines the tests above look for. The line constants say where each call is: add new code at the end.
public static class WaitSamples
{
    public const int CallsOnceLine = 267;
    public const int CallsTwiceFirstLine = 274;
    public const int CallsTwiceSecondLine = 275;
    public const int ViaHelperDirectLine = 280;
    public const int ViaHelperLine = 281;
    public const int ViaNoInliningDirectLine = 287;
    public const int LockLine = 296;
    public const int SemaphoreLine = 306;
    public const int ParallelLine = 320;
    public const int StringResultLine = 327;
    public const int IntResultLine = 329;
    public const int ListResultLine = 330;
    public const int ResultBeforeWaitLine = 336;
    public const int WaitLine = 338;
    public const int GetsLookupLine = 348;
    public const int GetsForwardedLine = 358;

    private static readonly object Ledger = new();
    private static readonly SemaphoreSlim Dock = new(1, 1);
    private static readonly List<Task<int>> Kept = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Target(int x) => x + 1;

    private static int Helper(int x) => Target(x) * 2;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int NeverInlinedHelper(int x) => Target(x) * 3;

    public static int CallsOnce(int x)
    {
        var y = x * 2;
        var z = Target(y);
        return z + y;
    }

    public static int CallsTwice(int x)
    {
        if (x > 0)
            return Target(x);
        return Target(-x);
    }

    public static int CallsViaHelper(int x)
    {
        var a = Target(x);
        var b = Helper(a);
        return a + b;
    }

    public static int CallsViaNoInliningHelper(int x)
    {
        var a = Target(x);
        var b = NeverInlinedHelper(a);
        return a + b;
    }

    public static int TakesLock(int x)
    {
        var label = x > 0 ? "in" : "out";
        var weight = label.Length + x % 3;
        lock (Ledger)
        {
            return weight + 1;
        }
    }

    public static int WaitsForSemaphore(int x)
    {
        var label = x > 0 ? "in" : "out";
        var weight = label.Length + x % 3;
        Dock.Wait();
        try
        {
            return weight;
        }
        finally
        {
            Dock.Release();
        }
    }

    public static int RunsInParallel(Task<List<string>> load, Task<int> save)
    {
        var items = load.Result;
        Parallel.ForEach(items, item => GC.KeepAlive(item));
        var saved = save.Result;
        return items.Count + saved;
    }

    public static int TwoResults(Task<string> name, Task<int> limit, Task<List<string>> tags)
    {
        var customer = name.Result;
        var key = string.Concat(customer, "-");
        var value = limit.Result;
        var all = tags.Result;
        return key.Length + value + all.Count;
    }

    public static int ResultThenWait(Task<int> first, Task second)
    {
        var value = first.Result;
        var doubled = value * 2;
        second.Wait();
        return doubled;
    }

    private static async Task<int> LookupAsync()
    {
        await Task.Yield();
        return 1;
    }

    public static int GetsLookup() => LookupAsync().GetAwaiter().GetResult();

    private static async Task<int> InnerAsync()
    {
        await Task.Yield();
        return 2;
    }

    public static Task<int> Forward() => InnerAsync();

    public static int GetsForwarded() => Forward().Result;

    private static async Task<int> SharedAsync()
    {
        await Task.Yield();
        return 3;
    }

    public static int FirstUser() => SharedAsync().Result;

    public static int SecondUser() => SharedAsync().GetAwaiter().GetResult();

    private static async Task<int> KeptAsync()
    {
        await Task.Yield();
        return 4;
    }

    public static int GetsKept() => KeptAsync().Result;

    public static void Keeps() => Kept.Add(KeptAsync());

    private static async Task<int> NeverInlinedAsync()
    {
        await Task.Yield();
        return 5;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GetsNeverInlined() => NeverInlinedAsync().Result;
}

public interface IStockSource
{
    int Count(int shelf);
}

public sealed class StockSource : IStockSource
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Count(int shelf) => shelf;
}

public sealed class CountedShelf : IStockSource
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    int IStockSource.Count(int shelf) => shelf * 2;
}

public static class InterfaceSamples
{
    public const int CountLine = 414;

    public static int CallsThroughInterface(IStockSource source, int shelf)
    {
        var next = shelf + 1;
        var count = source.Count(next);
        return count + next;
    }
}

public sealed class ShelfException : Exception
{
    public ShelfException(int shelf) : base("Shelf " + WaitSamples.Target(shelf)) { }
}

public static class ThrowSamples
{
    public const int TargetLine = 432;

    public static int ChecksThenCalls(int shelf)
    {
        if (shelf < 0) throw new ShelfException(shelf);
        var next = shelf + 1;
        return WaitSamples.Target(next);
    }
}
