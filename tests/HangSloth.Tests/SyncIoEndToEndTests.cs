using HangSloth.Analysis;

namespace HangSloth.Tests;

/// HangApp's thread pool workers blocked in a synchronous socket read that never gets any data.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class SyncIoEndToEndTests
{
    [Fact]
    public void Thread_pool_starvation_by_synchronous_socket_reads()
    {
        var report = SampleApp.Analyze("sync-io-starvation", until: r => r.Findings.Any(x => x.Id == "threadpool-starvation"));
        var f = Assert.Single(report.Findings);
        Assert.Equal("threadpool-starvation", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.StartsWith("Thread pool starvation: 8 of 8 thread pool threads are blocked (all in a synchronous socket read (Socket.Receive) at SyncIoScenarios.cs:36)", f.Summary);
        Assert.Contains("8 threads: a synchronous socket read (Socket.Receive) at SyncIoStarvationScenario.MailFeed.ReadGreeting (SyncIoScenarios.cs:36).", f.Details);
        Assert.Equal("Don't do synchronous I/O on thread pool threads: await Socket.ReceiveAsync instead of calling Socket.Receive in SyncIoScenarios.cs:36. Raising ThreadPool.SetMinThreads only hides the problem.", f.Advice);
    }
}
