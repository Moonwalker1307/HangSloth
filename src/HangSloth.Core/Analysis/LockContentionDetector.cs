using HangSloth.Model;

namespace HangSloth.Analysis;

/// Threads piling up behind one lock whose owner is slow or itself blocked.
public sealed class LockContentionDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var l in snapshot.Locks.OrderByDescending(l => l.WaiterThreadIds.Count))
        {
            var waiters = l.WaiterThreadIds.Where(id => !claimed.Contains(id)).ToList();
            if (waiters.Count == 0) continue;

            var owner = l.OwnerThreadId is { } id ? snapshot.FindThread(id) : null;
            var ownerKind = owner is null ? BlockingKind.None : FrameClassifier.Classify(owner.Frames);
            var ownerBlocked = FrameClassifier.IsBlocked(ownerKind);

            var who = waiters.Count == 1 ? snapshot.FindThread(waiters[0])!.Label : $"{waiters.Count} threads are";
            var verb = waiters.Count == 1 ? " is" : "";
            string summary;
            if (owner is null)
            {
                summary = $"{who}{verb} waiting for the lock on {l.DisplayName}, but the thread that took it no longer exists. The lock was never released (orphaned lock).";
            }
            else
            {
                summary = $"{who}{verb} stuck waiting for the lock on {l.DisplayName}, held by {owner.Label}, which is {Describe.Activity(owner)}" +
                          (ownerBlocked ? " while holding it." : ".");
            }

            var waitSpots = waiters
                .Select(w => Describe.Spot(FrameClassifier.BestUserFrame(snapshot.FindThread(w)!.Frames)))
                .GroupBy(s => s)
                .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ({g.Count()} threads)");

            var ownerSite = owner is null ? null : FrameClassifier.BestUserFrame(owner.Frames);
            yield return new Finding
            {
                Id = "lock-contention",
                Severity = ownerBlocked || owner is null || waiters.Count >= 2 ? Severity.Warning : Severity.Info,
                Title = owner is null ? "Orphaned lock" : "Threads blocked behind a lock",
                Summary = summary,
                Details = [$"Waiting at: {Describe.JoinAnd(waitSpots)}."],
                Advice = ownerBlocked
                    ? "Don't do slow or blocking work while holding a lock: copy what you need, release the lock, then do the slow part."
                    : "Keep the work inside this lock short, or use a finer-grained or reader/writer lock.",
                ThreadIds = owner is null ? waiters : [owner.ManagedId, .. waiters],
                Location = ownerSite?.Source,
            };
        }
    }
}
