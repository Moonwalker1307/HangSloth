using HangSloth.Analysis;
using HangSloth.Model;
using HangSloth.Reporting;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// The report never says "No hang patterns found" when the dump had no managed heap, and when nothing is
/// found it says what the app's own threads are doing.
public class ReportHonestyTests
{
    private static string Text(ProcessSnapshot snapshot, bool allThreads = false) =>
        Text(new HangAnalyzer().Analyze(snapshot), allThreads);

    private static string Text(AnalysisReport report, bool allThreads = false)
    {
        var w = new StringWriter { NewLine = "\n" };
        TextReporter.Write(report, w, new TextReportOptions { AllThreads = allThreads });
        return w.ToString();
    }

    private static readonly StackFrameInfo[] WaitOne = [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Threading.WaitHandle", "WaitOne")];

    private static ThreadSnapshot BlockedOnResult(int id) =>
        Thread(id, [.. ResultWait, App("MyApp.OrderController", "Get", "OrderController.cs", 10), App("MyApp.Program", "Main", "Program.cs", 9)]);

    private static ThreadSnapshot IdlePoolWorker(int id) =>
        Thread(id, [Fx("System.Threading.LowLevelLifoSemaphore", "WaitForSignal"), Fx("System.Threading.PortableThreadPool+WorkerThread", "WorkerThreadStart")], ".NET TP Worker") with { IsThreadPoolWorker = true };

    // ------------------------------------------------------------ dumps without the managed heap

    [Fact]
    public void A_heapless_dump_with_nothing_found_gets_no_green_verdict_and_the_warning_comes_first()
    {
        var text = Text(Process(BlockedOnResult(1), IdlePoolWorker(4)) with { HeapAvailable = false });

        Assert.DoesNotContain("No hang patterns found", text);
        var lines = text.Split('\n');
        Assert.Equal("", lines[1]);
        Assert.Equal("▲ Can't tell: this dump has no managed heap", lines[2]);
        var flat = string.Join(" ", lines.Select(l => l.Trim()));
        Assert.Contains("HangSloth could not read the managed heap from this dump, so it only sees the threads' stacks.", flat);
        Assert.Contains("Without the heap it cannot see who holds a lock, what a task or async method is waiting for, semaphores, queues, connection pools, the thread pool or thread names, so most hangs go unnoticed.", flat);
        Assert.Contains("Mini and triage dumps leave the heap out: createdump --normal or --triage, dotnet-dump collect --type Mini or Triage, and procdump on Windows without -ma.", flat);
        Assert.Contains("Fix: Take a full dump instead: run hangsloth <pid> on the running app, or use dotnet-dump collect --type Full, createdump --full (or --withheap) or procdump -ma.", flat);
        Assert.DoesNotContain("The findings below come from the stacks alone", flat);

        // What the stacks do show: the blocked thread, before its stack.
        var line = Array.IndexOf(lines, "   Thread 1 is blocked on .Result in OrderController.Get (OrderController.cs:10).");
        Assert.True(line > Array.IndexOf(lines, "▲ Can't tell: this dump has no managed heap"), text);
        Assert.Equal("Threads in your code", lines[line - 1]);
        Assert.Contains("Stacks of the threads in your code", text);
        Assert.Contains("› OrderController.Get  OrderController.cs:10", text);
        Assert.DoesNotContain("Thread 4", text);
    }

    [Fact]
    public void A_heapless_dump_with_a_finding_still_reports_it_under_the_warning()
    {
        var snapshot = Process(
            Thread(1, [.. MonitorEnter, App("MyApp.Ledger", "Post", "Ledger.cs", 20)]) with { WaitingForLock = 0x200 },
            Thread(2, [.. MonitorEnter, App("MyApp.Ledger", "Close", "Ledger.cs", 40)]) with { WaitingForLock = 0x100 }) with
        {
            HeapAvailable = false,
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Ledger._entries", OwnerThreadId = 1, WaiterThreadIds = [2] },
                new MonitorLock { Object = new ObjectRef(0x200, "System.Object"), FieldName = "Ledger._totals", OwnerThreadId = 2, WaiterThreadIds = [1] },
            ],
        };
        var report = new HangAnalyzer().Analyze(snapshot);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Critical);

