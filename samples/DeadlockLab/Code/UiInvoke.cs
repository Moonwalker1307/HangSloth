using System.Collections.Concurrent;

namespace DeadlockLab.Code;

/// Like WinForms' Control.Invoke / WPF's Dispatcher.Invoke: Send runs the callback on the UI
/// thread and waits for it to finish.
public sealed class UiDispatcherContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State, ManualResetEventSlim? Done)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state, null));

    public override void Send(SendOrPostCallback d, object? state)
    {
        using var done = new ManualResetEventSlim();
        _queue.Add((d, state, done));
        done.Wait();
    }

    public void Run(Action first)
    {
        SetSynchronizationContext(this);
        Post(_ => first(), null);
        foreach (var (callback, state, done) in _queue.GetConsumingEnumerable())
        {
            callback(state);
            done?.Set();
        }
    }
}

public sealed class ExportWindow(SynchronizationContext ui)
{
    private string _status = "";

    public string Status => _status;

    /// The "Export" button: runs the export in the background and waits for it, to keep it simple.
    public void OnExportClicked()
    {
        var export = Task.Run(Export);
        export.Wait(); // BUG: blocks the UI thread...
    }

    private void Export()
    {
        Thread.Sleep(50);
        // ...while the export synchronously asks the UI thread to update the status label.
        ui.Send(_ => _status = "exported", null);
    }
}

internal static class UiInvokeScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var ui = new UiDispatcherContext();
        ui.Run(() => new ExportWindow(ui).OnExportClicked());
    }
}
