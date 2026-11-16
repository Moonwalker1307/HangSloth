using System.Diagnostics;
using System.Globalization;
using HangSloth.Analysis;

namespace HangSloth.Tests;

/// Runs every samples/DeadlockLab scenario, dumps it, and checks that HangSloth reports exactly the findings in
/// Verdicts (which include the ones the scenario declares; for the healthy controls, nothing). Database scenarios
/// need PostgreSQL and the sqlserver-* ones SQL Server: a connection string from DEADLOCKLAB_PG / DEADLOCKLAB_SQLSERVER,
/// the lab's default login on localhost, or a container started with Testcontainers; each run works in a database of
/// its own there. Without any, they're skipped, unless HANGSLOTH_REQUIRE_DB=1 / HANGSLOTH_REQUIRE_SQLSERVER=1.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class DeadlockLabTests
{
    public static TheoryData<string> CodeScenarios =>
    [
        "transfer", "dining-philosophers", "event-callback", "library-sync-over-async", "semaphore-order", "async-semaphore-reentry",
        "rwlock-vs-lock", "rwlock-upgrade-slow-reader", "rwlock-readers-block-writer", "lazy-init", "static-constructor", "lock-then-wait", "thread-join-under-lock", "timer-dispose",
        "parallel-foreach-under-lock", "ui-invoke", "inline-continuation", "throttle-reentry", "bounded-queue", "lost-pulse", "idle-monitor-consumer",
        "http-connection-limit", "http-no-response", "http-connect-stall", "http-busy-connections", "db-connect-stall", "sqlclient-connect-stall", "timer-overlap", "cancellation-dispose", "finalizer-wait", "child-process-pipe", "test-blocks-on-async", "healthy",
    ];

    public static TheoryData<string> DatabaseScenarios =>
    [
        "db-forgotten-transaction", "db-deadlock-retry", "db-app-lock-cycle", "db-pool-exhaustion", "db-async-row-lock",
        "db-migration-lock", "db-advisory-lock", "db-starvation", "db-async-pool-exhaustion",
        "ef-row-lock", "ef-nested-context", "db-large-select", "db-select-blocks-alter", "db-select-for-update", "db-healthy",
    ];

    public static TheoryData<string> SqlServerScenarios => ["sqlserver-lock-escalation", "sqlserver-read-blocked", "sqlserver-pool-exhaustion"];

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
        ["sqlserver-pool-exhaustion"] = ["Connection pool deadlock: 8 threads are waiting for a free SQL Server connection and the pool is full (4 of 4 connections in use, 0 idle).", "ShipmentAudit.Write"],
        ["db-async-row-lock"] = ["AsyncRefundService.RefundAsync", "`UPDATE lab_async_wallets SET balance = balance + 5 WHERE id = 1`"],
    };

    /// Every finding each scenario reports, with its severity (see Verdict): an extra finding, another severity or Info
    /// noise on a healthy control fails the test. This is the one place to change when a verdict changes on purpose.
    private static readonly Dictionary<string, string> Verdicts = new()
    {
        ["transfer"] = "critical lock-deadlock",
        ["dining-philosophers"] = "critical lock-deadlock",
        ["event-callback"] = "critical lock-deadlock",
        ["library-sync-over-async"] = "critical sync-over-async-deadlock",
        ["semaphore-order"] = "critical semaphore-deadlock",
        ["async-semaphore-reentry"] = "critical semaphore-wait",
        ["rwlock-vs-lock"] = "critical lock-deadlock",
        ["rwlock-upgrade-slow-reader"] = "warning lock-contention",
        ["rwlock-readers-block-writer"] = "warning lock-contention",
        ["lazy-init"] = "critical wait-cycle",
        ["static-constructor"] = "critical type-initializer-deadlock",
        ["lock-then-wait"] = "critical wait-cycle",
        ["thread-join-under-lock"] = "critical wait-cycle",
        ["timer-dispose"] = "warning lock-contention",
        // The workers that queue for the lock behind the cycle are told as contention too, when there are enough cores for them to start.
        ["parallel-foreach-under-lock"] = "critical wait-cycle, warning lock-contention?",
        ["ui-invoke"] = "critical wait-cycle",
        ["inline-continuation"] = "critical lock-deadlock",
        ["throttle-reentry"] = "critical semaphore-wait",
        ["bounded-queue"] = "critical bounded-queue-deadlock",
        ["lost-pulse"] = "critical monitor-wait",
        ["idle-monitor-consumer"] = "",
        ["http-connection-limit"] = "critical http-connection-limit",
        ["http-no-response"] = "warning http-wait",
        ["http-connect-stall"] = "warning http-connect",
        ["http-busy-connections"] = "warning http-connection-limit",
        ["db-connect-stall"] = "warning database-connect",
        ["sqlclient-connect-stall"] = "warning database-connect",
        // The overlapping callbacks hold every pool thread, and they queue for the sync lock.
        ["timer-overlap"] = "critical threadpool-starvation, warning lock-contention",
        ["cancellation-dispose"] = "critical wait-cycle",
        ["finalizer-wait"] = "critical wait-cycle",
        ["child-process-pipe"] = "warning process-pipe-deadlock",
        // The test threads wait for async work, which is told as information; under xunit's context that is no deadlock.
        ["test-blocks-on-async"] = "info blocked-on-task",
        ["healthy"] = "",
        ["db-forgotten-transaction"] = "warning database-wait",
        ["db-deadlock-retry"] = "warning database-wait",
        ["db-app-lock-cycle"] = "critical database-deadlock",
        ["db-pool-exhaustion"] = "critical connection-pool-exhausted",
        ["db-async-row-lock"] = "warning database-wait",
        ["db-migration-lock"] = "warning database-wait",
        ["db-advisory-lock"] = "warning database-wait",
        // The queries the blocked requests wait for are noted as information: the pool is the problem.
        ["db-starvation"] = "critical threadpool-starvation, info database-wait",
        ["db-async-pool-exhaustion"] = "critical connection-pool-exhausted",
        ["ef-row-lock"] = "warning database-wait",
        ["ef-nested-context"] = "critical connection-pool-exhausted",
        ["db-large-select"] = "warning lock-contention",
        ["db-select-blocks-alter"] = "warning database-wait",
        ["db-select-for-update"] = "warning database-wait",
        ["db-healthy"] = "",
        ["sqlserver-lock-escalation"] = "warning database-wait",
        ["sqlserver-read-blocked"] = "warning database-wait",
        ["sqlserver-pool-exhaustion"] = "critical connection-pool-exhausted",
    };

    [Fact]
    public void Every_scenario_is_covered()
    {
        var listed = Lab.Expected.Keys.Order().ToList();
        var tested = ((IEnumerable<object[]>)CodeScenarios).Concat(DatabaseScenarios).Concat(SqlServerScenarios).Select(row => (string)row[0]).Order().ToList();
        Assert.Equal(listed, tested);
        Assert.Equal(listed, Verdicts.Keys.Order().ToList());
        // The verdict includes what the lab itself declares, and starts with it.
        foreach (var (scenario, ids) in Lab.Expected)
        {
            var required = Verdict.RequiredLoudIds(Verdicts[scenario]);
            Assert.True(ids.All(required.Contains) && (ids.Length == 0 || required[0] == ids[0]),
                $"'{scenario}': the lab expects {string.Join(", ", ids)}, the verdict here is \"{Verdicts[scenario]}\"");
        }
    }

    [Theory]
    [MemberData(nameof(CodeScenarios))]
    public void Code_scenario(string scenario) => Check(scenario);

    [SkippableTheory]
    [MemberData(nameof(DatabaseScenarios))]
    public void Database_scenario(string scenario)
    {
        Lab.RequirePostgres();
        Check(scenario);
    }

    [SkippableTheory]
    [MemberData(nameof(SqlServerScenarios))]
    public void SqlServer_scenario(string scenario)
    {
        Lab.RequireSqlServer();
        Check(scenario);
    }

    private static void Check(string scenario)
    {
        var expected = Lab.Expected[scenario];
        Assert.True(Verdicts.TryGetValue(scenario, out var verdict), $"Add '{scenario}' to DeadlockLabTests.Verdicts, with every finding it reports and its severity.");

        var report = SampleApp.Analyze(scenario, project: "DeadlockLab",
            until: r => Verdict.Matches(r, verdict) && (expected.Length == 0 || r.Findings[0].Id == expected[0]));
        var text = string.Join("\n", report.Findings.SelectMany(f => new[] { f.Summary, f.Advice ?? "" }.Concat(f.Details)));
        var forbidden = report.Findings.Select(f => f.Id).Intersect(Lab.MustNotReport.GetValueOrDefault(scenario, [])).ToList();
        Assert.True(forbidden.Count == 0, $"'{scenario}' must not report {string.Join(", ", forbidden)}\n{text}");

        Verdict.Expect(report, verdict, scenario);
        if (expected.Length == 0) return;
        Assert.Equal(expected[0], report.Findings[0].Id);
        foreach (var phrase in Mentions.GetValueOrDefault(scenario, []))
            Assert.True(text.Contains(phrase, StringComparison.Ordinal), $"'{scenario}': expected the report to mention {phrase}\n{text}");
    }
}

