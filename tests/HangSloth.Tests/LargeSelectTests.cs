using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

public class LargeSelectTests
{
    private static readonly StackFrameInfo[] QueryWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        new() { TypeName = "Npgsql.NpgsqlCommand", MethodName = "ExecuteNonQuery", ModuleName = "/app/Npgsql.dll" },
    ];

    private static readonly StackFrameInfo[] ReadRow =
    [
        new() { TypeName = "Npgsql.NpgsqlDataReader", MethodName = "Read", ModuleName = "/app/Npgsql.dll" },
    ];

    private static readonly StackFrameInfo[] Sleep = [Fx("System.Threading.Thread", "Sleep")];

    private static DatabaseActivity Query(string sql, int session, string provider = "PostgreSQL") => new()
    {
        Provider = provider, Waiting = DatabaseWaitKind.Query, CommandText = sql, SessionId = session, Connections = [new DatabaseConnection(session, false)],
    };

    [Theory]
    [InlineData("ALTER TABLE lab_events ADD COLUMN source text", "lab_events")]
    [InlineData("alter table if exists only public.orders drop column x", "public.orders")]
    [InlineData("TRUNCATE TABLE audit", "audit")]
    [InlineData("CREATE UNIQUE INDEX ix_a ON accounts (email)", "accounts")]
    [InlineData("CREATE INDEX CONCURRENTLY ix_a ON accounts (email)", null)]
    [InlineData("SELECT * FROM lab_events", null)]
    public void Ddl_statements_name_the_table_they_lock(string sql, string? table) => Assert.Equal(table, Db.DdlTable(sql));

    [Theory]
    [InlineData("SELECT id FROM invoices WHERE due < now() FOR UPDATE", true)]
    [InlineData("select id from jobs for no key update skip locked", true)]
    [InlineData("SELECT id FROM jobs FOR SHARE", true)]
    [InlineData("SELECT id FROM jobs", false)]
    [InlineData("UPDATE jobs SET done = true", false)]
    public void Select_for_update_locks_rows(string sql, bool locks) => Assert.Equal(locks, Db.LocksRows(sql));

    [Fact]
    public void Alter_table_waiting_behind_an_open_reader_freezes_the_table()
    {
        var snapshot = Process(
            Thread(10, [.. Sleep, App("MyApp.EventExport", "Export", "Export.cs", 30)], "export") with
                { Database = new DatabaseActivity { Provider = "PostgreSQL", OpenQuery = "SELECT id, payload FROM events ORDER BY id", Connections = [new DatabaseConnection(100, false)] } },
            Thread(11, [.. QueryWait, App("MyApp.Migration", "AddColumn", "Migration.cs", 12)], "migration") with { Database = Query("ALTER TABLE events ADD COLUMN source text", 200) },
            Thread(12, [.. QueryWait, App("MyApp.Dashboard", "Count", "Dashboard.cs", 8)], "dashboard-1") with { Database = Query("SELECT count(*) FROM events", 300) },
            Thread(13, [.. QueryWait, App("MyApp.Dashboard", "Count", "Dashboard.cs", 8)], "dashboard-2") with { Database = Query("SELECT count(*) FROM events", 301) });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Lock queue on events: schema change waiting behind a running query", f.Title);
        Assert.Contains("2 queries that came later are queued behind the ALTER", f.Summary);
        Assert.Contains(f.Details, d => d.StartsWith("Thread 10 (\"export\") still has a reader open on `SELECT id, payload FROM events ORDER BY id`"));
        Assert.Contains("lock_timeout", f.Advice);
        Assert.Equal([11, 12, 13, 10], f.ThreadIds);
    }

    [Fact]
    public void Rows_locked_with_select_for_update_suggest_skip_locked_batches()
    {
        var snapshot = Process(
            Thread(10, [.. Sleep, App("MyApp.InvoiceBatch", "Run", "Batch.cs", 20)], "batch") with
            {
                Database = new DatabaseActivity
                {
                    Provider = "PostgreSQL", Connections = [new DatabaseConnection(100, true)],
                    StackCommands = ["SELECT id FROM invoices WHERE due < now() FOR UPDATE"],
                },
            },
            Thread(11, [.. QueryWait, App("MyApp.Payments", "PayOne", "Payments.cs", 9)], "payment") with { Database = Query("UPDATE invoices SET paid = true WHERE id = 4", 200) });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Database query blocked by rows locked with SELECT ... FOR UPDATE", f.Title);
        Assert.Contains("SKIP LOCKED", f.Advice);
        Assert.Contains(f.Details, d => d.Contains("`SELECT id FROM invoices WHERE due < now() FOR UPDATE`"));
    }

    [Fact]
    public void Sql_server_repeatable_read_scan_explains_lock_escalation()
    {
        var snapshot = Process(
            Thread(10, [.. Sleep, App("MyApp.MonthEnd", "Run", "MonthEnd.cs", 20)], "report") with
            {
                Database = new DatabaseActivity
                {
                    Provider = "SQL Server", Isolation = "RepeatableRead", Connections = [new DatabaseConnection(55, true)],
                    StackCommands = ["SELECT customer, SUM(amount) FROM orders GROUP BY customer"],
                },
            },
            Thread(11, [.. QueryWait, App("MyApp.Orders", "Take", "Orders.cs", 9)], "order") with { Database = Query("UPDATE orders SET amount = 1 WHERE id = 25000", 56, "SQL Server") });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Database writes blocked by a big read holding its locks (lock escalation)", f.Title);
        Assert.Contains("REPEATABLE READ", f.Summary + string.Join(" ", f.Details));
        Assert.Contains("SNAPSHOT", f.Advice);
    }

    [Fact]
    public void Sql_server_read_waiting_on_an_uncommitted_write_points_at_read_committed_snapshot()
    {
        var snapshot = Process(
            Thread(10, [.. Sleep, App("MyApp.Stock", "Adjust", "Stock.cs", 20)], "adjust") with
            {
                Database = new DatabaseActivity
                {
                    Provider = "SQL Server", Connections = [new DatabaseConnection(55, true)], StackCommands = ["UPDATE stock SET quantity = 1 WHERE sku = 'A'"],
                },
            },
            Thread(11, [.. QueryWait, App("MyApp.ProductPage", "Show", "Page.cs", 9)], "page") with { Database = Query("SELECT quantity FROM stock WHERE sku = 'A'", 56, "SQL Server") });

        var f = Assert.Single(new DatabaseWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Contains("READ_COMMITTED_SNAPSHOT", f.Summary + string.Join(" ", f.Details) + f.Advice);
        Assert.DoesNotContain("table lock", f.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reading_rows_is_informational_and_names_the_row_count()
    {
        var reader = Thread(10, [.. ReadRow, App("MyApp.ReportCache", "Refresh", "Cache.cs", 30)], "refresh") with
        {
            Database = new DatabaseActivity
            {
                Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ReadingRows, CommandText = "SELECT id, kind FROM page_events", RowsRead = 12345,
                Connections = [new DatabaseConnection(100, false)],
            },
        };

        var f = Assert.Single(new DatabaseReadDetector().Detect(Process(reader), new HashSet<int>()));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("`SELECT id, kind FROM page_events`", f.Summary);
        Assert.Contains("12,345 rows so far", f.Summary);
        Assert.Empty(new DatabaseReadDetector().Detect(Process(reader), new HashSet<int> { 10 }));
    }

    [Fact]
    public void Lock_owner_reading_rows_counts_as_blocking_the_waiters()
    {
        var snapshot = Process(
            Thread(10, [.. ReadRow, App("MyApp.ReportCache", "Refresh", "Cache.cs", 30)], "refresh") with
            {
                Database = new DatabaseActivity { Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ReadingRows, CommandText = "SELECT id FROM page_events", RowsRead = 900 },
            },
            Thread(11, [.. MonitorEnter, App("MyApp.ReportCache", "Get", "Cache.cs", 12)], "request-1") with { WaitingForLock = 0x100 },
            Thread(12, [.. MonitorEnter, App("MyApp.ReportCache", "Get", "Cache.cs", 12)], "request-2") with { WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "ReportCache._cacheLock", OwnerThreadId = 10, WaiterThreadIds = [11, 12] }],
        };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("lock-contention", f.Id);
        Assert.Contains("swap", f.Advice);
    }
}
