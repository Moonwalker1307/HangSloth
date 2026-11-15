using HangSloth.Model;

namespace HangSloth.Analysis;

internal static class Db
{
    public static bool WaitingForQuery(ThreadSnapshot t) => t.Database?.Waiting == DatabaseWaitKind.Query;

    public static bool InTransaction(ThreadSnapshot t) => t.Database?.Connections.Any(c => c.InTransaction) == true;

    public static int? TransactionSession(ThreadSnapshot t) => t.Database?.Connections.FirstOrDefault(c => c.InTransaction)?.SessionId;

    public static string Session(int? id) => id is null ? "" : $" (session {id})";

    /// "is waiting in Refund (X.cs:22) for PostgreSQL to answer `UPDATE ...`", including the async case.
    public static string WaitPhrase(ThreadSnapshot t)
    {
        var db = t.Database!;
        var answer = $"for {db.Provider} to answer {Describe.Sql(db.CommandText)}{Session(db.SessionId)}";
        if (t.BlockedOnTask is { } task)
        {
            var mine = Describe.Chain(task).LastOrDefault(Describe.IsAppAsyncMethod);
            var via = mine is null ? "" : $" on {Describe.Task(mine)}";
            return $"is blocked in {FrameClassifier.DescribeTaskWait(t.Frames)} at {Describe.Spot(FrameClassifier.BestUserFrame(t.Frames))}{via}, waiting {answer}";
        }
        return $"is waiting in {Describe.Where(FrameClassifier.BestUserFrame(t.Frames))} {answer}";
    }

    /// A SELECT without FOR UPDATE/SHARE: it never waits for a row lock, only for a table lock.
    public static bool IsPlainSelect(string? sql) =>
        sql is not null && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
        && !sql.Contains(" FOR UPDATE", StringComparison.OrdinalIgnoreCase) && !sql.Contains(" FOR SHARE", StringComparison.OrdinalIgnoreCase)
        && !sql.Contains(" FOR NO KEY UPDATE", StringComparison.OrdinalIgnoreCase) && !sql.Contains(" FOR KEY SHARE", StringComparison.OrdinalIgnoreCase)
        && !IsAppLockSql(sql) && !sql.Contains("pg_sleep", StringComparison.OrdinalIgnoreCase);

    /// Application-level locks taken through SQL: PostgreSQL advisory locks, SQL Server sp_getapplock, MySQL GET_LOCK.
    public static bool IsAppLockSql(string? sql) =>
        sql is not null && (sql.Contains("pg_advisory_lock", StringComparison.OrdinalIgnoreCase)
                            || sql.Contains("pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase)
                            || sql.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase)
                            || sql.Contains("GET_LOCK(", StringComparison.OrdinalIgnoreCase));

    public static string? ConfirmAppLock(string provider, IEnumerable<int?> sessions) => provider switch
    {
        "PostgreSQL" => "To see who holds it: SELECT l.pid, l.granted, a.state, a.query FROM pg_locks l JOIN pg_stat_activity a USING (pid) WHERE l.locktype = 'advisory';",
        _ => ConfirmOnServer(provider, sessions),
    };

    /// PostgreSQL can show who blocks whom; tell the reader how to confirm the theory on the server.
    public static string? ConfirmOnServer(string provider, IEnumerable<int?> sessions)
    {
        var ids = sessions.OfType<int>().Distinct().ToList();
        if (ids.Count == 0) return null;
        return provider switch
        {
            "PostgreSQL" => $"To confirm on the server: SELECT pid, pg_blocking_pids(pid), state, wait_event_type, query FROM pg_stat_activity WHERE pid IN ({string.Join(", ", ids)});",
            "SQL Server" => $"To confirm on the server: SELECT session_id, blocking_session_id, wait_type, status FROM sys.dm_exec_requests WHERE session_id IN ({string.Join(", ", ids)});",
            _ => null,
        };
    }
}