internal static class Lab
{
    /// DeadlockLab --list, read once: name, area, difficulty, expected ids, story, ids it must not report.
    private static readonly Lazy<string[][]> List = new(() => SampleApp.Run("dotnet", $"\"{SampleApp.Build("DeadlockLab")}\" --list", expectSuccess: true)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r').Split('\t'))
        .ToArray());

    /// Scenario name -> expected finding ids, as declared by the lab itself (DeadlockLab --list).
    public static Dictionary<string, string[]> Expected => ExpectedIds.Value;

    private static readonly Lazy<Dictionary<string, string[]>> ExpectedIds = new(() => List.Value.ToDictionary(cols => cols[0], cols => cols[3] == "-" ? [] : cols[3].Split(',')));

    /// Scenario name -> finding ids it must not produce at any severity (DeadlockLab --list, last column).
    public static Dictionary<string, string[]> MustNotReport => ForbiddenIds.Value;

    private static readonly Lazy<Dictionary<string, string[]>> ForbiddenIds = new(() => List.Value.ToDictionary(cols => cols[0], cols => cols.Length < 6 || cols[5] == "-" ? [] : cols[5].Split(',')));

    /// Each database is set up only when the first test that needs it runs, so the code scenarios never wait for
    /// (or fail with) a database.
    private static readonly Lazy<LabServer> Postgres = new(() => LabServer.Provide("postgres", "PostgreSQL", "DEADLOCKLAB_PG",
        () => new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16-alpine").Build()));

    private static readonly Lazy<LabServer> SqlServer = new(() => LabServer.Provide("sqlserver", "SQL Server", "DEADLOCKLAB_SQLSERVER",
        () => new Testcontainers.MsSql.MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()));

    /// PostgreSQL for the database scenarios: DEADLOCKLAB_PG if set, else the lab's default login on localhost if it
    /// works, else a throwaway container started with Testcontainers (Linux and macOS with Docker only).
    public static bool PostgresAvailable => Postgres.Value.Problem is null;

    /// SQL Server for the sqlserver-* scenarios, found the same way (DEADLOCKLAB_SQLSERVER, localhost, container).
    public static bool SqlServerAvailable => SqlServer.Value.Problem is null;

    /// Skips the calling test when PostgreSQL can't be had, or fails it with the reason when HANGSLOTH_REQUIRE_DB=1.
    public static void RequirePostgres() => Require(Postgres.Value, "HANGSLOTH_REQUIRE_DB");

    /// The same for SQL Server and HANGSLOTH_REQUIRE_SQLSERVER=1.
    public static void RequireSqlServer() => Require(SqlServer.Value, "HANGSLOTH_REQUIRE_SQLSERVER");

    private static void Require(LabServer server, string requireVariable)
    {
        if (server.Problem is null) return;
        if (Environment.GetEnvironmentVariable(requireVariable) == "1") Assert.Fail($"{requireVariable}=1 but {server.Problem}");
        Skip.If(true, server.Problem);
    }

    /// Runs `DeadlockLab --lab-db ...`; `server` replaces the connection string the lab would use (null: its default).
    internal static (int Exit, string Output, string Error) Admin(string kind, string variable, string? server, string command)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { SampleApp.Build("DeadlockLab"), "--lab-db", kind }.Concat(command.Split(' '))) start.ArgumentList.Add(arg);
        if (server is null) start.Environment.Remove(variable);
        else start.Environment[variable] = server;
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "it didn't answer within 2 minutes");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result.Trim(), stderr.Result.Trim());
    }
}

