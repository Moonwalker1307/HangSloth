using System.Net.Sockets;
using System.Text.RegularExpressions;
using HangSloth.Analysis;

namespace HangSloth.Tests;

/// Runs every samples/DeadlockLab scenario, dumps it, and checks that HangSloth reports the findings the
/// scenario declares (and, for the healthy controls, nothing). Database scenarios need PostgreSQL and the
/// sqlserver-* ones SQL Server: a connection string from DEADLOCKLAB_PG / DEADLOCKLAB_SQLSERVER, a server on the
/// default local port, or a container started with Testcontainers. Without any, they're skipped, unless
/// HANGSLOTH_REQUIRE_DB=1 / HANGSLOTH_REQUIRE_SQLSERVER=1.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class DeadlockLabTests
{
    public static TheoryData<string> CodeScenarios =>
    [
        "transfer", "dining-philosophers", "event-callback", "library-sync-over-async", "semaphore-order", "async-semaphore-reentry",
        "rwlock-vs-lock", "rwlock-upgrade-slow-reader", "rwlock-readers-block-writer", "lazy-init", "static-constructor", "lock-then-wait", "thread-join-under-lock", "timer-dispose",
        "parallel-foreach-under-lock", "ui-invoke", "inline-continuation", "throttle-reentry", "bounded-queue", "lost-pulse", "idle-monitor-consumer",
        "http-connection-limit", "http-no-response", "http-connect-stall", "http-busy-connections", "db-connect-stall", "sqlclient-connect-stall", "timer-overlap", "cancellation-dispose", "finalizer-wait", "child-process-pipe", "healthy",
    ];

    public static TheoryData<string> DatabaseScenarios =>
    [
        "db-forgotten-transaction", "db-deadlock-retry", "db-app-lock-cycle", "db-pool-exhaustion", "db-async-row-lock",
        "db-migration-lock", "db-advisory-lock", "db-starvation", "db-async-pool-exhaustion",
        "ef-row-lock", "ef-nested-context", "db-large-select", "db-select-blocks-alter", "db-select-for-update", "db-healthy",
    ];

    public static TheoryData<string> SqlServerScenarios => ["sqlserver-lock-escalation", "sqlserver-read-blocked"];

    /// Phrases the explanation must contain: names of the app's own locks, methods and SQL, not just the right finding id.
    private static readonly Dictionary<string, string[]> Mentions = new()
    {
        ["transfer"] = ["Account.Sync (Id = \"alice\")", "Account.Sync (Id = \"bob\")", "sorted by id"],
        ["dining-philosophers"] = ["waiting for each other's locks in a circle", "Philosopher.Dine (Philosophers.cs:"],
        ["event-callback"] = ["OrderBook._ordersLock", "Inventory._stockLock", "OrderBook.OnStockChanged (OrderBook.cs:"],
        ["library-sync-over-async"] = ["SettingsClient.GetSetting", "SettingsStore.LoadAsync"],
        ["semaphore-order"] = ["ShippingService.CarrierApi", "ShippingService.LabelPrinter"],
        ["async-semaphore-reentry"] = ["PriceCache._gate", "PriceCache.RefreshAsync", "isn't reentrant"],
        ["rwlock-vs-lock"] = ["the write lock on ConfigStore._rw", "AuditLog._sync"],
        ["rwlock-upgrade-slow-reader"] = ["is waiting to upgrade to the write lock on PriceCatalog._rw (a ReaderWriterLockSlim) while 1 other thread holds it for reading: Thread",
                                          "(\"report-export\")", "holds the read lock and is sleeping (Thread.Sleep) in PriceCatalog.ExportReport (PriceCatalog.cs:"],
        ["rwlock-readers-block-writer"] = ["(\"price-update\") is waiting for PriceCatalog._rw (a ReaderWriterLockSlim), which 3 threads hold for reading: Thread",
                                           "(\"report-export-3\") holds the read lock"],
        ["lazy-init"] = ["CatalogService._catalog (Lazy<Catalog>)", "Task.Run(CatalogService.LoadRecommendations)"],
        ["static-constructor"] = ["the static constructor of PriceTable"],
        ["lock-then-wait"] = ["ReportService._sync", "Task.Run(ReportService.CollectRows)"],
        ["thread-join-under-lock"] = ["EmailWorker._stateLock", "to finish (Thread.Join)"],
        ["timer-dispose"] = ["MetricsFlusher._bufferLock", "running a timer callback (MetricsFlusher.Flush"],
        ["db-forgotten-transaction"] = ["`UPDATE lab_wallets SET balance = balance + 5 WHERE id = 1`", "has an open transaction", "RefundService.ChargeWithApproval"],
        ["db-deadlock-retry"] = ["each inside its own open transaction", "`UPDATE lab_ledger"],
        ["db-app-lock-cycle"] = ["ProductCache._cacheLock", "`UPDATE lab_products SET views = views + 1 WHERE id = 1`", "pg_blocking_pids"],
        ["db-pool-exhaustion"] = ["4 of 4 connections in use", "AuditTrail.Write"],
        ["parallel-foreach-under-lock"] = ["a lock the caller holds isn't reentrant for them", "ThumbnailService._cacheLock", "a Parallel.For/ForEach worker"],
        ["ui-invoke"] = ["UiDispatcherContext.Send, called from ExportWindow.Export (UiInvoke.cs:", "Task.Run(ExportWindow.Export)"],
        ["inline-continuation"] = ["OrderPipeline._pipelineLock", "OrderPipeline.ShippingLock", "inline: OrderPipeline.MarkPaid (InlineContinuation.cs:", "RunContinuationsAsynchronously"],
        ["throttle-reentry"] = ["PartnerApi._throttle", "3 slots and 3 async callers waiting"],
        ["bounded-queue"] = ["EventBus._queue", "full (2 of 2)", "EventBus._subscribersLock"],
        ["lost-pulse"] = ["Monitor.Wait(WorkQueue._sync)", "WorkQueue._items (1 item)", "(Thread.Join)", "holds work already, so the Pulse for it was most likely sent before"],
        ["db-migration-lock"] = ["`SELECT count(*) FROM lab_catalog`", "has an open transaction", "ALTER TABLE"],
        ["db-advisory-lock"] = ["`SELECT pg_advisory_lock(4242)`", "NightlyJob._leaderConnection", "locktype = 'advisory'"],
        ["db-starvation"] = ["ReportsController.GetCount", "`SELECT count(*) FROM lab_reports, pg_sleep(30)`"],
        ["db-async-pool-exhaustion"] = ["3 of 3 connections in use", "AsyncCheckoutHandler.WriteAuditAsync", "AsyncCheckoutHandler.HandleAsync"],
        ["http-connection-limit"] = ["CatalogApi.GetProductPageAsync keeps an earlier HttpResponseMessage", "allows 2 connections (MaxConnectionsPerServer)", "/reviews/"],
        ["http-no-response"] = ["`POST http://127.0.0.1:", "/charges`", "Timeout = Infinite"],
        ["timer-overlap"] = ["the same timer callback (InventorySync.SyncNow)", "PeriodicTimer"],
        ["cancellation-dispose"] = ["Download._stateLock", "disposing a CancellationTokenRegistration in Download.Complete", "Unregister()"],
        ["finalizer-wait"] = ["HandleLog.Sync", "(finalizer)", "NativeBuffer.Finalize", "GC.WaitForPendingFinalizers"],
        ["child-process-pipe"] = ["DeadlockLab", "--spew", "standard output is redirected", "ReadToEnd"],
        ["ef-row-lock"] = ["`UPDATE lab_ef_orders SET status = @p0 WHERE id = @p1;`", "has an open transaction", "OrderApproval.Approve"],
        ["ef-nested-context"] = ["2 of 2 connections in use", "EfAudit.Log", "Database.UseTransaction"],
        ["db-large-select"] = ["EventReportCache._cacheLock", "rows so far", "`SELECT id, kind FROM lab_page_events`"],
        ["db-select-blocks-alter"] = ["queued behind the ALTER", "`ALTER TABLE lab_events ADD COLUMN source text`", "EventExport.Export"],
        ["db-select-for-update"] = ["FOR UPDATE", "SKIP LOCKED", "InvoiceBatch.Run"],
        ["sqlserver-lock-escalation"] = ["REPEATABLE READ", "escalat", "MonthEndReport.Run"],
        ["sqlserver-read-blocked"] = ["READ_COMMITTED_SNAPSHOT", "StockAdjustment.Adjust"],
        ["db-async-row-lock"] = ["AsyncRefundService.RefundAsync", "`UPDATE lab_async_wallets SET balance = balance + 5 WHERE id = 1`"],
    };

    [Fact]
    public void Every_scenario_is_covered()
    {
        var listed = Lab.Expected.Keys.Order().ToList();
        var tested = ((IEnumerable<object[]>)CodeScenarios).Concat(DatabaseScenarios).Concat(SqlServerScenarios).Select(row => (string)row[0]).Order().ToList();
        Assert.Equal(listed, tested);
    }

    [Theory]
    [MemberData(nameof(CodeScenarios))]
    public void Code_scenario(string scenario) => Check(scenario);

    [SkippableTheory]
    [MemberData(nameof(DatabaseScenarios))]
    public void Database_scenario(string scenario)
    {
        Skip.IfNot(Lab.PostgresAvailable, "PostgreSQL isn't reachable; set DEADLOCKLAB_PG to run the database scenarios.");
        Check(scenario);
    }

    [SkippableTheory]
    [MemberData(nameof(SqlServerScenarios))]
    public void SqlServer_scenario(string scenario)
    {
        Skip.IfNot(Lab.SqlServerAvailable, "SQL Server isn't reachable and no container could be started; set DEADLOCKLAB_SQLSERVER to run these.");
        Check(scenario);
    }

    private static void Check(string scenario)
    {
        var expected = Lab.Expected[scenario];
        static List<string> Found(AnalysisReport r) => r.Findings.Where(f => f.Severity >= Severity.Warning).Select(f => f.Id).Distinct().ToList();

        var report = SampleApp.Analyze(scenario, project: "DeadlockLab", until: r => expected.All(Found(r).Contains));
        var found = Found(report);
        var text = string.Join("\n", report.Findings.SelectMany(f => new[] { f.Summary, f.Advice ?? "" }.Concat(f.Details)));
        var forbidden = report.Findings.Select(f => f.Id).Intersect(Lab.MustNotReport.GetValueOrDefault(scenario, [])).ToList();
        Assert.True(forbidden.Count == 0, $"'{scenario}' must not report {string.Join(", ", forbidden)}\n{text}");

        if (expected.Length == 0)
        {
            Assert.True(found.Count == 0, $"Expected no findings for '{scenario}', got: {string.Join(", ", found)}\n{text}");
            return;
        }
        Assert.True(expected.All(found.Contains), $"Expected {string.Join(", ", expected)} for '{scenario}', got: {string.Join(", ", found)}\n{text}");
        Assert.Equal(expected[0], report.Findings[0].Id);
        foreach (var phrase in Mentions.GetValueOrDefault(scenario, []))
            Assert.True(text.Contains(phrase, StringComparison.Ordinal), $"'{scenario}': expected the report to mention {phrase}\n{text}");
    }
}

