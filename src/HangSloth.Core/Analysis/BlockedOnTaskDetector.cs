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

            var chain = Describe.Chain(waited).ToList();
            var leaf = chain[^1];
            var call = FrameClassifier.DescribeTaskWait(thread.Frames);
            var site = FrameClassifier.BestUserFrame(thread.Frames);

            var leafText = leaf.Kind switch
            {
                TaskKind.Promise => $"{leaf.Description} that nothing has completed yet",
                TaskKind.Delay => "a Task.Delay that hasn't elapsed yet",
                TaskKind.Delegate => $"{leaf.Description}, which is still running",
                _ => leaf.Description,
            };

            var path = chain.Count == 1
                ? leafText
                : $"{Describe.Task(chain[0])}" + string.Concat(chain.Skip(1).SkipLast(1).Select(t => $", which awaits {Describe.Task(t)}")) + $", which awaits {leafText}";

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
}
