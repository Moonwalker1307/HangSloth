using HangSloth.Model;

namespace HangSloth.Analysis;

/// A thread holds a lock while it waits on a BlockingCollection (adding to a full one, or taking from an
/// empty one), and the thread that would drain or fill the collection is waiting for that lock.
public sealed class BoundedQueueDeadlockDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var t in snapshot.Threads)
        {
            if (t.WaitingOnCollection is not { } wait || claimed.Contains(t.ManagedId)) continue;
            if (wait.Adding && (wait.Capacity is null || wait.Count < wait.Capacity)) continue;

            var held = snapshot.Locks.Where(l => l.OwnerThreadId == t.ManagedId && l.WaiterThreadIds.Any(id => !claimed.Contains(id))).ToList();
            if (held.Count == 0) continue;

            var waiters = held.SelectMany(l => l.WaiterThreadIds).Where(id => !claimed.Contains(id)).Distinct().Select(id => snapshot.FindThread(id)!).ToList();
            var owner = OwnerType(wait);
            var partner = waiters.FirstOrDefault(w => owner is not null && w.Frames.Any(f => FrameClassifier.IsUserFrame(f) && Names.ShortType(f.TypeName) == owner));
            var other = partner ?? waiters[0];
            var lockNames = Describe.JoinAnd(held.Select(Describe.Held));
            var state = wait.Adding
                // Add only blocks on a full collection; its count may not be readable.
                ? wait.Count is null ? $"which is full (capacity {wait.Capacity})" : $"which is full ({wait.Count} of {wait.Capacity})"
                : "which is empty";
            var role = wait.Adding ? "takes items out of" : "adds items to";
            var site = FrameClassifier.BestUserFrame(t.Frames);
            var otherSite = Describe.Where(FrameClassifier.BestUserFrame(other.Frames));

            var details = new List<string>
            {
                $"{t.Label} is waiting in {Describe.Where(site)} to {(wait.Adding ? "add to" : "take from")} {wait.DisplayName}, {state}, and holds {lockNames}.",
                $"{other.Label} is waiting in {otherSite} for {lockNames}.",
            };
            details.Add(partner is not null
                ? $"{other.Label} runs {owner} code, the class that owns {wait.DisplayName}, so it is most likely the one that {role} it."
                : $"If {other.Label} is what {role} {wait.DisplayName}, this is a deadlock.");

            yield return new Finding
            {
                Id = "bounded-queue-deadlock",
                Severity = partner is not null ? Severity.Critical : Severity.Warning,
                Title = wait.Adding
                    ? "Deadlock: holding a lock while waiting for room in a full queue"
                    : "Deadlock: holding a lock while waiting for an empty queue",
                Summary = $"{t.Label} is blocked on {wait.DisplayName} ({Names.ShortType(wait.Collection.TypeName)}), {state}, while holding {lockNames}. " +
                          $"{other.Label} {(partner is not null ? "" : "probably ")}{role} that queue, but first needs {lockNames}, so {(wait.Adding ? "the queue never drains" : "nothing is ever added")}." +
                          (site?.Source is null ? "" : $" Start at {site.Source}."),
                Details = details,
                Advice = $"Don't hold a lock across BlockingCollection.{(wait.Adding ? "Add" : "Take")}: it can block until another thread {(wait.Adding ? "takes an item" : "adds one")}. " +
                         $"Release the lock before {(wait.Adding ? "adding" : "taking")}, use TryAdd/TryTake with a timeout, or don't take the lock on the {(wait.Adding ? "consumer" : "producer")} side.",
                ThreadIds = [t.ManagedId, .. waiters.Select(w => w.ManagedId)],
                Location = site?.Source,
            };
        }
    }

    /// "EventBus._queue" -> "EventBus".
    private static string? OwnerType(CollectionWait wait) =>
        wait.FieldName is { } f && f.LastIndexOf('.') is var i and > 0 ? f[..i] : null;
}
