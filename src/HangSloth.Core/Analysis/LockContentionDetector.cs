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
            if (l.Kind == LockKind.ReaderWriter && (l.OwnerThreadId is null || l.UpgraderThreadId is { } u && waiters.Contains(u)))
            {
                yield return ReadersInTheWay(snapshot, l, waiters);
                continue;
            }

            var owner = l.OwnerThreadId is { } id ? snapshot.FindThread(id) : null;
            var ownerKind = owner is null ? BlockingKind.None : FrameClassifier.Classify(owner.Frames);
            var ownerBlocked = FrameClassifier.IsBlocked(ownerKind) || owner?.Database?.Waiting == DatabaseWaitKind.ReadingRows;

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

    /// A ReaderWriterLockSlim that only readers stand in the way of: a writer waiting while no thread holds
    /// the write lock, or the thread in upgradeable mode waiting to upgrade. Not orphaned, and (no cycle having
    /// been found) not a deadlock: it continues once the readers leave, so the readers are the story.
    private static Finding ReadersInTheWay(ProcessSnapshot snapshot, MonitorLock l, List<int> waiters)
    {
        var upgrader = l.UpgraderThreadId is { } u && waiters.Contains(u) ? snapshot.FindThread(u) : null;
        var queued = waiters.Where(w => w != upgrader?.ManagedId).Select(w => snapshot.FindThread(w)!).ToList();
        // The lock's own count includes the thread in upgradeable mode; it doesn't stand in its own way.
        var count = l.ReaderCount - (l.UpgraderThreadId is null ? 0 : 1);
        var readers = l.ReaderThreadIds.Where(id => id != l.UpgraderThreadId && !waiters.Contains(id))
            .Select(snapshot.FindThread).OfType<ThreadSnapshot>().ToList();
        var readerLabels = Describe.JoinAnd(readers.Select(r => r.Label));
        var named = readers.Count == 0 ? ""
            : readers.Count >= count ? $": {readerLabels}"
            : $", among them {readerLabels}";
        var unknown = "Readers aren't recorded per thread, so look for code that entered the read lock and is slow or blocked before ExitReadLock.";

        // Readers and upgraders that came after a waiting writer queue behind it, not behind the readers.
        var late = upgrader is null ? queued.Where(WaitGraph.EnteringReadOrUpgradeable).ToList() : [];
        var writers = queued.Except(late).ToList();
        if (writers.Count == 0) (writers, late) = (queued, []);
        var who = writers.Count == 1 ? writers[0].Label : $"{writers.Count} threads are";
        var verb = writers.Count == 1 ? " is" : "";
        string summary;
        if (upgrader is not null)
        {
            summary = count <= 0
                ? $"{upgrader.Label} is waiting to upgrade to the write lock on {l.DisplayName} (a ReaderWriterLockSlim), but no other thread holds it for reading any more: " +
                  "the dump most likely caught the moment the last reader left."
                : $"{upgrader.Label} is waiting to upgrade to the write lock on {l.DisplayName} (a ReaderWriterLockSlim) while " +
                  $"{Describe.Plural(count, "other thread holds", "other threads hold")} it for reading{named}. " +
                  (readers.Count == 0 ? unknown : $"The upgrade goes ahead as soon as {(count == 1 ? "that reader leaves" : "they leave")}.");
        }
        else
        {
            summary = count <= 0
                ? $"{who}{verb} waiting for {l.DisplayName} (a ReaderWriterLockSlim), but no thread holds it any more: the dump most likely caught the moment it was handed over."
                : $"{who}{verb} waiting for {l.DisplayName} (a ReaderWriterLockSlim), which {Describe.Plural(count, "thread holds", "threads hold")} for reading{named}." +
                  (readers.Count == 0 ? $" {unknown}" : "");
        }

        var waitSpots = waiters
            .Select(w => Describe.Spot(FrameClassifier.BestUserFrame(snapshot.FindThread(w)!.Frames)))
            .GroupBy(s => s)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ({g.Count()} threads)");
        var details = new List<string> { $"Waiting at: {Describe.JoinAnd(waitSpots)}." };
        details.AddRange(readers.Select(r => $"{r.Label} holds the read lock and is {Describe.Activity(r, withMethod: true)}."));
        if (upgrader is not null && queued.Count > 0)
            details.Add($"{Describe.JoinAnd(queued.Select(q => q.Label))} {(queued.Count == 1 ? "is" : "are")} queued behind the upgradeable read lock that {upgrader.Label} holds.");
        if (late.Count > 0)
            details.Add($"{Describe.JoinAnd(late.Select(q => q.Label))} {(late.Count == 1 ? "is" : "are")} queued behind the waiting {(writers.Count == 1 ? "writer" : "writers")}: a reader or upgrader that comes after a waiting writer waits for it.");

        var readerSite = readers.Select(r => FrameClassifier.BestUserFrame(r.Frames)).FirstOrDefault(f => f?.Source is not null);
        return new Finding
        {
            Id = "lock-contention",
            Severity = count <= 0 ? Severity.Info : Severity.Warning,
            Title = upgrader is not null ? "Upgrade to a write lock waiting for readers" : "Writer waiting behind readers",
            Summary = summary,
            Details = details,
            Advice = upgrader is not null
                ? "An upgradeable read lock can only become a write lock once every reader has left. Keep the work inside read locks short " +
                  "(don't sleep, block or wait for other threads while holding one), or take the write lock directly when you know you'll write."
                : "A writer has to wait until every reader has left, and readers that come later queue behind it. Keep the work inside read locks short: " +
                  "don't sleep, block or wait for other threads while holding one.",
            ThreadIds = [.. upgrader is null ? [] : new[] { upgrader.ManagedId }, .. queued.Select(q => q.ManagedId), .. readers.Select(r => r.ManagedId)],
            Location = readerSite?.Source,
        };
    }

    private static bool IsTimerCallback(ThreadSnapshot t) =>
        t.Frames.Any(f => f.TypeName.StartsWith("System.Threading.TimerQueueTimer", StringComparison.Ordinal) && f.MethodName is "Fire" or "CallCallback");
}
