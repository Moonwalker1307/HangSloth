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
            if (t.WaitingForLock is { } addr && locks.TryGetValue(addr, out var l) && l.OwnerThreadId is { } owner)
                waitsFor[t.ManagedId] = (owner, l);
        }

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

    private static Finding Explain(ProcessSnapshot snapshot, List<int> cycle, Dictionary<int, (int Owner, MonitorLock Lock)> waitsFor)
    {
        var threads = cycle.Select(id => snapshot.FindThread(id)!).ToList();
        var details = new List<string>();
        foreach (var t in threads)
        {
            var (owner, wanted) = waitsFor[t.ManagedId];
            var held = snapshot.Locks.Where(l => l.OwnerThreadId == t.ManagedId).Select(l => l.DisplayName).ToList();
            var holds = held.Count == 0 ? "" : $"holds the lock on {Describe.JoinAnd(held)} and ";
            details.Add($"{t.Label} {holds}is waiting in {Describe.Where(FrameClassifier.BestUserFrame(t.Frames))} " +
                        $"for the lock on {wanted.DisplayName}, which {snapshot.FindThread(owner)?.Label ?? $"thread {owner}"} holds.");
        }

        var labels = threads.Select(t => t.Label).ToList();
        var summary = threads.Count == 2
            ? $"{labels[0]} and {labels[1]} are each waiting for a lock the other one holds, so neither can ever continue."
            : $"{Describe.JoinAnd(labels)} are waiting for each other's locks in a circle, so none of them can ever continue.";

        var order = cycle.Select(id => waitsFor[id].Lock.DisplayName).Distinct().ToList();
        var first = FrameClassifier.BestUserFrame(threads[0].Frames);
        return new Finding
        {
            Id = "lock-deadlock",
            Severity = Severity.Critical,
            Title = "Deadlock: threads waiting on each other's locks",
            Summary = summary + (first?.Source is null ? "" : $" Start at {first.Source}."),
            Details = details,
            Advice = $"Take these locks in one consistent order everywhere ({string.Join(" before ", order.AsEnumerable().Reverse())} or the reverse, but always the same), " +
                     "or avoid acquiring a second lock while holding the first.",
            ThreadIds = cycle,
            Location = first?.Source,
        };
    }
}
