namespace DeadlockLab.Code;

// SemaphoreSlim(1, 1) is a popular "lock that also works with async". It has no owner,
// so neither the runtime nor most tools can tell who is holding it.

public sealed class ShippingService
{
    internal static readonly SemaphoreSlim LabelPrinter = new(1, 1);
    internal static readonly SemaphoreSlim CarrierApi = new(1, 1);

    public static void PrintThenBook(Barrier barrier)
    {
        LabelPrinter.Wait();
        try
        {
            barrier.SignalAndWait();
            CarrierApi.Wait();
            try { Console.WriteLine("booked"); } finally { CarrierApi.Release(); }
        }
        finally { LabelPrinter.Release(); }
    }

    public static void BookThenPrint(Barrier barrier)
    {
        CarrierApi.Wait();
        try
        {
            barrier.SignalAndWait();
            LabelPrinter.Wait();
            try { Console.WriteLine("printed"); } finally { LabelPrinter.Release(); }
        }
        finally { CarrierApi.Release(); }
    }
}

internal static class SemaphoreOrderScenario
{
    public static void Run()
    {
        using var barrier = new Barrier(2);
        new Thread(() => ShippingService.PrintThenBook(barrier)) { Name = "label-worker" }.Start();
        new Thread(() => ShippingService.BookThenPrint(barrier)) { Name = "carrier-worker" }.Start();
        Ready.SignalLater();
        Ready.Forever();
    }
}

/// An async cache guarded by a SemaphoreSlim. GetAsync takes the gate and, on a miss,
/// calls RefreshAsync, which was written separately and also takes the gate.
public sealed class PriceCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, decimal> _prices = [];

    public async Task<decimal> GetAsync(string sku)
    {
        await _gate.WaitAsync();
        try
        {
            if (_prices.TryGetValue(sku, out var price)) return price;
            await RefreshAsync();
            return _prices[sku];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync(); // never returns: GetAsync already holds the gate
        try
        {
            await Task.Delay(10);
            _prices["sloth-plush"] = 19.99m;
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal static class AsyncCacheScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var cache = new PriceCache();
        Console.WriteLine(cache.GetAsync("sloth-plush").GetAwaiter().GetResult());
    }
}
