namespace DeadlockLab.Code;

// Two classes, each with its own lock, each perfectly reasonable on its own.
// The deadlock only exists in the call graph between them: OrderBook -> Inventory
// on one thread, Inventory -> (event) -> OrderBook on the other.

public sealed class Inventory
{
    private readonly object _stockLock = new();
    private readonly Dictionary<string, int> _stock = new() { ["sloth-plush"] = 3 };

    public event Action<string, int>? StockChanged;

    public bool TryReserve(string sku)
    {
        lock (_stockLock)
        {
            if (_stock.GetValueOrDefault(sku) == 0) return false;
            _stock[sku]--;
            return true;
        }
    }

    public void Restock(string sku, int count)
    {
        lock (_stockLock)
        {
            _stock[sku] = _stock.GetValueOrDefault(sku) + count;
            // Raising an event while holding a lock runs foreign code under that lock.
            StockChanged?.Invoke(sku, _stock[sku]);
        }
    }
}

public sealed class OrderBook
{
    private readonly object _ordersLock = new();
    private readonly List<string> _backorders = ["sloth-plush"];
    private readonly Inventory _inventory;
    internal Action? AfterLockingOrders;

    public OrderBook(Inventory inventory)
    {
        _inventory = inventory;
        _inventory.StockChanged += OnStockChanged;
    }

    public bool Place(string sku)
    {
        lock (_ordersLock)
        {
            AfterLockingOrders?.Invoke();
            if (_inventory.TryReserve(sku)) return true;
            _backorders.Add(sku);
            return false;
        }
    }

    private void OnStockChanged(string sku, int available)
    {
        lock (_ordersLock)
        {
            _backorders.RemoveAll(s => s == sku && available > 0);
        }
    }
}

internal static class OrderBookScenario
{
    public static void Run()
    {
        var inventory = new Inventory();
        var orders = new OrderBook(inventory);

        // Make the unlucky interleaving deterministic: the checkout thread takes the
        // OrderBook lock, then the warehouse thread restocks (taking the Inventory lock).
        using var ordersLocked = new ManualResetEventSlim();
        using var restocking = new ManualResetEventSlim();
        orders.AfterLockingOrders = () => { ordersLocked.Set(); restocking.Wait(); Thread.Sleep(200); };

        new Thread(() => orders.Place("sloth-plush")) { Name = "checkout" }.Start();
        new Thread(() =>
        {
            ordersLocked.Wait();
            restocking.Set();
            inventory.Restock("sloth-plush", 10);
        }) { Name = "warehouse" }.Start();

        Ready.SignalLater(1000);
        Ready.Forever();
    }
}
