using HangSloth.Analysis;

namespace HangSloth.Tests;

/// HangApp's pool modes: parked pool threads with nothing queued, a pool stuck behind one lock, and a long backlog.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class StarvationAccuracyEndToEndTests
{
    [Fact]
    public void Pool_threads_parked_in_long_lived_loops_are_not_starvation()
    {
        var report = SampleApp.Analyze("parked-pool");
        Assert.Equal(0, report.Snapshot.ThreadPool?.QueuedWorkItems);
        var loud = report.Findings.Where(f => f.Severity >= Severity.Warning).ToList();
        Assert.True(loud.Count == 0, string.Join("\n", loud.Select(f => $"{f.Id}: {f.Summary}")));

        // The pollers and the watcher are noted; the two consumers waiting for mail are idle and left out.
        var parked = Assert.Single(report.Findings, f => f.Id == "threadpool-parked");
        Assert.Equal(Severity.Info, parked.Severity);
        Assert.Matches(@"^14 of 1[4-8] thread pool threads are parked in blocking calls \(13 of them in Thread\.Sleep at PoolScenarios\.cs:36\), and nothing is waiting in the queue\.$", parked.Summary);
    }

    [Fact]
    public void Starvation_behind_one_lock_names_the_lock_and_its_holder()
    {
        var report = SampleApp.Analyze("pool-lock-convoy");
        // 8 requests in the global queue and 16 audit tasks in the local queues of the blocked threads.
        Assert.Equal(24, report.Snapshot.ThreadPool?.QueuedWorkItems);

        var f = report.Findings[0];
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in lock (Monitor.Enter) at PoolScenarios.cs:90), and 24 work items are waiting in the queue.", f.Summary);
        Assert.Contains(f.Details, d => System.Text.RegularExpressions.Regex.IsMatch(d,
            @"^All of them are waiting for the lock on PoolLockConvoyScenario\.Catalog\._sync, held by Thread \d+ \(""catalog-rebuild""\), which is sleeping \(Thread\.Sleep\) in PoolScenarios\.cs:77 while holding it\.$"));
        Assert.Contains("as Thread", f.Advice);
        Assert.Contains("(\"catalog-rebuild\") does in PoolScenarios.cs:77", f.Advice);

        var contention = Assert.Single(report.Findings, x => x.Id == "lock-contention");
        Assert.Equal(Severity.Warning, contention.Severity);
        Assert.Matches(@"^8 threads are stuck waiting for the lock on PoolLockConvoyScenario\.Catalog\._sync, held by Thread \d+ \(""catalog-rebuild""\)", contention.Summary);
    }

    [Fact]
    public void Starvation_counts_every_queued_work_item_and_names_the_call_the_app_made()
    {
        var report = SampleApp.Analyze("pool-backlog");
        Assert.Equal(992, report.Snapshot.ThreadPool?.QueuedWorkItems);

        var f = Assert.Single(report.Findings);
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.Equal("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in ManualResetEventSlim.Wait at PoolScenarios.cs:121), and 992 work items are waiting in the queue.", f.Summary);
        Assert.Contains("8 threads: ManualResetEventSlim.Wait at PoolBacklogScenario.PriceFeed.Publish (PoolScenarios.cs:121).", f.Details);
    }
}
