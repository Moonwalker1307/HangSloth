using System.Text.RegularExpressions;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Reporting;

namespace HangSloth.Tests;

/// tests/NamesProbe, a program written with top-level statements, local functions and lambdas: the report names its
/// code the way the source does, threads in the same wait are one finding, and each finding claims only the threads it
/// explains. Thread numbers differ between operating systems, so sentences are compared with "Thread N".
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class NamesAndGroupingEndToEndTests
{
    private static readonly Dictionary<string, string?> Optimized = new() { ["DOTNET_TieredCompilation"] = "0" };

    private static string Stable(string text) => Regex.Replace(text, @"\bThread \d+\b", "Thread N");

    [Theory]
    [InlineData("local-async", false)]
    [InlineData("local-async", true)]
    [InlineData("local-result", false)]
    [InlineData("local-result", true)]
    [InlineData("lambda-deadlock", false)]
    [InlineData("lambda-deadlock", true)]
    [InlineData("lock-this", false)]
    [InlineData("lock-this", true)]
    public void No_compiler_generated_name_reaches_the_report(string mode, bool optimized)
    {
        var report = Analyze(mode, optimized ? Optimized : null, r => r.Findings.Any(f => f.Severity >= Severity.Warning));
        var text = new StringWriter();
        TextReporter.Write(report, text, new TextReportOptions { AllThreads = true });
        Assert.True(report.Findings.Count > 0, Verdict.Stacks(report));
        foreach (var line in text.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => !l.StartsWith("note:", StringComparison.Ordinal)))
        {
            Assert.False(Regex.IsMatch(line, @"<<|>\$|\$>|__DisplayClass|>d__|>b__|>g__|\.\.$|<Main>|\w+>P\b"), $"{line}\n{Verdict.Stacks(report)}");
        }
    }

    [Fact]
    public void Top_level_await_of_an_async_local_function_names_the_local_function()
    {
        var report = Analyze("local-async", null, r => r.Findings.Any(f => f.Id == "blocked-on-task"));
        Verdict.Expect(report, "warning blocked-on-task");
        Assert.Equal("Thread N is waiting for the app's async Main, which is paused at Program.cs:13, awaiting Program.ImportAsync (local function in top-level statements) " +
                     "(paused at Program.cs:37), which awaits a TaskCompletionSource<int> task that nothing has completed yet.", Stable(report.Findings[0].Summary));
    }

    [Fact]
    public void A_local_function_blocking_on_another_names_both()
    {
        var report = Analyze("local-result", null, r => r.Findings.Any(f => f.Id == "blocked-on-task"));
        Verdict.Expect(report, "warning blocked-on-task");
        Assert.Equal("Thread N is blocked in .Result at Program.cs:39, waiting for Program.SumAsync (local function in top-level statements) (paused at Program.cs:41), " +
                     "which awaits a TaskCompletionSource<int> task that nothing has completed yet.", Stable(report.Findings[0].Summary));
        Assert.Contains(report.Snapshot.Threads.SelectMany(t => t.Frames), f => Model.Names.ShortMethod(f.TypeName, f.MethodName) == "Program.LoadTotal (local function in top-level statements)");
    }

    [Fact]
    public void Locks_on_captured_locals_are_named_after_the_locals()
    {
        var report = Analyze("lambda-deadlock", null, r => r.Findings.Any(f => f.Id == "lock-deadlock"));
        Verdict.Expect(report, "critical lock-deadlock");
        var details = report.Findings[0].Details.Select(Stable).ToList();
        Assert.Contains("Thread N (\"place-order\") holds the lock on orders (a captured local in Program) and is waiting in Program (lambda in top-level statements) (Program.cs:21) " +
                        "for the lock on stock (a captured local in Program), which Thread N (\"return-item\") holds.", details);
        Assert.Contains("Thread N (\"return-item\") holds the lock on stock (a captured local in Program) and is waiting in Program.ReturnItem (local function in top-level statements) " +
                        "(Program.cs:45) for the lock on orders (a captured local in Program), which Thread N (\"place-order\") holds.", details);
    }

    [Fact]
    public void Two_objects_that_lock_themselves_are_told_apart_by_their_ids()
    {
        var report = Analyze("lock-this", null, r => r.Findings.Any(f => f.Id == "lock-deadlock"));
        Verdict.Expect(report, "critical lock-deadlock");
        var f = report.Findings[0];
        Assert.Contains("Thread N (\"cash->bank\") holds the lock on Ledger (Id = \"cash\") and is waiting in Ledger.MoveTo (Program.cs:65) for the lock on Ledger (Id = \"bank\"), " +
                        "which Thread N (\"bank->cash\") holds.", f.Details.Select(Stable));
        Assert.StartsWith("These are all Ledger objects, taken in a different order by each thread", f.Advice);
    }

    [Fact]
    public void Lazy_values_whose_factories_need_each_other_are_explained_as_such()
    {
        var report = Analyze("lazy-cycle", null, r => r.Findings.Any(f => f.Id == "lock-deadlock"));
        Verdict.Expect(report, "critical lock-deadlock");
        var f = report.Findings[0];
        Assert.Equal("Deadlock: Lazy<T> values whose factories need each other", f.Title);
        Assert.Contains("Thread N (\"retail-rates\") is running the value factory of a Lazy<RateTable> and, in RateTables.Get (Program.cs:104), needs the Value of another Lazy<RateTable>, " +
                        "whose factory Thread N (\"wholesale-rates\") is running.", f.Details.Select(Stable));
        Assert.DoesNotContain("or the reverse", f.Advice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Thirty_threads_in_one_wait_are_one_finding_and_two_semaphores_with_one_name_are_two(bool optimized)
    {
        var report = Analyze("same-wait", optimized ? Optimized : null, r => r.Findings.Sum(f => f.ThreadIds.Count) >= 32);
        Verdict.Expect(report, "warning blocked-on-task, warning semaphore-wait, warning semaphore-wait");
        var workers = report.Findings[0];
        Assert.Equal(30, workers.ThreadIds.Count);
        Assert.Matches(@"^30 ""import-worker"" threads \(Thread \d+, Thread \d+, Thread \d+, Thread \d+, Thread \d+ and 25 more\) are blocked in \.GetAwaiter\(\)\.GetResult\(\) at Program\.cs:126, " +
                       @"waiting for Imports\.FetchAsync \(paused at Program\.cs:138\), which awaits a TaskCompletionSource<string> task that nothing has completed yet\.$", workers.Summary);
        // The two packers wait at one line for two semaphores that read alike: one finding each.
        var packers = report.Findings.Skip(1).ToList();
        Assert.All(packers, p => Assert.Single(p.ThreadIds));
        Assert.NotEqual(packers[0].ThreadIds[0], packers[1].ThreadIds[0]);
        Assert.All(packers, p => Assert.Contains("which is waiting to enter Bay.Gate (Name = \"returns\") (SemaphoreSlim.WaitAsync(), 0 of 1 slots free).", p.Summary));
        // One stack for the thirty.
        var text = new StringWriter();
        TextReporter.Write(report, text);
        Assert.Contains("30 threads stopped at the same place", text.ToString());
    }

    [Fact]
    public void A_lock_owner_blocked_on_a_task_gets_the_finding_that_follows_its_task()
    {
        var report = Analyze("lock-owner-task", null, r => r.Findings.Count >= 2);
        Verdict.Expect(report, "warning lock-contention, warning blocked-on-task");
        Assert.Equal("2 threads are stuck waiting for the lock on PriceBook.Sync, held by Thread N (\"rebuilder\"), which is blocked on .Result in Program.cs:174 while holding it.",
            Stable(report.Findings[0].Summary));
        Assert.Equal("Thread N (\"rebuilder\") is blocked in .Result at Program.cs:174, waiting for PriceBook.LoadFromSupplierAsync (paused at Program.cs:183), " +
                     "which awaits a TaskCompletionSource<int> task that nothing has completed yet.", Stable(report.Findings[1].Summary));
    }

    [Fact]
    public void The_rest_of_an_async_method_after_Task_Delay_is_not_called_a_timer_callback()
    {
        var report = Analyze("delay-then-lock", null, r => r.Findings.Any(f => f.Id == "lock-contention"));
        Verdict.Expect(report, "warning lock-contention");
        var details = report.Findings[0].Details.Select(Stable).ToList();
        Assert.Contains("Thread N (\".NET TP Worker\") is running the continuation of a Task.Delay (Settlement.CloseDayAsync (Program.cs:202)): " +
                        "the rest of an async method after await Task.Delay(...) runs on the thread that fires the delay's timer.", details);
        Assert.DoesNotContain(details, d => d.Contains("timer callback"));
    }

    [SkippableFact]
    public void WaitForExit_waiting_for_output_that_a_background_process_keeps_open()
    {
        Skip.If(OperatingSystem.IsWindows(), "The probe starts /bin/sh.");
        var report = Analyze("process-grandchild", null, r => r.Findings.Any(f => f.Id == "blocked-on-task"));
        Verdict.Expect(report, "info blocked-on-task");
        var f = report.Findings[0];
        Assert.Equal("Waiting for the end of a child process's output", f.Title);
        Assert.Matches(@"^Thread \d+ is blocked in Process\.WaitForExit\(\) at Program\.cs:221 for `sh -c sleep 120 & echo started` \(pid \d+\)\. The child process has exited, " +
                       @"and WaitForExit\(\) now waits for the end of its redirected output", f.Summary);
        Assert.DoesNotContain("Prefer await", f.Advice);
    }

    [Fact]
    public void Parallel_ForEach_gets_advice_about_its_loop_body_not_about_await()
    {
        var report = SampleApp.Analyze("parallel-then-result");
        var f = Assert.Single(report.Findings, f => f.Id == "blocked-on-task");
        Assert.DoesNotContain("Prefer await over Parallel", f.Advice);
        Assert.StartsWith("Parallel.For/ForEach holds the thread that calls it until every iteration has finished", f.Advice);
    }

    [Fact]
    public void A_Send_in_optimized_code_is_not_called_from_itself()
    {
        var report = SampleApp.Analyze("ui-invoke", project: "DeadlockLab", until: r => r.Findings.Any(f => f.Id == "wait-cycle"), environment: Optimized);
        var f = Assert.Single(report.Findings, f => f.Id == "wait-cycle");
        var send = Assert.Single(f.Details, d => d.Contains("UiDispatcherContext.Send"));
        Assert.DoesNotContain("called from UiDispatcherContext.Send", send);
        Assert.Contains("is blocked in UiDispatcherContext.Send", send);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_semaphores_whose_owners_have_no_id_are_told_apart(bool optimized)
    {
        var report = SampleApp.Analyze("semaphore-injected-lock", project: "DeadlockLab",
            until: r => r.Findings.Any(f => f.Id == "semaphore-deadlock"), environment: optimized ? Optimized : null);
        var text = string.Join("\n", report.Findings.SelectMany(f => new[] { f.Summary }.Concat(f.Details)));
        Assert.DoesNotMatch(@"\((?<name>[^()]+(\([^()]*\))?) and \k<name>\)", text);
        if (optimized)
        {
            // SharedGate.Enter is inlined, so no SharedGate is on the stack to name the semaphores by.
            Assert.Contains("that is already taken (2 different semaphores, each a SemaphoreSlim).", text);
            return;
        }
        Assert.Contains("SharedGate.slot (in Billing.ledger)", text);
        Assert.Contains("SharedGate.slot (in Billing.journal)", text);
    }

    private static readonly Lazy<string> Probe = new(() =>
    {
        // Copied into the run's own folder and built there, so that runs from one checkout never share its obj folder.
        var source = Path.Combine(SampleApp.RepoRoot(), "tests", "NamesProbe");
        var copy = Directory.CreateDirectory(Path.Combine(TestRun.Directory, "probe-source", "NamesProbe")).FullName;
        foreach (var file in new[] { "NamesProbe.csproj", "Program.cs" })
            File.Copy(Path.Combine(source, file), Path.Combine(copy, file), overwrite: true);
        var output = Path.Combine(TestRun.Directory, "samples", "NamesProbe");
        SampleApp.Run("dotnet", $"build \"{Path.Combine(copy, "NamesProbe.csproj")}\" -c Release -o \"{output}\" -nologo -v q -nodeReuse:false", expectSuccess: true);
        return Path.Combine(output, "NamesProbe.dll");
    });

    private static AnalysisReport Analyze(string mode, IReadOnlyDictionary<string, string?>? environment, Func<AnalysisReport, bool> until)
    {
        var dump = TestRun.NewDumpPath(mode);
        using var sample = Sample.Start(Probe.Value, [mode], environment);
        try
        {
            sample.WaitUntilReady(TimeSpan.FromSeconds(60));
            for (var attempt = 1; ; attempt++)
            {
                DumpLoader.CollectDump(sample.Process.Id, dump);
                var report = new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump));
                File.Delete(dump);
                if (until(report) || attempt == 3) return report;
                Thread.Sleep(700);
            }
        }
        finally
        {
            try { File.Delete(dump); } catch { }
        }
    }
}
