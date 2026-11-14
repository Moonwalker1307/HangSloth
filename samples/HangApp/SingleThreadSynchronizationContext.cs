using System.Collections.Concurrent;

namespace HangApp;

/// A message-pump style context, like WinForms/WPF or classic ASP.NET:
/// continuations posted to it only run on the one thread that pumps it.
public sealed class SingleThreadSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public void RunOnCurrentThread(Action first)
    {
        SetSynchronizationContext(this);
        Post(_ => first(), null);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            callback(state);
    }
}
