using System.Collections.Concurrent;

namespace DeadlockLab.Code;

// A SemaphoreSlim is also a common "work available" signal: created with no free slot, released by whoever has work,
// waited on in a loop by the worker. An idle worker sits in Wait() on a semaphore with 0 free slots, exactly like a
// thread waiting for a lock that's taken.

/// A background flusher that sleeps on its own wake-up signal and flushes once per Kick().
public sealed class WakeUpFlusher(string name)
{
    private readonly SemaphoreSlim _wakeUp = new(0, 1);
    private int _flushes;

    public void Start() => new Thread(Run) { IsBackground = true, Name = name }.Start();

    public void Kick()
    {
        try { _wakeUp.Release(); }
        catch (SemaphoreFullException) { } // already awake
    }

    private void Run()
    {
        while (true)
        {
            _wakeUp.Wait();
            Interlocked.Increment(ref _flushes);
        }
    }
}

/// Add queues a message and releases one slot of _workAvailable; the sender takes one slot per message it sends.
public sealed class ReminderMailer
{
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly ConcurrentQueue<string> _messages = new();

    public void Start() => new Thread(Send) { IsBackground = true, Name = "reminder-mailer" }.Start();

    public void Add(string message)
    {
        _messages.Enqueue(message);
        _workAvailable.Release();
    }

    private void Send()
    {
        while (true)
        {
            _workAvailable.Wait();
            if (_messages.TryDequeue(out var message)) Console.WriteLine($"sent {message}");
        }
    }
}

internal static class SignalWorkersScenario
{
    private static readonly List<string> OverdueReminders = [];

    public static void Run()
    {
        var invoices = new WakeUpFlusher("invoice-flusher");
        var receipts = new WakeUpFlusher("receipt-flusher");
        invoices.Start();
        receipts.Start();
        invoices.Kick(); // one flush each, then back to waiting for the next Kick()
        receipts.Kick();

        var mailer = new ReminderMailer();
        mailer.Start();
        // The producer: a timer that mails overdue reminders. None are overdue, so it has nothing to add.
        using var reminders = new Timer(_ =>
        {
            lock (OverdueReminders)
                foreach (var reminder in OverdueReminders) mailer.Add(reminder);
        }, null, 0, 100);
        Ready.SignalLater();
        Ready.Forever();
    }
}
