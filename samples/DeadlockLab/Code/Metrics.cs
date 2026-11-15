namespace DeadlockLab.Code;

public sealed class MetricsFlusher
{
    private readonly object _bufferLock = new();
    private readonly List<double> _buffer = [];
    private readonly Timer _timer;
    internal ManualResetEventSlim? CallbackStarted;

    public MetricsFlusher() => _timer = new Timer(Flush, null, 0, 50);

    public void Record(double value)
    {
        lock (_bufferLock) _buffer.Add(value);
    }

    public void Stop()
    {
        lock (_bufferLock)
        {
            // Wait for in-flight callbacks so nothing is flushed after Stop()... while
            // holding the lock the in-flight callback needs.
            using var drained = new ManualResetEvent(false);
            _timer.Dispose(drained);
            drained.WaitOne();
            _buffer.Clear();
        }
    }

    private void Flush(object? _)
    {
        CallbackStarted?.Set();
        Thread.Sleep(100); // "sending to the metrics backend"
        lock (_bufferLock)
        {
            _buffer.Clear();
        }
    }
}

internal static class MetricsScenario
{
    public static void Run()
    {
        using var started = new ManualResetEventSlim();
        var flusher = new MetricsFlusher { CallbackStarted = started };
        started.Wait();
        flusher.Record(1);
        Ready.SignalLater();
        flusher.Stop();
    }
}