internal static class Lab
{
    /// Scenario name -> expected finding ids, as declared by the lab itself (DeadlockLab --list).
    public static readonly Dictionary<string, string[]> Expected = SampleApp.Run("dotnet", $"\"{SampleApp.Build("DeadlockLab")}\" --list", expectSuccess: true)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('\t'))
        .ToDictionary(cols => cols[0], cols => cols[3] == "-" ? [] : cols[3].Split(','));

    /// Scenario name -> finding ids it must not produce at any severity (DeadlockLab --list, last column).
    public static readonly Dictionary<string, string[]> MustNotReport = SampleApp.Run("dotnet", $"\"{SampleApp.Build("DeadlockLab")}\" --list", expectSuccess: true)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r').Split('\t'))
        .ToDictionary(cols => cols[0], cols => cols.Length < 6 || cols[5] == "-" ? [] : cols[5].Split(','));

    // Kept alive for the whole test run (declared first: static fields initialize in order); Testcontainers' reaper removes them when the process exits.
    private static readonly List<DotNet.Testcontainers.Containers.IDatabaseContainer> Containers = [];

    /// PostgreSQL for the database scenarios: DEADLOCKLAB_PG if set, else localhost:5432 if something listens
    /// there, else a throwaway container started with Testcontainers (Linux and macOS with Docker only).
    public static bool PostgresAvailable { get; } = Provide(
        "DEADLOCKLAB_PG", "PostgreSQL", defaultPort: 5432, "HANGSLOTH_REQUIRE_DB",
        () => new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16-alpine").Build());

    /// SQL Server for the sqlserver-* scenarios, found the same way (DEADLOCKLAB_SQLSERVER, localhost:1433, container).
    public static bool SqlServerAvailable { get; } = Provide(
        "DEADLOCKLAB_SQLSERVER", "SQL Server", defaultPort: 1433, "HANGSLOTH_REQUIRE_SQLSERVER",
        () => new Testcontainers.MsSql.MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build());

    private static bool Provide(string variable, string name, int defaultPort, string requireVariable,
        Func<DotNet.Testcontainers.Containers.IDatabaseContainer> container)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (configured is not null ? Reachable(configured, defaultPort) : Reachable("Host=localhost", defaultPort)) return true;

        string? problem = configured is not null ? $"{name} isn't reachable at the address in {variable}." : null;
        if (problem is null && OperatingSystem.IsWindows())
            problem = $"{name} isn't running on localhost and Testcontainers needs Linux containers, which Windows runners don't have.";
        if (problem is null)
        {
            try
            {
                var db = container();
                db.StartAsync().GetAwaiter().GetResult();
                Containers.Add(db);
                // The lab processes inherit this, so they talk to the container.
                Environment.SetEnvironmentVariable(variable, db.GetConnectionString());
                return true;
            }
            catch (Exception ex)
            {
                problem = $"{name} isn't running on localhost and a container couldn't be started ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
            }
        }
        if (Environment.GetEnvironmentVariable(requireVariable) == "1")
            throw new InvalidOperationException($"{requireVariable}=1 but {problem}");
        return false;
    }

    private static bool Reachable(string cs, int defaultPort)
    {
        var host = Regex.Match(cs, @"(?:^|;)\s*(?:Host|Server|Data Source)\s*=\s*(?:tcp:)?([^;,]+)", RegexOptions.IgnoreCase) is { Success: true } h ? h.Groups[1].Value.Trim() : "localhost";
        var port = Regex.Match(cs, @"(?:^|;)\s*Port\s*=\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } p ? int.Parse(p.Groups[1].Value)
            : Regex.Match(cs, @"(?:Server|Data Source)\s*=\s*[^;,]+,(\d+)", RegexOptions.IgnoreCase) is { Success: true } sp ? int.Parse(sp.Groups[1].Value)
            : defaultPort;
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
