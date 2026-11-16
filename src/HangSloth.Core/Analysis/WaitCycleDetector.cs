using HangSloth.Model;

namespace HangSloth.Analysis;

internal enum WaitKind { Lock, Task, Join, TypeInitializer, Send, Callback, Finalizer }

/// One thread waiting for another: for a lock it owns, a task it is running, for it to exit,
/// for a static constructor it is running, or to run a callback Sent to its SynchronizationContext.
internal sealed record WaitEdge(int From, int To, WaitKind Kind, MonitorLock? Lock = null, TaskInfo? Task = null, string? TypeName = null);

internal static class WaitGraph
{
    /// Each blocked thread waits for at most one thing, so the graph is a map from waiter to edge.
    public static Dictionary<int, WaitEdge> Build(ProcessSnapshot snapshot)
    {
        var edges = new Dictionary<int, WaitEdge>();
        var locks = new Dictionary<ulong, MonitorLock>();
        foreach (var l in snapshot.Locks) locks.TryAdd(l.Object.Address, l);
        var runningTask = new Dictionary<ulong, int>();
        foreach (var t in snapshot.Threads)
            if (t.RunningTask is { } task) runningTask.TryAdd(task, t.ManagedId);

        foreach (var t in snapshot.Threads)
        {
            if (t.WaitingForLock is { } addr && locks.TryGetValue(addr, out var l) && l.OwnerThreadId is { } owner && owner != t.ManagedId)
            {
                edges[t.ManagedId] = new WaitEdge(t.ManagedId, owner, WaitKind.Lock, Lock: l);
            }
            else if (t.BlockedOnTask is { } waited
                     && Describe.Chain(waited).FirstOrDefault(x => x.State == TaskState.Pending && runningTask.ContainsKey(x.Address)) is { } running
                     && runningTask[running.Address] != t.ManagedId)
            {
                edges[t.ManagedId] = new WaitEdge(t.ManagedId, runningTask[running.Address], WaitKind.Task, Task: running);
            }
            else if (t.JoiningThreadId is { } joined && snapshot.FindThread(joined) is not null)
            {
                edges[t.ManagedId] = new WaitEdge(t.ManagedId, joined, WaitKind.Join);
            }
            else if (t.SendingTo is { } context
                     && snapshot.Threads.FirstOrDefault(o => o.ManagedId != t.ManagedId && o.SynchronizationContext?.Address == context.Address) is { } owner2)
            {
                edges[t.ManagedId] = new WaitEdge(t.ManagedId, owner2.ManagedId, WaitKind.Send, TypeName: context.TypeName);
            }
            else if (t.WaitingForCallbackOnThread is { } runner && snapshot.FindThread(runner) is not null)
            {
                edges[t.ManagedId] = new WaitEdge(t.ManagedId, runner, WaitKind.Callback);
            }
            else if (FrameClassifier.Classify(t.Frames) == BlockingKind.Finalizers
                     && snapshot.Threads.FirstOrDefault(o => o.IsFinalizer && o.ManagedId != t.ManagedId) is { } finalizer)
            {
                edges[t.ManagedId] = new WaitEdge(t.ManagedId, finalizer.ManagedId, WaitKind.Finalizer);
            }
            else if (FrameClassifier.Classify(t.Frames) == BlockingKind.TypeInitializer && FindInitializer(snapshot, t) is var (initializer, type))
            {
                edges[t.ManagedId] = new WaitEdge(t.ManagedId, initializer, WaitKind.TypeInitializer, TypeName: type);
            }
        }
        AddReaderEdges(snapshot, edges, e => e.To, (t, l, reader) => new WaitEdge(t.ManagedId, reader, WaitKind.Lock, Lock: l));
        return edges;
    }

    /// The readers of a ReaderWriterLockSlim that stand in a waiting thread's way: for a writer while no thread
    /// holds the write or upgradeable lock, or for the thread in upgradeable mode waiting to upgrade. Only the
    /// readers the dump shows, never the thread itself. A thread waiting to read is held up by a waiting writer instead.
    public static IReadOnlyList<int> BlockingReaders(ThreadSnapshot t, MonitorLock l)
    {
        if (l.Kind != LockKind.ReaderWriter || EnteringReadOrUpgradeable(t)) return [];
        if (l.OwnerThreadId is not null && l.UpgraderThreadId != t.ManagedId) return [];
        return l.ReaderThreadIds.Where(id => id != t.ManagedId).ToList();
    }

    /// A thread entering a ReaderWriterLockSlim for reading or in upgradeable mode: readers don't keep it out,
    /// a writer (or an upgrader, or a writer waiting ahead of it) does.
    public static bool EnteringReadOrUpgradeable(ThreadSnapshot t) =>
        t.Frames.Any(f => f.TypeName == "System.Threading.ReaderWriterLockSlim" && f.MethodName is "TryEnterReadLockCore" or "TryEnterUpgradeableReadLockCore");

