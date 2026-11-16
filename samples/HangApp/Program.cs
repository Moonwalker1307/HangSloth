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
    case "tiered-starvation": TieredStarvationScenario.Run(); break;
    case "healthy": HealthyScenario.Run(); break;
    case "lock-type-deadlock": LockTypeDeadlockScenario.Run(); break;
    case "sync-io-starvation": SyncIoStarvationScenario.Run(); break;
    case "task-run-async": TaskRunAsyncScenario.Run(); break;
    case "task-run-async-result": TaskRunAsyncResultScenario.Run(); break;
    case "wait-async": WaitAsyncScenario.Run(); break;
    case "unwrap": UnwrapScenario.Run(); break;
    case "continue-with": ContinueWithScenario.Run(); break;
    case "lock-task-run-async": LockTaskRunAsyncScenario.Run(lockBeforeFirstAwait: false); break;
    case "lock-task-run-async-start": LockTaskRunAsyncScenario.Run(lockBeforeFirstAwait: true); break;
    case "semaphore-token": SemaphoreTokenScenario.Run(); break;
    case "when-any": WhenAnyScenario.Run(); break;
    case "when-any-timeout": WhenAnyTimeoutScenario.Run(); break;
    case "wait-any-timeout": WaitAnyTimeoutScenario.Run(); break;
    case "task-run-returns-task": TaskRunReturnsTaskScenario.Run(); break;
    case "resumed-monitor-wait": ResumedMonitorWaitScenario.Run(); break;
    case "parked-pool": ParkedPoolScenario.Run(); break;
    case "pool-lock-convoy": PoolLockConvoyScenario.Run(); break;
    case "pool-backlog": PoolBacklogScenario.Run(); break;
    case "never-started": NeverStartedTaskScenario.Run(); break;
    case "queued-task": QueuedTaskScenario.Run(); break;
    case "when-any-two": TwoTaskWhenAnyScenario.Run(); break;
    case "when-any-two-timeout": TwoTaskWhenAnyTimeoutScenario.Run(); break;
    case "host-start-stuck": GenericHostScenario.Run(stuckStart: true); break;
    case "host-running": GenericHostScenario.Run(stuckStart: false); break;
    // These await, which makes this file the app's async Main.
    case "async-main-shutdown": await AsyncMainShutdownScenario.WaitForShutdown(); break;
    case "async-main-import": await AsyncMainImportScenario.RunAsync(); break;
    case "host-start-stuck-async": await GenericHostScenario.RunAsync(stuckStart: true); break;

    default:
        Console.Error.WriteLine("usage: HangApp <sync-over-async|deadlock|contention|starvation|tiered-starvation|never-completes|healthy|lock-type-deadlock|sync-io-starvation|task-run-async|task-run-async-result|wait-async|unwrap|continue-with|lock-task-run-async|lock-task-run-async-start|semaphore-token|when-any|when-any-timeout|wait-any-timeout|task-run-returns-task|resumed-monitor-wait|parked-pool|pool-lock-convoy|pool-backlog|never-started|queued-task|when-any-two|when-any-two-timeout|async-main-shutdown|async-main-import|host-start-stuck|host-start-stuck-async|host-running>");
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
