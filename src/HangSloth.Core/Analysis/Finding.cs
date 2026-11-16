using HangSloth.Model;

namespace HangSloth.Analysis;

public enum Severity { Info, Warning, Critical }

public sealed record Finding
{
    /// Stable id for the pattern, e.g. "sync-over-async-deadlock".
    public required string Id { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }

    /// One or two plain-language sentences: what is wrong and where.
    public required string Summary { get; init; }

    /// Supporting facts, each a full sentence.
    public IReadOnlyList<string> Details { get; init; } = [];

    /// What to change.
    public string? Advice { get; init; }

    /// The threads this finding is about, most important first.
    public IReadOnlyList<int> ThreadIds { get; init; } = [];

    /// The threads this finding explains, when that is fewer than the ones it is about: later detectors still look at
    /// the others. A lock contention names the owner, but when the owner is blocked on a task, the finding about that
    /// task says what it waits for. Null: every thread in ThreadIds.
    internal IReadOnlyList<int>? ExplainedThreadIds { get; init; }

    /// The line of the app's code to look at first.
    public SourceLocation? Location { get; init; }
}

public sealed record AnalysisReport(ProcessSnapshot Snapshot, IReadOnlyList<Finding> Findings)
{
    /// Names of detectors that threw; their findings are missing, so "nothing found" cannot be trusted.
    public IReadOnlyList<string> FailedDetectors { get; init; } = [];
}

public interface IDetector
{
    /// <param name="claimed">Threads already explained by an earlier, more specific detector.</param>
    IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed);
}