    /// A thread waiting for readers waits for all of them, so it only takes part in a cycle through a reader
    /// that waits, directly or through others, for it: then that reader can never leave. Add an edge to such a
    /// reader. A thread whose readers will all finish gets no edge: it is waiting, not deadlocked.
    public static void AddReaderEdges<T>(ProcessSnapshot snapshot, Dictionary<int, T> edges, Func<T, int> to, Func<ThreadSnapshot, MonitorLock, int, T> edge)
    {
        var waiting = new List<(ThreadSnapshot Thread, MonitorLock Lock, IReadOnlyList<int> Readers)>();
        foreach (var t in snapshot.Threads)
        {
            if (edges.ContainsKey(t.ManagedId) || t.WaitingForLock is not { } addr || snapshot.FindLock(addr) is not { } l) continue;
            var readers = BlockingReaders(t, l);
            if (readers.Count > 0) waiting.Add((t, l, readers));
        }
        if (waiting.Count == 0) return;

        var readersOf = waiting.ToDictionary(w => w.Thread.ManagedId, w => w.Readers);
        IEnumerable<int> Next(int id) =>
            edges.TryGetValue(id, out var e) ? [to(e)] : readersOf.TryGetValue(id, out var r) ? r : [];
        bool Reaches(int from, int target)
        {
            var seen = new HashSet<int>();
            var frontier = new Queue<int>([from]);
            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                if (current == target) return true;
                if (!seen.Add(current)) continue;
                foreach (var n in Next(current)) frontier.Enqueue(n);
            }
            return false;
        }

        var chosen = new Dictionary<int, T>();
        foreach (var (t, l, readers) in waiting)
        {
            foreach (var reader in readers.Where(r => Reaches(r, t.ManagedId)).Take(1))
                chosen[t.ManagedId] = edge(t, l, reader);
        }
        foreach (var (id, e) in chosen) edges[id] = e;
    }

    /// Which thread is running the static constructor this thread waits for? The runtime's class-init lock
    /// isn't visible in a dump, so match the type the waiting code belongs to, or the only candidate.
    private static (int Thread, string? Type)? FindInitializer(ProcessSnapshot snapshot, ThreadSnapshot waiter)
    {
        var running = snapshot.Threads
            .Where(t => t.ManagedId != waiter.ManagedId)
            .SelectMany(t => FrameClassifier.RunningStaticConstructors(t.Frames).Select(type => (t.ManagedId, Type: type)))
            .ToList();
        if (running.Count == 0) return null;

        var caller = FrameClassifier.FirstUserFrame(waiter.Frames)?.TypeName ?? "";
        foreach (var r in running)
        {
            if (caller == r.Type || caller.StartsWith(r.Type + "+", StringComparison.Ordinal))
                return (r.ManagedId, r.Type);
        }
        return running.Select(r => r.ManagedId).Distinct().Count() == 1 ? (running[0].ManagedId, null) : null;
    }

    public static List<int>? FindCycle(int start, Dictionary<int, WaitEdge> edges)
    {
        var path = new List<int>();
        var current = start;
        while (edges.TryGetValue(current, out var edge))
        {
            var index = path.IndexOf(current);
            if (index >= 0) return path[index..];
            path.Add(current);
            current = edge.To;
        }
        return null;
    }
}

