using HangSloth.Analysis;

namespace HangSloth.Tests;

/// The HangApp modes whose stacks change in optimized code (DOTNET_TieredCompilation=0): the verdict must stay
/// the one cold code gets.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class OptimizedHangAppEndToEndTests
{
    private static readonly Dictionary<string, string?> Optimized = new() { ["DOTNET_TieredCompilation"] = "0" };

    [Fact]
    public void Monitor_Wait_after_an_await_in_optimized_code_is_still_found()
    {
        // In MoveNext, Monitor.Wait is inlined and the native call goes through the runtime's marshalling stub.
        var report = SampleApp.Analyze("resumed-monitor-wait", until: r => r.Findings.Any(f => f.Id == "monitor-wait"), environment: Optimized);
        Verdict.Expect(report, "warning monitor-wait");
        var f = Assert.Single(report.Findings);
        Assert.Matches(@"^Thread \d+( \(""[^""]*""\))? is waiting in Monitor\.Wait on ResumedMonitorWaitScenario\.PrintSpooler\._sync, .*Thread \d+ is waiting for it to finish\. Start at TaskChainScenarios\.cs:200\.$", f.Summary);
    }
}
