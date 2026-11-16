using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using HangSloth.Analysis;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// samples/DriverVersions built against older drivers, whose pool types and private fields have other names:
/// the same hangs must get the same findings, with the pool's numbers. Each driver version is built once and
/// reused for its scenarios. They need PostgreSQL (SQL Server for the SqlClient one), found like the
/// DeadlockLab database scenarios.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class DriverVersionsEndToEndTests
{
    private const string NestedPoolDeadlock =
        "Connection pool deadlock: 6 threads are waiting for a free PostgreSQL connection and the pool is full (3 of 3 connections in use, 0 idle). " +
        "Every connection is held by threads that are themselves waiting for another connection, so none is ever returned.";

    [SkippableTheory]
    [InlineData("6.0.13")]
    [InlineData("7.0.10")]
    [InlineData("10.0.3")]
    public void Npgsql_pool_exhausted_by_nested_connections(string version)
    {
        Skip.IfNot(Lab.PostgresAvailable, "PostgreSQL isn't reachable; set DEADLOCKLAB_PG to run the database scenarios.");
        var report = Analyze($"NpgsqlVersion={version}", "pool-exhaustion", r => r.Findings.Any(f => f.Severity == Severity.Critical));

        var f = report.Findings[0];
        Assert.Equal("connection-pool-exhausted", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal(NestedPoolDeadlock, f.Summary);
        Assert.Contains("Waiting for a connection in Pg.Open (Invoices.cs:121), called from InvoiceLog.Write (Invoices.cs:36) (3 threads).", f.Details);
        Assert.DoesNotContain(report.Snapshot.Warnings, w => w.Contains("not recognised"));
    }

    [SkippableTheory]
    [InlineData("6.0.13")]
    [InlineData("7.0.10")]
    public void Npgsql_pool_exhausted_by_nested_async_connections(string version)
    {
        Skip.IfNot(Lab.PostgresAvailable, "PostgreSQL isn't reachable; set DEADLOCKLAB_PG to run the database scenarios.");
        var report = Analyze($"NpgsqlVersion={version}", "async-pool-exhaustion", r => r.Findings.Any(f => f.Severity == Severity.Critical));

        var f = report.Findings[0];
        Assert.Equal("connection-pool-exhausted", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: connection pool exhausted by nested async connections", f.Title);
        Assert.StartsWith("Connection pool deadlock: the PostgreSQL pool is full (3 of 3 connections in use, 0 idle) and every connection is held by an async method " +
                          "that is itself awaiting another connection from the same pool.", f.Summary);
        Assert.Contains("3 calls of InvoiceRun.IssueAsync hold an open connection and are paused at Invoices.cs:27, waiting for another connection from the same pool.", f.Details);
        Assert.DoesNotContain(report.Snapshot.Warnings, w => w.Contains("not recognised"));
    }

    [SkippableFact]
    public void Npgsql_6_row_lock_names_the_waiting_session()
    {
        Skip.IfNot(Lab.PostgresAvailable, "PostgreSQL isn't reachable; set DEADLOCKLAB_PG to run the database scenarios.");
        var report = Analyze("NpgsqlVersion=6.0.13", "row-lock", r => r.Findings.Any(f => f.Severity >= Severity.Warning));

        var f = report.Findings[0];
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains(f.Details, d => Regex.IsMatch(d, @"^Thread \d+ \(""correction""\) is waiting in Pg\.Execute \(Invoices\.cs:\d+\) for PostgreSQL to answer " +
                                                        @"`UPDATE compat_locked_invoices SET total = total - 1 WHERE id = 1` \(session \d+\)\.$"));
        Assert.Contains(f.Details, d => Regex.IsMatch(d, @"WHERE pid IN \(\d+, \d+\);$"));
    }

    [SkippableFact]
    public void Npgsql_4_pool_internals_are_reported_as_not_recognised()
    {
        Skip.IfNot(Lab.PostgresAvailable, "PostgreSQL isn't reachable; set DEADLOCKLAB_PG to run the database scenarios.");
        var report = Analyze("NpgsqlVersion=4.1.14", "pool-exhaustion", r => r.Findings.Count > 0);

        Assert.Contains("Npgsql 4.1.14: connection pool internals not recognised, so pool exhaustion cannot be detected.", report.Snapshot.Warnings);
    }

    [SkippableFact]
    public void SqlClient_5_pool_exhausted_by_nested_connections()
    {
        Skip.IfNot(Lab.SqlServerAvailable, "SQL Server isn't reachable and no container could be started; set DEADLOCKLAB_SQLSERVER to run these.");
        var report = Analyze("SqlClientVersion=5.2.3", "sqlserver-pool-exhaustion", r => r.Findings.Any(f => f.Severity == Severity.Critical));

        var f = report.Findings[0];
        Assert.Equal("connection-pool-exhausted", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal(NestedPoolDeadlock.Replace("PostgreSQL", "SQL Server"), f.Summary);
        Assert.DoesNotContain(report.Snapshot.Warnings, w => w.Contains("not recognised"));
    }

    private static readonly ConcurrentDictionary<string, Lazy<string>> Builds = new();

    /// Builds samples/DriverVersions once per driver version (an MSBuild property such as NpgsqlVersion=6.0.13).
    private static string Build(string property) =>
        Builds.GetOrAdd(property, _ => new Lazy<string>(() =>
        {
            var csproj = Path.Combine(SampleApp.RepoRoot(), "samples", "DriverVersions", "DriverVersions.csproj");
            var output = Path.Combine(Path.GetTempPath(), "hangsloth-sample", "DriverVersions", property.Replace('=', '-'));
            SampleApp.Run("dotnet", $"build \"{csproj}\" -c Release -p:{property} -o \"{output}\" -nologo -v q", expectSuccess: true);
            return Path.Combine(output, "DriverVersions.dll");
        })).Value;

    /// Runs one mode, dumps it once it is READY (again, up to 3 times, until the report satisfies <paramref name="until"/>) and analyses the dump.
    private static AnalysisReport Analyze(string property, string mode, Func<AnalysisReport, bool> until)
    {
        var app = Build(property);
        var dump = Path.Combine(Path.GetTempPath(), $"hangsloth-test-drivers-{mode}-{Guid.NewGuid():N}.dmp");
        using var process = Process.Start(new ProcessStartInfo("dotnet", [app, mode])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = Task.Run(() =>
            {
                while (process.StandardOutput.ReadLine() is { } line)
                    if (line == "READY") return true;
                return false;
            });
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)) && ready.Result, $"DriverVersions '{mode}' ({property}) never became ready.{(process.HasExited ? " " + stderr.Result : "")}");

            for (var attempt = 1; ; attempt++)
            {
                DumpLoader.CollectDump(process.Id, dump);
                var report = new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump));
                File.Delete(dump);
                if (until(report) || attempt == 3) return report;
                Thread.Sleep(700);
            }
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { File.Delete(dump); } catch { }
        }
    }
}
