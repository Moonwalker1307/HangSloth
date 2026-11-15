using Npgsql;

namespace DeadlockLab.Database;

// A deadlock that spans the app and the database. Neither side can detect it:
// .NET sees a thread waiting on a socket, PostgreSQL sees a session waiting for a row
// lock held by a session that is "idle in transaction".

public sealed class ProductCache
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<int, decimal> _prices = [];
    internal Action? WhileLocked;

    /// Refreshes the cached price, bumping the view counter while it's at it.
    public void Refresh(int productId)
    {
        lock (_cacheLock)
        {
            WhileLocked?.Invoke();
            using var connection = Db.Open();
            Db.Execute(connection, $"UPDATE lab_products SET views = views + 1 WHERE id = {productId}");
            _prices[productId] = 0;
        }
    }

    public void Invalidate(int productId)
    {
        lock (_cacheLock) _prices.Remove(productId);
    }
}

public sealed class PricingService(ProductCache cache)
{
    internal Action? AfterUpdate;

    public void ChangePrice(int productId, decimal price)
    {
        using var connection = Db.Open();
        using var tx = connection.BeginTransaction();
        Db.Execute(connection, $"UPDATE lab_products SET price = {price} WHERE id = {productId}", tx);
        AfterUpdate?.Invoke();
        cache.Invalidate(productId); // BUG: app lock taken while holding a row lock
        tx.Commit();
    }
}

internal static class CacheAndRowLockScenario
{
    public static void Run()
    {
        Db.Reset("lab_products", "id int primary key, price numeric, views int", "(1, 20, 0)");
        var cache = new ProductCache();
        var pricing = new PricingService(cache);

        var rowLocked = new ManualResetEventSlim();
        var cacheLocked = new ManualResetEventSlim();
        pricing.AfterUpdate = () => { rowLocked.Set(); cacheLocked.Wait(); Thread.Sleep(200); };
        cache.WhileLocked = () => cacheLocked.Set();

        new Thread(() => pricing.ChangePrice(1, 25)) { Name = "price-change" }.Start();
        new Thread(() => { rowLocked.Wait(); cache.Refresh(1); }) { Name = "cache-refresh" }.Start();

        Ready.SignalLater(1200);
        Ready.Forever();
    }
}
