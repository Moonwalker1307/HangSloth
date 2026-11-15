using HangSloth.Model;

namespace HangSloth.Analysis;

/// Threads blocked in BlockingCollection.Add because the bounded collection is full and nothing takes
/// items out. A thread in Take or GetConsumingEnumerable on an empty collection is an idle consumer,
/// which is normal, so it isn't reported. A lock held across either wait is BoundedQueueDeadlockDetector's.
public sealed class FullQueueDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var full = snapshot.Threads
            .Where(t => !claimed.Contains(t.ManagedId)
                        && t.WaitingOnCollection is { Adding: true } wait
                        && !RoomKnown(wait))
            .GroupBy(t => t.WaitingOnCollection!.Collection.Address);
        foreach (var group in full)
        {
            var waiters = group.ToList();
            var wait = waiters[0].WaitingOnCollection!;
            var ids = waiters.Select(t => t.ManagedId).ToList();
            var running = SemaphoreFacts.AppCodeRunningOutside(snapshot, ids);
            var site = FrameClassifier.BestUserFrame(waiters[0].Frames);
            var who = waiters.Count == 1 ? $"{waiters[0].Label} is" : $"{waiters.Count} threads are";
            var count = wait.Count is >= 0 ? wait.Count : null;
            var fill = (wait.Capacity, count) switch
            {
                ({ } c, null) => $", which is at its bounded capacity of {Describe.Plural(c, "item")}",
                ({ } c, { } n) when n == c => $", which holds {Describe.Plural(n, "item")}, its bounded capacity",
                ({ } c, { } n) => $", which holds {Describe.Plural(n, "item")} (bounded capacity {c})",
                (null, { } n) => $", which holds {Describe.Plural(n, "item")}",
                _ => "",
            };
            yield return new Finding
            {
                Id = "full-queue",
                Severity = running ? Severity.Info : Severity.Warning,
                Title = "Waiting for room in a full BlockingCollection",
                Summary = $"{who} waiting in {Describe.Where(site)} to add to {wait.DisplayName} (full BlockingCollection){fill}." +
                          (running
                              ? " Other app code is still running and may yet take items out; if this persists across two dumps, nothing is taking items out."
                              : " No other thread is running app code that could take items out."),
                Details = ["Add on a bounded BlockingCollection blocks until another thread takes an item out with Take, TryTake or GetConsumingEnumerable."],
                Advice = $"Check that the code that takes items out of {wait.DisplayName} is still running and hasn't stopped or thrown. " +
                         "If producers can outpace it, use TryAdd with a timeout and handle a full queue, or raise the bounded capacity.",
                ThreadIds = ids,
                Location = site?.Source,
            };
        }
    }

    /// Add can only park when there is no free slot, so the collection counts as full unless its count
    /// was read and is below a known capacity (a dump may leave either unreadable).
    private static bool RoomKnown(CollectionWait wait) =>
        wait is { Capacity: { } capacity, Count: >= 0 and var n } && n < capacity;
}
