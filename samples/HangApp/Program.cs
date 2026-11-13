// A deliberately broken app. Each mode hangs in a different, classic way so
// HangSloth has something real to explain. Run: HangApp <mode>
// It prints "READY" once the hang is in place.
using HangApp;

var mode = args.Length > 0 ? args[0] : "";
switch (mode)
{
    case "sync-over-async": SyncOverAsyncScenario.Run(); break;
    case "deadlock": LockDeadlockScenario.Run(); break;
    case "contention": LockContentionScenario.Run(); break;
    case "starvation": StarvationScenario.Run(); break;
    case "never-completes": NeverCompletesScenario.Run(); break;
    case "healthy": HealthyScenario.Run(); break;
#if NET9_0_OR_GREATER
    case "lock-type-deadlock": LockTypeDeadlockScenario.Run(); break;
#endif
    default:
        Console.Error.WriteLine("usage: HangApp <sync-over-async|deadlock|contention|starvation|never-completes|healthy|lock-type-deadlock>");
        Environment.Exit(2);
        break;
}

static class Ready
{
    public static void Signal()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();
    }

    /// Signal readiness from a background thread after the hang has had time to settle.
    public static void SignalLater(int ms = 750) =>
        new Thread(() => { Thread.Sleep(ms); Signal(); }) { IsBackground = true, Name = "ready-signal" }.Start();
}
