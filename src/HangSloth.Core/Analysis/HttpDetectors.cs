using HangSloth.Model;

namespace HangSloth.Analysis;

/// Threads blocked on async HTTP calls: waiting for a connection from a full per-server pool, or for
/// a server's response. The stack only shows .Result or .Wait(); the await chain shows the request.
public sealed class HttpDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var poolWaits = new List<(ThreadSnapshot Thread, List<TaskInfo> Chain, HttpPoolInfo Pool)>();
        var responseWaits = new List<(ThreadSnapshot Thread, List<TaskInfo> Chain, HttpCall Call)>();
        foreach (var t in snapshot.Threads)
        {
            if (claimed.Contains(t.ManagedId) || t.BlockedOnTask is not { State: TaskState.Pending } waited) continue;
            var chain = Describe.Chain(waited).ToList();
            if (chain.FirstOrDefault(c => c.HttpPool is not null)?.HttpPool is { } pool && WaitingForConnection(chain))
                poolWaits.Add((t, chain, pool));
            else if (chain.FirstOrDefault(c => c.Http is not null)?.Http is { } call && chain[^1].TypeName.Contains("System.Net.Http.HttpConnection+", StringComparison.Ordinal))
                responseWaits.Add((t, chain, call));
        }

        foreach (var group in poolWaits.GroupBy(w => w.Pool.Server))
        {
            var pool = group.First().Pool;
            var full = pool.OpenConnections >= pool.MaxConnections;
            // The pool queues a request the same way while a new connection is being established for it. Below
            // the limit (unless the dump shows none is being established), or with every connection still being
            // established, nothing holds the connection it waits for.
            if (!full && pool.PendingConnections is not 0 || pool.PendingConnections is { } pending && pending > 0 && pending >= pool.OpenConnections)
            {
                yield return Connecting(group.ToList(), pool, full);
                continue;
            }
            var holders = group.SelectMany(w => w.Chain.Where(c => c.HoldsHttpResponse)).Select(c => c.Description).Distinct().ToList();
            var nested = full && holders.Count > 0;
            var first = group.First();
            var site = FrameClassifier.BestUserFrame(first.Thread.Frames);
            var details = SameWait.Lines(group, w => w.Thread, (w, who) => $"{who.Who} {who.Is} blocked in {FrameClassifier.DescribeTaskWait(w.Thread.Frames)} at {Describe.Where(FrameClassifier.BestUserFrame(w.Thread.Frames))}, " +
                                                                           $"waiting for {AppPath(w.Chain)}, which is queued for a connection to {pool.Server}.");
            details.Add($"The pool for {pool.Server} " +
                        (pool.MaxConnections == int.MaxValue ? "has no connection limit (MaxConnectionsPerServer isn't set)" : $"allows {Describe.Plural(pool.MaxConnections, "connection")} (MaxConnectionsPerServer)") +
                        $" and {pool.OpenConnections} {(pool.OpenConnections == 1 ? "is" : "are")} open.");
            if (pool.PendingConnections is > 0 and var stillConnecting)
                details.Add($"{(stillConnecting == 1 ? "1 of them is" : $"{stillConnecting} of them are")} still being established (TCP connect or TLS handshake not finished).");
            if (nested)
                details.Add($"{Describe.JoinAnd(holders)} {(holders.Count == 1 ? "keeps" : "keep")} an earlier HttpResponseMessage in a local while making this request. " +
                            "A response holds its connection until its body has been read or it is disposed, so each such call needs two connections at once.");

            yield return new Finding
            {
                Id = "http-connection-limit",
                Severity = nested ? Severity.Critical : Severity.Warning,
                Title = nested ? "Deadlock: HTTP calls holding a connection while waiting for another" : "HTTP calls waiting for a connection",
                Summary = $"{Describe.Labels(group.Select(w => w.Thread))} {(group.Count() == 1 ? "is" : "are")} waiting for a free HTTP connection to {pool.Server}" +
                          (full ? pool.MaxConnections == 1 ? ", and the only allowed connection is in use." : $", and all {pool.MaxConnections} allowed connections are in use." : ".") +
                          (nested ? " The connections are held by responses that the waiting calls themselves haven't read or disposed, so none is ever returned." : "") +
                          (site?.Source is null ? "" : $" Start at {site.Source}."),
                Details = details,
                Advice = nested
                    ? "Dispose each HttpResponseMessage (using var response = ...) or read its body before making the next request to the same server. " +
                      "ResponseHeadersRead keeps the connection busy until the body is consumed. Raising MaxConnectionsPerServer only raises the number of concurrent calls it takes to hang."
                    : "Find what the open connections are doing (slow responses, unread bodies), dispose responses promptly, and give the HttpClient a timeout.",
                ThreadIds = group.Select(w => w.Thread.ManagedId).ToList(),
                Location = site?.Source,
            };
        }

        // Threads blocked at the same line on requests to the same server read as one finding that says how many they are.
        foreach (var group in responseWaits.GroupBy(ResponseWaitKey))
        {
            var waits = group.ToList();
            var (thread, chain, call) = waits[0];
            var site = FrameClassifier.BestUserFrame(thread.Frames);
            var timeout = call.InfiniteTimeout ? "The HttpClient has no timeout (Timeout = Infinite), so it will wait forever."
                : call.Timeout is { } t ? $"The HttpClient's timeout is {Format(t)}." : null;
            var uris = waits.Select(w => w.Call.Uri).Distinct().ToList();
            var request = uris.Count == 1 ? $"`{call.Method} {call.Uri}`" : $"`{call.Method}` requests to {Authority(call.Uri)} (for example `{call.Method} {call.Uri}`)";
            var one = waits.Count == 1;
            yield return new Finding
            {
                Id = "http-wait",
                Severity = call.InfiniteTimeout || call.Timeout > TimeSpan.FromMinutes(5) ? Severity.Warning : Severity.Info,
                Title = "Waiting for an HTTP response",
                Summary = $"{Describe.Many(waits.Select(w => w.Thread).ToList())} {(one ? "is" : "are")} blocked in {FrameClassifier.DescribeTaskWait(thread.Frames)} at {Describe.Spot(site)}, waiting for {request}: " +
                          (uris.Count == 1 && one ? "the request was sent and the server hasn't answered." : "the requests were sent and the server hasn't answered.") + (timeout is null ? "" : $" {timeout}"),
                Details = chain.Any(Describe.IsAppAsyncMethod) ? [$"Async path: {AppPath(chain)}, which is reading the response from the server."] : [],
                Advice = (call.InfiniteTimeout ? "Give the HttpClient a timeout (or pass a CancellationToken with one) so a server that never answers fails the call instead of hanging it. " : "") +
                         $"Await the call instead of {FrameClassifier.DescribeTaskWait(thread.Frames)} so a slow server doesn't hold a thread.",
                ThreadIds = waits.Select(w => w.Thread.ManagedId).ToList(),
                Location = site?.Source,
            };
        }
    }

    /// The same wait: the same call at the same line, through the same async path, for the same kind of request
    /// to the same server with the same timeout.
    private static string ResponseWaitKey((ThreadSnapshot Thread, List<TaskInfo> Chain, HttpCall Call) w) =>
        string.Join("\u0002", FrameClassifier.DescribeTaskWait(w.Thread.Frames), Describe.Spot(FrameClassifier.BestUserFrame(w.Thread.Frames)),
            FrameClassifier.BestUserFrame(w.Thread.Frames)?.FullName ?? "", string.Join(", ", w.Chain.Where(Describe.IsAppAsyncMethod).Select(Describe.Task)),
            w.Call.Method, Authority(w.Call.Uri), w.Call.Timeout?.ToString() ?? "", w.Call.InfiniteTimeout.ToString());

    /// "http://127.0.0.1:23117" for "http://127.0.0.1:23117/quote/9".
    private static string Authority(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : uri;

    /// Requests queued in a pool that isn't at its limit: each waits for a connection that is still being
    /// established (TCP connect, TLS handshake, proxy), not for one to be returned.
    private static Finding Connecting(List<(ThreadSnapshot Thread, List<TaskInfo> Chain, HttpPoolInfo Pool)> group, HttpPoolInfo pool, bool full)
    {
        var first = group[0];
        var site = FrameClassifier.BestUserFrame(first.Thread.Frames);
        var one = group.Count == 1;
        var details = SameWait.Lines(group, w => w.Thread, (w, who) => $"{who.Who} {who.Is} blocked in {FrameClassifier.DescribeTaskWait(w.Thread.Frames)} at {Describe.Where(FrameClassifier.BestUserFrame(w.Thread.Frames))}, " +
                                                                       $"waiting for {AppPath(w.Chain)}, which is waiting for a connection to {pool.Server} to be established.");
        var limit = pool.MaxConnections == int.MaxValue ? "has no connection limit (MaxConnectionsPerServer isn't set)" : $"allows {Describe.Plural(pool.MaxConnections, "connection")} (MaxConnectionsPerServer)";
        var connections = pool.PendingConnections is not { } p ? $"it has {Describe.Plural(pool.OpenConnections, "connection")} open"
            : p == pool.OpenConnections ? (p == 1 ? "its only connection is still being established" : $"all {p} of its connections are still being established")
            : $"it has {Describe.Plural(pool.OpenConnections, "connection")}, {p} of them still being established";
        details.Add($"The pool for {pool.Server} {limit}; {connections}.");
        var noTimeout = group.Any(w => w.Chain.Any(c => c.Http is { InfiniteTimeout: true }));

        return new Finding
        {
            Id = "http-connect",
            Severity = Severity.Warning,
            Title = one ? "HTTP call waiting for a connection to be established" : "HTTP calls waiting for a connection to be established",
            Summary = $"{Describe.Labels(group.Select(w => w.Thread))} {(one ? "is" : "are")} waiting for a new connection to {pool.Server} to be established. " +
                      (full ? "Every connection the pool has is still connecting" : "The pool isn't at its limit") +
                      ", so this isn't a wait for a free connection: the connect itself (TCP connect, TLS handshake or proxy) hasn't finished." +
                      (site?.Source is null ? "" : $" Start at {site.Source}."),
            Details = details,
            Advice = $"Check that {pool.Server} is up and reachable from this machine (server, firewall, proxy, DNS). " +
                     "SocketsHttpHandler.ConnectTimeout is infinite by default: set it (for example to 10 seconds) so a connect that never finishes fails with an error" +
                     (noTimeout ? ", and give the HttpClient a Timeout: it has none (Timeout = Infinite), so nothing else ends this wait." : "."),
            ThreadIds = group.Select(w => w.Thread.ManagedId).ToList(),
            Location = site?.Source,
        };
    }

    /// The pool queues the request as a TaskCompletionSource&lt;HttpConnection&gt; until a connection is free.
    private static bool WaitingForConnection(List<TaskInfo> chain) =>
        chain[^1].TypeName.Contains("System.Net.Http.HttpConnection", StringComparison.Ordinal) && chain[^1].Kind == TaskKind.Promise;

    private static string AppPath(List<TaskInfo> chain)
    {
        var app = chain.Where(Describe.IsAppAsyncMethod).Select(Describe.Task).ToList();
        var http = chain.FirstOrDefault(c => c.Http is not null);
        var request = http?.Http is { } call ? $"`{call.Method} {call.Uri}`" : "an HTTP request";
        return app.Count == 0 ? request : $"{string.Join(", which awaits ", app)}, which awaits {request}";
    }

    /// "100 seconds" for HttpClient's default, minutes from two minutes on.
    private static string Format(TimeSpan t) =>
        t.TotalSeconds >= 120 ? $"{Describe.Number(t.TotalMinutes)} minutes" : $"{Describe.Number(t.TotalSeconds)} seconds";
}

