using HangSloth.Analysis;

namespace HangSloth.Tests;

/// The end-to-end tests pin every finding of a scenario with its severity, so an extra or misjudged finding fails them.
public class VerdictTests
{
    private static AnalysisReport Report(params (Severity Severity, string Id)[] findings) =>
        new(SnapshotFactory.Process(), findings.Select(f => new Finding { Id = f.Id, Severity = f.Severity, Title = f.Id, Summary = f.Id }).ToList());

    [Fact]
    public void The_exact_set_matches_in_any_order()
    {
        var report = Report((Severity.Warning, "lock-contention"), (Severity.Critical, "wait-cycle"));
        Assert.True(Verdict.Matches(report, "critical wait-cycle, warning lock-contention"));
    }

    [Fact]
    public void An_extra_finding_does_not_match_even_at_info()
    {
        Assert.False(Verdict.Matches(Report((Severity.Critical, "wait-cycle"), (Severity.Warning, "lock-contention")), "critical wait-cycle"));
        Assert.False(Verdict.Matches(Report((Severity.Critical, "wait-cycle"), (Severity.Info, "threadpool-parked")), "critical wait-cycle"));
        Assert.False(Verdict.Matches(Report((Severity.Info, "threadpool-parked")), ""));
    }

    [Fact]
    public void Another_severity_does_not_match()
    {
        Assert.False(Verdict.Matches(Report((Severity.Warning, "lock-deadlock")), "critical lock-deadlock"));
    }

    [Fact]
    public void A_missing_finding_does_not_match_unless_it_is_optional()
    {
        var report = Report((Severity.Critical, "wait-cycle"));
        Assert.False(Verdict.Matches(report, "critical wait-cycle, warning lock-contention"));
        Assert.True(Verdict.Matches(report, "critical wait-cycle, warning lock-contention?"));
        Assert.True(Verdict.Matches(Report((Severity.Critical, "wait-cycle"), (Severity.Warning, "lock-contention")), "critical wait-cycle, warning lock-contention?"));
    }

    [Fact]
    public void Nothing_matches_only_an_empty_report()
    {
        Assert.True(Verdict.Matches(Report(), ""));
    }

    [Fact]
    public void A_mismatch_names_what_was_found()
    {
        var report = Report((Severity.Critical, "lock-deadlock"), (Severity.Warning, "lock-contention"));
        var ex = Assert.ThrowsAny<Exception>(() => Verdict.Expect(report, "critical lock-deadlock", "transfer"));
        Assert.StartsWith("'transfer': expected exactly \"critical lock-deadlock\", got \"critical lock-deadlock, warning lock-contention\"", ex.Message);
    }

    [Fact]
    public void A_mismatch_shows_the_stacks_that_run_the_apps_code()
    {
        var waiting = SnapshotFactory.Thread(4, [SnapshotFactory.Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), SnapshotFactory.App("Shop.Exporter", "Wait", "Exporter.cs", 12)], "export");
        var idle = SnapshotFactory.Thread(5, [SnapshotFactory.Fx("System.Threading.Thread", "Sleep")]);
        var report = new AnalysisReport(SnapshotFactory.Process(waiting, idle), []);
        var ex = Assert.ThrowsAny<Exception>(() => Verdict.Expect(report, "warning process-pipe-deadlock"));
        Assert.Contains("Stacks of the 1 threads in the app's code:", ex.Message);
        Assert.Contains("Thread 4 (\"export\"): System.Threading.WaitHandle.WaitOneNoCheck < Shop.Exporter.Wait (Exporter.cs:12)", ex.Message);
        Assert.DoesNotContain("Thread 5", ex.Message);
    }

    [Fact]
    public void A_misspelt_severity_is_an_error_in_the_test()
    {
        Assert.Throws<ArgumentException>(() => Verdict.Matches(Report(), "critcal lock-deadlock"));
    }

    [Fact]
    public void The_required_loud_ids_leave_out_info_and_optional_findings()
    {
        Assert.Equal(["wait-cycle", "full-queue"], Verdict.RequiredLoudIds("critical wait-cycle, warning lock-contention?, info threadpool-parked, warning full-queue"));
    }
}
