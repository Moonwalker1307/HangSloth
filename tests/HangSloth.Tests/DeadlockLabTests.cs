using System.Net.Sockets;
using System.Text.RegularExpressions;
using HangSloth.Analysis;

namespace HangSloth.Tests;

/// Runs every samples/DeadlockLab scenario, dumps it, and checks that HangSloth reports the findings the
/// scenario declares (and, for the healthy controls, nothing). Database scenarios need PostgreSQL
/// (DEADLOCKLAB_PG, default localhost:5432) and are skipped without it, unless HANGSLOTH_REQUIRE_DB=1.
[Trait("Category", "EndToEnd")]
public class DeadlockLabTests
{
    public static TheoryData<string> CodeScenarios =>
    [
        "transfer", "dining-philosophers", "event-callback", "library-sync-over-async", "semaphore-order", "async-semaphore-reentry",
        "rwlock-vs-lock", "lazy-init", "static-constructor", "lock-then-wait", "thread-join-under-lock", "timer-dispose", "healthy",
    ];

    public static TheoryData<string> DatabaseScenarios =>
    [
        "db-forgotten-transaction", "db-deadlock-retry", "db-app-lock-cycle", "db-pool-exhaustion", "db-async-row-lock", "db-healthy",
    ];

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
        ["lazy-init"] = ["CatalogService._catalog (Lazy<Catalog>)", "Task.Run(CatalogService.LoadRecommendations)"],
        ["static-constructor"] = ["the static constructor of PriceTable"],
        ["lock-then-wait"] = ["ReportService._sync", "Task.Run(ReportService.CollectRows)"],
        ["thread-join-under-lock"] = ["EmailWorker._stateLock", "to finish (Thread.Join)"],
        ["timer-dispose"] = ["MetricsFlusher._bufferLock", "running a timer callback (MetricsFlusher.Flush"],
        ["db-forgotten-transaction"] = ["`UPDATE lab_wallets SET balance = balance + 5 WHERE id = 1`", "has an open transaction", "RefundService.ChargeWithApproval"],
        ["db-deadlock-retry"] = ["each inside its own open transaction", "`UPDATE lab_ledger"],
        ["db-app-lock-cycle"] = ["ProductCache._cacheLock", "`UPDATE lab_products SET views = views + 1 WHERE id = 1`", "pg_blocking_pids"],
        ["db-pool-exhaustion"] = ["4 of 4 connections in use", "AuditTrail.Write"],
        ["db-async-row-lock"] = ["AsyncRefundService.RefundAsync", "`UPDATE lab_async_wallets SET balance = balance + 5 WHERE id = 1`"],
    };

    [Fact]
    public void Every_scenario_is_covered()
    {
        var listed = Lab.Expected.Keys.Order().ToList();
        var tested = ((IEnumerable<object[]>)CodeScenarios).Concat(DatabaseScenarios).Select(row => (string)row[0]).Order().ToList();
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

    private static void Check(string scenario)
    {
        var expected = Lab.Expected[scenario];
        static List<string> Found(AnalysisReport r) => r.Findings.Where(f => f.Severity >= Severity.Warning).Select(f => f.Id).Distinct().ToList();

        var report = SampleApp.Analyze(scenario, project: "DeadlockLab", until: r => expected.All(Found(r).Contains));
        var found = Found(report);
        var text = string.Join("\n", report.Findings.SelectMany(f => new[] { f.Summary, f.Advice ?? "" }.Concat(f.Details)));

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

    public static bool PostgresAvailable { get; } = CheckPostgres();

    private static bool CheckPostgres()
    {
        var cs = Environment.GetEnvironmentVariable("DEADLOCKLAB_PG") ?? "Host=localhost";
        var host = Regex.Match(cs, @"(?:^|;)\s*(?:Host|Server)\s*=\s*([^;,]+)", RegexOptions.IgnoreCase) is { Success: true } h ? h.Groups[1].Value.Trim() : "localhost";
        var port = Regex.Match(cs, @"(?:^|;)\s*Port\s*=\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } p ? int.Parse(p.Groups[1].Value) : 5432;
        try
        {
            using var client = new TcpClient();
            if (client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && client.Connected) return true;
        }
        catch
        {
            // Fall through.
        }
        if (Environment.GetEnvironmentVariable("HANGSLOTH_REQUIRE_DB") == "1")
            throw new InvalidOperationException($"HANGSLOTH_REQUIRE_DB=1 but PostgreSQL isn't reachable at {host}:{port}.");
        return false;
    }
}
