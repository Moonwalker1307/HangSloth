using HangSloth.Model;

namespace HangSloth.Analysis;

/// A thread waiting in Monitor.Wait, called straight from app code, that nobody is going to pulse:
/// the classic lost wakeup, where Pulse came before Wait, or the condition was never re-checked.
/// An idle consumer in Monitor.Wait is normal, so this only speaks up when another thread is waiting for
/// this one, or when work is already queued in the collection the waiting code checks. When the dump can't
/// say which collection that is, a non-empty one next to the object is only mentioned, as a possibility.
public sealed class MonitorWaitDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var edges = WaitGraph.Build(snapshot);
        foreach (var t in snapshot.Threads)
        {
            if (t.WaitingForPulse is not { } wait || claimed.Contains(t.ManagedId)) continue;

            var dependents = WaitersOn(t.ManagedId, edges).Where(id => !claimed.Contains(id)).Select(id => snapshot.FindThread(id)!).ToList();
            var pending = wait.PendingWork.Count > 0;
            var possible = !pending && wait.PossibleWork.Count > 0;
            if (!pending && !possible && dependents.Count == 0) continue;

            var except = new HashSet<int>(dependents.Select(d => d.ManagedId)) { t.ManagedId };
            var running = SemaphoreFacts.AppCodeRunningOutside(snapshot, except);
            var site = FrameClassifier.BestUserFrame(t.Frames);

            var details = new List<string>
            {
                $"{t.Label} is in Monitor.Wait({wait.DisplayName}) in {Describe.Where(site)}, waiting for another thread to call Monitor.Pulse({wait.DisplayName}).",
            };
            if (pending)
                details.Add($"{Describe.JoinAnd(wait.PendingWork)} {(wait.PendingWork.Count == 1 ? "holds" : "hold")} work already, so the Pulse for it was most likely sent before {t.Label} started waiting, and lost.");
            if (possible)
                details.Add($"{Describe.JoinAnd(wait.PossibleWork)} {(wait.PossibleWork.Count == 1 ? "isn't" : "aren't")} empty. If that is the work {t.Label} waits for, " +
                            "the Pulse for it was lost; the dump doesn't show which collection the waiting code checks.");
            foreach (var d in dependents)
                details.Add($"{d.Label} is waiting for {t.Label} ({DescribeEdge(edges[d.ManagedId])}) in {Describe.Where(FrameClassifier.BestUserFrame(d.Frames))}.");
            details.Add(running
                ? "Other app code is still running and may yet pulse it; if this persists across two dumps, the wakeup was lost."
                : "No other thread is running app code that could pulse it.");

            // Work next to the object that may be unrelated is a hint, not a hang, unless someone waits for this thread.
            var hintOnly = possible && dependents.Count == 0;
            yield return new Finding
            {
                Id = "monitor-wait",
                Severity = hintOnly ? Severity.Info : running ? Severity.Warning : pending ? Severity.Critical : Severity.Warning,
                Title = pending ? "Lost wakeup: waiting in Monitor.Wait while work is already queued"
                    : hintOnly ? "Possible lost wakeup: waiting in Monitor.Wait next to a collection that holds items"
                    : "Thread waiting for a Monitor.Pulse that may never come",
                Summary = $"{t.Label} is waiting in Monitor.Wait on {wait.DisplayName}" +
                          (pending ? $" although {Describe.JoinAnd(wait.PendingWork)} {(wait.PendingWork.Count == 1 ? "isn't" : "aren't")} empty" : "") +
                          (hintOnly ? $", and {Describe.JoinAnd(wait.PossibleWork)} {(wait.PossibleWork.Count == 1 ? "isn't" : "aren't")} empty: if that is its work, a Pulse was lost" : "") +
                          (running || hintOnly ? "" : ", and no other thread is running code that could pulse it") +
                          (dependents.Count > 0 ? $". {Describe.JoinAnd(dependents.Select(d => d.Label))} {(dependents.Count == 1 ? "is" : "are")} waiting for it to finish" : "") +
                          "." + (site?.Source is null ? "" : $" Start at {site.Source}."),
                Details = details,
                Advice = "Monitor.Pulse only wakes a thread that is already waiting; a Pulse sent earlier is lost. Always wait in a loop on the condition " +
                         "(while (queue.Count == 0) Monitor.Wait(sync);) and pulse whenever the condition changes, not only when someone seems to be waiting. " +
                         "Simpler still: use a BlockingCollection, a Channel or a SemaphoreSlim instead of Wait/Pulse.",
                ThreadIds = [t.ManagedId, .. dependents.Select(d => d.ManagedId)],
                Location = site?.Source,
            };
        }
    }

    /// Threads waiting for this one, directly or through others (a Join on a thread that joins it, and so on).
    private static List<int> WaitersOn(int thread, Dictionary<int, WaitEdge> edges)
    {
        var result = new List<int>();
        var frontier = new Queue<int>([thread]);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var e in edges.Values.Where(e => e.To == current && e.From != thread && !result.Contains(e.From)))
            {
                result.Add(e.From);
                frontier.Enqueue(e.From);
            }
        }
        return result;
    }

    private static string DescribeEdge(WaitEdge e) => e.Kind switch
    {
        WaitKind.Join => "Thread.Join",
        WaitKind.Task => $"blocked on {Describe.Task(e.Task!)}",
        WaitKind.Lock => $"for {Describe.Wanted(e.Lock!)}",
        _ => "indirectly",
    };
}
