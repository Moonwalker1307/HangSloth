using HangSloth.Analysis;

namespace HangSloth.Tests;

/// The complete set of findings a report may have, written as one line such as
/// "critical wait-cycle, warning lock-contention?": every finding id with its severity, in any order. A "?" marks
/// a finding that may or may not be there (one that depends on timing or on the number of cores); every other one
/// must be there, and nothing else may be, not even at Info. "" means no finding at all.
internal static class Verdict
{
    public static void Expect(AnalysisReport report, string expected, string? scenario = null)
    {
        if (Matches(report, expected)) return;
        var text = string.Join("\n", report.Findings.Select(f => $"[{Name(f.Severity)}] {f.Id}: {f.Summary}"));
        Assert.Fail($"{(scenario is null ? "" : $"'{scenario}': ")}expected exactly \"{expected}\", got \"{Of(report)}\"\n{text}\n{Stacks(report)}");
    }

    /// The top of every stack that runs the app's code, for the message of a failed check. A run on another
    /// operating system (CI's Windows job) can't be looked at afterwards: its frames are what explains the verdict.
    public static string Stacks(AnalysisReport report, int threads = 12, int frames = 10)
    {
        var inApp = report.Snapshot.Threads.Where(t => t.Frames.Any(FrameClassifier.IsUserFrame)).ToList();
        var lines = inApp.Take(threads).Select(t => $"  {t.Label}: {string.Join(" < ", t.Frames.Take(frames).Select(f => f.ToString()))}");
        return $"Stacks of the {inApp.Count} threads in the app's code{(inApp.Count > threads ? $" (the first {threads})" : "")}:\n{string.Join("\n", lines)}";
    }

    public static bool Matches(AnalysisReport report, string expected)
    {
        var entries = Parse(expected);
        var found = report.Findings.Select(f => $"{Name(f.Severity)} {f.Id}").ToHashSet();
        return entries.Where(e => !e.Optional).All(e => found.Contains(e.Finding))
               && found.All(f => entries.Any(e => e.Finding == f));
    }

    /// The verdict of a report in the same notation, for messages.
    public static string Of(AnalysisReport report) =>
        string.Join(", ", report.Findings.Select(f => $"{Name(f.Severity)} {f.Id}").Distinct());

    /// The ids that must be there at Warning or Critical, in the order written.
    public static IReadOnlyList<string> RequiredLoudIds(string expected) =>
        Parse(expected).Where(e => !e.Optional && !e.Finding.StartsWith("info ", StringComparison.Ordinal)).Select(e => e.Finding.Split(' ')[1]).ToList();

    private static List<(string Finding, bool Optional)> Parse(string expected)
    {
        var entries = new List<(string, bool)>();
        foreach (var raw in expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var optional = raw.EndsWith('?');
            var parts = raw.TrimEnd('?').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] is not ("info" or "warning" or "critical"))
                throw new ArgumentException($"'{raw}' isn't \"<info|warning|critical> <finding id>\".", nameof(expected));
            entries.Add(($"{parts[0]} {parts[1]}", optional));
        }
        return entries;
    }

    private static string Name(Severity severity) => severity.ToString().ToLowerInvariant();
}
