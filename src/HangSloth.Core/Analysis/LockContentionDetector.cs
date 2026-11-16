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
            var ownerBlocked = FrameClassifier.IsBlocked(ownerKind) || owner?.Database?.Waiting == DatabaseWaitKind.ReadingRows;

            var who = waiters.Count == 1 ? snapshot.FindThread(waiters[0])!.Label : $"{waiters.Count} threads are";
            var verb = waiters.Count == 1 ? " is" : "";
            string summary;
            if (owner is null && l.Kind == LockKind.ReaderWriter)
            {
                summary = $"{who}{verb} waiting for {l.DisplayName} (a ReaderWriterLockSlim), which {Describe.Plural(l.ReaderCount, "thread holds", "threads hold")} for reading. " +
                          "Readers aren't recorded per thread, so look for code that entered the read lock and is slow or blocked before ExitReadLock.";
            }
            else if (owner is null)
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
            var details = new List<string> { $"Waiting at: {Describe.JoinAnd(waitSpots)}." };
            var timerCallbacks = waiters.Select(snapshot.FindThread).OfType<ThreadSnapshot>().Where(IsTimerCallback).ToList();
            if (owner is not null && timerCallbacks.Count > 0)
            {
                var callback = Describe.Where(FrameClassifier.BestUserFrame(timerCallbacks[0].Frames));
                details.Add($"{Describe.JoinAnd(timerCallbacks.Select(t => t.Label))} {(timerCallbacks.Count == 1 ? "is" : "are")} running a timer callback ({callback}).");
                if (ownerKind is BlockingKind.WaitHandle or BlockingKind.TaskWait)
                    details.Add($"If {owner.Label} is waiting for the timer's callbacks to finish (Timer.Dispose(WaitHandle) or DisposeAsync), this is a deadlock: the callback it waits for is the one stuck on this lock.");
            }
            yield return new Finding
            {
                Id = "lock-contention",
                Severity = ownerBlocked || owner is null || waiters.Count >= 2 ? Severity.Warning : Severity.Info,
                Title = owner is null ? "Orphaned lock" : "Threads blocked behind a lock",
                Summary = summary,
                Details = details,
                Advice = owner?.Database?.Waiting == DatabaseWaitKind.ReadingRows
                    ? $"Don't read a big query result while holding {l.DisplayName}: load the rows into a local without the lock, then swap the reference in under the lock. " +
                      "Better still, read only what you need (WHERE, LIMIT, paging) or aggregate in SQL."
                    : ownerKind == BlockingKind.SyncIO
                    ? SyncIoAdvice(owner!, l)
                    : ownerBlocked
                    ? "Don't do slow or blocking work while holding a lock: copy what you need, release the lock, then do the slow part."
                    : "Keep the work inside this lock short, or use a finer-grained or reader/writer lock.",
                ThreadIds = owner is null ? waiters : [owner.ManagedId, .. waiters],
                Location = ownerSite?.Source,
            };
        }
    }

    private static string SyncIoAdvice(ThreadSnapshot owner, MonitorLock l)
    {
        var async = FrameClassifier.SyncIoAsyncAlternative(owner.Frames);
        return $"Don't do I/O while holding a lock: {FrameClassifier.SyncIoMethod(owner.Frames)} waits for as long as the other side takes to answer, and every thread that needs {l.DisplayName} waits with it. " +
               "Do the I/O before taking the lock and hold the lock only to update shared state" +
               (async is null ? "." : $"; if the lock has to cover the I/O, use a SemaphoreSlim with WaitAsync and await {async}.");
    }

    private static bool IsTimerCallback(ThreadSnapshot t) =>
        t.Frames.Any(f => f.TypeName.StartsWith("System.Threading.TimerQueueTimer", StringComparison.Ordinal) && f.MethodName is "Fire" or "CallCallback");
}
