using HangSloth.Model;

namespace HangSloth.Analysis;

/// Threads that each hold a lock and wait for a lock held by the next one, in a cycle.
public sealed class LockDeadlockDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var locks = snapshot.Locks.ToDictionary(l => l.Object.Address);

        // Edge: waiting thread -> owner of the lock it wants.
        var waitsFor = new Dictionary<int, (int Owner, MonitorLock Lock)>();
        foreach (var t in snapshot.Threads)
        {
            // Never an edge to itself: a ReaderWriterLockSlim's upgrader owns the lock it waits on, and waits for its readers.
            if (t.WaitingForLock is { } addr && locks.TryGetValue(addr, out var l) && l.OwnerThreadId is { } owner && owner != t.ManagedId)
                waitsFor[t.ManagedId] = (owner, l);
        }
        WaitGraph.AddReaderEdges<(int Owner, MonitorLock Lock)>(snapshot, waitsFor, e => e.Owner, (t, l, reader) => (reader, l));

        var reported = new HashSet<string>();
        foreach (var start in waitsFor.Keys.Order())
        {
            var cycle = FindCycle(start, waitsFor);
            if (cycle is null) continue;
            var key = string.Join(",", cycle.Order());
            if (!reported.Add(key)) continue;
            yield return Explain(snapshot, cycle, waitsFor);
        }
    }

    private static List<int>? FindCycle(int start, Dictionary<int, (int Owner, MonitorLock Lock)> waitsFor)
    {
        var path = new List<int>();
        var current = start;
        while (waitsFor.TryGetValue(current, out var edge))
        {
            var index = path.IndexOf(current);
            if (index >= 0) return path[index..];
            path.Add(current);
            current = edge.Owner;
        }
        return null;
    }

    /// The lock a Lazy&lt;T&gt; takes while its value factory runs (LazyThreadSafetyMode.ExecutionAndPublication).
    private static bool IsLazy(MonitorLock l) => l.DisplayName.Contains("Lazy<", StringComparison.Ordinal);

    private static bool Unnamed(MonitorLock l) => l.DisplayName.StartsWith("Lazy<", StringComparison.Ordinal);

    /// Each thread runs the value factory of one Lazy&lt;T&gt; and needs the Value of another, whose factory the next thread runs.
    private static Finding LazyCycle(ProcessSnapshot snapshot, List<int> cycle, List<ThreadSnapshot> threads, Dictionary<int, (int Owner, MonitorLock Lock)> waitsFor)
    {
        var details = new List<string>();
        foreach (var t in threads)
        {
            var (owner, wanted) = waitsFor[t.ManagedId];
            var running = snapshot.Locks.FirstOrDefault(l => l.OwnerThreadId == t.ManagedId && IsLazy(l));
            var other = snapshot.FindThread(owner)?.Label ?? $"thread {owner}";
            // An unnamed Lazy is "a Lazy<RateTable>"; two with one name are "a" and "another".
            var held = running is null ? "a Lazy<T>" : Unnamed(running) || running.DisplayName == wanted.DisplayName ? $"a {running.DisplayName}" : running.DisplayName;
            var needs = running is not null && running.DisplayName == wanted.DisplayName ? $"another {wanted.DisplayName}" : Unnamed(wanted) ? $"a {wanted.DisplayName}" : wanted.DisplayName;
            details.Add($"{t.Label} is running the value factory of {held} and, in {Describe.Where(FrameClassifier.BestUserFrame(t.Frames))}, needs the Value of {needs}, whose factory {other} is running.");
        }
        var labels = threads.Select(t => t.Label).ToList();
        var first = FrameClassifier.BestUserFrame(threads[0].Frames);
        return new Finding
        {
            Id = "lock-deadlock",
            Severity = Severity.Critical,
            Title = "Deadlock: Lazy<T> values whose factories need each other",
            Summary = (threads.Count == 2
                          ? $"{labels[0]} and {labels[1]} are each creating the value of a Lazy<T> and need the value the other one is creating, so neither can ever continue."
                          : $"{Describe.JoinAnd(labels)} are each creating the value of a Lazy<T> and need the value the next one is creating, in a circle, so none of them can ever continue.") +
                      (first?.Source is null ? "" : $" Start at {first.Source}."),
            Details = details,
            Advice = "A Lazy<T> runs its value factory under a lock (LazyThreadSafetyMode.ExecutionAndPublication), and each of these factories reads the Value of a Lazy whose factory is " +
                     "running on another thread. Break the cycle between the factories: don't read another Lazy's Value (or GetOrAdd another key of the same cache) inside a value factory " +
                     "when that one can need this value too; create values that need each other in one factory, or pass what a factory needs in.",
            ThreadIds = cycle,
            Location = first?.Source,
        };
    }

    /// "different Ledger objects", "different lock objects (new object())", or "different locks, each Philosopher.right".
    private static string DifferentLocks(List<MonitorLock> locks)
    {
        if (locks.Any(l => l.FieldName is not null))
            return $"different locks, each {Describe.JoinAnd(locks.Select(l => l.DisplayName).Distinct())}";
        var types = locks.Select(l => l.Object.ShortTypeName).Distinct().ToList();
        return types is ["object"] ? "different lock objects (each a new object())" : $"different {Describe.JoinAnd(types)} objects";
    }

    /// "Account.Sync (Id = "alice")" -> "Account.Sync".
    private static string? BaseName(string? field) =>
        field is not null && field.IndexOf(" (", StringComparison.Ordinal) is var p and > 0 ? field[..p] : field;

    private static Finding Explain(ProcessSnapshot snapshot, List<int> cycle, Dictionary<int, (int Owner, MonitorLock Lock)> waitsFor)
    {
        var threads = cycle.Select(id => snapshot.FindThread(id)!).ToList();
        // Lazy<T> values whose factories need each other: nobody wrote a lock there, so it isn't about lock order.
        if (cycle.All(id => IsLazy(waitsFor[id].Lock)))
            return LazyCycle(snapshot, cycle, threads, waitsFor);
        var details = new List<string>();
        foreach (var t in threads)
        {
            var (owner, wanted) = waitsFor[t.ManagedId];
            var held = Describe.HeldBy(snapshot, t.ManagedId);
            var holds = held.Count == 0 ? "" : $"holds {Describe.JoinAnd(held)} and ";
            details.Add($"{t.Label} {holds}is waiting in {Describe.Where(FrameClassifier.BestUserFrame(t.Frames))} " +
                        $"{Describe.WaitFor(wanted, t.ManagedId, owner, snapshot.FindThread(owner)?.Label ?? $"thread {owner}")}.");
        }
        var inline = threads.Select(Describe.InlineContinuation).OfType<string>().ToList();
        details.AddRange(inline);

        var labels = threads.Select(t => t.Label).ToList();
        var summary = threads.Count == 2
            ? $"{labels[0]} and {labels[1]} are each waiting for a lock the other one holds, so neither can ever continue."
            : $"{Describe.JoinAnd(labels)} are waiting for each other's locks in a circle, so none of them can ever continue.";

        var order = cycle.Select(id => waitsFor[id].Lock.DisplayName).Distinct().ToList();
        var fields = cycle.Select(id => BaseName(waitsFor[id].Lock.FieldName)).Distinct().ToList();
        var objects = cycle.Select(id => waitsFor[id].Lock.Object.Address).Distinct().Count();
        var first = FrameClassifier.BestUserFrame(threads[0].Frames);
        var advice = fields is [{ } sameField] && order.Count > 1
            ? $"These are all {sameField}{(cycle.All(id => waitsFor[id].Lock.Object.ShortTypeName == sameField) ? " objects" : ", on different objects")}, taken in a different order by each thread (typically a transfer from A to B racing one from B to A). " +
              "Lock the objects in one fixed order everywhere, for example sorted by id, or protect the whole operation with a single lock."
            // Different objects that the dump can't tell apart: "X before X" would say nothing.
            : order.Count < objects
            ? $"These are {objects} {DifferentLocks(cycle.Select(id => waitsFor[id].Lock).ToList())}, taken in a different order by each thread. " +
              "Lock them in one fixed order everywhere, for example sorted by an id, or protect the whole operation with a single lock."
            : $"Take these locks in one consistent order everywhere ({string.Join(" before ", order.AsEnumerable().Reverse())} or the reverse, but always the same), " +
              "or avoid acquiring a second lock while holding the first.";
        return new Finding
        {
            Id = "lock-deadlock",
            Severity = Severity.Critical,
            Title = "Deadlock: threads waiting on each other's locks",
            Summary = summary + (first?.Source is null ? "" : $" Start at {first.Source}."),
            Details = details,
            Advice = advice + (inline.Count > 0 ? Describe.InlineContinuationAdvice : ""),
            ThreadIds = cycle,
            Location = first?.Source,
        };
    }
}
