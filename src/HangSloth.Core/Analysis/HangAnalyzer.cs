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
            new ThreadPoolStarvationDetector(),
            new LockContentionDetector(),
            new BlockedOnTaskDetector(),
        ];
    }

    public AnalysisReport Analyze(ProcessSnapshot snapshot)
    {
        var claimed = new HashSet<int>();
        var findings = new List<Finding>();
        foreach (var detector in _detectors)
        {
            var found = detector.Detect(snapshot, claimed).ToList();
            findings.AddRange(found);
            foreach (var f in found.Where(f => f.Severity >= Severity.Warning))
                claimed.UnionWith(f.ThreadIds);
        }
        return new AnalysisReport(snapshot, findings.OrderByDescending(f => f.Severity).ToList());
    }
}
