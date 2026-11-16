namespace DeadlockLab.Code;

/// Shipping tariffs from a slow carrier service, cached. Only LoadAsync takes the gate, once, and always releases it.
/// The first lookup holds the gate while it waits minutes for the carrier; the next lookup blocks on .Result behind it.
/// Slow, not deadlocked: GetAsync runs on the same object but never takes the gate itself.
public sealed class TariffCache(CarrierTariffs carrier)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, decimal> _tariffs = [];

    public async Task<decimal> GetAsync(string zone)
    {
        if (_tariffs.TryGetValue(zone, out var known)) return known;
        return await LoadAsync(zone);
    }

    private async Task<decimal> LoadAsync(string zone)
    {
        await _gate.WaitAsync();
        try
        {
            var tariff = await carrier.FetchAsync(zone);
            _tariffs[zone] = tariff;
            return tariff;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class CarrierTariffs
{
    public async Task<decimal> FetchAsync(string zone)
    {
        await Task.Delay(TimeSpan.FromMinutes(10)); // the carrier's service takes minutes to answer
        return zone.Length * 1.5m;
    }
}

internal static class TariffCacheScenario
{
    public static void Run()
    {
        var cache = new TariffCache(new CarrierTariffs());
        _ = cache.GetAsync("north"); // takes the gate and waits for the carrier
        Ready.SignalLater();
        Console.WriteLine(cache.GetAsync("south").Result);
    }
}