/// A parent waiting for a child process to exit while the child's redirected output is unread: the
/// child fills the pipe and blocks writing, so it never exits.
public sealed class ProcessPipeDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var t in snapshot.Threads)
        {
            if (claimed.Contains(t.ManagedId) || t.WaitingForProcess is not { UnreadOutput.Count: > 0 } wait) continue;
            var site = FrameClassifier.BestUserFrame(t.Frames);
            var streams = Describe.JoinAnd(wait.UnreadOutput);
            yield return new Finding
            {
                Id = "process-pipe-deadlock",
                Severity = Severity.Warning,
                Title = "Waiting for a child process whose output nobody reads",
                Summary = $"{t.Label} is waiting in {Describe.Where(site)} for `{wait.CommandLine}` (pid {wait.ProcessId}) to exit, but its {streams} {(wait.UnreadOutput.Count == 1 ? "is" : "are")} redirected and nothing has read {(wait.UnreadOutput.Count == 1 ? "it" : "them")} yet. " +
                          "Once the child writes more than the pipe holds (64 KB on Linux, 4 KB on Windows), it blocks until someone reads, so it never exits.",
                Details = [$"To confirm, look at the child (pid {wait.ProcessId}): it will be blocked writing to its {wait.UnreadOutput[0]}."],
                Advice = "Read the redirected output before (or while) waiting for exit: string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); " +
                         "or use BeginOutputReadLine/BeginErrorReadLine, which read in the background. With both streams redirected, read at least one asynchronously.",
                ThreadIds = [t.ManagedId],
                Location = site?.Source,
            };
        }
    }
}
