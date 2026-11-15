using Microsoft.EntityFrameworkCore;

namespace DeadlockLab.Database;

public sealed class LabOrder
{
    public int Id { get; set; }
    public string Status { get; set; } = "";
}

public sealed class LabAuditEntry
{
    public int Id { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ShopContext(string connectionString) : DbContext
{
    public DbSet<LabOrder> Orders => Set<LabOrder>();
    public DbSet<LabAuditEntry> Audit => Set<LabAuditEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNpgsql(connectionString);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LabOrder>().ToTable("lab_ef_orders").Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        model.Entity<LabOrder>().Property(o => o.Status).HasColumnName("status");
        model.Entity<LabAuditEntry>().ToTable("lab_ef_audit").Property(a => a.Id).HasColumnName("id");
        model.Entity<LabAuditEntry>().Property(a => a.Message).HasColumnName("message");
    }

    public static void ResetSchema()
    {
        Db.Reset("lab_ef_orders", "id int primary key, status text", "(1, 'new'), (2, 'new'), (3, 'new')");
        Db.Reset("lab_ef_audit", "id serial primary key, message text", "(DEFAULT, 'start')");
    }
}

/// Approving an order opens an EF Core transaction, saves the new status, and then asks a
/// fraud check for a second opinion before committing. While it waits, the order row stays locked,
/// and the warehouse's SaveChanges on the same order waits too.
public static class OrderApproval
{
    public static void Approve(ManualResetEventSlim fraudCheck, ManualResetEventSlim saved)
    {
        using var db = new ShopContext(Db.ConnectionString());
        using var tx = db.Database.BeginTransaction();
        db.Orders.Single(o => o.Id == 1).Status = "approved";
        db.SaveChanges();
        saved.Set();
        fraudCheck.Wait(); // BUG: waiting for something slow inside the transaction
        tx.Commit();
    }

    public static void Ship()
    {
        using var db = new ShopContext(Db.ConnectionString());
        db.Orders.Single(o => o.Id == 1).Status = "shipped";
        db.SaveChanges(); // waits for the approval's row lock
    }
}

internal static class EfRowLockScenario
{
    public static void Run()
    {
        ShopContext.ResetSchema();
        var fraudCheck = new ManualResetEventSlim();
        var saved = new ManualResetEventSlim();
        new Thread(() => OrderApproval.Approve(fraudCheck, saved)) { Name = "order-approval" }.Start();
        saved.Wait();
        new Thread(OrderApproval.Ship) { Name = "warehouse" }.Start();
        Ready.SignalLater(1500);
        Ready.Forever();
    }
}

/// A unit of work holds a DbContext with an open transaction and calls an audit service that
/// creates its own DbContext. Each context needs its own connection, so with more concurrent
/// checkouts than pool slots, every checkout holds one connection and waits for a second.
public static class EfCheckout
{
    private const string PoolSettings = "Maximum Pool Size=2;Timeout=0";

    public static void Checkout(int orderId)
    {
        using var db = new ShopContext(Db.ConnectionString(PoolSettings));
        using var tx = db.Database.BeginTransaction();
        db.Orders.Single(o => o.Id == orderId).Status = "paid";
        db.SaveChanges();
        Thread.Sleep(300);
        EfAudit.Log($"order {orderId} paid"); // BUG: a second context, a second connection
        tx.Commit();
    }
}

public static class EfAudit
{
    public static void Log(string message)
    {
        using var db = new ShopContext(Db.ConnectionString("Maximum Pool Size=2;Timeout=0"));
        db.Audit.Add(new LabAuditEntry { Message = message });
        db.SaveChanges();
    }
}

internal static class EfNestedContextScenario
{
    public static void Run()
    {
        ShopContext.ResetSchema();
        for (var i = 1; i <= 3; i++)
        {
            var id = i;
            new Thread(() => EfCheckout.Checkout(id)) { Name = $"checkout-{id}" }.Start();
        }
        Ready.SignalLater(2000);
        Ready.Forever();
    }
}
