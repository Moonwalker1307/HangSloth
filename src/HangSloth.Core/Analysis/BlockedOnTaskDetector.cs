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
            // The generic host waiting for a hosted service's StartAsync: the app never gets to run, however
            // healthy the host's own wait looks.
            var hostStarting = chain.Any(IsHostStarting);
            // A test runner or host waiting for its own work (testhost's Main, host.RunAsync().GetAwaiter().GetResult()):
            // that's how a healthy process looks.
            if (!hostStarting && (FrameClassifier.Classify(thread.Frames) == BlockingKind.Idle || chain.Any(FrameClassifier.IsHostRunTask)))
                continue;

            var leaf = chain[^1];
            var asyncMain = Describe.IsAsyncMainWait(thread.Frames);
            if (asyncMain && !IsShownBehindAsyncMain(chain, hostStarting))
                continue;
            // An app whose async Main runs until it is told to stop may wait for its stop signal further down its own
            // code too. The dump can't tell that from a hang, so the finding keeps its severity and says so.
            var mayBeStopSignal = asyncMain && !hostStarting;

            var call = HostRunCall(thread.Frames) ?? FrameClassifier.DescribeTaskWait(thread.Frames);
            var site = FrameClassifier.BestUserFrame(thread.Frames) ?? FrameClassifier.WaitingFrame(thread.Frames);
            // For async Main, the story starts where Main is paused, not at the compiler's wait for it.
            var path = asyncMain ? chain.Skip(1).ToList() : chain;
            var opening = asyncMain
                ? $"{thread.Label} is waiting for the app's async Main, which " + (chain[0].AwaitLocation is { } paused ? $"is paused at {paused}, awaiting " : "awaits ")
                : $"{thread.Label} is blocked in {call} at {Describe.Spot(site)}, waiting for ";
            var location = asyncMain ? chain[0].AwaitLocation : site?.Source;

            if (leaf.Kind == TaskKind.SemaphoreWait)
            {
                var semaphoreWait = SemaphoreWait(snapshot, thread, chain, opening, path, location);
                yield return mayBeStopSignal && semaphoreWait.Severity == Severity.Warning
                    ? semaphoreWait with { Details = [.. semaphoreWait.Details, StopSignalDetail] }
                    : semaphoreWait;
                continue;
            }

            var leafText = leaf.Kind switch
            {
                TaskKind.Promise => $"{leaf.Description} that nothing has completed yet",
                TaskKind.Delay => "a Task.Delay that hasn't elapsed yet",
                TaskKind.Delegate => $"{leaf.Description}, which is still running",
                TaskKind.NotStarted => $"{leaf.Description}, which was never started",
                TaskKind.Queued => $"{leaf.Description}, which is queued and hasn't started running: no thread has picked it up yet",
                _ => leaf.Description,
            };

            List<string> details = leaf.Kind switch
            {
                TaskKind.Promise => ["A TaskCompletionSource task only finishes when some code calls SetResult/SetException/SetCanceled on it. Check the code that should do that: it may have failed, been skipped, or be waiting itself."],
                TaskKind.NotStarted => ["A task created with new Task(...) runs only once something calls Start() on it. Until then, every wait for it waits forever."],
                _ => [],
            };
            if (hostStarting)
                details.Add(HostStartingDetail);
            if (mayBeStopSignal && leaf.Kind == TaskKind.Promise)
                details.Add(StopSignalDetail);

            yield return new Finding
            {
                Id = "blocked-on-task",
                // A host that hasn't finished starting isn't serving, whatever StartAsync waits for.
                Severity = leaf.Kind is TaskKind.NotStarted or TaskKind.Promise || hostStarting ? Severity.Warning : Severity.Info,
                Title = leaf.Kind switch
                {
                    TaskKind.Promise => "Waiting for a task nobody completes",
                    TaskKind.NotStarted => "Waiting for a task that was never started",
                    _ when hostStarting => "The host hasn't finished starting",
                    _ => "Thread blocked on a task",
                },
                Summary = $"{opening}{PathText(path, leafText)}.",
                Details = details,
                Advice = leaf.Kind switch
                {
                    TaskKind.Promise => "Make sure every path completes the TaskCompletionSource (including errors and cancellation), and consider a timeout.",
                    TaskKind.NotStarted => "Call Start() on the task, or create it with Task.Run, which starts it right away.",
                    _ when hostStarting => HostStartingAdvice,
                    _ => $"Prefer await over {call} so the thread isn't held while waiting.",
                },
                ThreadIds = [thread.ManagedId],
                Location = location,
            };
        }
    }

    /// async Main (and top-level await) keeps the main thread waiting for Main's task for as long as the app runs,
    /// so that wait is no finding by itself. What Main awaits itself, without going through the app's own code, is how
    /// such an app waits to be stopped: a shutdown TaskCompletionSource, a delay, its workers. Shown: a task nobody can
    /// start, a host that never finishes starting, and a task or SemaphoreSlim nobody completes further down the app's code.
    private static bool IsShownBehindAsyncMain(List<TaskInfo> chain, bool hostStarting)
    {
        if (chain.Count < 2) return false;
        if (hostStarting || chain[^1].Kind == TaskKind.NotStarted) return true;
        return chain[^1].Kind is TaskKind.Promise or TaskKind.SemaphoreWait && chain.Skip(1).Any(Describe.IsAppAsyncMethod);
    }

    private const string StopSignalDetail =
        "If this is how the app waits to be told to stop, waiting here for as long as it runs is normal. If the app should have moved on by now, this is where it is stuck.";

    /// The generic host's StartAsync, which starts the hosted services.
    private static bool IsHostStarting(TaskInfo task) =>
        task.TypeName.Contains("Microsoft.Extensions.Hosting.Internal.Host+<StartAsync>", StringComparison.Ordinal);

    private const string HostStartingDetail =
        "The generic host is still starting: Host.StartAsync waits for every hosted service's StartAsync to return, and until they all have, the app has not finished starting.";

    private const string HostStartingAdvice =
        "Don't wait for long-running work in StartAsync: start it in the background (BackgroundService.ExecuteAsync), or give the wait a timeout.";

    /// host.Run() and app.Run() block on the host's RunAsync task: name that call rather than the GetResult inside it.
    private static string? HostRunCall(IReadOnlyList<StackFrameInfo> frames) =>
        FrameClassifier.FrameworkPrefix(frames).LastOrDefault(FrameClassifier.IsHostRunFrame) is { } run ? $".{run.MethodName}()" : null;

    /// "X (paused at A), which awaits Y (paused at B), which awaits {leafText}".
    private static string PathText(List<TaskInfo> chain, string leafText) =>
        chain.Count == 1
            ? leafText
            : $"{Describe.Task(chain[0])}" + string.Concat(chain.Skip(1).SkipLast(1).Select((t, i) => $", {Describe.Link(chain[i])} {Describe.Task(t)}")) + $", {Describe.Link(chain[^2])} {leafText}";

    private static string Path(List<TaskInfo> chain, string leafText) =>
        chain.Count == 1
            ? leafText
            : $"{Describe.Task(chain[0])}" + string.Concat(chain.Skip(1).SkipLast(1).Select((t, i) => $", {Describe.Link(chain[i])} {Describe.Task(t)}")) + $", which {leafText}";

    /// An async chain parked in SemaphoreSlim.WaitAsync, often because something further up the same
    /// chain already holds that semaphore (SemaphoreSlim isn't reentrant).
    private static Finding SemaphoreWait(ProcessSnapshot snapshot, ThreadSnapshot thread, List<TaskInfo> chain, string opening, List<TaskInfo> path, SourceLocation? location)
    {
        var semaphore = chain[^1].Semaphore is { } a ? snapshot.FindSemaphore(a) : null;
        var name = semaphore?.DisplayName ?? "a SemaphoreSlim";
        var slots = semaphore is null ? "" : $", {SemaphoreFacts.Slots(semaphore)}";
        var waiting = chain.Count >= 2 ? chain[^2] : null;
        var leafText = path.Count == 1 ? $"SemaphoreSlim.WaitAsync() on {name}{slots}" : $"is waiting to enter {name} (SemaphoreSlim.WaitAsync(){slots})";

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
            Summary = $"{opening}{Path(path, leafText)}.",
            Details = details,
            Advice = sameOwner.Count > 0
                ? $"Don't take {name} twice on one call path: give {waiting!.Description} a variant that expects the semaphore to be held already, or release it before calling. " +
                  "A WaitAsync timeout turns this hang into an error you can see."
                : $"Find the code that took {name} and make sure it calls Release() on every path (try/finally), and that it isn't waiting for this chain while holding it.",
            ThreadIds = [thread.ManagedId],
            Location = location,
        };
    }
}
