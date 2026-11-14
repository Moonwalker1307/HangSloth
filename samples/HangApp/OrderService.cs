namespace HangApp;

public sealed class OrderService
{
    public async Task<Order> GetOrderAsync(int id)
    {
        // Simulates I/O. The continuation after this await wants to resume on the
        // captured SynchronizationContext (the UI/request thread).
        await Task.Delay(50);
        return new Order(id, "Sloth plushie");
    }

    public async Task<string> FetchPriceAsync(int id)
    {
        await Task.Delay(20).ConfigureAwait(false);
        await Task.Yield();
        return $"{id * 3} EUR";
    }
}

public sealed record Order(int Id, string Item);
