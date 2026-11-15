namespace DeadlockLab.Code;

public sealed class EmailWorker
{
    private readonly object _stateLock = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private string _state = "idle";

    public EmailWorker() => _thread = new Thread(Loop) { Name = "email-worker" };

    public string State => _state;

    public void Start() => _thread.Start();

    public void Stop()
    {
        lock (_stateLock)
        {
            _state = "stopping";
            _stopping = true;
            _thread.Join(); // waits for Loop to finish, while holding _stateLock
        }
    }

    private void Loop()
    {
        while (!_stopping) Thread.Sleep(10);
        OnStopped();
    }

    private void OnStopped()
    {
        lock (_stateLock) // the stopping thread holds this and is waiting for us
        {
            _state = "stopped";
        }
    }
}

internal static class WorkerScenario
{
    public static void Run()
    {
        var worker = new EmailWorker();
        worker.Start();
        Thread.Sleep(50);
        Ready.SignalLater();
        worker.Stop();
    }
}
