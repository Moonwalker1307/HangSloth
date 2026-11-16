using HangSloth.Analysis;

namespace HangSloth.Tests;

/// Every DeadlockLab code scenario again, fully optimized from the first call (DOTNET_TieredCompilation=0), the way
/// a process looks after it has run for a while: small framework methods are inlined into the app's methods and
/// their frames are gone. Each scenario must still give what the lab declares.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class OptimizedLabEndToEndTests
{
    private static readonly Dictionary<string, string?> Optimized = new() { ["DOTNET_TieredCompilation"] = "0" };

    /// The waits whose public method is inlined in optimized code, and what shows that their target was found.
    private static readonly Dictionary<string, string[]> Mentions = new()
    {
        ["thread-join-under-lock"] = ["EmailWorker._stateLock", "(\"email-worker\") to finish (Thread.Join)"],
        ["lost-pulse"] = ["Monitor.Wait(WorkQueue._sync)", "WorkQueue._items (1 item)", "(Thread.Join)"],
        ["finalizer-wait"] = ["HandleLog.Sync", "(finalizer)", "GC.WaitForPendingFinalizers"],
        ["child-process-pipe"] = ["--spew", "standard output is redirected"],
        ["ui-invoke"] = ["UiDispatcherContext.Send", "since that SynchronizationContext belongs to Thread"],
    };

    /// The scenarios that optimized code reads differently, on purpose, with what they give there. A lock class's
    /// Enter() is inlined into the code that takes two of its locks in opposite order, so the two waits "in Enter(),
    /// which took nothing" of cold code are one possible deadlock here (SemaphoreOptimizedCodeEndToEndTests has the
    /// sentences).
    private static readonly Dictionary<string, string[]> ReadsDifferently = new()
    {
        ["semaphore-lock-class"] = ["semaphore-deadlock"],
    };

    [Theory]
    [MemberData(nameof(DeadlockLabTests.CodeScenarios), MemberType = typeof(DeadlockLabTests))]
    public void Code_scenario_in_optimized_code(string scenario)
    {
        var expected = ReadsDifferently.GetValueOrDefault(scenario) ?? Lab.Expected[scenario];
        static List<string> Found(AnalysisReport r) => r.Findings.Where(f => f.Severity >= Severity.Warning).Select(f => f.Id).Distinct().ToList();

        var report = SampleApp.Analyze(scenario, project: "DeadlockLab", until: r => expected.All(Found(r).Contains), environment: Optimized);
        var found = Found(report);
        var text = string.Join("\n", report.Findings.SelectMany(f => new[] { $"{f.Severity} {f.Id}: {f.Summary}", f.Advice ?? "" }.Concat(f.Details)));
        // A failure in CI's Windows job can't be looked at afterwards: the message carries the stacks too.
        var shown = $"{text}\n{Verdict.Stacks(report)}";
        var forbidden = report.Findings.Select(f => f.Id).Intersect(Lab.MustNotReport.GetValueOrDefault(scenario, [])).ToList();
        Assert.True(forbidden.Count == 0, $"'{scenario}' (optimized) must not report {string.Join(", ", forbidden)}\n{shown}");

        if (expected.Length == 0)
        {
            Assert.True(found.Count == 0, $"Expected no findings for '{scenario}' (optimized), got: {string.Join(", ", found)}\n{shown}");
            return;
        }
        Assert.True(expected.All(found.Contains), $"Expected {string.Join(", ", expected)} for '{scenario}' (optimized), got: {string.Join(", ", found)}\n{shown}");
        Assert.Equal(expected[0], report.Findings[0].Id);
        foreach (var phrase in Mentions.GetValueOrDefault(scenario, []))
            Assert.True(text.Contains(phrase, StringComparison.Ordinal), $"'{scenario}' (optimized): expected the report to mention {phrase}\n{shown}");
    }
}
