<p align="center">
  <img src="assets/logo.png" alt="HangSloth: find out why it's stuck" width="640">
</p>

**HangSloth** is a hang explainer for .NET. Point it at a hung process or a memory dump and it tells you, in plain language, what's wrong and which line of your code to look at.

`dotnet-dump` gives you the raw data, but reading it takes an expert. HangSloth does the reading for you.

```text
$ hangsloth core.4242

1. ✖ Deadlock: sync-over-async
   Thread 1 is blocking with .Result on a task that's waiting for the same SynchronizationContext.
   Classic sync-over-async, in OrderController.cs:10.
   • Thread 1 runs the SingleThreadSynchronizationContext, and it is stuck inside .Result in
     OrderController.Get (OrderController.cs:10).
   • It is waiting for OrderService.GetOrderAsync (paused at OrderService.cs:9).
   • Task.Delay(...) has already finished, and the rest of OrderService.GetOrderAsync has been
     posted to the SingleThreadSynchronizationContext. It is sitting in that queue, waiting for
     Thread 1, which will never get to it.
   Fix: Make the caller async and await the task instead of using .Result. If that isn't possible
   yet, add .ConfigureAwait(false) to the awaits in OrderService.GetOrderAsync (and everything it
   calls) so they don't need the blocked context.

Stacks of the threads involved
   Thread 1 · OS thread 0x585 · SingleThreadSynchronizationContext
        Monitor.Wait
        ManualResetEventSlim.Wait
        Task.SpinThenBlockingWait
        Task.InternalWaitCore
        Task<T>.GetResultCore
      › OrderController.Get  OrderController.cs:10
      › SyncOverAsyncScenario.Run (lambda)  Scenarios.cs:12
      ...
```

## What it detects

| Pattern | What HangSloth tells you |
|---|---|
| **Sync-over-async deadlock** | A thread blocks with `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on a task whose continuation needs that same thread's `SynchronizationContext` (WinForms, WPF, classic ASP.NET, custom pumps). It follows the await chain to the method that's stuck and the line it's paused at. |
| **Lock deadlock** | Two or more threads each holding a lock the next one wants (`lock`/`Monitor` and .NET 9's `System.Threading.Lock`). Locks are named after the field that holds them, e.g. `Billing._ledger`. |
| **Lock contention** | Threads piling up behind one lock, and what the owner is doing while holding it (sleeping, waiting on a task, waiting for another lock). Also orphaned locks whose owner is gone. |
| **Thread pool starvation** | Most pool threads blocked (typically sync-over-async in ASP.NET Core), how many work items are queued, and whether the pool is at its maximum. |
| **Task nobody completes** | A thread waiting on an async chain that ends in a `TaskCompletionSource` nobody has completed, a long `Task.Delay`, or a still-running `Task.Run`. |

Source lines come from your portable PDBs (side-by-side or embedded), including the `await` an async method is currently paused at.

## Usage

```text
hangsloth <dump-file>   [options]   Analyze a memory dump
hangsloth <pid>         [options]   Capture a dump of a running .NET process and analyze it

  --json               Findings plus the full snapshot as JSON
  --all-threads        Show every thread's stack, not just the ones involved
  --symbols <dir>      Extra folder with the app's .dll/.pdb files (repeatable)
  --dac <path>         The runtime's libmscordaccore.so / mscordaccore.dll, for dumps from
                       another machine whose DAC can't be downloaded
  --keep-dump <path>   With a pid: keep the captured dump
  --no-color           Disable colored output

Exit codes: 0 = nothing wrong found, 2 = a hang pattern was found, 1 = error.
```

Dumps from `dotnet-dump collect`, `createdump`, `procdump` or Task Manager all work. For a live process, HangSloth captures a dump through the same diagnostics channel `dotnet-dump` uses, so it needs no debugger and pauses the app only while the dump is written.

If the dump comes from another machine, pass `--symbols` with a folder containing the app's `.dll` and `.pdb` files to get file and line numbers.

## Build and run from source

Requires the .NET 10 SDK. HangSloth runs on .NET 10 and can analyze dumps of apps on any .NET (Core) 3.1+ runtime.

```sh
dotnet build -c Release
dotnet src/HangSloth.Cli/bin/Release/net10.0/hangsloth.dll <dump-or-pid>

# or install it as a global tool
dotnet pack src/HangSloth.Cli -c Release -o artifacts
dotnet tool install -g HangSloth --add-source ./artifacts
```

Try it on the included broken app:

```sh
dotnet run --project samples/HangApp -c Release -- sync-over-async &
hangsloth $!
```

Modes: `sync-over-async`, `deadlock`, `contention`, `starvation`, `never-completes`, `healthy`, `lock-type-deadlock`.

## How it works

```
dump / pid ──► ClrMD ──► SnapshotBuilder ──► ProcessSnapshot ──► detectors ──► findings ──► text / JSON
                         (+ PDB resolver)    (plain records)
```

- **`HangSloth.Core/Collection`** reads the dump with [ClrMD](https://github.com/microsoft/clrmd) and gathers facts: every thread's stack with file:line, held monitors and who is trying to enter them, each thread's `SynchronizationContext`, the task a blocked thread is waiting on and the async chain behind it (state machine state, awaited task, and where its continuation is registered or already queued), plus thread pool counters and queue length.
- **`HangSloth.Core/Analysis`** holds the detectors. They only read the snapshot, so each is unit-tested with hand-built snapshots. They run from most to least specific; a thread explained by one finding isn't reported again by a more generic one.
- **`tests/`** has unit tests for every detector and end-to-end tests that run `samples/HangApp` in each mode, dump it, and check the explanation word for word, in Release and Debug builds.

## Limitations

- .NET Core 3.1+ / .NET 5+ only (no .NET Framework yet).
- One dump is one moment. A thread that is merely slow can look stuck; if in doubt, take two dumps a few seconds apart.
- `ValueTask`-based awaits from custom `IValueTaskSource`s, `SemaphoreSlim.WaitAsync` chains and `ReaderWriterLockSlim` aren't followed yet.

## License

MIT