/// A database server for the lab: found or started once per run, with a database of the run's own on it, which the
/// lab's processes get through the connection string variable. `Problem` says why there is none.
internal sealed record LabServer(string? Problem)
{
    /// Leftover run databases (of runs that were killed) older than this are dropped when a run sets up the server.
    private static readonly TimeSpan Stale = TimeSpan.FromHours(3);

    public static LabServer Provide(string kind, string name, string variable, Func<DotNet.Testcontainers.Containers.IDatabaseContainer> container)
    {
        // A real login with the connection string the lab will use, not just an open port.
        var configured = Environment.GetEnvironmentVariable(variable);
        var server = configured;
        var inContainer = false;
        var login = Lab.Admin(kind, variable, configured, "check");
        if (login.Exit != 0)
        {
            if (configured is not null)
                return new($"{name} doesn't accept the lab's login with the connection string in {variable} ({login.Error}).");
            if (OperatingSystem.IsWindows())
                return new($"{name} isn't usable on localhost ({login.Error}) and Testcontainers needs Linux containers, which Windows runners don't have.");
            try
            {
                var db = container();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8)))
                    db.StartAsync(timeout.Token).GetAwaiter().GetResult();
                TestRun.OnEnd(() => db.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30)));
                server = db.GetConnectionString();
                inContainer = true;
            }
            catch (Exception ex)
            {
                return new($"{name} isn't usable on localhost ({login.Error}) and a container couldn't be started ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).");
            }
        }

        // The run's own database: two runs against one server must not share the lab's tables and advisory locks.
        foreach (var leftover in Lab.Admin(kind, variable, server, "list").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var stamp = leftover.Length >= DatabasePrefix.Length + 14 ? leftover.Substring(DatabasePrefix.Length, 14) : "";
            if (DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
                && created < DateTime.UtcNow - Stale)
                Lab.Admin(kind, variable, server, $"drop {leftover}");
        }

        var own = $"{DatabasePrefix}{TestRun.Id}";
        var made = Lab.Admin(kind, variable, server, $"create {own}");
        if (made.Exit == 0 && made.Output.Length > 0)
        {
            // A container of the run's own goes as a whole.
            if (!inContainer) TestRun.OnEnd(() => Lab.Admin(kind, variable, server, $"drop {own}"));
            // The lab processes inherit this, so they work in the run's database.
            Environment.SetEnvironmentVariable(variable, made.Output);
        }
        else
        {
            // A login that may not create databases: the scenarios use the database it names, as they always did,
            // and two runs against this server at the same time can block each other.
            Environment.SetEnvironmentVariable(variable, server);
        }
        return new((string?)null);
    }

    private const string DatabasePrefix = "hangsloth_run_";
}
