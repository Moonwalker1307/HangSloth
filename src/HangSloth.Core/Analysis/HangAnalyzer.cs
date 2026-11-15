using HangSloth.Model;

namespace HangSloth.Analysis;

/// Runs the detectors from most to least specific. Once a thread is explained by a
/// finding, later, more generic detectors don't report it again.
public sealed class HangAnalyzer
{
    private readonly IReadOnlyList<IDetector> _detectors;

    public HangAnalyzer(IEnumerable<IDetector>? detectors = null)
    {
        _detectors = detectors?.ToList() ??
        [
            new SyncOverAsyncDetector(),
            new LockDeadlockDetector(),
            new WaitCycleDetector(),
            new DatabaseDeadlockDetector(),
            new ConnectionPoolDetector(),
            new SemaphoreDeadlockDetector(),
            new BoundedQueueDeadlockDetector(),
            new MonitorWaitDetector(),
            new HttpDetector(),
            new ProcessPipeDetector(),
            new ThreadPoolStarvationDetector(),
            new DatabaseWaitDetector(),
            new LockContentionDetector(),
            new DatabaseReadDetector(),
            new SemaphoreWaitDetector(),
            new BlockedOnTaskDetector(),
        ];
    }

    public AnalysisReport Analyze(ProcessSnapshot snapshot)
    {
        var claimed = new HashSet<int>();
        var findings = new List<Finding>();
        var failed = new List<string>();
        var warnings = new List<string>();
        foreach (var detector in _detectors)
        {
            List<Finding> found;
            try
            {
                found = detector.Detect(snapshot, claimed).ToList();
            }
            catch (Exception e)
            {
                // One broken check must not lose what the others found.
                var name = detector.GetType().Name;
                failed.Add(name);
                warnings.Add($"The {name} check failed ({e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}); the other checks are not affected.");
                continue;
            }
            findings.AddRange(found);
            foreach (var f in found.Where(f => f.Severity >= Severity.Warning))
                claimed.UnionWith(f.ThreadIds);
        }
        if (warnings.Count > 0) snapshot = snapshot with { Warnings = [.. snapshot.Warnings, .. warnings] };
        return new AnalysisReport(snapshot, findings.OrderByDescending(f => f.Severity).ToList()) { FailedDetectors = failed };
    }
}
