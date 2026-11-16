using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Waiting for a connection to be established is not waiting for a free pooled connection; and the database
/// and HTTP wording only says what the snapshot shows.
public class ConnectionWaitTests
{
    private static StackFrameInfo Lib(string type, string method) => new() { TypeName = type, MethodName = method, ModuleName = "/app/Driver.dll" };

    private static readonly StackFrameInfo[] Caller = [App("MyApp.Startup", "CheckDatabase", "Startup.cs", 12), App("MyApp.Program", "Main", "Program.cs", 5)];

    /// Npgsql 10 inside NpgsqlConnection.Open, as in a dump of a server that accepted the TCP connection and never answered.
    private static readonly StackFrameInfo[] NpgsqlConnecting =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Net.Sockets.Socket", "Receive"),
        Fx("System.Net.Sockets.NetworkStream", "Read"),
        Lib("Npgsql.Internal.NpgsqlReadBuffer", "Ensure"),
        Lib("Npgsql.Internal.NpgsqlConnector+<SetupEncryption>d__215", "MoveNext"),
        Lib("Npgsql.Internal.NpgsqlConnector", "Open"),
        Lib("Npgsql.PoolingDataSource+<OpenNewConnector>d__36", "MoveNext"),
        Lib("Npgsql.PoolingDataSource", "OpenNewConnector"),
        Lib("Npgsql.PoolingDataSource+<<Get>g__RentAsync|33_0>d", "MoveNext"),
        Lib("Npgsql.PoolingDataSource", "Get"),
        Lib("Npgsql.NpgsqlConnection", "Open"),
    ];

    private static readonly StackFrameInfo[] NpgsqlQueued =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Threading.ManualResetEventSlim", "Wait"),
        Lib("Npgsql.PoolingDataSource+<<Get>g__RentAsync|33_0>d", "MoveNext"),
        Lib("Npgsql.PoolingDataSource", "Get"),
        Lib("Npgsql.NpgsqlConnection", "Open"),
    ];

    /// Microsoft.Data.SqlClient 7 waiting for the server's answer to its PRELOGIN.
    private static readonly StackFrameInfo[] SqlClientConnecting =
    [
        Fx("System.Net.Sockets.Socket", "Receive"),
        Fx("Microsoft.Data.SqlClient.Parser.TdsParser", "ConsumePreLoginHandshake"),
        Fx("Microsoft.Data.SqlClient.Parser.TdsParser", "Connect"),
        Fx("Microsoft.Data.SqlClient.Connection.SqlConnectionInternal", "AttemptOneLogin"),
        Fx("Microsoft.Data.SqlClient.Connection.SqlConnectionInternal", ".ctor"),
        Fx("Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "CreateObject"),
        Fx("Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "UserCreateRequest"),
        Fx("Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "TryGetConnection"),
        Fx("Microsoft.Data.SqlClient.SqlConnection", "Open"),
    ];

    private static readonly StackFrameInfo[] SqlClientQueued =
    [
        Fx("System.Threading.WaitHandle", "WaitAny"),
        Fx("Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "TryGetConnection"),
        Fx("Microsoft.Data.SqlClient.SqlConnection", "Open"),
    ];

    private static ThreadSnapshot PoolThread(int id, StackFrameInfo[] driver, string provider, ConnectionPoolInfo? pool, int? holding = null,
        string? dataSource = null, int? timeout = null) =>
        Thread(id, [.. driver, .. Caller]) with
        {
            Database = new DatabaseActivity
            {
                Provider = provider, Waiting = DatabaseWaitKind.ConnectionPool, Pool = pool, DataSource = dataSource, ConnectTimeout = timeout,
                Connections = holding is { } session ? [new DatabaseConnection(session, false)] : [],
            },
        };

    private static List<Finding> Pool(params ThreadSnapshot[] threads) => new ConnectionPoolDetector().Detect(Process(threads), new HashSet<int>()).ToList();

    [Fact]
    public void Connecting_to_a_postgres_server_that_does_not_answer_is_not_a_full_pool()
    {
        var f = Assert.Single(Pool(PoolThread(1, NpgsqlConnecting, "PostgreSQL", new ConnectionPoolInfo(100, 1, 0), dataSource: "db1:5432", timeout: 0)));

        Assert.Equal("database-connect", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting to connect to PostgreSQL", f.Title);
        Assert.Equal("Thread 1 is still connecting to PostgreSQL at db1:5432: the network connect, TLS handshake or login hasn't finished. " +
                     "It isn't waiting for a free connection from the pool (1 of 100 connections in use, counting the one being opened).", f.Summary);
        Assert.Equal(
            [
                "Opening a new connection in Startup.CheckDatabase (Startup.cs:12), called from Program.Main (Program.cs:5) (Thread 1).",
                "The connection string turns the connect timeout off (Timeout=0), so this wait never ends on its own.",
            ], f.Details);
        Assert.Equal("Check that the PostgreSQL server at db1:5432 is up and reachable from this machine: a server that is down or still starting, " +
                     "a firewall or proxy that swallows the traffic, a wrong host or port, DNS. " +
                     "Don't turn the connect timeout off: with Timeout set to a few seconds, an unreachable server fails fast with an error instead of hanging.", f.Advice);
        Assert.DoesNotContain("free PostgreSQL connection", f.Summary);
        Assert.DoesNotContain("connection holders", f.Advice);
    }

    [Fact]
    public void Connecting_with_a_connect_timeout_says_when_it_gives_up()
    {
        var f = Assert.Single(Pool(PoolThread(1, NpgsqlConnecting, "PostgreSQL", new ConnectionPoolInfo(100, 1, 0), dataSource: "db1:5432", timeout: 15)));

        Assert.Contains("The connect timeout is 15 seconds (Timeout=15), so the attempt fails with a timeout error after that.", f.Details);
        Assert.EndsWith("Keep the connect timeout (Timeout in the connection string) short, so an unreachable server fails fast with an error instead of hanging.", f.Advice);
    }

    [Fact]
    public void Connecting_to_sql_server_without_pool_numbers()
    {
        var f = Assert.Single(Pool(PoolThread(2, SqlClientConnecting, "SQL Server", null, dataSource: "sql1,1433")));

        Assert.Equal("database-connect", f.Id);
        Assert.Equal("Thread 2 is still connecting to SQL Server at sql1,1433: the network connect, TLS handshake or login hasn't finished. " +
                     "It isn't waiting for a free connection from the pool.", f.Summary);
        Assert.Equal(["Opening a new connection in Startup.CheckDatabase (Startup.cs:12), called from Program.Main (Program.cs:5) (Thread 2)."], f.Details);
        Assert.EndsWith("Keep the connect timeout (Connect Timeout in the connection string) short, so an unreachable server fails fast with an error instead of hanging.", f.Advice);
    }

    [Theory]
    [InlineData("Npgsql.PoolingDataSource", "OpenNewConnector", true)]
    [InlineData("Npgsql.PoolingDataSource+<OpenNewConnector>d__36", "MoveNext", true)]
    [InlineData("Npgsql.PoolingDataSource", "Get", false)]
    [InlineData("Microsoft.Data.SqlClient.SqlInternalConnectionTds", "OpenLoginEnlist", true)]
    [InlineData("Microsoft.Data.SqlClient.Connection.SqlConnectionInternal", "LoginNoFailover", true)]
    [InlineData("Microsoft.Data.SqlClient.Parser.TdsParser", "Connect", true)]
    [InlineData("Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "UserCreateRequest", true)]
    [InlineData("Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "TryGetConnection", false)]
    [InlineData("Microsoft.Data.SqlClient.SqlCommand", "ExecuteReader", false)]
    [InlineData("MySqlConnector.Core.ServerSession+<ConnectAsync>d__100", "MoveNext", true)]
    [InlineData("MySqlConnector.Core.ConnectionPool+<ConnectSessionAsync>d__20", "MoveNext", true)]
    [InlineData("MySqlConnector.Core.ConnectionPool+<GetSessionAsync>d__11", "MoveNext", false)]
    public void Driver_frames_that_open_a_new_connection(string type, string method, bool connecting) =>
        Assert.Equal(connecting, Db.IsConnectFrame(Lib(type, method)));

    [Fact]
    public void Threads_queued_behind_connections_that_are_still_being_opened()
    {
        var pool = new ConnectionPoolInfo(2, 2, 0);
        var findings = Pool(
            PoolThread(1, NpgsqlConnecting, "PostgreSQL", pool, timeout: 0), PoolThread(2, NpgsqlConnecting, "PostgreSQL", pool, timeout: 0),
            PoolThread(3, NpgsqlQueued, "PostgreSQL", pool), PoolThread(4, NpgsqlQueued, "PostgreSQL", pool));

        var f = Assert.Single(findings);
        Assert.Equal("database-connect", f.Id);
        Assert.Equal("2 threads are still connecting to PostgreSQL: the network connect, TLS handshake or login hasn't finished. " +
                     "They aren't waiting for a free connection from the pool (2 of 2 connections in use, counting the ones being opened). " +
                     "2 other threads are queued in the pool behind them: the pool is full, but only with connections that are still being opened.", f.Summary);
        Assert.Contains("Waiting for a free connection in Startup.CheckDatabase (Startup.cs:12), called from Program.Main (Program.cs:5) (2 threads).", f.Details);
        Assert.Equal([1, 2, 3, 4], f.ThreadIds);
    }

    [Fact]
    public void Threads_queued_behind_a_connect_do_not_call_an_unknown_pool_full()
    {
        var f = Assert.Single(Pool(PoolThread(1, SqlClientConnecting, "SQL Server", null), PoolThread(2, SqlClientQueued, "SQL Server", null)));

        Assert.Equal("database-connect", f.Id);
        Assert.EndsWith("It isn't waiting for a free connection from the pool. Thread 2 is queued in the pool behind it.", f.Summary);
        Assert.DoesNotContain("full", f.Summary);
    }

    [Fact]
    public void Queued_threads_with_a_visible_holder_keep_the_pool_finding()
    {
        var pool = new ConnectionPoolInfo(2, 2, 0);
        var holder = Thread(5, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Job", "Run", "Job.cs", 4)]) with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(105, false)] },
        };
        var findings = Pool(PoolThread(1, NpgsqlConnecting, "PostgreSQL", pool), PoolThread(3, NpgsqlQueued, "PostgreSQL", pool), holder);

        Assert.Equal(["database-connect", "connection-pool-exhausted"], findings.Select(f => f.Id));
        Assert.Equal("Thread 3 is waiting for a free PostgreSQL connection and the pool is full (2 of 2 connections in use, 0 idle).", findings[1].Summary);
    }

    [Fact]
    public void An_unknown_pool_is_not_called_full()
    {
        var f = Assert.Single(Pool(PoolThread(2, SqlClientQueued, "SQL Server", null)));

        Assert.Equal("connection-pool-exhausted", f.Id);
        Assert.Equal("Thread 2 is waiting for a free SQL Server connection.", f.Summary);
    }

    [Fact]
    public void One_nested_holder_of_a_hundred_connections_is_not_every_connection()
    {
        var f = Assert.Single(Pool(PoolThread(1, NpgsqlQueued, "PostgreSQL", new ConnectionPoolInfo(100, 100, 0), holding: 101)));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a database connection", f.Title);
        Assert.Equal("Thread 1 is waiting for a free PostgreSQL connection and the pool is full (100 of 100 connections in use, 0 idle).", f.Summary);
        Assert.Contains("Only 1 of the 100 connections in use is held on a thread's stack. The others are held by code that isn't on any stack: " +
                        "async methods, objects that keep a connection, or connections that were never disposed.", f.Details);
        Assert.StartsWith("Don't open a second connection while holding one", f.Advice);
    }

    [Fact]
    public void A_single_nested_holder_that_holds_the_whole_pool_is_a_deadlock()
    {
        var f = Assert.Single(Pool(PoolThread(1, NpgsqlQueued, "PostgreSQL", new ConnectionPoolInfo(1, 1, 0), holding: 101)));

        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Connection pool deadlock: Thread 1 is waiting for a free PostgreSQL connection and the pool is full (1 of 1 connections in use, 0 idle). " +
                     "Every connection is held by a thread that is itself waiting for another connection, so none is ever returned.", f.Summary);
    }

    [Fact]
    public void A_nested_holder_of_an_unknown_pool_is_not_called_a_deadlock()
    {
        var f = Assert.Single(Pool(PoolThread(1, SqlClientQueued, "SQL Server", null, holding: 55)));

        Assert.Equal("connection-pool-exhausted", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a database connection", f.Title);
        Assert.Equal("Thread 1 is waiting for a free SQL Server connection.", f.Summary);
        Assert.Contains("Thread 1 already holds an open connection while waiting for another one.", f.Details);
        Assert.Contains("The dump doesn't show how many connections the SQL Server pool allows or has in use, so it can't tell whether every connection is held this way. " +
                        "If Max Pool Size is no bigger than the number of threads doing this, none of them can ever continue.", f.Details);
        Assert.StartsWith("Don't open a second connection while holding one", f.Advice);
    }

    [Fact]
    public void A_thread_opening_a_connection_is_described_as_connecting_elsewhere()
    {
        Assert.Equal("opening a new database connection in Startup.CheckDatabase (Startup.cs:12)",
            Describe.Activity(PoolThread(1, NpgsqlConnecting, "PostgreSQL", null), withMethod: true));
        Assert.Equal("opening a new database connection in Startup.cs:12", Describe.Activity(PoolThread(1, SqlClientConnecting, "SQL Server", null)));
        Assert.Equal("waiting for a database connection from the pool in Startup.cs:12", Describe.Activity(PoolThread(1, NpgsqlQueued, "PostgreSQL", null)));
    }

    [Fact]
    public void Pool_waits_on_two_providers_are_reported_per_provider()
    {
        var findings = Pool(
            PoolThread(1, NpgsqlQueued, "PostgreSQL", new ConnectionPoolInfo(5, 5, 0), holding: 101),
            PoolThread(2, SqlClientQueued, "SQL Server", null), PoolThread(3, SqlClientQueued, "SQL Server", null));

        Assert.Equal(
            [
                "Thread 1 is waiting for a free PostgreSQL connection and the pool is full (5 of 5 connections in use, 0 idle).",
                "2 threads are waiting for a free SQL Server connection.",
            ], findings.Select(f => f.Summary));
        Assert.Equal([2, 3], findings[1].ThreadIds);
    }

    // ---------------------------------------------------------------- HTTP

    private static readonly TaskInfo QueuedForConnection = new()
    {
        Address = 0x40, TypeName = "System.Threading.Tasks.Task<System.Net.Http.HttpConnection>", Kind = TaskKind.Promise, Description = "a TaskCompletionSource<HttpConnection> task",
    };

    private static ThreadSnapshot HttpWaiter(HttpPoolInfo pool, bool holds = false, bool infinite = true)
    {
        var poolTask = new TaskInfo { Address = 0x30, TypeName = "Box<System.Net.Http.HttpConnectionPool+<SendWithVersionDetectionAndRetryAsync>d>", Kind = TaskKind.AsyncMethod, Description = "HttpConnectionPool.SendWithVersionDetectionAndRetryAsync", HttpPool = pool, Awaiting = QueuedForConnection };
        var client = new TaskInfo
        {
            Address = 0x20, TypeName = "Box<System.Net.Http.HttpClient+<GetStringAsyncCore>d>", Kind = TaskKind.AsyncMethod, Description = "HttpClient.GetStringAsyncCore",
            Http = new HttpCall("GET", "https://prices.example/prices/A-1", infinite ? null : TimeSpan.FromSeconds(100), infinite), Awaiting = poolTask,
        };
        var app = new TaskInfo
        {
            Address = 0x10, TypeName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox<MyApp.Prices+<GetAsync>d__2>",
            Kind = TaskKind.AsyncMethod, Description = "Prices.GetAsync", HoldsHttpResponse = holds, Awaiting = client,
        };
        return Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = app };
    }

    private static Finding Http(HttpPoolInfo pool, bool holds = false, bool infinite = true) =>
        Assert.Single(new HttpDetector().Detect(Process(HttpWaiter(pool, holds, infinite)), new HashSet<int>()));

    [Fact]
    public void Http_request_waiting_for_its_own_connection_to_be_established()
    {
        var f = Http(new HttpPoolInfo("prices.example:443", int.MaxValue, 1, 1));

        Assert.Equal("http-connect", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("HTTP call waiting for a connection to be established", f.Title);
        Assert.Equal("Thread 1 is waiting for a new connection to prices.example:443 to be established. The pool isn't at its limit, so this isn't a wait for a free connection: " +
                     "the connect itself (TCP connect, TLS handshake or proxy) hasn't finished. Start at Program.cs:5.", f.Summary);
        Assert.Equal(
            [
                "Thread 1 is blocked in .Result at Program.Main (Program.cs:5), waiting for Prices.GetAsync, which awaits `GET https://prices.example/prices/A-1`, " +
                "which is waiting for a connection to prices.example:443 to be established.",
                "The pool for prices.example:443 has no connection limit (MaxConnectionsPerServer isn't set); its only connection is still being established.",
            ], f.Details);
        Assert.Equal("Check that prices.example:443 is up and reachable from this machine (server, firewall, proxy, DNS). " +
                     "SocketsHttpHandler.ConnectTimeout is infinite by default: set it (for example to 10 seconds) so a connect that never finishes fails with an error, " +
                     "and give the HttpClient a Timeout: it has none (Timeout = Infinite), so nothing else ends this wait.", f.Advice);
        Assert.DoesNotContain(f.Details, d => d.Contains("2147483647"));
    }

    [Fact]
    public void Http_connect_wording_with_a_limit_and_without_the_pending_count()
    {
        var limited = Http(new HttpPoolInfo("api:443", 10, 3, 1), infinite: false);
        Assert.Equal("http-connect", limited.Id);
        Assert.Contains("The pool for api:443 allows 10 connections (MaxConnectionsPerServer); it has 3 connections, 1 of them still being established.", limited.Details);
        Assert.EndsWith("so a connect that never finishes fails with an error.", limited.Advice);

        var unknown = Http(new HttpPoolInfo("api:443", int.MaxValue, 1));
        Assert.Contains("The pool for api:443 has no connection limit (MaxConnectionsPerServer isn't set); it has 1 connection open.", unknown.Details);
    }

    [Fact]
    public void Http_full_pool_whose_connections_are_all_still_connecting()
    {
        var f = Http(new HttpPoolInfo("api:443", 2, 2, 2));

        Assert.Equal("http-connect", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.StartsWith("Thread 1 is waiting for a new connection to api:443 to be established. Every connection the pool has is still connecting, so this isn't a wait for a free connection", f.Summary);
        Assert.Contains("The pool for api:443 allows 2 connections (MaxConnectionsPerServer); all 2 of its connections are still being established.", f.Details);
    }

    [Fact]
    public void Http_full_pool_with_one_connection_still_connecting_keeps_the_pool_wording()
    {
        var f = Http(new HttpPoolInfo("api:80", 2, 2, 1));

        Assert.Equal("http-connection-limit", f.Id);
        Assert.Equal("Thread 1 is waiting for a free HTTP connection to api:80, and all 2 allowed connections are in use. Start at Program.cs:5.", f.Summary);
        Assert.Contains("The pool for api:80 allows 2 connections (MaxConnectionsPerServer) and 2 are open.", f.Details);
        Assert.Contains("1 of them is still being established (TCP connect or TLS handshake not finished).", f.Details);
    }

    [Fact]
    public void Http_pool_below_its_limit_with_nothing_connecting_keeps_the_pool_wording()
    {
        var f = Http(new HttpPoolInfo("api:80", 4, 2, 0));

        Assert.Equal("http-connection-limit", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is waiting for a free HTTP connection to api:80. Start at Program.cs:5.", f.Summary);
        Assert.Contains("The pool for api:80 allows 4 connections (MaxConnectionsPerServer) and 2 are open.", f.Details);
        Assert.DoesNotContain(f.Details, d => d.Contains("being established"));

        var unlimited = Http(new HttpPoolInfo("api:80", int.MaxValue, 0, 0));
        Assert.Equal("http-connection-limit", unlimited.Id);
        Assert.Contains("The pool for api:80 has no connection limit (MaxConnectionsPerServer isn't set) and 0 are open.", unlimited.Details);
        Assert.DoesNotContain(unlimited.Details, d => d.Contains("2147483647") || d.Contains("all 0"));
    }

    [Fact]
    public void Http_pool_limit_of_one_connection_is_singular()
    {
        var f = Http(new HttpPoolInfo("api:80", 1, 1, 0));

        Assert.Contains("The pool for api:80 allows 1 connection (MaxConnectionsPerServer) and 1 is open.", f.Details);
    }

    [Fact]
    public void Http_full_pool_without_a_held_response_is_not_a_deadlock()
    {
        var f = Http(new HttpPoolInfo("api:80", 2, 2, 0));

        Assert.Equal("http-connection-limit", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("HTTP calls waiting for a connection", f.Title);
        Assert.DoesNotContain(f.Details, d => d.Contains("still being established"));
    }

    // ---------------------------------------------------------------- database wording

    private static readonly StackFrameInfo[] QueryWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Net.Sockets.Socket", "Receive"),
        Lib("Npgsql.Internal.NpgsqlReadBuffer", "Ensure"),
        Lib("Npgsql.NpgsqlCommand", "ExecuteNonQuery"),
    ];

    private static ThreadSnapshot Waiting(int id, string sql, int session, string provider = "PostgreSQL", string type = "MyApp.Jobs", string file = "Jobs.cs", int line = 10) =>
        Thread(id, [.. QueryWait, App(type, "Run", file, line)]) with
        {
            Database = new DatabaseActivity { Provider = provider, Waiting = DatabaseWaitKind.Query, CommandText = sql, SessionId = session, Connections = [new DatabaseConnection(session, false)] },
        };

    private static ThreadSnapshot IdleTransaction(int id, int session, string provider = "PostgreSQL") =>
        Thread(id, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Approvals", "Wait", "Approvals.cs", 30)]) with
        {
            Database = new DatabaseActivity { Provider = provider, Connections = [new DatabaseConnection(session, true)] },
        };

    private static List<Finding> DbWait(params ThreadSnapshot[] threads) => new DatabaseWaitDetector().Detect(Process(threads), new HashSet<int>()).ToList();

    [Theory]
    [InlineData("ALTER TABLE events ADD COLUMN source text", "ALTER")]
    [InlineData("ALTER\nTABLE events\nADD COLUMN source text", "ALTER")]
    [InlineData("ALTER\tTABLE events ADD COLUMN source text", "ALTER")]
    [InlineData("\r\n  truncate\n    events", "TRUNCATE")]
    [InlineData("   ", "statement")]
    [InlineData(null, "statement")]
    public void The_verb_is_the_first_keyword_whatever_whitespace_follows(string? sql, string verb) => Assert.Equal(verb, Db.Verb(sql));

    [Fact]
    public void A_schema_change_written_over_several_lines_names_its_verb()
    {
        var f = Assert.Single(DbWait(Waiting(11, "TRUNCATE\n    events", 201), Waiting(12, "SELECT count(*) FROM events", 202)));

        Assert.Equal("Lock queue on events: schema change waiting for a table lock", f.Title);
        Assert.EndsWith("1 query that came later is queued behind the TRUNCATE, so nothing can use events until the first statement finishes.", f.Summary);
        Assert.Contains(f.Details, d => d.EndsWith("even a plain SELECT queues behind a waiting TRUNCATE, so the table is frozen for everyone."));
    }

    [Fact]
    public void A_schema_change_with_no_holder_and_no_queue_is_not_behind_a_running_query()
    {
        var f = Assert.Single(DbWait(Waiting(11, "CREATE INDEX ix_events_kind ON events (kind)", 201)));
        Assert.Equal("Schema change waiting for a lock on events", f.Title);
    }

    [Fact]
    public void Schema_change_advice_uses_the_servers_own_lock_timeout_syntax()
    {
        var sqlServer = Assert.Single(DbWait(Waiting(11, "ALTER TABLE stock ADD note nvarchar(100)", 55, "SQL Server")));
        Assert.StartsWith("Run schema changes with a short lock_timeout (SET LOCK_TIMEOUT 2000) and retry", sqlServer.Advice);
        Assert.DoesNotContain("SET lock_timeout = '2s'", sqlServer.Advice);

        var postgres = Assert.Single(DbWait(Waiting(11, "ALTER TABLE stock ADD note text", 201)));
        Assert.StartsWith("Run schema changes with a short lock_timeout (SET lock_timeout = '2s') and retry", postgres.Advice);

        var mysql = Assert.Single(DbWait(Waiting(11, "ALTER TABLE stock ADD note text", 7, "MySQL")));
        Assert.StartsWith("Run schema changes with a short lock wait timeout (SET SESSION lock_wait_timeout = 2) and retry", mysql.Advice);

        var unknown = Assert.Single(DbWait(Waiting(11, "ALTER TABLE stock ADD note text", 7, "the database")));
        Assert.StartsWith("Run schema changes with a short lock timeout and retry", unknown.Advice);
    }

    [Fact]
    public void Query_waits_on_two_servers_are_explained_per_server()
    {
        var findings = DbWait(
            Waiting(11, "UPDATE stock SET quantity = 1 WHERE sku = 'A'", 55, "SQL Server"),
            Waiting(12, "UPDATE wallets SET balance = 0 WHERE id = 1", 200),
            IdleTransaction(10, 100));

        Assert.Equal(2, findings.Count);
        var sqlServer = findings[0];
        Assert.Equal(Severity.Info, sqlServer.Severity);
        Assert.StartsWith("Thread 11 is waiting for SQL Server to answer.", sqlServer.Summary);
        Assert.Contains("To confirm on the server: SELECT session_id, blocking_session_id, wait_type, status FROM sys.dm_exec_requests WHERE session_id IN (55);", sqlServer.Details);
        Assert.Equal([11], sqlServer.ThreadIds);

        var postgres = findings[1];
        Assert.Equal("Database query blocked by an idle transaction", postgres.Title);
        Assert.StartsWith("Thread 12 is waiting for PostgreSQL, while Thread 10 keeps a transaction open and waits for something else in Approvals.cs:30.", postgres.Summary);
        Assert.Contains("To confirm on the server: SELECT pid, pg_blocking_pids(pid), state, wait_event_type, query FROM pg_stat_activity WHERE pid IN (200, 100);", postgres.Details);
    }

    [Fact]
    public void A_transaction_that_is_reading_rows_is_not_called_idle()
    {
        var reader = Thread(10, [Lib("Npgsql.NpgsqlDataReader", "Read"), App("MyApp.Report", "Run", "Report.cs", 20)]) with
        {
            Database = new DatabaseActivity
            {
                Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ReadingRows, CommandText = "SELECT * FROM orders", RowsRead = 900,
                Connections = [new DatabaseConnection(100, true)],
            },
        };
        var f = Assert.Single(DbWait(Waiting(11, "UPDATE orders SET status = 'paid' WHERE id = 1", 200), reader));

        Assert.Equal("Database query blocked by an open transaction", f.Title);
        Assert.StartsWith("Thread 11 is waiting for PostgreSQL, while Thread 10 keeps a transaction open and reads the rows of another query in Report.cs:20.", f.Summary);
        Assert.Contains("Thread 10 has an open transaction (session 100) and is reading the rows of `SELECT * FROM orders` from PostgreSQL (900 rows so far) in Report.Run (Report.cs:20). " +
                        "Every row that transaction has changed stays locked until it commits or rolls back.", f.Details);
        Assert.DoesNotContain(f.Details, d => d.Contains("isn't talking to the database"));
    }

    [Fact]
    public void A_transaction_that_is_running_code_is_not_waiting_for_something_else()
    {
        var running = Thread(10, [App("MyApp.Orders", "ComputeTotals", "Orders.cs", 15)]) with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(100, true)] },
        };
        var f = Assert.Single(DbWait(Waiting(11, "UPDATE orders SET status = 'paid' WHERE id = 1", 200), running));

        Assert.StartsWith("Thread 11 is waiting for PostgreSQL, while Thread 10 keeps a transaction open and runs code in Orders.cs:15.", f.Summary);
        Assert.Contains("Thread 10 has an open transaction (session 100) and is running Orders.ComputeTotals (Orders.cs:15). " +
                        "Every row that transaction has changed stays locked until it commits or rolls back.", f.Details);
    }

    [Fact]
    public void An_idle_transaction_is_preferred_as_the_blocker_and_keeps_its_wording()
    {
        var running = Thread(9, [App("MyApp.Orders", "ComputeTotals", "Orders.cs", 15)]) with
        {
            Database = new DatabaseActivity { Provider = "PostgreSQL", Connections = [new DatabaseConnection(99, true)] },
        };
        var f = Assert.Single(DbWait(Waiting(11, "UPDATE wallets SET balance = 0 WHERE id = 1", 200), running, IdleTransaction(10, 100)));

        Assert.StartsWith("Thread 11 is waiting for PostgreSQL, while Thread 10 keeps a transaction open and waits for something else in Approvals.cs:30.", f.Summary);
        Assert.Contains("Thread 10 has an open transaction (session 100) but isn't talking to the database: it is sleeping (Thread.Sleep) in Approvals.Wait (Approvals.cs:30). " +
                        "Every row that transaction has changed stays locked until it commits or rolls back.", f.Details);
    }
}
