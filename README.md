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
| **Lock deadlock** | Two or more threads each holding a lock the next one wants (`lock`/`Monitor`, .NET 9's `System.Threading.Lock`, and `ReaderWriterLockSlim` write locks). Locks are named after the field that holds them, e.g. `Billing._ledger`, and after their object when that's what differs, e.g. `Account.Sync (Id = "alice")`. When one of the locks is taken by an `await` continuation running inline inside `TaskCompletionSource.SetResult`, it says so. |
| **Mixed wait cycles** | Threads waiting on each other through different kinds of waits: a lock holder blocked on a `Task.Run` (or `Thread.Join`) that needs the lock, a `Lazy<T>` factory waiting for work that reads the same `Lazy<T>`, a static constructor waiting for a thread that needs the type, a `Parallel.ForEach` under a lock its body takes, a UI thread blocked on work that `Send`s (`Control.Invoke` style) back to its `SynchronizationContext`, a `CancellationTokenRegistration.Dispose` waiting for a callback that needs your lock, and `GC.WaitForPendingFinalizers` under a lock a finalizer takes. |
| **HTTP and child processes** | Threads blocked on `HttpClient` calls, with the method and URL from the await chain: waiting for a response with no timeout, or for a connection when `MaxConnectionsPerServer` is used up by responses the waiting code hasn't read or disposed. And `Process.WaitForExit` on a child whose redirected output nobody reads. |
| **SemaphoreSlim deadlocks** | Two `SemaphoreSlim(1, 1)` "locks" taken in opposite order, and async code waiting in `WaitAsync` for a semaphore it already holds further up the same await chain, including a throttle (`SemaphoreSlim(N)`) whose every slot is held by a call waiting for a nested slot. |
| **Queues and Monitor.Wait** | A producer holding a lock while it waits for room in a full `BlockingCollection` that the consumer can't drain without that lock, a producer waiting for room in a full `BlockingCollection` that nothing is taking items out of (an idle consumer waiting in `Take` or `GetConsumingEnumerable` is normal and isn't reported), and the lost wakeup: a thread in `Monitor.Wait` with work already queued next to it and nobody left to pulse it. |
| **Database waits** | Threads waiting on SQL (PostgreSQL via Npgsql and SQL Server via Microsoft.Data.SqlClient, read from the dump; MySQL and others by their driver frames), with the SQL text and server session: a query blocked by another thread's idle open transaction, transactions that look deadlocked inside the database, a deadlock between an app lock and a database row lock that neither side can see, reads stuck behind a table lock (a migration's `ALTER TABLE` left uncommitted), an advisory lock (`pg_advisory_lock`) held by an idle connection kept in a field, a big `SELECT` whose open reader makes an `ALTER TABLE` wait while every later query queues behind it, rows locked by `SELECT ... FOR UPDATE`, SQL Server lock escalation and reads blocked under `READ COMMITTED`, a lock held while reading a huge result row by row, and a connection pool exhausted by nested connections, also when it's all async and no thread is blocked on the database. EF Core's `DbContext` transactions are seen through its wrappers. |
| **Lock contention** | Threads piling up behind one lock, and what the owner is doing while holding it (sleeping, waiting on a task, waiting for another lock). Also orphaned locks whose owner is gone. |
| **Thread pool starvation** | Most pool threads blocked (typically sync-over-async in ASP.NET Core), how many work items are queued, and whether the pool is at its maximum. It points out a timer whose callback outlasts its period, and the SQL that blocked requests are waiting for. |
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
  --version            Show the version

Exit codes: 0 = nothing wrong found, 2 = a hang pattern was found, 1 = error.
```

Exit code 1 also covers a check that failed when no other check found anything: the report is still printed, but stderr says which check failed, so do not read "No hang patterns found" as a clean result. An unexpected error is one line on stderr; set `HANGSLOTH_DEBUG=1` to see the full exception. Dumps from .NET Framework are not supported, and a Windows dump has to be analyzed on Windows.

Dumps from `dotnet-dump collect`, `createdump`, `procdump` or Task Manager all work. For a live process, HangSloth captures a dump through the same diagnostics channel `dotnet-dump` uses, so it needs no debugger and pauses the app only while the dump is written.

If the dump comes from another machine, pass `--symbols` with a folder containing the app's `.dll` and `.pdb` files to get file and line numbers. HangSloth checks that the `.dll` and `.pdb` files it finds are the build that was dumped, and uses that build wherever it is: at the app's original path or in any `--symbols` folder. If it only finds another build (for example because the app was rebuilt after the dump was taken), it still shows file and line numbers, with a note that they may be off for that module.

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

For harder cases, [`samples/DeadlockLab`](samples/DeadlockLab) is a shop app with 42 deadlocks and hangs, from textbook lock ordering to hidden ones in callbacks, static constructors, `Lazy<T>`, semaphores, queues, `Parallel.ForEach`, UI dispatching, `HttpClient`, finalizers, child processes and real PostgreSQL (with and without EF Core) and SQL Server databases. `scripts/try-deadlock-lab.sh` runs every scenario and prints what HangSloth found next to what it should find.

To check it on code written by someone else, `scripts/try-diagnostic-scenarios.sh` builds Microsoft's [DiagnosticScenarios sample](https://learn.microsoft.com/en-us/samples/dotnet/samples/diagnostic-scenarios/) (the app from the official [deadlock](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-deadlock) and [thread pool starvation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation) tutorials) in a temp folder, puts each scenario under load, and runs HangSloth on it. `taskasyncwait` is the healthy control and should report nothing.

## How it works

```
dump / pid ──► ClrMD ──► SnapshotBuilder ──► ProcessSnapshot ──► detectors ──► findings ──► text / JSON
                         (+ PDB resolver)    (plain records)
```

- **`HangSloth.Core/Collection`** reads the dump with [ClrMD](https://github.com/microsoft/clrmd) and gathers facts: every thread's stack with file:line, held monitors and who is trying to enter them, each thread's `SynchronizationContext`, the task a blocked thread is waiting on and the async chain behind it (state machine state, awaited task, and where its continuation is registered or already queued), plus thread pool counters and queue length.
- **`HangSloth.Core/Analysis`** holds the detectors. They only read the snapshot, so each is unit-tested with hand-built snapshots. They run from most to least specific; a thread explained by one finding isn't reported again by a more generic one.
- **`tests/`** has unit tests for every detector and end-to-end tests that run `samples/HangApp` in each mode and every `samples/DeadlockLab` scenario, dump them, and check the explanation. The DeadlockLab database scenarios use PostgreSQL (`DEADLOCKLAB_PG`) and SQL Server (`DEADLOCKLAB_SQLSERVER`); without them, the tests start both in containers with Testcontainers, or skip those scenarios when Docker isn't available.

## Limitations

- .NET Core 3.1+ / .NET 5+ only (no .NET Framework yet).
- One dump is one moment. A thread that is merely slow can look stuck; if in doubt, take two dumps a few seconds apart.
- `ValueTask`-based awaits from custom `IValueTaskSource`s aren't followed yet.
- `SemaphoreSlim` and `ReaderWriterLockSlim` read locks have no recorded owner, so deadlocks through them are inferred ("most likely"), not proven.
- For database waits HangSloth sees only the app's side. It names the blocked SQL, the sessions and any idle open transaction, and gives the query to confirm it on the server; it doesn't query the database itself. Transaction and pool details are read from Npgsql; other drivers get the SQL text only.
- Waiting on a `WaitHandle` (e.g. `Timer.Dispose(WaitHandle)`) can't be linked to the thread that would signal it, so those cycles show up as lock contention with a hint.

## License

MIT
