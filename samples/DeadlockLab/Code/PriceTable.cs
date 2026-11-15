namespace DeadlockLab.Code;

// The runtime runs a static constructor under a per-type lock. Any other thread that
// touches the type waits until the constructor finishes. This one waits for such a thread.

public static class PriceTable
{
    private static readonly Dictionary<string, decimal> Prices;
    private static readonly decimal VatRate = 0.25m;

    static PriceTable()
    {
        Prices = LoadPrices();
    }

    public static decimal Lookup(string sku) => Prices.GetValueOrDefault(sku);

    private static Dictionary<string, decimal> LoadPrices()
    {
        var prices = new Dictionary<string, decimal> { ["sloth-plush"] = 20m };
        // Precompute gross prices in the background... which reads PriceTable.VatRate,
        // so it waits for this very static constructor to finish.
        var gross = Task.Run(() => prices.ToDictionary(p => p.Key, p => p.Value * (1 + VatRate)));
        gross.Wait();
        return gross.Result;
    }
}

internal static class PriceTableScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        Console.WriteLine(PriceTable.Lookup("sloth-plush"));
    }
}
