using HangSloth.Model;

namespace HangSloth.Analysis;

/// A thread synchronously waiting for a task that isn't explained by a deadlock pattern:
/// say what that task is ultimately waiting for.
public sealed class BlockedOnTaskDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var thread in snapshot.Threads)
        {
            if (claimed.Contains(thread.ManagedId) || thread.BlockedOnTask is not { } waited || waited.State != TaskState.Pending)
                continue;

            var chain = Describe.ReadableChain(waited);
            // A test runner or host waiting for its own work (testhost's Main, host.RunAsync().GetAwaiter().GetResult()):
            // that's how a healthy process looks.
            if (FrameClassifier.Classify(thread.Frames) == BlockingKind.Idle || chain.Any(FrameClassifier.IsHostRunTask))
                continue;

            var leaf = chain[^1];
            var call = FrameClassifier.DescribeTaskWait(thread.Frames);
            var site = FrameClassifier.BestUserFrame(thread.Frames) ?? FrameClassifier.WaitingFrame(thread.Frames);

            if (leaf.Kind == TaskKind.SemaphoreWait)
            {
                yield return SemaphoreWait(snapshot, thread, chain, call, site);
                continue;
            }

            var leafText = leaf.Kind switch
            {
                TaskKind.Promise => $"{leaf.Description} that nothing has completed yet",
                TaskKind.Delay => "a Task.Delay that hasn't elapsed yet",
                TaskKind.Delegate => $"{leaf.Description}, which is still running",
                _ => leaf.Description,
            };

            var path = chain.Count == 1
                ? leafText
                : $"{Describe.Task(chain[0])}" + string.Concat(chain.Skip(1).SkipLast(1).Select((t, i) => $", {Describe.Link(chain[i])} {Describe.Task(t)}")) + $", {Describe.Link(chain[^2])} {leafText}";

            yield return new Finding
            {
                Id = "blocked-on-task",
                Severity = leaf.Kind == TaskKind.Promise ? Severity.Warning : Severity.Info,
                Title = leaf.Kind == TaskKind.Promise ? "Waiting for a task nobody completes" : "Thread blocked on a task",
                Summary = $"{thread.Label} is blocked in {call} at {Describe.Spot(site)}, waiting for {path}.",
                Details = leaf.Kind == TaskKind.Promise
                    ? ["A TaskCompletionSource task only finishes when some code calls SetResult/SetException/SetCanceled on it. Check the code that should do that: it may have failed, been skipped, or be waiting itself."]
                    : [],
                Advice = leaf.Kind == TaskKind.Promise
                    ? "Make sure every path completes the TaskCompletionSource (including errors and cancellation), and consider a timeout."
                    : $"Prefer await over {call} so the thread isn't held while waiting.",
                ThreadIds = [thread.ManagedId],
                Location = site?.Source,
            };
        }
    }

    private static string Path(List<TaskInfo> chain, string leafText) =>
        chain.Count == 1
            ? leafText
            : $"{Describe.Task(chain[0])}" + string.Concat(chain.Skip(1).SkipLast(1).Select((t, i) => $", {Describe.Link(chain[i])} {Describe.Task(t)}")) + $", which {leafText}";

    /// An async chain parked in SemaphoreSlim.WaitAsync, often because something further up the same
    /// chain already holds that semaphore (SemaphoreSlim isn't reentrant).
    private static Finding SemaphoreWait(ProcessSnapshot snapshot, ThreadSnapshot thread, List<TaskInfo> chain, string call, StackFrameInfo? site)
    {
        var semaphore = chain[^1].Semaphore is { } a ? snapshot.FindSemaphore(a) : null;
        var name = semaphore?.DisplayName ?? "a SemaphoreSlim";
        var slots = semaphore is null ? "" : $", {SemaphoreFacts.Slots(semaphore)}";
        var waiting = chain.Count >= 2 ? chain[^2] : null;
        var leafText = chain.Count == 1 ? $"SemaphoreSlim.WaitAsync() on {name}{slots}" : $"is waiting to enter {name} (SemaphoreSlim.WaitAsync(){slots})";

        var details = new List<string>();
        var sameOwner = semaphore?.ContainerAddress is { } owner && waiting is not null
            ? chain.SkipLast(2).Where(t => t.ThisAddress == owner).ToList()
            : [];
        if (sameOwner.Count > 0)
        {
            details.Add($"{Describe.JoinAnd(sameOwner.Select(t => t.Description))}, earlier in this same async chain, runs on the same object that owns {name}. " +
                        $"If it took {name} before calling {waiting!.Description}, the chain is waiting for itself: SemaphoreSlim isn't reentrant, so the second WaitAsync never completes.");
        }
        // A throttle (max N) where every slot is held by a call waiting for a nested slot: N nested waiters, 0 free.
        var throttle = sameOwner.Count > 0 && semaphore is { MaxCount: > 1 and < int.MaxValue, CurrentCount: 0 } t && t.AsyncWaiters >= t.MaxCount;
        if (throttle)
            details.Add($"{name} has {semaphore!.MaxCount} slots and {semaphore.AsyncWaiters} async callers waiting for one. If each of them is a nested call like this one, " +
                        $"every slot is held by a call that waits for another slot: a throttle deadlock that needs only {semaphore.MaxCount} concurrent calls.");
        if (semaphore is not null) details.Add(SemaphoreFacts.NoOwner(semaphore));

        return new Finding
        {
            Id = "semaphore-wait",
            // Paused past its own WaitAsync and waiting on the same one-slot semaphore: as good as proof.
            Severity = sameOwner.Count > 0 && (semaphore is { MaxCount: 1, CurrentCount: 0 } || throttle) ? Severity.Critical : Severity.Warning,
            Title = sameOwner.Count > 0 ? "Deadlock: async code waiting for a SemaphoreSlim it already holds" : "Waiting for a SemaphoreSlim nobody releases",
            Summary = $"{thread.Label} is blocked in {call} at {Describe.Spot(site)}, waiting for {Path(chain, leafText)}.",
            Details = details,
            Advice = sameOwner.Count > 0
                ? $"Don't take {name} twice on one call path: give {waiting!.Description} a variant that expects the semaphore to be held already, or release it before calling. " +
                  "A WaitAsync timeout turns this hang into an error you can see."
                : $"Find the code that took {name} and make sure it calls Release() on every path (try/finally), and that it isn't waiting for this chain while holding it.",
            ThreadIds = [thread.ManagedId],
            Location = site?.Source,
        };
    }
}
