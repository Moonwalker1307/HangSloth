using System.Text.RegularExpressions;
using HangSloth.Analysis;

namespace HangSloth.Tests;

/// DeadlockLab scenarios where a connection is never established, or a pool is busy without a deadlock, checked
/// sentence by sentence on real dumps. None of them needs a database: a silent local listener plays the server.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class ConnectionWaitEndToEndTests
{
    /// The lab's listener gets a new port every run, and the main thread's number is the runtime's choice
    /// (1 on Linux, 2 on Windows), so both are replaced before the sentences are compared.
    private static string Stable(string text) =>
        Regex.Replace(Regex.Replace(text, @"127\.0\.0\.1([:,])\d+", "127.0.0.1$1PORT"), @"\bThread \d+\b", "Thread N");

    private static Finding Analyze(string scenario, string id)
    {
        var report = SampleApp.Analyze(scenario, project: "DeadlockLab", until: r => r.Findings.Any(f => f.Id == id));
        var f = report.Findings[0];
        Assert.True(f.Id == id, $"'{scenario}': expected {id} first, got: {string.Join(", ", report.Findings.Select(x => $"{x.Id} ({x.Summary})"))}");
        return f;
    }

    [Fact]
    public void Https_call_to_a_server_that_never_answers_the_handshake()
    {
        var f = Analyze("http-connect-stall", "http-connect");

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("HTTP call waiting for a connection to be established", f.Title);
        Assert.Equal("Thread N is waiting for a new connection to 127.0.0.1:PORT to be established. The pool isn't at its limit, so this isn't a wait for a free connection: " +
                     "the connect itself (TCP connect, TLS handshake or proxy) hasn't finished. Start at ConnectStall.cs:8.", Stable(f.Summary));
        Assert.Equal(
            [
                "Thread N is blocked in .Result at SupplierPrices.GetPrice (ConnectStall.cs:8), waiting for `GET https://127.0.0.1:PORT/prices/A-1`, " +
                "which is waiting for a connection to 127.0.0.1:PORT to be established.",
                "The pool for 127.0.0.1:PORT has no connection limit (MaxConnectionsPerServer isn't set); its only connection is still being established.",
            ], f.Details.Select(Stable));
        Assert.Equal("Check that 127.0.0.1:PORT is up and reachable from this machine (server, firewall, proxy, DNS). " +
                     "SocketsHttpHandler.ConnectTimeout is infinite by default: set it (for example to 10 seconds) so a connect that never finishes fails with an error, " +
                     "and give the HttpClient a Timeout: it has none (Timeout = Infinite), so nothing else ends this wait.", Stable(f.Advice!));
    }

    [Fact]
    public void Buffered_response_kept_while_other_requests_use_the_connections_is_not_a_deadlock()
    {
        var f = Analyze("http-busy-connections", "http-connection-limit");

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("HTTP calls waiting for a connection", f.Title);
        Assert.Equal("Thread N is waiting for a free HTTP connection to 127.0.0.1:PORT, and all 2 allowed connections are in use. Start at BusyConnections.cs:30.", Stable(f.Summary));
        Assert.Contains("The pool for 127.0.0.1:PORT allows 2 connections (MaxConnectionsPerServer) and 2 are open.", f.Details.Select(Stable));
        Assert.DoesNotContain(f.Details, d => d.Contains("keeps an earlier HttpResponseMessage", StringComparison.Ordinal));
        Assert.Equal("Find what the open connections are doing (slow responses, unread bodies), dispose responses promptly, and give the HttpClient a timeout.", f.Advice);
    }

    [Fact]
    public void Unread_responses_holding_every_connection_are_still_a_deadlock()
    {
        var f = Analyze("http-connection-limit", "http-connection-limit");

        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: HTTP calls holding a connection while waiting for another", f.Title);
        Assert.Contains(f.Details, d => d.StartsWith("CatalogApi.GetProductPageAsync keeps an earlier HttpResponseMessage in a local while making this request.", StringComparison.Ordinal));
    }

    [Fact]
    public void Postgres_connection_to_a_server_that_never_answers()
    {
        var f = Analyze("db-connect-stall", "database-connect");

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting to connect to PostgreSQL", f.Title);
        Assert.Equal("Thread N is still connecting to PostgreSQL at 127.0.0.1:PORT: the network connect, TLS handshake or login hasn't finished. " +
                     "It isn't waiting for a free connection from the pool (1 of 100 connections in use, counting the one being opened).", Stable(f.Summary));
        Assert.Equal(
            [
                "Opening a new connection in StartupCheck.OpenPostgres (ConnectStall.cs:16), called from DbConnectStallScenario.Run (ConnectStall.cs:32) (Thread N).",
                "The connection string turns the connect timeout off (Timeout=0), so this wait never ends on its own.",
            ], f.Details.Select(Stable));
        Assert.Equal("Check that the PostgreSQL server at 127.0.0.1:PORT is up and reachable from this machine: a server that is down or still starting, " +
                     "a firewall or proxy that swallows the traffic, a wrong host or port, DNS. " +
                     "Don't turn the connect timeout off: with Timeout set to a few seconds, an unreachable server fails fast with an error instead of hanging.", Stable(f.Advice!));
    }

    [Fact]
    public void Sql_server_connection_to_a_server_that_never_answers()
    {
        var f = Analyze("sqlclient-connect-stall", "database-connect");

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting to connect to SQL Server", f.Title);
        Assert.Equal("Thread N is still connecting to SQL Server at 127.0.0.1,PORT: the network connect, TLS handshake or login hasn't finished. " +
                     "It isn't waiting for a free connection from the pool (1 of 100 connections in use, counting the one being opened).", Stable(f.Summary));
        Assert.Equal(
            [
                "Opening a new connection in StartupCheck.OpenSqlServer (ConnectStall.cs:22), called from SqlConnectStallScenario.Run (ConnectStall.cs:42) (Thread N).",
                "The connection string turns the connect timeout off (Connect Timeout=0), so this wait never ends on its own.",
            ], f.Details.Select(Stable));
    }
}
