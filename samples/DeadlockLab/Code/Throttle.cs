namespace DeadlockLab.Code;

/// A throttle that allows 3 concurrent calls to a partner API. Fetching an order also fetches
/// its customer through the same throttle, so 3 concurrent order fetches use up every slot and
/// then wait for a 4th.
public sealed class PartnerApi
{
    private readonly SemaphoreSlim _throttle = new(3, 3);

    public async Task<string> GetOrderAsync(int id)
    {
        await _throttle.WaitAsync();
        try
        {
            await Task.Delay(20);
            var customer = await GetCustomerAsync(id); // BUG: needs a second slot while holding one
            return $"order {id} for {customer}";
        }
        finally { _throttle.Release(); }
    }

    public async Task<string> GetCustomerAsync(int id)
    {
        await _throttle.WaitAsync();
        try
        {
            await Task.Delay(20);
            return $"customer {id}";
        }
        finally { _throttle.Release(); }
    }
}

internal static class ThrottleScenario
{
    public static void Run()
    {
        var api = new PartnerApi();
        var orders = Enumerable.Range(1, 3).Select(api.GetOrderAsync).ToList();
        Ready.SignalLater();
        orders[0].Wait();
    }
}