/// A deadlock that spans the app and the database: thread A holds an app lock while it waits for the
/// database, and thread B, inside an open transaction, waits for that app lock. If A's query needs a row
/// B's transaction has locked, neither side can ever finish, and neither .NET nor the database sees a cycle.
public sealed class DatabaseDeadlockDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var a in snapshot.Threads.Where(t => Db.WaitingForQuery(t) && !claimed.Contains(t.ManagedId)))
        {
            foreach (var l in snapshot.Locks.Where(l => l.OwnerThreadId == a.ManagedId))
            {
                foreach (var b in l.WaiterThreadIds.Select(snapshot.FindThread).OfType<ThreadSnapshot>())
                {
                    if (claimed.Contains(b.ManagedId) || !Db.InTransaction(b)) continue;
                    var bSession = Db.TransactionSession(b);
                    if (bSession is not null && bSession == a.Database!.SessionId) continue;
                    yield return Explain(a, b, l);
                }
            }
        }
    }

    private static Finding Explain(ThreadSnapshot a, ThreadSnapshot b, MonitorLock l)
    {
        var provider = a.Database!.Provider;
        var bSession = Db.TransactionSession(b);
        var site = FrameClassifier.BestUserFrame(b.Frames);
        var details = new List<string>
        {
            $"{a.Label} holds {Describe.Held(l)} and {Db.WaitPhrase(a)}.",
            $"{b.Label} is waiting in {Describe.Where(site)} for {Describe.Wanted(l)}, while its own transaction{Db.Session(bSession)} is still open, so every row it has changed stays locked.",
        };
        if (Db.ConfirmOnServer(provider, [a.Database.SessionId, bSession]) is { } confirm) details.Add(confirm);

        return new Finding
        {
            Id = "database-deadlock",
            Severity = Severity.Critical,
            Title = "Deadlock between an app lock and a database lock",
            Summary = $"{a.Label} holds {Describe.Held(l)} while waiting for {provider}, and {b.Label} is waiting for that lock inside an open transaction. " +
                      $"If that query needs a row the transaction has locked, neither thread can ever continue, and neither .NET nor {provider} can see the cycle." +
                      (site?.Source is null ? "" : $" Start at {site.Source}."),
            Details = details,
            Advice = $"Don't take app locks inside a database transaction, and don't hold an app lock while waiting on the database. Here, take {l.DisplayName} after the transaction commits, " +
                     "or do the database work outside the lock. A command timeout (or lock_timeout in PostgreSQL) turns the hang into an error.",
            ThreadIds = [a.ManagedId, b.ManagedId],
            Location = site?.Source,
        };
    }
}

