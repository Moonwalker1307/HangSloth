namespace DeadlockLab.Code;

/// A hand-rolled outbox done right: the sender waits in a loop on its queue, and every Send
/// pulses. The class also keeps a log of the mails it sent, which isn't empty, and isn't work.
public sealed class MailOutbox
{
    private readonly object _sync = new();
    private readonly Queue<string> _outgoing = new();
    private readonly List<string> _sent = [];

    public void Send(string mail)
    {
        lock (_sync)
        {
            _outgoing.Enqueue(mail);
            Monitor.Pulse(_sync);
        }
    }

    public void RunSender()
    {
        while (true)
        {
            string mail;
            lock (_sync)
            {
                while (_outgoing.Count == 0) Monitor.Wait(_sync);
                mail = _outgoing.Dequeue();
            }
            lock (_sent) _sent.Add(mail);
        }
    }

    public int SentCount
    {
        get { lock (_sent) return _sent.Count; }
    }
}

/// Also done right, with a counter as the condition: the printer waits while no receipt is due. Its list
/// of printed receipts isn't empty, and isn't work: the waiting code never looks at it.
public sealed class ReceiptPrinter
{
    private readonly object _sync = new();
    private readonly List<string> _printed = [];
    private int _due;

    public void Request()
    {
        lock (_sync)
        {
            _due++;
            Monitor.Pulse(_sync);
        }
    }

    public void Run()
    {
        while (true)
        {
            lock (_sync)
            {
                while (_due == 0) Monitor.Wait(_sync);
                _due--;
            }
            lock (_printed) _printed.Add($"receipt #{_printed.Count + 1}");
        }
    }

    public int PrintedCount
    {
        get { lock (_printed) return _printed.Count; }
    }
}

internal static class MailOutboxScenario
{
    public static void Run()
    {
        var outbox = new MailOutbox();
        new Thread(outbox.RunSender) { IsBackground = true, Name = "mail-sender" }.Start();
        outbox.Send("order confirmation #1042");
        outbox.Send("invoice #2077");
        var printer = new ReceiptPrinter();
        new Thread(printer.Run) { IsBackground = true, Name = "receipt-printer" }.Start();
        printer.Request();
        printer.Request();
        while (outbox.SentCount < 2 || printer.PrintedCount < 2) Thread.Sleep(10);
        // Both mails are sent and both receipts printed; the sender and the printer now wait for the next one.
        // That's normal, not a hang.
        Ready.SignalLater();
        Ready.Forever();
    }
}
