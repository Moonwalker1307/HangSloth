using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace HangApp;

/// Hangs in warmed-up code: methods the JIT has compiled again with optimizations (tier 1), as in any process that has
/// been running for a while. The dump has no line map for that code, so the line of each frame has to come from the call
/// it is making. The tests assert line numbers in this file: add new code at the end.
static class OptimizedWaitScenarios
{
    public static void Run(string mode)
    {
        switch (mode)
        {
            case "tiered-lock": TieredLock(); break;
            case "tiered-two-results": TieredTwoResults(); break;
            case "parallel-then-result": ParallelThenResult(); break;
            case "minimal-api": MinimalApiScenario.Run(); break;
            case "tiered-ledger": TieredLedgerScenario.Run(); break;
        }
    }

    /// Two threads wait for a lock and one for a SemaphoreSlim, in tier-1 code, while "stock-audit" holds both.
    private static void TieredLock()
    {
        var warehouse = new Warehouse();
        WarmUp("HangApp.Warehouse", ["Restock", "Recount", "Adjust", "Unload"], () =>
        {
            for (var i = 1; i <= 2000; i++)
            {
                warehouse.Restock(i);
                warehouse.Recount(i);
                warehouse.Unload(i);
            }
        });

        using var holding = new ManualResetEventSlim();
        new Thread(() => warehouse.Audit(holding)) { IsBackground = true, Name = "stock-audit" }.Start();
        holding.Wait();
        new Thread(() => warehouse.Restock(5)) { IsBackground = true, Name = "restock" }.Start();
        new Thread(() => warehouse.Recount(4)) { IsBackground = true, Name = "recount" }.Start();
        new Thread(() => warehouse.Unload(3)) { IsBackground = true, Name = "unload" }.Start();
        Ready.SignalLater(1000);
        Thread.Sleep(Timeout.Infinite);
    }

    /// A tier-1 method with two .Result calls: the first one completes, the second one hangs.
    private static void TieredTwoResults()
    {
        var check = new CreditCheck();
        WarmUp("HangApp.CreditCheck", ["Check"], () =>
        {
            for (var i = 1; i <= 2000; i++) check.Check(i);
        });

        check.Accounts.LimitsDown = true;
        new Thread(() => check.Check(7)) { IsBackground = true, Name = "credit-check" }.Start();
        Ready.SignalLater(1000);
        Thread.Sleep(Timeout.Infinite);
    }

    /// Parallel.ForEach whose workers never finish, in a method that also has .Result calls after it.
    private static void ParallelThenResult()
    {
        var update = new PriceUpdate();
        new Thread(update.Run) { IsBackground = true, Name = "price-update" }.Start();
        Ready.SignalLater(1000);
        Thread.Sleep(Timeout.Infinite);
    }

    /// Calls <paramref name="work"/> often, with time in between, until the runtime says it has compiled every one of
    /// <paramref name="methods"/> with optimizations, the way TieredStarvationScenario does.
    internal static void WarmUp(string type, string[] methods, Action work)
    {
        using (var jit = new OptimizedMethodsListener(type, methods))
        {
            var warmUp = System.Diagnostics.Stopwatch.StartNew();
            while (!jit.AllOptimized)
            {
                if (warmUp.Elapsed > TimeSpan.FromSeconds(30))
                {
                    Console.Error.WriteLine($"The runtime did not recompile {type} with optimizations within 30 seconds, so this mode cannot show what it is meant to show.");
                    Environment.Exit(3);
                }
                work();
                Thread.Sleep(50);
            }
        }
        work(); // The event is sent just before the new code is switched in.
        Thread.Sleep(50);
    }
}

