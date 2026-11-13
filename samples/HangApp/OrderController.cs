namespace HangApp;

public sealed class OrderController
{
    private readonly OrderService _orders = new();

    public string Get(int id)
    {
        // BUG: blocking on async code while holding the single-threaded context.
        var order = _orders.GetOrderAsync(id).Result;
        return order.Item;
    }

    public string GetPrice(int id)
    {
        // BUG: blocks a thread pool thread waiting for work that needs a thread pool thread.
        return _orders.FetchPriceAsync(id).Result;
    }
}