        var text = Text(report);
        var lines = text.Split('\n');
        Assert.Equal("▲ This dump has no managed heap", lines[2]);
        Assert.Contains("The findings below come from the stacks alone, and there may be more.", string.Join(" ", lines.Select(l => l.Trim())));
        Assert.True(text.IndexOf("This dump has no managed heap", StringComparison.Ordinal) < text.IndexOf("1. ", StringComparison.Ordinal));
        Assert.DoesNotContain("No hang patterns found", text);
        Assert.DoesNotContain("Threads in your code", text);
        Assert.Contains("Stacks of the threads involved", text);
    }

    [Fact]
    public void The_json_report_says_whether_the_heap_was_available()
    {
        var complete = JsonReporter.Serialize(new HangAnalyzer().Analyze(Process(BlockedOnResult(1))));
        Assert.Contains("\"heapAvailable\": true,", complete);

        var heapless = JsonReporter.Serialize(new HangAnalyzer().Analyze(Process(BlockedOnResult(1)) with { HeapAvailable = false }));
        Assert.Contains("\"heapAvailable\": false,", heapless);
        Assert.True(heapless.IndexOf("\"heapAvailable\"", StringComparison.Ordinal) < heapless.IndexOf("\"findings\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hand_built_snapshot_has_the_heap()
    {
        Assert.True(Process(BlockedOnResult(1)).HeapAvailable);
    }

    // ------------------------------------------------------------ nothing found on a complete dump

    [Fact]
    public void The_healthy_verdict_keeps_its_sentences_and_lists_the_app_thread()
    {
        var text = Text(Process(
            Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 12)]),
            IdlePoolWorker(4)));

        Assert.Equal(
            "HangSloth · test · .NET ? · 2 managed threads\n" +
            "\n" +
            "No hang patterns found.\n" +
            "1 thread is waiting on something, but nothing looks like a deadlock, starvation or a stuck task.\n" +
            "If the app still seems hung, take a second dump a few seconds later and compare, or rerun with --all-threads.\n" +
            "\n" +
            "Threads in your code\n" +
            "   Thread 1 is sleeping (Thread.Sleep) in Program.Main (Program.cs:12).\n" +
            "\n" +
            "Stacks of the threads in your code\n" +
            "   Thread 1 · OS thread 0x0\n" +
            "        Thread.Sleep\n" +
            "      › Program.Main  Program.cs:12\n" +
            "\n", text);
    }

    [Fact]
    public void A_thread_running_app_code_is_named_and_the_count_does_not_say_0_threads()
    {
        var text = Text(Process(Thread(1, [App("MyApp.Parser", "Loop", "Parser.cs", 88), App("MyApp.Program", "Main", "Program.cs", 5)])));

        Assert.DoesNotContain("0 threads are waiting", text);
        Assert.Contains("No thread is waiting on something, but 1 thread is in your code: if the process is using CPU, look there first.\n", text);
        Assert.Contains("   Thread 1 is running Parser.Loop (Parser.cs:88).\n", text);
        Assert.Contains("› Parser.Loop  Parser.cs:88", text);
    }

    [Fact]
    public void An_app_with_no_thread_in_its_code_looks_idle()
    {
        var host = Thread(1, [Fx("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions", "Run"), App("MyApp.Program", "Main", "Program.cs", 7)]);
        var finalizer = Thread(2, [Fx("System.GC", "RunFinalizers"), App("MyApp.Invoice", "Finalize", "Invoice.cs", 30)]) with { IsFinalizer = true };
        var text = Text(Process(host, finalizer, IdlePoolWorker(4)));

        Assert.Contains("No hang patterns found.\nNo thread is waiting on something or running your code: the app looks idle.\n", text);
        Assert.DoesNotContain("Threads in your code", text);
        Assert.DoesNotContain("Stacks of", text);
    }

    [Fact]
    public void A_thread_in_a_framework_call_that_is_not_recognised_is_not_called_running()
    {
        var text = Text(Process(Thread(1,
        [
            Fx("System.Text.RegularExpressions.RegexInterpreter", "TryMatchAtCurrentPosition"), Fx("System.Text.RegularExpressions.Regex", "RunSingleMatch"),
            Fx("System.Text.RegularExpressions.Regex", "IsMatch"),
            App("MyApp.Validator", "IsEmail", "Validator.cs", 12),
        ])));

        Assert.Contains("   Thread 1 is in Regex.IsMatch, called from Validator.IsEmail (Validator.cs:12).\n", text);
        Assert.DoesNotContain("is running", text);
    }

    [Fact]
    public void A_thread_in_a_synchronous_read_says_which_read()
    {
        var text = Text(Process(Thread(1,
        [
            Fx("Interop+Sys", "Read"), Fx("System.ConsolePal", "Read"), Fx("System.IO.StreamReader", "ReadLine"), Fx("System.Console", "ReadLine"),
            App("MyApp.Program", "Main", "Program.cs", 5),
        ])));

        // The sentence is longer than a line of the report, so it wraps.
        Assert.Contains("   Thread 1 is blocked in a synchronous console read (Console.ReadLine) in Program.Main\n     (Program.cs:5).\n", text);
        Assert.DoesNotContain("is running", text);
    }

    [Fact]
    public void Threads_stopped_at_the_same_place_are_one_line()
    {
        ThreadSnapshot Drain(int id) => Thread(id, [.. WaitOne, App("MyApp.OrderQueue", "Drain", "OrderQueue.cs", 41)]);
        var text = Text(Process(Thread(1, [.. WaitOne, App("MyApp.Program", "Main", "Program.cs", 20)]), Drain(4), Drain(5), Drain(6)));

        var lines = text.Split('\n');
        var at = Array.IndexOf(lines, "Threads in your code");
        Assert.Equal("   Threads 4, 5 and 6 are waiting on a WaitHandle in OrderQueue.Drain (OrderQueue.cs:41).", lines[at + 1]);
        Assert.Equal("   Thread 1 is waiting on a WaitHandle in Program.Main (Program.cs:20).", lines[at + 2]);
        Assert.Equal("", lines[at + 3]);
        Assert.Contains("Threads 4, 5, 6 · 3 threads stopped at the same place", text);
    }

    [Fact]
    public void Five_hundred_threads_stay_readable()
    {
        // 20 places in the app's code, 25 threads at each.
        var threads = Enumerable.Range(0, 500)
            .Select(i => Thread(10 + i, [.. WaitOne, App("MyApp.Mailer", $"Send{i % 20}", "Mailer.cs", 100 + i % 20)]))
            .ToArray();
        var text = Text(Process(threads));

        var lines = text.Split('\n');
        var at = Array.IndexOf(lines, "Threads in your code");
        var section = lines.Skip(at + 1).TakeWhile(l => l.Length > 0).ToList();
        var listed = section.Where(l => l.StartsWith("   25 threads (", StringComparison.Ordinal)).ToList();
        Assert.Equal(8, listed.Count);
        // Long lines wrap with a deeper indent, so each group still reads as one entry.
        Assert.Equal("   25 threads (10, 30, 50, 70, 90, 110, 130, 150, 170, 190 and more) are waiting on a WaitHandle in", listed[0]);
        Assert.Equal("     Mailer.Send0 (Mailer.cs:100).", section[1]);
        Assert.Equal("   (+300 more threads in your code; use --all-threads to see every stack)", section[^1]);
        Assert.Equal(17, section.Count);
        // Stacks only for the groups listed above, so the report stays a few screens long.
        Assert.Equal(8, lines.Count(l => l.Contains("threads stopped at the same place", StringComparison.Ordinal)));
        Assert.True(lines.Length < 120, $"{lines.Length} lines");
    }

    [Fact]
    public void With_all_threads_the_advice_does_not_suggest_it_and_the_heading_has_a_blank_line_before_it()
    {
        var text = Text(Process(Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 12)]), IdlePoolWorker(4)), allThreads: true);

        Assert.DoesNotContain("rerun with --all-threads", text);
        Assert.Contains("If the app still seems hung, take a second dump a few seconds later and compare.\n\n", text);
        Assert.Contains("\n\nThreads\n", text);
        Assert.Contains("Thread 4 (\".NET TP Worker\")", text);
    }

    [Fact]
    public void Info_findings_keep_their_stacks_and_the_other_app_threads_are_listed()
    {
        var snapshot = Process(
            Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 12)]),
            Thread(5, [.. WaitOne, App("MyApp.Exporter", "Run", "Exporter.cs", 33)]));
        var info = new Finding { Id = "note", Severity = Severity.Info, Title = "Something to know", Summary = "Thread 1 sleeps.", ThreadIds = [1] };
        var text = Text(new AnalysisReport(snapshot, [info]));

        Assert.DoesNotContain("No hang patterns found", text);
        Assert.Contains("1. ● Something to know", text);
        Assert.Contains("Threads in your code\n   Thread 5 is waiting on a WaitHandle in Exporter.Run (Exporter.cs:33).\n\n", text);
        Assert.DoesNotContain("Thread 1 is sleeping", text);
        Assert.Contains("Stacks of the threads involved", text);
        Assert.Contains("› Exporter.Run  Exporter.cs:33", text);
        Assert.Contains("› Program.Main  Program.cs:12", text);
    }

    [Fact]
    public void A_warning_finding_does_not_get_the_threads_in_your_code_section()
    {
        var snapshot = Process(
            Thread(1, [.. WaitOne, App("MyApp.Program", "Main", "Program.cs", 20)]),
            Thread(5, [.. WaitOne, App("MyApp.Exporter", "Run", "Exporter.cs", 33)]));
        var warning = new Finding { Id = "note", Severity = Severity.Warning, Title = "Something wrong", Summary = "Thread 5 is stuck.", ThreadIds = [5] };
        var text = Text(new AnalysisReport(snapshot, [warning]));

        Assert.DoesNotContain("Threads in your code", text);
        Assert.DoesNotContain("Program.Main", text);
    }

    [Fact]
    public void Lock_waits_in_a_heapless_dump_are_worded_on_their_own()
    {
        ThreadSnapshot Reader(int id) => Thread(id, [.. MonitorEnter, App("MyApp.PriceCache", "Read", "PriceCache.cs", 30)]);
        var snapshot = Process(
            Reader(4), Reader(5), Reader(6),
            Thread(7, [Fx("System.Threading.ReaderWriterLockSlim", "TryEnterWriteLockCore"), App("MyApp.Settings", "Save", "Settings.cs", 12)])) with { HeapAvailable = false };
        var report = new HangAnalyzer().Analyze(snapshot);
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);

        var text = Text(report);
        var lines = text.Split('\n');
        var at = Array.IndexOf(lines, "Threads in your code");
        Assert.Equal("   Threads 4, 5 and 6 are waiting to enter a lock in PriceCache.Read (PriceCache.cs:30).", lines[at + 1]);
        Assert.Equal("   Thread 7 is waiting for a ReaderWriterLockSlim in Settings.Save (Settings.cs:12).", lines[at + 2]);
        Assert.DoesNotContain("itself", text);
    }

    [Fact]
    public void A_dump_taken_mid_gc_is_told_to_take_another_dump_not_a_full_one()
    {
        var snapshot = Process(BlockedOnResult(1)) with { HeapAvailable = false, Warnings = [Collection.SnapshotBuilder.HeapNotWalkableWarning] };
        var text = Text(snapshot);

        var lines = text.Split('\n');
        Assert.Equal("▲ Can't tell: the managed heap in this dump could not be read", lines[2]);
        var flat = string.Join(" ", lines.Select(l => l.Trim()));
        Assert.Contains("HangSloth could not read the managed heap from this dump, so it only sees the threads' stacks.", flat);
        Assert.Contains("The dump was probably taken while a garbage collection was running. Fix: Take another dump a moment later.", flat);
        Assert.DoesNotContain("Mini and triage dumps", flat);
        Assert.DoesNotContain("Take a full dump", flat);
        Assert.DoesNotContain("No hang patterns found", text);
    }
}