/// Warmed-up code that ends up waiting for a lock and a SemaphoreSlim: not task waits.
public sealed class Warehouse
{
    private readonly object _ledgerLock = new();
    private readonly SemaphoreSlim _dock = new(1, 1);
    private int _stock;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Restock(int crates)
    {
        var quantity = crates * 12;
        var total = Adjust(quantity);
        return total + crates;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Recount(int shelf)
    {
        if (shelf % 2 == 0)
            return Adjust(shelf);
        return Adjust(-shelf);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Adjust(int delta)
    {
        if (delta == 0) throw new ArgumentOutOfRangeException(nameof(delta));
        var label = delta > 0 ? "in" : "out";
        var weight = label.Length + delta % 3;
        lock (_ledgerLock)
        {
            _stock += weight;
            return _stock;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Unload(int crates)
    {
        if (crates <= 0) throw new ArgumentOutOfRangeException(nameof(crates));
        var label = crates > 100 ? "bulk" : "single";
        var weight = label.Length + crates % 3;
        _dock.Wait();
        try
        {
            _stock -= weight;
            return _stock;
        }
        finally
        {
            _dock.Release();
        }
    }

    /// Takes the dock and the ledger and never gives them back.
    public void Audit(ManualResetEventSlim holding)
    {
        _dock.Wait();
        lock (_ledgerLock)
        {
            holding.Set();
            Thread.Sleep(Timeout.Infinite);
        }
    }
}

/// A validator with two blocking calls on tasks of different types.
public sealed class CreditCheck
{
    public AccountsApi Accounts { get; } = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Check(int orderId)
    {
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        var customer = Accounts.GetCustomerAsync(orderId).Result;
        var key = string.Concat(customer, "-", orderId.ToString());
        var limit = Accounts.GetLimitAsync(key).Result;
        return customer.Length + limit;
    }
}

public sealed class AccountsApi
{
    /// While warming up, answers at once; afterwards waits for a limits service that never answers.
    public volatile bool LimitsDown;
    private readonly TaskCompletionSource<int> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> GetCustomerAsync(int orderId) => Task.FromResult(orderId % 2 == 0 ? "even" : "odd");

    public Task<int> GetLimitAsync(string key) => LimitsDown ? WaitForLimitsAsync(key) : Task.FromResult(key.Length);

    private async Task<int> WaitForLimitsAsync(string key)
    {
        await Task.Yield();
        return await _never.Task + key.Length;
    }
}

/// Reprices every SKU in parallel; the items handed to pool threads wait for a price feed that never connects.
public sealed class PriceUpdate
{
    private readonly ManualResetEventSlim _feedConnected = new(false);
    private int _waitingForFeed;

    public void Run()
    {
        var skus = LoadSkusAsync().Result;
        Parallel.ForEach(skus, new ParallelOptions { MaxDegreeOfParallelism = 3 }, sku => Reprice(sku));
        var saved = SaveAsync(skus).Result;
        Console.WriteLine(saved);
    }

    private void Reprice(string sku)
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            Interlocked.Increment(ref _waitingForFeed);
            _feedConnected.Wait();
        }
        // The thread that called Parallel.ForEach leaves work for the pool threads, then does the rest at once.
        SpinWait.SpinUntil(() => Volatile.Read(ref _waitingForFeed) > 0, 5000);
        GC.KeepAlive(sku);
    }

    private static async Task<List<string>> LoadSkusAsync()
    {
        await Task.Delay(20);
        return Enumerable.Range(0, 50).Select(i => $"SKU-{i}").ToList();
    }

    private static async Task<int> SaveAsync(List<string> skus)
    {
        await Task.Delay(20);
        return skus.Count;
    }
}

/// Listens to the runtime's own JIT events to learn when methods of one type have all been compiled with
/// optimizations: at tier 1 normally, or straight away when tiered compilation is switched off.
sealed class OptimizedMethodsListener(string type, string[] methods) : EventListener
{
    private const EventKeywords JitKeyword = (EventKeywords)0x10;
    private readonly HashSet<string> _optimized = [];

    public bool AllOptimized
    {
        get
        {
            lock (_optimized) return methods.All(_optimized.Contains);
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, JitKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)) return;
        if (e.PayloadNames is not { } names || e.Payload is not { } payload) return;
        if (Value("MethodNamespace") as string != type || Value("MethodName") is not string method || Value("MethodFlags") is not { } flags) return;

        // Bits 7 to 9 of MethodFlags say how the code was compiled: 2 is optimized (no tiering), 4 is tier 1.
        var tier = (Convert.ToUInt32(flags) >> 7) & 7;
        if (tier is 2 or 4)
            lock (_optimized) _optimized.Add(method);

        object? Value(string name)
        {
            var index = names.IndexOf(name);
            return index >= 0 && index < payload.Count ? payload[index] : null;
        }
    }
}

/// Waits in tier-1 methods called from cold code: one lock after a log line and a list (which mustn't count as places
/// that take it), and two methods whose IL can't tell which of two locks or two .Result calls is waiting, which must be
/// named without a line rather than at their caller's.
static class TieredLedgerScenario
{
    public static void Run()
    {
        var ledger = new Ledger();
        OptimizedWaitScenarios.WarmUp("HangApp.Ledger", ["Post", "Stamp", "Total"], () =>
        {
            for (var i = 1; i <= 2000; i++)
            {
                ledger.Post(i);
                ledger.Stamp(i);
                ledger.Total(i);
            }
        });

        ledger.RatesDown = true;
        using var holding = new ManualResetEventSlim();
        new Thread(() => ledger.Close(holding)) { IsBackground = true, Name = "ledger-close" }.Start();
        holding.Wait();
        new Thread(() => ledger.Post(5)) { IsBackground = true, Name = "posting" }.Start();
        new Thread(() => ledger.Stamp(3)) { IsBackground = true, Name = "stamping" }.Start();
        new Thread(() => ledger.Total(4)) { IsBackground = true, Name = "totals" }.Start();
        Ready.SignalLater(1000);
        Thread.Sleep(Timeout.Infinite);
    }
}

public sealed class Ledger
{
    private readonly object _gate = new();
    private readonly TextWriter _log = TextWriter.Null;
    private readonly TaskCompletionSource<int> _rates = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _balance;

    /// While warming up, rates come at once; afterwards from a rate service that never answers.
    public volatile bool RatesDown;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Post(int entry)
    {
        _log.WriteLine($"posting {entry}");
        var lines = new List<int> { entry };
        lock (_gate)
        {
            _balance += lines.Count;
            return _balance;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Stamp(int entry)
    {
        var mark = entry % 7;
        if (entry % 2 == 0) { lock (_gate) { mark++; } }
        else { lock (_gate) { mark--; } }
        return mark;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Total(int entry)
    {
        if (entry <= 0) throw new ArgumentOutOfRangeException(nameof(entry));
        var net = Rate(entry).Result;
        var tax = Rate(entry + 1000).Result;
        return net + tax;
    }

    private Task<int> Rate(int entry) => RatesDown && entry > 1000 ? _rates.Task : Task.FromResult(entry % 10);

    /// Takes the ledger and never gives it back.
    public void Close(ManualResetEventSlim holding)
    {
        lock (_gate)
        {
            holding.Set();
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
