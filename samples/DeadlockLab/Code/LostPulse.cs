namespace DeadlockLab.Code;

/// A hand-rolled work queue with a classic lost wakeup: the producer checks "is anyone
/// waiting?" before the consumer has started waiting, so it never pulses.
public sealed class WorkQueue
{
    private readonly object _sync = new();
    private readonly Queue<string> _items = [];
    private bool _consumerWaiting;

    public void Enqueue(string item)
    {
        lock (_sync)
        {
            _items.Enqueue(item);
            if (_consumerWaiting) Monitor.Pulse(_sync); // BUG: skipped if the consumer isn't waiting *yet*
        }
    }

    public string Dequeue()
    {
        lock (_sync)
        {
            _consumerWaiting = true;
            Monitor.Wait(_sync); // BUG: waits without checking the queue first
            _consumerWaiting = false;
            return _items.Dequeue();
        }
    }
}

internal static class LostPulseScenario
{
    public static void Run()
    {
        var queue = new WorkQueue();
        queue.Enqueue("resize img-1.png"); // before the worker has started
        var worker = new Thread(() => Console.WriteLine(queue.Dequeue())) { Name = "queue-worker" };
        worker.Start();
        Ready.SignalLater();
        worker.Join();
    }
}