/// Threads waiting for a free connection from a full pool, typically because each request holds one
/// connection while it opens a second (nested helpers that open their own connection).
public sealed class ConnectionPoolDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var waiters = snapshot.Threads
            .Where(t => t.Database?.Waiting == DatabaseWaitKind.ConnectionPool && !claimed.Contains(t.ManagedId))
            .ToList();
        if (waiters.Count == 0)
        {
            foreach (var f in AsyncPoolFindings(snapshot, claimed)) yield return f;
            yield break;
        }

        var provider = waiters[0].Database!.Provider;
        var pool = waiters.Select(t => t.Database!.Pool).FirstOrDefault(p => p is not null);
        var holding = snapshot.Threads.Where(t => t.Database?.Connections.Count > 0).ToList();
        var holdAndWait = holding.Where(waiters.Contains).ToList();
        var full = pool is null || pool.OpenConnections >= pool.MaxSize;
        var deadlock = holdAndWait.Count > 0 && full && holding.All(waiters.Contains);

        var poolText = pool is null ? "" : $" ({pool.OpenConnections} of {pool.MaxSize} connections in use, {pool.IdleConnections} idle)";
        var who = waiters.Count == 1 ? $"{waiters[0].Label} is" : $"{waiters.Count} threads are";
        var summary = deadlock
            ? $"Connection pool deadlock: {who} waiting for a free {provider} connection and the pool is full{poolText}. " +
              $"Every connection is held by {(holdAndWait.Count == 1 ? "a thread that is" : "threads that are")} themselves waiting for another connection, so none is ever returned."
            : $"{who} waiting for a free {provider} connection{(full ? $" and the pool is full{poolText}" : poolText)}.";

        var details = waiters
            .GroupBy(t => Describe.WhereWithCaller(t.Frames))
            .Select(g => $"Waiting for a connection in {g.Key}" + (g.Count() > 1 ? $" ({g.Count()} threads)." : $" ({g.First().Label})."))
            .ToList();
        if (holdAndWait.Count > 0)
            details.Add($"{Describe.JoinAnd(holdAndWait.Select(t => t.Label))} {(holdAndWait.Count == 1 ? "already holds" : "each already hold")} an open connection while waiting for another one.");
        foreach (var h in holding.Except(waiters).Take(5))
            details.Add($"{h.Label} holds a connection and is {Describe.Activity(h, withMethod: true)}.");

        var nested = holdAndWait.FirstOrDefault() ?? waiters[0];
        var site = FrameClassifier.BestUserFrame(nested.Frames);
        yield return new Finding
        {
            Id = "connection-pool-exhausted",
            Severity = deadlock ? Severity.Critical : Severity.Warning,
            Title = deadlock ? "Deadlock: connection pool exhausted by nested connections" : "Waiting for a database connection",
            Summary = summary,
            Details = details,
            Advice = holdAndWait.Count > 0
                ? $"Don't open a second connection while holding one: in {Describe.WhereWithCaller(nested.Frames)}, reuse the caller's open connection (and transaction) instead, " +
                  "or finish with the first connection before opening the next. A bigger pool only raises the number of concurrent requests it takes to hang."
                : "Find what the connection holders are waiting for and keep connections open only for the database work itself.",
            ThreadIds = waiters.Concat(holding).Distinct().Select(t => t.ManagedId).ToList(),
            Location = site?.Source,
        };
    }

    /// The async version: no thread waits for the pool, but paused async methods do, and the ones holding
    /// every connection are among them.
    private static IEnumerable<Finding> AsyncPoolFindings(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var wait in snapshot.AsyncPoolWaits)
        {
            var holdAndWait = wait.Methods.Where(m => m.HoldsConnection && m.WaitingForConnection).ToList();
            var holders = wait.Methods.Where(m => m.HoldsConnection).ToList();
            var waiting = wait.Methods.Where(m => m.WaitingForConnection && !m.HoldsConnection).ToList();
            var deadlock = holdAndWait.Count > 0 && holdAndWait.Count == holders.Count && holders.Count >= wait.Pool.MaxSize;
            var pool = wait.Pool;
            var poolText = $"{pool.OpenConnections} of {pool.MaxSize} connections in use, {pool.IdleConnections} idle";

            // Threads synchronously waiting on that work (Task.WaitAll, .Result) are what the user sees hanging.
            var blocked = snapshot.Threads
                .Where(t => !claimed.Contains(t.ManagedId) && FrameClassifier.Classify(t.Frames) == BlockingKind.TaskWait && FrameClassifier.FirstUserFrame(t.Frames) is not null)
                .ToList();

            var details = Group(holdAndWait)
                .Select(g => $"{Count(g.Count(), "call")} of {g.Key.Method} hold{(g.Count() == 1 ? "s" : "")} an open connection and {(g.Count() == 1 ? "is" : "are")} paused at {At(g.Key.PausedAt)}, waiting for another connection from the same pool.")
                .ToList();
            details.AddRange(Group(holders.Except(holdAndWait))
                .Select(g => $"{Count(g.Count(), "call")} of {g.Key.Method} hold{(g.Count() == 1 ? "s" : "")} an open connection, paused at {At(g.Key.PausedAt)}."));
            details.AddRange(Group(waiting)
                .Select(g => $"{Count(g.Count(), "call")} of {g.Key.Method} {(g.Count() == 1 ? "is" : "are")} waiting for a connection at {At(g.Key.PausedAt)}."));
            foreach (var t in blocked.Take(3))
                details.Add($"{t.Label} is blocked in {FrameClassifier.DescribeTaskWait(t.Frames)} at {Describe.Where(FrameClassifier.BestUserFrame(t.Frames))}, waiting for this work.");

            var nested = holdAndWait.FirstOrDefault() ?? holders.FirstOrDefault() ?? waiting.FirstOrDefault();
            yield return new Finding
            {
                Id = "connection-pool-exhausted",
                Severity = deadlock ? Severity.Critical : Severity.Warning,
                Title = deadlock ? "Deadlock: connection pool exhausted by nested async connections" : "Async code waiting for a database connection",
                Summary = deadlock
                    ? $"Connection pool deadlock: the {wait.Provider} pool is full ({poolText}) and every connection is held by an async method that is itself awaiting another connection from the same pool. " +
                      "No thread is blocked on the database, so stacks don't show it: the waits are paused async methods."
                    : $"Async code is waiting for a free {wait.Provider} connection and the pool is full ({poolText}).",
                Details = details,
                Advice = holdAndWait.Count > 0
                    ? $"Don't open a second connection while holding one: {(holdAndWait[0].OpeningIn is { } opener ? $"in {opener}, use the connection {holdAndWait[0].Method} already has open (pass it in, with its transaction) instead of opening a new one, " : "pass the caller's open connection (and transaction) down instead of opening a new one, ")}" +
                      "or finish with the first connection before opening the next. A bigger pool only raises the number of concurrent requests it takes to hang, and a pool Timeout turns the hang into errors."
                    : "Find what the async methods holding connections are waiting for, and keep connections open only for the database work itself.",
                ThreadIds = blocked.Select(t => t.ManagedId).ToList(),
                Location = nested?.PausedAt,
            };
        }
    }

    private static IEnumerable<IGrouping<(string Method, SourceLocation? PausedAt), AsyncConnectionUser>> Group(IEnumerable<AsyncConnectionUser> users) =>
        users.GroupBy(u => (u.Method, u.PausedAt));

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string At(SourceLocation? location) => location?.ToString() ?? "an await";
}