/// Threads waiting on each other in a circle where at least one wait isn't a plain lock: a lock holder
/// blocked on a Task.Run that needs the lock, a Join on a thread that needs a lock, a Lazy&lt;T&gt; factory
/// waiting for work that reads the same Lazy, a static constructor waiting for a thread that needs the type.
public sealed class WaitCycleDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var edges = WaitGraph.Build(snapshot);
        var reported = new HashSet<string>();
        foreach (var start in edges.Keys.Order())
        {
            var cycle = WaitGraph.FindCycle(start, edges);
            if (cycle is null || cycle.Any(claimed.Contains)) continue;
            if (cycle.All(id => edges[id].Kind == WaitKind.Lock)) continue; // LockDeadlockDetector's job
            if (!reported.Add(string.Join(",", cycle.Order()))) continue;
            yield return Explain(snapshot, Rotate(cycle, edges), edges);
        }
    }

    /// Start the story at the thread that holds the most context: the static constructor, else the
    /// thread blocked on a task or join while holding a lock.
    private static List<int> Rotate(List<int> cycle, Dictionary<int, WaitEdge> edges)
    {
        var first = cycle.FindIndex(id => edges.Values.Any(e => e.Kind == WaitKind.TypeInitializer && e.To == id));
        if (first < 0) first = cycle.FindIndex(id => edges[id].Kind is WaitKind.Task or WaitKind.Join or WaitKind.Callback or WaitKind.Finalizer);
        return first <= 0 ? cycle : [.. cycle[first..], .. cycle[..first]];
    }

    private static Finding Explain(ProcessSnapshot snapshot, List<int> cycle, Dictionary<int, WaitEdge> edges)
    {
        var threads = cycle.Select(id => snapshot.FindThread(id)!).ToList();
        var kinds = cycle.Select(id => edges[id].Kind).ToHashSet();
        var typeInit = cycle.Select(id => edges[id]).FirstOrDefault(e => e.Kind == WaitKind.TypeInitializer);
        var send = cycle.Select(id => edges[id]).FirstOrDefault(e => e.Kind == WaitKind.Send);
        var callback = kinds.Contains(WaitKind.Callback);
        var finalizerWait = cycle.Select(id => edges[id]).FirstOrDefault(e => e.Kind == WaitKind.Finalizer);
        var parallel = cycle.Select(id => edges[id].Task).Any(t => t?.Description.StartsWith("a Parallel.", StringComparison.Ordinal) == true);
        var lazy = cycle.Select(id => edges[id].Lock).FirstOrDefault(l => l?.DisplayName.Contains("Lazy<", StringComparison.Ordinal) == true);

        var details = threads.Select(t => Step(snapshot, t, edges[t.ManagedId])).ToList();
        var labels = threads.Select(t => t.Label).ToList();
        var site = FrameClassifier.BestUserFrame(threads[0].Frames);
        var start = site?.Source is null ? "" : $" Start at {site.Source}.";

        string id, title, summary, advice;
        if (typeInit is not null)
        {
            var initializer = snapshot.FindThread(typeInit.To)!;
            var waiter = snapshot.FindThread(typeInit.From)!;
            var type = TypeLabel(typeInit, initializer);
            id = "type-initializer-deadlock";
            title = "Deadlock: static constructor waiting for a thread that needs it";
            summary = $"{initializer.Label} is running the static constructor of {type} and waits for {waiter.Label}, but {waiter.Label} uses {type} " +
                      $"and has to wait for that static constructor to finish. Neither can continue.{start}";
            advice = $"Don't wait for other threads or tasks inside a static constructor or static field initializer, least of all for code that uses the same type. " +
                     $"Move the slow initialization out of {type}'s static constructor (an explicit Initialize() call, or a Lazy<T> created on first use), or do the work on the same thread.";
        }
        else
        {
            id = "wait-cycle";
            title = lazy is not null ? "Deadlock: Lazy<T> factory waiting for code that needs the same Lazy<T>"
                : send is not null ? $"Deadlock: {snapshot.FindThread(send.To)!.Label} waits for work that is waiting for it"
                : finalizerWait is not null ? "Deadlock: waiting for finalizers while holding a lock a finalizer needs"
                : callback ? "Deadlock: unregistering a cancellation callback that is waiting for you"
                : parallel ? "Deadlock: Parallel loop body needs a lock its caller holds"
                : "Deadlock: threads waiting on each other";
            summary = (threads.Count == 2
                ? $"{labels[0]} and {labels[1]} are waiting for each other, so neither can ever continue."
                : $"{Describe.JoinAnd(labels)} are waiting for each other in a circle, so none of them can ever continue.") + start;
            advice = lazy is not null
                ? $"A Lazy<T> runs its factory under a lock (LazyThreadSafetyMode.ExecutionAndPublication), and this factory waits for work that reads {LazyField(lazy)}.Value. " +
                  "Don't read the Value from inside the factory, directly or from work the factory waits for: pass the data it needs in instead."
                : send is not null
                    ? $"{snapshot.FindThread(send.To)!.Label} owns the {Names.ShortType(send.TypeName!)} and is blocked, so the callback Sent to it (Send, Control.Invoke, Dispatcher.Invoke) can never run. " +
                      "Don't block a thread with a SynchronizationContext (a UI thread) on work that calls back into it: await the work instead of Wait()/.Result, or use Post/BeginInvoke for the callback."
                : finalizerWait is not null
                    ? "GC.WaitForPendingFinalizers waits for every queued finalizer, on the single finalizer thread, and one of them needs a lock this thread holds. " +
                      "Don't call it while holding locks, and keep finalizers free of locks and shared state (use SafeHandle instead of a finalizer where you can)."
                : callback
                    ? "CancellationTokenRegistration.Dispose (and CancellationTokenSource.Dispose of a linked source) waits for the callback if it is running right now. " +
                      "Don't dispose the registration while holding a lock the callback takes: dispose it outside the lock, or use registration.Unregister(), which doesn't wait."
                : parallel
                    ? "Parallel.For/ForEach runs the loop body on other threads, so a lock the caller holds isn't reentrant for them. Don't hold a lock around a parallel loop whose body takes it: " +
                      "release it first, or collect results without the lock and apply them afterwards."
                : kinds.Contains(WaitKind.Join)
                    ? "Don't Join a thread while holding a lock that thread needs to finish. Release the lock before calling Join (set the stop flag under the lock, Join outside it)."
                    : "Don't block on a task while holding a lock the task needs. Release the lock before waiting, await outside the lock, or let the task do its work without taking the lock.";
        }

        return new Finding
        {
            Id = id,
            Severity = Severity.Critical,
            Title = title,
            Summary = summary,
            Details = details,
            Advice = advice,
            ThreadIds = cycle,
            Location = site?.Source,
        };
    }

    /// The code that called Send, rather than the Send override itself. Null when the stack shows no other frame of the
    /// app's (optimized code may have none left).
    private static StackFrameInfo? SendCaller(ThreadSnapshot t, WaitEdge edge)
    {
        var i = t.Frames.ToList().FindIndex(f => f.MethodName == "Send" && f.TypeName == edge.TypeName);
        var caller = i < 0 ? FrameClassifier.BestUserFrame(t.Frames) : FrameClassifier.BestUserFrame(t.Frames.Skip(i + 1).ToList()) ?? FrameClassifier.BestUserFrame(t.Frames);
        return caller is not null && caller.MethodName == "Send" && caller.TypeName == edge.TypeName ? null : caller;
    }

    /// "UiDispatcherContext.Send, called from ExportWindow.Export (UiInvoke.cs:30)", or "UiDispatcherContext.Send (UiInvoke.cs:17)"
    /// when the Send override is the only frame of the app's.
    private static string SendCall(ThreadSnapshot t, WaitEdge edge)
    {
        var send = $"{Names.ShortType(edge.TypeName!)}.Send";
        if (SendCaller(t, edge) is { } caller) return $"{send}, called from {Describe.Where(caller)}";
        var own = t.Frames.FirstOrDefault(f => f.MethodName == "Send" && f.TypeName == edge.TypeName);
        return own?.Source is { } at ? $"{send} ({at})" : send;
    }

    /// "CatalogService._catalog (Lazy<Catalog>)" -> "CatalogService._catalog".
    private static string LazyField(MonitorLock l) =>
        l.DisplayName.IndexOf(" (Lazy<", StringComparison.Ordinal) is var i and > 0 ? l.DisplayName[..i] : "the Lazy<T>";

    private static string TypeLabel(WaitEdge edge, ThreadSnapshot initializer) =>
        edge.TypeName is { } t ? Names.ShortType(t)
        : FrameClassifier.RunningStaticConstructors(initializer.Frames).Select(Names.ShortType).FirstOrDefault() ?? "a type";

    private static string Step(ProcessSnapshot snapshot, ThreadSnapshot t, WaitEdge edge)
    {
        var other = snapshot.FindThread(edge.To)?.Label ?? $"thread {edge.To}";
        var held = Describe.HeldBy(snapshot, t.ManagedId);
        var cctor = FrameClassifier.RunningStaticConstructors(t.Frames).Select(Names.ShortType).FirstOrDefault();
        var context = new List<string>();
        if (cctor is not null && edge.Kind != WaitKind.TypeInitializer) context.Add($"is running the static constructor of {cctor}");
        if (held.Count > 0) context.Add($"holds {Describe.JoinAnd(held)}");
        var holds = context.Count == 0 ? "" : string.Join(", ", context) + " and ";
        var where = Describe.Where(FrameClassifier.BestUserFrame(t.Frames));
        return edge.Kind switch
        {
            WaitKind.Lock => $"{t.Label} {holds}is waiting in {where} {Describe.WaitFor(edge.Lock!, t.ManagedId, edge.To, other)}.",
            WaitKind.Task => $"{t.Label} {holds}is blocked in {FrameClassifier.DescribeTaskWait(t.Frames)} at {where}, waiting for {Describe.Task(edge.Task!)}, which is running on {other}.",
            WaitKind.Join => $"{t.Label} {holds}is waiting in {where} for {other} to finish (Thread.Join).",
            WaitKind.Callback => $"{t.Label} {holds}is disposing a CancellationTokenRegistration in {where}, which waits for its callback to finish running on {other}.",
            WaitKind.Finalizer => $"{t.Label} {holds}is waiting in {where} for {other} to finish running finalizers (GC.WaitForPendingFinalizers).",
            WaitKind.Send => $"{t.Label} {holds}is blocked in {SendCall(t, edge)}, waiting for {other} to run the callback, since that SynchronizationContext belongs to {other}.",
            _ => $"{t.Label} {holds}needs {(edge.TypeName is { } type ? Names.ShortType(type) : "a type")} in {where}, so it waits for its static constructor, which {other} is running.",
        };
    }
}
