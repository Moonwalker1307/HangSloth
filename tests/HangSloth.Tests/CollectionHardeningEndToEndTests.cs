using System.Text.Json;
using System.Text.RegularExpressions;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;

namespace HangSloth.Tests;

/// Collection on real dumps: one unreadable thread doesn't cost the snapshot, and the heap is walked at most once.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class CollectionHardeningEndToEndTests
{
    [Fact]
    public void A_thread_whose_name_cannot_be_read_is_kept_and_the_deadlock_is_still_found()
    {
        WithDump("deadlock", r => r.Findings.Any(f => f.Id == "lock-deadlock"), dump =>
        {
            var clean = DumpLoader.LoadDump(dump);
            var victim = clean.Threads.Single(t => t.Name == "reserve-worker");

            var report = new HangAnalyzer().Analyze(LoadWithFault(dump, victim.ManagedId, "name"));

            Assert.Equal(clean.Threads.Select(t => t.ManagedId), report.Snapshot.Threads.Select(t => t.ManagedId));
            var kept = report.Snapshot.FindThread(victim.ManagedId)!;
            Assert.Null(kept.Name);
            Assert.Equal(Json(victim with { Name = null }), Json(kept));
            Assert.Contains(SnapshotBuilder.IncompleteThreadsWarning([victim.ManagedId]), report.Snapshot.Warnings);

            var f = Assert.Single(report.Findings, x => x.Severity == Severity.Critical);
            Assert.Equal("lock-deadlock", f.Id);
            Assert.Contains("Thread N and Thread N (\"billing-worker\") are each waiting for a lock the other one holds", Stable(f.Summary));
        });
    }

    [Fact]
    public void A_thread_whose_waits_cannot_be_read_keeps_its_stack_and_every_other_thread_is_unchanged()
    {
        WithDump("deadlock", r => r.Findings.Any(f => f.Id == "lock-deadlock"), dump =>
        {
            var clean = DumpLoader.LoadDump(dump);
            var victim = clean.Threads.First(t => t.Name is not ("reserve-worker" or "billing-worker") && t.Frames.Count > 0);

            var faulted = LoadWithFault(dump, victim.ManagedId, "waits");

            var kept = faulted.FindThread(victim.ManagedId)!;
            Assert.Equal(victim.Name, kept.Name);
            Assert.Equal(Json(victim.Frames), Json(kept.Frames));
            Assert.Equal(clean.Threads.Where(t => t.ManagedId != victim.ManagedId).Select(Json),
                         faulted.Threads.Where(t => t.ManagedId != victim.ManagedId).Select(Json));
            Assert.Equal(SnapshotBuilder.IncompleteThreadsWarning([victim.ManagedId]), Assert.Single(faulted.Warnings.Except(clean.Warnings)));
            Assert.Equal(new HangAnalyzer().Analyze(clean).Findings.Select(f => f.Summary),
                         new HangAnalyzer().Analyze(faulted).Findings.Select(f => f.Summary));
        });
    }

    [Fact]
    public void A_detail_that_cannot_be_read_costs_neither_the_wait_nor_the_name()
    {
        WithDump("deadlock", r => r.Findings.Any(f => f.Id == "lock-deadlock"), dump =>
        {
            var clean = DumpLoader.LoadDump(dump);
            var victim = clean.Threads.Single(t => t.Name == "reserve-worker");

            // The running task is read before the lock the thread waits for, and the context before the name.
            foreach (var step in new[] { "running task", "context" })
            {
                var report = new HangAnalyzer().Analyze(LoadWithFault(dump, victim.ManagedId, step));

                Assert.Equal(Json(victim), Json(report.Snapshot.FindThread(victim.ManagedId)!));
                Assert.Contains(SnapshotBuilder.IncompleteThreadsWarning([victim.ManagedId]), report.Snapshot.Warnings);
                var f = Assert.Single(report.Findings, x => x.Severity == Severity.Critical);
                Assert.Equal("lock-deadlock", f.Id);
                Assert.Contains("Thread N (\"reserve-worker\") and Thread N (\"billing-worker\") are each waiting for a lock the other one holds", Stable(f.Summary));
            }
        });
    }

    [Fact]
    public void A_lock_deadlock_needs_no_heap_walk()
    {
        WithDump("deadlock", r => r.Findings.Any(f => f.Id == "lock-deadlock"), dump =>
        {
            SnapshotBuilder.HeapWalks = 0;
            var snapshot = DumpLoader.LoadDump(dump);
            Assert.Equal(0, SnapshotBuilder.HeapWalks);
            // Thread names come from the handle table now.
            Assert.Contains(snapshot.Threads, t => t.Name == "reserve-worker");
            Assert.Contains(snapshot.Threads, t => t.Name == "billing-worker");
            Assert.True(snapshot.HeapAvailable);
        });
    }

    [Fact]
    public void A_wrapper_and_a_semaphore_in_one_chain_take_one_heap_walk()
    {
        WithDump("semaphore-token", r => r.Findings.Any(f => f.Id == "semaphore-wait"), dump =>
        {
            SnapshotBuilder.HeapWalks = 0;
            var report = new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump));
            Assert.Equal(1, SnapshotBuilder.HeapWalks);
            // The wrapper's source task and the semaphore's name both come from that one walk.
            Assert.Contains("which is waiting to enter SemaphoreTokenScenario.LabelPrinter._device (", report.Findings.Single(f => f.Id == "semaphore-wait").Summary);
        });
    }

    private static ProcessSnapshot LoadWithFault(string dump, int threadId, string step)
    {
        SnapshotBuilder.ThreadReadFault = (id, at) =>
        {
            if (id == threadId && at == step) throw new ArgumentOutOfRangeException("minimumLength", "must be a non-negative value");
        };
        try
        {
            return DumpLoader.LoadDump(dump);
        }
        finally
        {
            SnapshotBuilder.ThreadReadFault = null;
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static string Stable(string text) => Regex.Replace(text, @"Thread \d+", "Thread N");

    /// Starts a HangApp mode, dumps it (again, up to 3 times, until <paramref name="ready"/> holds) and runs <paramref name="check"/> on the dump.
    private static void WithDump(string mode, Func<AnalysisReport, bool> ready, Action<string> check)
    {
        var app = SampleApp.Build("HangApp");
        var dump = TestRun.NewDumpPath(mode);
        using var sample = Sample.Start(app, [mode]);
        try
        {
            sample.WaitUntilReady(TimeSpan.FromSeconds(30));
            for (var attempt = 1; ; attempt++)
            {
                DumpLoader.CollectDump(sample.Process.Id, dump);
                if (ready(new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump))) || attempt == 3) break;
                File.Delete(dump);
                Thread.Sleep(700);
            }
            check(dump);
        }
        finally
        {
            try { File.Delete(dump); } catch { }
        }
    }
}