/// Threads waiting for the database to answer. Often just a slow query; but if another thread is holding
/// an open transaction while it does something else, or several waiting threads are each in a transaction,
/// say what is probably blocking them.
public sealed class DatabaseWaitDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var waiting = snapshot.Threads.Where(t => Db.WaitingForQuery(t) && !claimed.Contains(t.ManagedId)).ToList();
        if (waiting.Count == 0) yield break;

        var provider = waiting[0].Database!.Provider;
        var idleTx = snapshot.Threads.Where(t => !Db.WaitingForQuery(t) && Db.InTransaction(t)).ToList();
        var inTx = waiting.Where(Db.InTransaction).ToList();
        var site = FrameClassifier.BestUserFrame(waiting[0].Frames);
        var details = waiting.Select(t => $"{t.Label} {Db.WaitPhrase(t)}" + (Db.InTransaction(t) ? ", inside an open transaction." : ".")).ToList();
        var sessions = waiting.Select(t => t.Database!.SessionId).Concat(idleTx.Select(Db.TransactionSession)).Concat(inTx.Select(Db.TransactionSession));

        if (idleTx.Count > 0)
        {
            foreach (var t in idleTx)
                details.Add($"{t.Label} has an open transaction{Db.Session(Db.TransactionSession(t))} but isn't talking to the database: it is {Describe.Activity(t, withMethod: true)}. " +
                            "Every row that transaction has changed stays locked until it commits or rolls back.");
            if (Db.ConfirmOnServer(provider, sessions) is { } confirm) details.Add(confirm);
            var blocker = idleTx[0];
            // In PostgreSQL (and with snapshot isolation elsewhere) a plain read never waits for a row lock;
            // if only reads are stuck, the transaction holds a table lock, typically from DDL.
            var tableLock = waiting.All(t => Db.IsPlainSelect(t.Database!.CommandText));
            yield return new Finding
            {
                Id = "database-wait",
                Severity = Severity.Warning,
                Title = tableLock ? "Database reads blocked by a table lock in an idle transaction" : "Database query blocked by an idle transaction",
                Summary = $"{(waiting.Count == 1 ? $"{waiting[0].Label} is" : $"{waiting.Count} threads are")} waiting for {provider}, while {blocker.Label} keeps a transaction open and waits for something else " +
                          $"in {Describe.Spot(FrameClassifier.BestUserFrame(blocker.Frames))}. " +
                          (tableLock
                              ? "A plain SELECT doesn't wait for row locks, so that transaction most likely holds a lock on the whole table: ALTER TABLE, DROP, TRUNCATE, CREATE INDEX (without CONCURRENTLY) or LOCK TABLE."
                              : "The query is most likely waiting for a row lock held by that transaction."),
                Details = details,
                Advice = tableLock
                    ? "Keep schema changes in short transactions that commit right away, and run them with a lock_timeout (SET lock_timeout = '5s') so a migration gives up instead of freezing the table. " +
                      "Don't wait for anything else between the DDL and the commit."
                    : "Don't wait for anything slow (user input, HTTP calls, other threads, sleeps) inside a database transaction: do that first, then open the transaction, write, and commit. " +
                      "A lock_timeout or command timeout makes the blocked query fail instead of hanging.",
                ThreadIds = [.. waiting.Select(t => t.ManagedId), .. idleTx.Select(t => t.ManagedId)],
                Location = FrameClassifier.BestUserFrame(blocker.Frames)?.Source ?? site?.Source,
            };
            yield break;
        }

        if (inTx.Count >= 2)
        {
            if (Db.ConfirmOnServer(provider, sessions) is { } confirm) details.Add(confirm);
            details.Add($"{provider} detects a deadlock inside the database after a timeout and aborts one transaction (PostgreSQL error 40P01, SQL Server error 1205). " +
                        "If the app retries the same way, the transactions keep colliding: it looks like a hang, with a deadlock error in the log every second or so.");
            yield return new Finding
            {
                Id = "database-wait",
                Severity = Severity.Warning,
                Title = "Possible database deadlock",
                Summary = $"{Describe.JoinAnd(inTx.Select(t => t.Label))} are {(inTx.Count == 2 ? "both" : "all")} waiting for {provider}, each inside its own open transaction. " +
                          $"If each has locked rows {(inTx.Count == 2 ? "the other" : "another one")} needs, for example by updating the same rows in a different order, this is a database deadlock.",
                Details = details,
                Advice = "Touch rows in one consistent order in every transaction (for example, update accounts sorted by id), keep transactions short, " +
                         "and on a deadlock error retry with a random backoff instead of immediately.",
                ThreadIds = waiting.Select(t => t.ManagedId).ToList(),
                Location = site?.Source,
            };
            yield break;
        }

        var advisory = waiting.Where(t => Db.IsAppLockSql(t.Database!.CommandText)).ToList();
        if (advisory.Count > 0)
        {
            // Session-level advisory locks belong to a connection, not a transaction: look for connections
            // that are open but idle, above all ones kept in a field.
            var holders = snapshot.Threads
                .Where(t => !Db.WaitingForQuery(t) && t.Database?.Connections.Count > 0)
                .OrderByDescending(t => t.Database!.Connections.Any(c => c.Field is not null))
                .ThenByDescending(RunsOwnerCode)
                .DistinctBy(t => Held(t).SessionId ?? -t.ManagedId) // one thread per session: a kept connection is reachable from several
                .ToList();
            foreach (var h in holders)
            {
                var conn = Held(h);
                details.Add($"{h.Label} keeps a connection open{Db.Session(conn.SessionId)}{(conn.Field is null ? "" : $" in {conn.Field}")} while it is {Describe.Activity(h, withMethod: true)}. " +
                            "If that session took the lock, it holds it until it unlocks or the connection closes.");
            }
            var lockSessions = sessions.Concat(holders.SelectMany(h => h.Database!.Connections.Select(c => c.SessionId)));
            if (Db.ConfirmAppLock(provider, lockSessions) is { } confirm) details.Add(confirm);
            var first = holders.FirstOrDefault();
            yield return new Finding
            {
                Id = "database-wait",
                Severity = Severity.Warning,
                Title = "Waiting for a database advisory lock",
                Summary = $"{(advisory.Count == 1 ? $"{advisory[0].Label} is" : $"{advisory.Count} threads are")} waiting to take an application lock in {provider} ({Describe.Sql(advisory[0].Database!.CommandText)}). " +
                          "That lock belongs to another session and isn't tied to a transaction, so nothing releases it until that session unlocks or disconnects." +
                          (first is null ? "" : $" {first.Label} has a connection open and isn't using it, which makes it the likely holder."),
                Details = details,
                Advice = "Release advisory locks in a finally block (pg_advisory_unlock, sp_releaseapplock, RELEASE_LOCK), or use the transaction-scoped kind (pg_advisory_xact_lock) so commit or rollback releases it. " +
                         "Don't keep the connection that holds one open \"for later\". Use the try variant (pg_try_advisory_lock) or a lock_timeout so a second node gives up instead of hanging.",
                ThreadIds = [.. waiting.Select(t => t.ManagedId), .. holders.Take(1).Select(t => t.ManagedId)],
                Location = first is null ? site?.Source : FrameClassifier.BestUserFrame(first.Frames)?.Source ?? site?.Source,
            };
            yield break;
        }

        yield return new Finding
        {
            Id = "database-wait",
            Severity = Severity.Info,
            Title = "Waiting for the database",
            Summary = $"{(waiting.Count == 1 ? $"{waiting[0].Label} is" : $"{waiting.Count} threads are")} waiting for {provider} to answer. " +
                      "One dump can't tell a slow query from one blocked inside the database; the server's view of the session(s) can.",
            Details = Db.ConfirmOnServer(provider, sessions) is { } c ? [.. details, c] : details,
            Advice = "If this persists, check the server for blocking sessions and long-running transactions, and give commands a timeout.",
            ThreadIds = waiting.Select(t => t.ManagedId).ToList(),
            Location = site?.Source,
        };
    }

    private static DatabaseConnection Held(ThreadSnapshot t) =>
        t.Database!.Connections.FirstOrDefault(c => c.Field is not null) ?? t.Database.Connections[0];

    /// Is the thread running code of the class whose field keeps the connection ("NightlyJob._leaderConnection")?
    private static bool RunsOwnerCode(ThreadSnapshot t) =>
        Held(t).Field is { } f && f.LastIndexOf('.') is var i and > 0
        && FrameClassifier.BestUserFrame(t.Frames) is { } frame && Names.ShortType(frame.TypeName) == f[..i];
}
