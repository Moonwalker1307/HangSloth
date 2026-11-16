namespace DeadlockLab.Code;

/// Freight quotes from a slow supplier, one request at a time. GetAsync tries the gate without waiting
/// (WaitAsync(0)); when another quote has it, it goes the slow way, which waits for the gate. The first quote holds
/// the gate while the supplier takes minutes, so the second one waits in the slow path: slow, not deadlocked. Its
/// failed WaitAsync(0) took nothing.
public sealed class FreightQuotes
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<decimal> GetAsync(string route)
    {
        if (!await _gate.WaitAsync(0)) return await GetQueuedAsync(route);
        try
        {
            return await AskSupplierAsync(route);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<decimal> GetQueuedAsync(string route)
    {
        await _gate.WaitAsync();
        try
        {
            return await AskSupplierAsync(route);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<decimal> AskSupplierAsync(string route)
    {
        await Task.Delay(TimeSpan.FromMinutes(10)); // the supplier takes minutes to answer
        return route.Length * 2.5m;
    }
}

internal static class FreightQuotesScenario
{
    public static void Run()
    {
        var quotes = new FreightQuotes();
        _ = quotes.GetAsync("rotterdam-hamburg"); // gets the gate and waits for the supplier
        Ready.SignalLater();
        Console.WriteLine(quotes.GetAsync("hamburg-oslo").Result);
    }
}
