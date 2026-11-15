namespace DeadlockLab.Code;

/// Unregistering a cancellation callback waits for that callback if it is running right now.
/// Here the callback needs the download's lock, and the code that unregisters it holds that lock.
public sealed class Download
{
    private readonly object _stateLock = new();
    private CancellationTokenRegistration _onCancel;
    private string _state = "running";
    public string State => _state;

    public void Start(CancellationToken token, ManualResetEventSlim callbackStarted) =>
        _onCancel = token.Register(() =>
        {
            callbackStarted.Set();
            lock (_stateLock) _state = "cancelled"; // waits for Complete() to release the lock
        });

    public void Complete(ManualResetEventSlim callbackStarted)
    {
        lock (_stateLock)
        {
            callbackStarted.Wait();
            _state = "done";
            _onCancel.Dispose(); // BUG: waits for the running callback, which waits for this lock
        }
    }
}

internal static class CancellationDisposeScenario
{
    public static void Run()
    {
        var cts = new CancellationTokenSource();
        var download = new Download();
        var started = new ManualResetEventSlim();
        download.Start(cts.Token, started);
        var completer = new Thread(() => download.Complete(started)) { Name = "download-complete" };
        completer.Start();
        Thread.Sleep(100);
        new Thread(() => cts.Cancel()) { Name = "user-cancel" }.Start();
        Ready.SignalLater(1000);
        completer.Join();
    }
}
