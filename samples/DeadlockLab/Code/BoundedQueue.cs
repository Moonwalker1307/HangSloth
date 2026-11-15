using System.Collections.Concurrent;

namespace DeadlockLab.Code;

public sealed class EventBus
{
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 2);
    private readonly object _subscribersLock = new();
    private readonly List<string> _subscribers = ["audit"];

    /// Publishing under the subscriber lock. Fine while the queue has room.
    public void Publish(string message)
    {
        lock (_subscribersLock)
        {
            _queue.Add($"{message} -> {string.Join(",", _subscribers)}"); // blocks when the queue is full
        }
    }

    /// The consumer looks up subscribers for each message before taking the next one.
    public void Consume(Action whileStarting)
    {
        whileStarting();
        while (true)
        {
            lock (_subscribersLock) _ = _subscribers.Count;
            Console.WriteLine(_queue.Take());
        }
    }
}

internal static class BoundedQueueScenario
{
    public static void Run()
    {
        var bus = new EventBus();
        using var queueFull = new ManualResetEventSlim();
        new Thread(() => bus.Consume(() => queueFull.Wait())) { Name = "bus-consumer" }.Start();
        new Thread(() =>
        {
            for (var i = 0; ; i++)
            {
                if (i == 2) queueFull.Set();
                bus.Publish($"event-{i}");
            }
        }) { Name = "publisher" }.Start();
        Ready.SignalLater();
        Ready.Forever();
    }
}
