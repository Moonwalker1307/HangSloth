using System.Reflection;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// "Lost wakeup" needs evidence about the collection the waiting code checks, not just any non-empty
/// collection next to the object it waits on.
public class MonitorWaitEvidenceTests
{
    private static readonly StackFrameInfo[] DirectWait = [Fx("System.Threading.Monitor", "Wait")];

    private static ThreadSnapshot Sender(PulseWait wait) =>
        Thread(4, [.. DirectWait, App("MyApp.MailOutbox", "RunSender", "MailOutbox.cs", 27)], "mail-sender") with { WaitingForPulse = wait };

    [Fact]
    public void Work_next_to_the_object_that_the_dump_cannot_tie_to_the_wait_is_only_a_hint()
    {
        var wait = new PulseWait { Object = new ObjectRef(0x100, "System.Object"), FieldName = "MailOutbox._sync", PossibleWork = ["MailOutbox._sent (2 items)"] };
        var f = Assert.Single(new MonitorWaitDetector().Detect(Process(Sender(wait)), new HashSet<int>()));

        Assert.Equal("monitor-wait", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Possible lost wakeup: waiting in Monitor.Wait next to a collection that holds items", f.Title);
        Assert.Equal("Thread 4 (\"mail-sender\") is waiting in Monitor.Wait on MailOutbox._sync, and MailOutbox._sent (2 items) isn't empty: if that is its work, a Pulse was lost. " +
                     "Start at MailOutbox.cs:27.", f.Summary);
        Assert.Contains("MailOutbox._sent (2 items) isn't empty. If that is the work Thread 4 (\"mail-sender\") waits for, the Pulse for it was lost; " +
                        "the dump doesn't show which collection the waiting code checks.", f.Details);
    }

    [Fact]
    public void A_hint_keeps_the_warning_when_another_thread_waits_for_the_waiter()
    {
        var wait = new PulseWait { Object = new ObjectRef(0x100, "System.Object"), FieldName = "MailOutbox._sync", PossibleWork = ["MailOutbox._sent (2 items)"] };
        var snapshot = Process(
            Thread(1, [Fx("System.Threading.Thread", "Join"), App("MyApp.Program", "Main", "Program.cs", 9)]) with { JoiningThreadId = 4 },
            Sender(wait));

        var f = Assert.Single(new MonitorWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread waiting for a Monitor.Pulse that may never come", f.Title);
        Assert.StartsWith("Thread 4 (\"mail-sender\") is waiting in Monitor.Wait on MailOutbox._sync, and no other thread is running code that could pulse it. Thread 1 is waiting for it to finish.", f.Summary);
        Assert.Contains(f.Details, d => d.StartsWith("MailOutbox._sent (2 items) isn't empty. If that is the work", StringComparison.Ordinal));
    }

    [Fact]
    public void An_idle_consumer_whose_checked_queue_is_empty_is_not_reported()
    {
        // The collection side found that the code after Wait checks an empty queue: no pending or possible work.
        var wait = new PulseWait { Object = new ObjectRef(0x100, "System.Object"), FieldName = "MailOutbox._sync" };
        Assert.Empty(new MonitorWaitDetector().Detect(Process(Sender(wait)), new HashSet<int>()));
    }

    // ---------------------------------------------------------------- which collection the code after Wait reads

    private sealed class Outbox
    {
        private readonly object _sync = new();
        private readonly Queue<string> _outgoing = new();
        private readonly List<string> _sent = ["welcome"];
        private bool _waiting;
        private int _queued;

        public bool IsWaiting => _waiting;

        public string WhileLoop()
        {
            string mail;
            lock (_sync)
            {
                while (_outgoing.Count == 0) Monitor.Wait(_sync);
                mail = _outgoing.Dequeue();
            }
            _sent.Add(mail);
            return mail;
        }

        public string BreakLoop()
        {
            lock (_sync)
            {
                while (true)
                {
                    if (_outgoing.Count > 0) break;
                    Monitor.Wait(_sync);
                }
                return _outgoing.Dequeue();
            }
        }

        private bool IsEmpty => _outgoing.Count == 0;

        public void PropertyLoop()
        {
            lock (_sync)
            {
                while (IsEmpty) Monitor.Wait(_sync);
                _sent.Add(_outgoing.Dequeue());
            }
        }

        /// The lost-pulse bug: waits without checking, then takes from the queue.
        public string NoCheck()
        {
            lock (_sync)
            {
                _waiting = true;
                Monitor.Wait(_sync);
                _waiting = false;
                return _outgoing.Dequeue();
            }
        }

        /// A counter, not a collection, is the condition; the code then only touches the log after the lock.
        public void CounterThenLeave()
        {
            lock (_sync)
            {
                while (_queued == 0) Monitor.Wait(_sync);
                _queued--;
            }
            _sent.Add("done");
        }

        /// A flag is the condition, and a log line is written through another object before the lock is left.
        public void FlagThenLog(Action<string> log)
        {
            lock (_sync)
            {
                while (!_waiting) Monitor.Wait(_sync);
                log("woken");
            }
        }

        /// The condition is asked of another app object, which may read the queue some other way.
        public void AskOther(OutboxState state)
        {
            lock (_sync)
            {
                while (!state.HasMail) Monitor.Wait(_sync);
                _sent.Add("done");
            }
        }
    }

    private sealed class OutboxState
    {
        public bool HasMail { get; set; }
    }

    private static (IlInstructions.Outcome Outcome, string? Field) FirstCollectionReadAfterWait(string methodName)
    {
        var type = typeof(Outbox);
        var method = type.GetMethod(methodName)!;
        var code = IlInstructions.Decode(method.GetMethodBody()!.GetILAsByteArray()!);
        var wait = code.First(i => i.IsCall && type.Module.ResolveMethod(i.Operand) is { Name: "Wait", DeclaringType.Name: "Monitor" });
        string? FieldOf(IlInstructions.Instruction load) =>
            type.Module.ResolveField(load.Operand) is { DeclaringType: var d, Name: "_outgoing" or "_sent" } f && d == type ? f.Name : null;
        // The object's own methods are followed, the framework's skipped, and other app code can't be followed.
        List<IlInstructions.Instruction>? CalleeOf(IlInstructions.Instruction call) =>
            type.Module.ResolveMethod(call.Operand) is MethodInfo m && m.DeclaringType == type ? IlInstructions.Decode(m.GetMethodBody()!.GetILAsByteArray()!)
            : type.Module.ResolveMethod(call.Operand)?.DeclaringType?.Assembly == type.Assembly ? IlInstructions.Opaque
            : null;
        return IlInstructions.FirstRead(code, wait.Next, FieldOf, CalleeOf);
    }

    [Theory]
    [InlineData(nameof(Outbox.WhileLoop), "_outgoing")]
    [InlineData(nameof(Outbox.BreakLoop), "_outgoing")]
    [InlineData(nameof(Outbox.PropertyLoop), "_outgoing")]
    [InlineData(nameof(Outbox.NoCheck), "_outgoing")]
    public void The_collection_a_wait_is_about_is_the_first_one_read_after_it_inside_the_lock(string method, string? field) =>
        Assert.Equal((IlInstructions.Outcome.Read, field), FirstCollectionReadAfterWait(method));

    /// A counter or a flag as the condition proves the collections aren't the work, which is different from not knowing.
    [Theory]
    [InlineData(nameof(Outbox.CounterThenLeave), "NoneRead")]
    [InlineData(nameof(Outbox.FlagThenLog), "NoneRead")]
    [InlineData(nameof(Outbox.AskOther), "Unknown")]
    public void Code_that_reads_no_collection_after_the_wait_is_told_apart_from_code_that_cant_be_followed(string method, string outcome) =>
        Assert.Equal((Enum.Parse<IlInstructions.Outcome>(outcome), (string?)null), FirstCollectionReadAfterWait(method));

    [Fact]
    public void A_switch_with_a_bad_case_count_ends_the_decoding()
    {
        // switch with a case count of -1, then ret: the decoder must stop, not loop.
        byte[] il = [0x45, 0xFF, 0xFF, 0xFF, 0xFF, 0x2A];
        Assert.Empty(IlInstructions.Decode(il));
    }
}
