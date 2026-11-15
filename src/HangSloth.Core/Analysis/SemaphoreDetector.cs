using HangSloth.Model;

namespace HangSloth.Analysis;

/// Threads blocked in SemaphoreSlim.Wait. A SemaphoreSlim has no owner, so a dump can't say who took
/// it; but two "mutex" semaphores (max 1) that are both taken, each with a thread waiting, while no other
/// app code is running to release them, is almost always two threads taking them in opposite order.
public sealed class SemaphoreDeadlockDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var taken = snapshot.Semaphores
            .Where(s => s.CurrentCount == 0 && s.MaxCount == 1)
            .Select(s => (Semaphore: s, Waiters: s.WaiterThreadIds.Where(id => !claimed.Contains(id)).ToList()))
            .Where(x => x.Waiters.Count > 0)
            .ToList();
        if (taken.Count < 2) yield break;

        var waiters = taken.SelectMany(x => x.Waiters).Distinct().ToList();
        var anyoneElseRunning = SemaphoreFacts.AppCodeRunningOutside(snapshot, waiters);
        var details = taken
            .SelectMany(x => x.Waiters.Select(id => (Thread: snapshot.FindThread(id)!, x.Semaphore)))
            .Select(x => $"{x.Thread.Label} is waiting in {Describe.Where(FrameClassifier.BestUserFrame(x.Thread.Frames))} for {x.Semaphore.DisplayName} (0 of 1 free).")
            .ToList();
        details.Add(anyoneElseRunning
            ? "Some other app code is still running, so one of them may yet release a semaphore; if this persists across two dumps, it's a deadlock."
            : "No other thread is running app code that could release them.");

        var names = taken.Select(x => x.Semaphore.DisplayName).ToList();
        var site = FrameClassifier.BestUserFrame(snapshot.FindThread(waiters[0])!.Frames);
        yield return new Finding
        {
            Id = "semaphore-deadlock",
            Severity = anyoneElseRunning ? Severity.Warning : Severity.Critical,
            Title = "Deadlock: SemaphoreSlim locks taken in opposite order",
            Summary = $"{Describe.JoinAnd(waiters.Select(id => snapshot.FindThread(id)!.Label))} are each waiting for a SemaphoreSlim(1, 1) that is already taken " +
                      $"({Describe.JoinAnd(names)}). A semaphore has no owner, so nothing reports this, but each of them most likely holds the one another is waiting for." +
                      (site?.Source is null ? "" : $" Start at {site.Source}."),
            Details = details,
            Advice = $"Take {Describe.JoinAnd(names)} in one consistent order everywhere, or replace them with a single semaphore. " +
                     "Unlike lock, SemaphoreSlim isn't tied to a thread, so neither the runtime nor a debugger will point at the holder.",
            ThreadIds = waiters,
            Location = site?.Source,
        };
    }
}

/// A single thread synchronously waiting on a SemaphoreSlim that has no free slots.
public sealed class SemaphoreWaitDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var s in snapshot.Semaphores.Where(s => s.CurrentCount == 0))
        {
            var waiters = s.WaiterThreadIds.Where(id => !claimed.Contains(id)).ToList();
            if (waiters.Count == 0) continue;
            var running = SemaphoreFacts.AppCodeRunningOutside(snapshot, waiters);
            var first = snapshot.FindThread(waiters[0])!;
            var site = FrameClassifier.BestUserFrame(first.Frames);
            var who = waiters.Count == 1 ? $"{first.Label} is" : $"{waiters.Count} threads are";
            yield return new Finding
            {
                Id = "semaphore-wait",
                Severity = running ? Severity.Info : Severity.Warning,
                Title = "Waiting for a SemaphoreSlim nobody releases",
                Summary = $"{who} waiting in {Describe.Where(site)} for {s.DisplayName}, a SemaphoreSlim with {SemaphoreFacts.Slots(s)}." +
                          (running ? "" : " No other thread is running app code that could release it."),
                Details = [SemaphoreFacts.NoOwner(s)],
                Advice = $"Find the code that takes {s.DisplayName} and make sure it calls Release() on every path (try/finally), and never waits for this thread while holding it.",
                ThreadIds = waiters,
                Location = site?.Source,
            };
        }
    }
}

internal static class SemaphoreFacts
{
    public static string Slots(SemaphoreInfo s) =>
        s.MaxCount == int.MaxValue ? $"{s.CurrentCount} slots free" : $"{s.CurrentCount} of {s.MaxCount} slots free";

    public static string NoOwner(SemaphoreInfo s) =>
        $"A SemaphoreSlim has no owner: whoever took the slot of {s.DisplayName} has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will.";

    /// Is any thread other than these running (not blocked, not idle) the app's own code?
    public static bool AppCodeRunningOutside(ProcessSnapshot snapshot, IReadOnlyCollection<int> except) =>
        snapshot.Threads.Any(t => !except.Contains(t.ManagedId)
                                  && FrameClassifier.FirstUserFrame(t.Frames) is not null
                                  && FrameClassifier.Classify(t.Frames) == BlockingKind.None
                                  && t.BlockedOnTask is null);
}
