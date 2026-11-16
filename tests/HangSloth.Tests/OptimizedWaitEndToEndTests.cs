namespace HangSloth.Tests;

/// Waits in warmed-up (optimized) code of the real HangApp sample: locks, semaphores, the callers' lines, a second
/// .Result, Parallel.ForEach and a minimal API handler that the JIT inlined into ASP.NET Core's request delegate.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class OptimizedWaitEndToEndTests
{
    [Fact]
    public void Lock_and_semaphore_waits_in_tier1_code_are_reported_at_their_own_lines()
    {
        var report = SampleApp.Analyze("tiered-lock");

        var contention = Assert.Single(report.Findings, f => f.Id == "lock-contention");
        Assert.Contains("Waiting at: OptimizedWaits.cs:123 (2 threads).", contention.Details);
        var semaphore = Assert.Single(report.Findings, f => f.Id == "semaphore-wait");
        Assert.Contains("(\"unload\") is waiting in Warehouse.Unload (OptimizedWaits.cs:136) for Warehouse._dock", semaphore.Summary);

        // The caller's frame is at its call of the waiting method, not at its first statement.
        var restock = Assert.Single(report.Snapshot.Threads, t => t.Name == "restock");
        Assert.Equal(105, restock.Frames.Single(f => f.MethodName == "Restock").Source?.Line);

        // Recount calls Adjust twice: the dump can't tell which call it is, so it gets no line and a note says why.
        var recount = Assert.Single(report.Snapshot.Threads, t => t.Name == "recount");
        Assert.Null(recount.Frames.Single(f => f.MethodName == "Recount").Source);
        Assert.Contains("Warehouse.Recount runs optimized code that the dump has no line map for, and it calls Warehouse.Adjust in more than one place (OptimizedWaits.cs:113, OptimizedWaits.cs:114), so its line is left out.", report.Snapshot.Warnings);
    }

    [Fact]
    public void Second_Result_in_tier1_code_is_reported_at_its_own_line()
    {
        var report = SampleApp.Analyze("tiered-two-results");

        var f = Assert.Single(report.Findings, f => f.Id == "blocked-on-task");
        Assert.Contains("(\"credit-check\") is blocked in .Result at OptimizedWaits.cs:171, waiting for AccountsApi.WaitForLimitsAsync (paused at OptimizedWaits.cs:189)", f.Summary);
    }

    [Fact]
    public void Parallel_ForEach_is_named_as_the_blocking_call()
    {
        var report = SampleApp.Analyze("parallel-then-result");

        var f = Assert.Single(report.Findings, f => f.Id == "blocked-on-task");
        Assert.Contains("(\"price-update\") is blocked in Parallel.ForEach at OptimizedWaits.cs:202, waiting for a Parallel.For/ForEach worker", f.Summary);
    }

    [Fact]
    public void Minimal_api_handler_inlined_into_lambda_method_is_reported_at_its_line()
    {
        var report = SampleApp.Analyze("minimal-api");

        // The scenario only tests something if the handler really runs inside the compiled request delegate.
        Assert.Contains(report.Snapshot.Threads, t => t.Frames.Any(f => f.MethodName.StartsWith("lambda_method", StringComparison.Ordinal)));
        var f = Assert.Single(report.Findings, f => f.Id == "threadpool-starvation");
        Assert.Contains("(all in .GetAwaiter().GetResult() at MinimalApi.cs:56)", f.Summary);
        Assert.Contains(f.Details, d => d.EndsWith(" threads: .GetAwaiter().GetResult() at PriceList.GetPrice (MinimalApi.cs:56).", StringComparison.Ordinal));
        Assert.Contains("await instead of .GetAwaiter().GetResult() in MinimalApi.cs:56", f.Advice);
        Assert.DoesNotContain("unknown", f.Summary + string.Join("\n", f.Details) + f.Advice);

        // The frame put back from the IL says so, in the JSON and in a note.
        var handler = report.Snapshot.Threads.SelectMany(t => t.Frames).First(fr => fr.MethodName == "GetPrice");
        Assert.StartsWith("lambda_method", handler.InlinedInto);
        Assert.Contains(report.Snapshot.Warnings, w => w.StartsWith("PriceList.GetPrice is shown above lambda_method", StringComparison.Ordinal)
            && w.EndsWith(" although the stack walk doesn't find it: the JIT inlined it into that compiled delegate (such as a minimal API handler), and it is the only method in the app's own code that blocks on PriceList.LookupAsync.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wait_whose_line_tier1_code_leaves_open_is_named_by_its_method_not_at_its_callers_line()
    {
        var report = SampleApp.Analyze("tiered-ledger");

        // Post logs and fills a list before its only lock: those calls don't take it, so the lock keeps its line.
        // Stamp has two locks: no line, and not the line of the cold lambda that called it either.
        var contention = Assert.Single(report.Findings, f => f.Id == "lock-contention");
        Assert.Contains("held by Thread N (\"ledger-close\"), which is sleeping (Thread.Sleep) in OptimizedWaits.cs:350 while holding it.", Stable(contention.Summary));
        Assert.Contains(contention.Details, d => d is "Waiting at: OptimizedWaits.cs:317 and Ledger.Stamp." or "Waiting at: Ledger.Stamp and OptimizedWaits.cs:317.");
        Assert.Equal(317, Frame(report, "posting", "Post").Source?.Line);
        Assert.DoesNotContain(report.Snapshot.Warnings, w => w.StartsWith("Ledger.Post ", StringComparison.Ordinal));

        var stamp = Frame(report, "stamping", "Stamp");
        Assert.Null(stamp.Source);
        Assert.Equal([328, 329], stamp.CandidateLines!.Select(l => l.Line));
        Assert.Contains("Ledger.Stamp runs optimized code that the dump has no line map for, and it takes a lock in more than one place (OptimizedWaits.cs:328, OptimizedWaits.cs:329), so its line is left out.", report.Snapshot.Warnings);

        // Total's two .Result calls wait for tasks of the same type.
        var blocked = Assert.Single(report.Findings, f => f.Id == "blocked-on-task");
        Assert.StartsWith("Thread N (\"totals\") is blocked in .Result at Ledger.Total, waiting for a TaskCompletionSource<int> task", Stable(blocked.Summary));
        Assert.Null(blocked.Location);
        Assert.Contains("Ledger.Total runs optimized code that the dump has no line map for, and it blocks with .Result in more than one place (OptimizedWaits.cs:337, OptimizedWaits.cs:338), so its line is left out.", report.Snapshot.Warnings);
    }

    private static Model.StackFrameInfo Frame(Analysis.AnalysisReport report, string thread, string method) =>
        Assert.Single(report.Snapshot.Threads, t => t.Name == thread).Frames.Single(f => f.MethodName == method);

    private static string Stable(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"Thread \d+", "Thread N");
}
