using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

// One wait per mode. Each prints READY once it is stuck; the test dumps it then.
switch (args[0])
{
    case "--spew":
        var line = new string('x', 1000);
        for (var i = 0; i < 2000; i++) Console.WriteLine(line);
        return 0;
    case "--quick":
        Console.WriteLine("done");
        return 0;
    case "export-warm": ExportScenarios.WarmThenStuck(); break;
    case "export-other-event": ExportScenarios.OtherEvent(); break;
    case "queue-with-stats-lock": QueueScenarios.StatsLock(); break;
    case "send-to-unpumped-context": SendScenarios.UnpumpedContext(); break;
    default:
        Console.Error.WriteLine($"unknown mode {args[0]}");
        return 1;
}
return 0;

static class Ready
{
    public static void After(int milliseconds) =>
        new Thread(() => { Thread.Sleep(milliseconds); Console.WriteLine("READY"); }) { IsBackground = true }.Start();
}

/// A parent waiting for a child whose redirected output nobody reads.
static class ExportScenarios
{
    /// Exporter.Wait is called often enough first that tiered compilation with PGO optimizes it and inlines
    /// Process.WaitForExit and the method under it: only WaitHandle's frame is left above Exporter.Wait.
    public static void WarmThenStuck()
    {
        WarmUp();
        Ready.After(1500);
        using var child = Exporter.Start("--spew");
        Exporter.Wait(child, timeout: false);
    }

    /// Its own method, so that the loops' optimized code doesn't inline the Exporter.Wait call that hangs.
    private static void WarmUp()
    {
        using var quick = Exporter.Start("--quick");
        quick.WaitForExit();
        // The framework's methods first, so that their profile is there when Exporter.Wait is optimized.
        Repeat(() => quick.WaitForExit());
        Repeat(() => Exporter.Wait(quick, timeout: false));
    }

    private static void Repeat(Action call)
    {
        foreach (var (calls, pause) in new[] { (20_000, 400), (20_000, 1500), (2_000, 500) })
        {
            for (var i = 0; i < calls; i++) call();
            Thread.Sleep(pause);
        }
    }

    /// The thread holds a chatty child but waits on an event of its own: no child-process wait.
    public static void OtherEvent()
    {
        var approved = new ManualResetEvent(false);
        Ready.After(1500);
        using var child = Exporter.Start("--spew");
        Exporter.WaitForApproval(child, approved);
    }
}

static class Exporter
{
    public static Process Start(string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(Exporter).Assembly.Location);
        info.ArgumentList.Add(mode);
        return Process.Start(info)!;
    }

    /// Not inlined into the warm-up loop, so that it is optimized on its own, with what it calls inlined into it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Wait(Process child, bool timeout)
    {
        if (timeout) child.WaitForExit(int.MaxValue);
        else child.WaitForExit();
        return child.StandardOutput.ReadToEnd();
    }

    public static int WaitForApproval(Process child, WaitHandle approved)
    {
        approved.WaitOne();
        return child.Id;
    }
}

/// A consumer that waits in Monitor.Wait although an item is already queued, in a method that also takes a
/// stats lock that was contended once (so that lock has a sync block too).
static class QueueScenarios
{
    public static void StatsLock()
    {
        var stats = new object();
        var holder = new Thread(() => { lock (stats) Thread.Sleep(300); }) { Name = "stats-holder" };
        holder.Start();
        Thread.Sleep(50);
        lock (stats) { }
        holder.Join();

        var queue = new ResizeQueue();
        queue.Enqueue("resize invoice-17.png");
        var worker = new Thread(() => Console.WriteLine(queue.TakeCounted(stats))) { Name = "resize-worker" };
        worker.Start();
        Ready.After(800);
        worker.Join();
    }
}

sealed class ResizeQueue
{
    private readonly object _sync = new();
    private readonly Queue<string> _jobs = new();
    private bool _consumerWaiting;
    public int Taken;

    public void Enqueue(string job)
    {
        lock (_sync)
        {
            _jobs.Enqueue(job);
            if (_consumerWaiting) Monitor.Pulse(_sync);
        }
    }

    public string TakeCounted(object stats)
    {
        lock (stats) Taken++;
        string job;
        lock (_sync)
        {
            _consumerWaiting = true;
            Monitor.Wait(_sync);
            _consumerWaiting = false;
            job = _jobs.Dequeue();
        }
        lock (stats) Taken++;
        return job;
    }
}

/// The UI thread has a context installed and joins a sender; the sender sends to a second context of the same
/// type that no thread has installed and whose pump is busy. Not a deadlock with the UI thread.
static class SendScenarios
{
    public static void UnpumpedContext()
    {
        var uiContext = new PumpContext();
        var mailContext = new PumpContext();
        new Thread(() => { Thread.Sleep(Timeout.Infinite); mailContext.Pump(); }) { IsBackground = true, Name = "mail-pump" }.Start();
        var sender = new Thread(() => { Thread.Sleep(200); mailContext.Send(_ => { }, null); }) { IsBackground = true, Name = "sender" };
        sender.Start();
        var ui = new Thread(() => { SynchronizationContext.SetSynchronizationContext(uiContext); sender.Join(); }) { IsBackground = true, Name = "ui" };
        ui.Start();
        Ready.After(600);
        ui.Join();
    }
}

class PumpContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State, ManualResetEventSlim Done)> _queue = new();

    public override void Send(SendOrPostCallback d, object? state)
    {
        var done = new ManualResetEventSlim();
        _queue.Add((d, state, done));
        done.Wait();
    }

    public void Pump()
    {
        foreach (var (callback, state, done) in _queue.GetConsumingEnumerable())
        {
            callback(state);
            done.Set();
        }
    }
}
