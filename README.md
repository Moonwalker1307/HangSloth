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
| **Sync-over-async deadlock** | A thread blocks with `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on a task whose continuation needs that same thread's `SynchronizationContext` (WinForms, WPF, classic ASP.NET, custom pumps). It follows the await chain to the method that's stuck and the line it's paused at. A test that blocks on async code under xunit's `AsyncTestSyncContext` or `MaxConcurrencySyncContext` isn't reported as one: those contexts run continuations on other threads, so it is a wait, unless a continuation is already stuck in their queue. |
| **Lock deadlock** | Two or more threads each holding a lock the next one wants (`lock`/`Monitor`, .NET 9's `System.Threading.Lock`, and `ReaderWriterLockSlim`, including an upgrade to the write lock that waits for a reader who waits for the upgrading thread). Locks are named after the field that holds them, e.g. `Billing._ledger`, and after their object when that's what differs, e.g. `Account.Sync (Id = "alice")`. When one of the locks is taken by an `await` continuation running inline inside `TaskCompletionSource.SetResult`, it says so. |
| **Mixed wait cycles** | Threads waiting on each other through different kinds of waits: a lock holder blocked on a `Task.Run` (also with an async lambda), an async method that takes the lock after an `await`, or a `Thread.Join` that needs the lock, a `Lazy<T>` factory waiting for work that reads the same `Lazy<T>`, a static constructor waiting for a thread that needs the type, a `Parallel.ForEach` under a lock its body takes, a UI thread blocked on work that `Send`s (`Control.Invoke` style) back to its `SynchronizationContext`, a `CancellationTokenRegistration.Dispose` waiting for a callback that needs your lock, and `GC.WaitForPendingFinalizers` under a lock a finalizer takes. |
| **HTTP and child processes** | Threads blocked on `HttpClient` calls, with the method and URL from the await chain: waiting for a response with no timeout, for a connection when `MaxConnectionsPerServer` is used up by responses the waiting code hasn't read or disposed (a response whose body was already read holds no connection and doesn't count), or for a new connection that is never established (a server that doesn't answer the connect or TLS handshake, which isn't a full pool). And `Process.WaitForExit` on a child whose redirected output nobody reads. |
| **SemaphoreSlim deadlocks** | Two `SemaphoreSlim(1, 1)` "locks" taken in opposite order, and async code waiting in `WaitAsync` for a semaphore it already holds further up the same await chain, including a throttle (`SemaphoreSlim(N)`) whose every slot is held by a call waiting for a nested slot. A `SemaphoreSlim` has no owner, so HangSloth reads the waiting code in the dump and calls it a deadlock only when that code took the semaphore earlier and nothing in between can release it: no `Release()` of it, of a semaphore the code can't name (`_gate?.Release()`), or in a method of the same class it calls (an async method, a lambda and a nested class count too), no handing the semaphore or the object itself to other code (`_gate.ReleaseQuietly()`, `new Releaser(_gate)`, `Cleanup.Finish(this)`), no new semaphore put into the field, and no such release later in a loop that comes back round to the wait. A `Wait(0)` or `WaitAsync(timeout)` counts only where its result says it got the slot and nothing else leads there (`if (!gate.Wait(t)) throw ...;`, not `if (!gate.Wait(t) && strict) throw ...;`). A `WaitAsync()` counts only where the code awaits it right there, not when its task is kept for later or handed to `Task.WhenAny`. A take inside an `if`, or inside a `try` whose `catch` carries on, counts only where every path went through it. Otherwise it says the semaphore has no free slots, and leaves out workers that wait in a loop on a "work available" signal (created with no free slot, and never released by the worker) the way idle queue consumers are left out; a `while (!gate.Wait(t))` retry is not such a loop, and a semaphore made with a count from a setting (`new SemaphoreSlim(maxJobs)`) is a throttle, not a signal. When it can't follow the waiting code (in optimized code the runtime inlines small helpers, such as the `Enter()` of a lock class), or the code doesn't show what the semaphore is (a lock class whose `Enter()` took nothing and that was handed its semaphore, or a semaphore reached through a property or an indexer), it still warns about a wait that nothing else could end, and reports threads waiting for each other's taken one-slot semaphores as a possible deadlock (a Warning). |
| **Queues and Monitor.Wait** | A producer holding a lock while it waits for room in a full `BlockingCollection` that the consumer can't drain without that lock, a producer waiting for room in a full `BlockingCollection` that nothing is taking items out of (an idle consumer waiting in `Take` or `GetConsumingEnumerable` is normal and isn't reported), and the lost wakeup: a thread in `Monitor.Wait` with work already queued in the collection its code checks after the wait (read from the method's code in the dump) and nobody left to pulse it, or with another thread blocked on the `Task.Run` or async method it is in. A non-empty collection next to it that can't be tied to the wait is only mentioned, as a possibility. |
| **Database waits** | Threads waiting on SQL (PostgreSQL via Npgsql and SQL Server via Microsoft.Data.SqlClient, read from the dump; MySQL and others by their driver frames), with the SQL text and server session: a query blocked by another thread's idle open transaction, transactions that look deadlocked inside the database, a deadlock between an app lock and a database row lock that neither side can see, reads stuck behind a table lock (a migration's `ALTER TABLE` left uncommitted), an advisory lock (`pg_advisory_lock`) held by an idle connection kept in a field, a big `SELECT` whose open reader makes an `ALTER TABLE` wait while every later query queues behind it, rows locked by `SELECT ... FOR UPDATE`, SQL Server lock escalation and reads blocked under `READ COMMITTED`, a lock held while reading a huge result row by row, a connection pool exhausted by nested connections, also when it's all async and no thread is blocked on the database, and a connection that is still being opened to a server that doesn't answer, told apart from a wait for a free pooled connection, with the server and connect timeout from the connection string. With two drivers in one process, each one's waits are explained with its own sessions and pool. EF Core's `DbContext` transactions are seen through its wrappers. The tests cover Npgsql 6.0, 7.0 and 10.0 and Microsoft.Data.SqlClient 5.2 and 7.1; for a driver whose pool internals aren't recognised, such as Npgsql 4.1, the report says so in a note instead of staying silent. |
| **Lock contention** | Threads piling up behind one lock, and what the owner is doing while holding it (sleeping, waiting on a task, waiting for another lock, blocked in synchronous I/O such as `Socket.Receive` or a synchronous `HttpClient.Send`). Also orphaned locks whose owner is gone, and a `ReaderWriterLockSlim` writer or upgrade waiting for readers, with which threads are reading and where. |
| **Thread pool starvation** | Most pool threads blocked (typically sync-over-async in ASP.NET Core, or synchronous socket, stream, pipe, TLS, DNS, HTTP and console calls such as `Socket.Receive`, `NetworkStream.Read` or `PipeStream.Read`, named with the async method to await instead), named after the call the app made (`ManualResetEventSlim.Wait`, `HttpClient.Send`) rather than the primitive inside it, how many work items are queued (all of the pool's queues, including each thread's local queue), and whether the pool is at its maximum. It points out a timer whose callback outlasts its period, and the SQL that blocked requests are waiting for. When most of the threads wait for one lock, semaphore or open transaction, it names it and its holder, and the finding that explains that wait is reported too. Pool threads parked in long-lived loops (queue consumers, pollers) while nothing is queued are not starvation: an idle consumer isn't reported, and threads parked in other waits get an informational note at most. |
| **Task nobody completes** | A thread waiting on an async chain that ends in a `TaskCompletionSource` nobody has completed, a task created with `new Task(...)` that was never started, a long `Task.Delay`, or a `Task.Run` that is still running or still queued for a thread. The chain is followed through `Task.Run(async ...)`, `Unwrap()`, `WaitAsync(...)` and `ContinueWith(...)` to the work behind them, and through `Task.WaitAny` or `Task.WhenAny` when none of their tasks can finish. An app's async Main (or top-level `await`) is not a hang by itself: the runtime keeps the main thread waiting for it as long as the app runs, so Main awaiting its stop signal directly isn't reported. A task or `SemaphoreSlim` nobody completes further down, in a method of the app that Main awaits, is reported as usual, with a note that it may be how the app waits to be stopped. A generic host whose hosted service never returns from `StartAsync` is reported as a warning, whatever `StartAsync` is waiting for, after `host.Run()` and `await host.RunAsync()` alike. |

Source lines come from your portable PDBs (side-by-side or embedded), including the `await` an async method is currently paused at. In code the JIT has optimized, as in any app that has been running for a while, the dump has no line map, so each frame's line comes from the call it is making: the `lock`, `SemaphoreSlim.Wait()`, `.Result` (told apart by the type of the task it waits for), `Parallel.ForEach`, `Process.WaitForExit` or the method it called. When a method makes that call in more than one place and nothing tells them apart, the finding names the method without a line (not its caller's line), its frame has no line, and a note lists the candidate lines (in `--json`, the frame's `candidateLines`), rather than a line that may be wrong. A minimal API handler that the JIT inlined into ASP.NET Core's compiled request delegate (`lambda_method`) is found from the async method its thread is waiting for and put back on the stack; a note says so, and in `--json` the frame has `inlinedInto`. The same goes for a task's lambda that tiered PGO inlined into the thread pool's dispatch loop, or into the code that runs a task inline inside another (found from the task the thread runs, so a lock it waits for still closes a deadlock with the task's waiter) and for a `ManualResetEventSlim.Wait` inlined into its caller (found from the event whose own lock the thread waits on in `Monitor.Wait`, so it isn't taken for the app's own `Monitor.Wait`).

## Usage

```text
hangsloth <dump-file>   [options]   Analyze a memory dump
hangsloth <pid>         [options]   Capture a dump of a running .NET process and analyze it

  --json               Findings plus the full snapshot as JSON (UTF-8)
  --all-threads        Show every thread's stack, not just the ones involved
  --symbols <dir>      Extra folder with the app's .dll/.pdb files (repeatable)
  --dac <path>         The runtime's libmscordaccore.so / mscordaccore.dll, for dumps from
                       another machine whose DAC can't be downloaded
  --keep-dump <path>   With a pid: keep the captured dump
  --no-color           Disable colored output
  --version            Show the version

An option's value can also be written as --name=value, which is the way to pass one that starts with '-'.

Exit codes: 0 = nothing wrong found, 2 = a hang pattern was found,
            1 = error, or nothing found but the dump has no managed heap or a check failed.
```

The exact meaning of the exit codes:

| Exit code | Meaning |
|---|---|
| `0` | No finding above informational. Informational findings (`●`) are still printed. |
| `2` | At least one warning (`▲`) or critical (`✖`) finding. |
| `1` | An error: bad arguments, a dump that can't be read, or a process that can't be reached. Also a check that failed when no other check found anything, and a dump without the managed heap in which nothing was found (see below): the report is still printed, but stderr says what was missing, so do not read it as a clean result. |

An unexpected error is one line on stderr; set `HANGSLOTH_DEBUG=1` to see the full exception. Dumps from .NET Framework are not supported, and a Windows dump has to be analyzed on Windows.

HangSloth needs a dump with the managed heap: `dotnet-dump collect` (its default `--type Full`, or `--type Heap`), `createdump` (its default `--withheap`, or `--full`), `procdump -ma` on Windows, or Task Manager's "Create dump file". Mini and triage dumps (`createdump --normal` or `--triage`, `dotnet-dump collect --type Mini` or `--type Triage`, and `procdump` on Windows without `-ma`) contain only the stacks, so locks, tasks, semaphores, queues, connection pools and the thread pool can't be seen. For such a dump HangSloth still reports what it can find from the stacks, but instead of "No hang patterns found" it says at the top that it can't tell, lists the threads in your code, and exits with 1 (2 if it did find something). `--json` has `"heapAvailable": false` for it. The same happens, rarely, for a full dump taken while a garbage collection was running, whose heap can't be read; then HangSloth asks for another dump.

When nothing is found, the report lists what the threads in your code are doing (for example `Thread 1 is waiting on a WaitHandle in SingleInstance.Run (SingleInstance.cs:13).` or `Thread 6 is running Parser.Loop (Parser.cs:88).`) with their stacks, so a wait HangSloth doesn't explain or a loop that never ends is visible without `--all-threads`. Idle thread pool workers, the finalizer and the runtime's own threads aren't listed.

For a live process, HangSloth captures a dump through the same diagnostics channel `dotnet-dump` uses, so it needs no debugger and pauses the app only while the dump is written. It says so on stderr first (`Taking a full dump of process 4242 to /tmp/hangsloth-x1Yz2a/4242.dmp (the app is paused while it is written; the file can be several GB)...`), and the app's own output shows a few `[createdump]` lines. The dump is a full one, hundreds of megabytes for a busy app and often much bigger than the app's memory use, and goes to a new folder in the temp directory (`TMPDIR` on Linux and macOS, `%TEMP%` on Windows; straight into the temp directory for an app that runs as another user, which writes the file itself), or to the `--keep-dump` path. It is deleted when HangSloth is done, also when you stop it with Ctrl+C or it gets SIGTERM; only a hard kill (`kill -9`, out of memory) can leave one behind, and on Windows so can a Ctrl+C while the dump is still being written. If the dump was taken but the analysis failed, HangSloth keeps it and prints its path, so you can retry with `hangsloth <that file>` without pausing the app again.

HangSloth has to run as the target's user, or as root (`sudo hangsloth <pid>`). On Linux it finds the target's diagnostics socket wherever the target's `TMPDIR` points; on macOS both have to see the same `TMPDIR` (the socket lives there; if the connection fails with a message about the socket path length, set `TMPDIR` to a shorter folder for both processes).

When the dump cannot be taken, HangSloth says why instead of waiting: a process that is not running, one stopped with Ctrl+Z or `kill -STOP`, one held by a debugger or `strace`, or the id of one of its threads instead of the process (the last three are checked up front on Linux). If the app does not write anything for 2 minutes (for example under `docker pause`), HangSloth gives up and says so; resume the app, or take a dump yourself with `createdump`, `gcore` or `procdump -ma` and pass the file. An app that runs as another user can still write the dump it was asked for once it is resumed, as `hangsloth-<pid>-<id>.dmp` in the temp directory; delete that file.

A dump whose file is shorter than its own headers say (an interrupted copy or download, or a disk that filled up while it was written) is reported as cut off, with exit code 1, instead of being analysed: the threads' stacks are near the end of a dump, so a cut-off dump would otherwise look like an app with nothing wrong.

Reports can contain SQL text, URLs, database server names and the command lines of child processes, taken from the dump. Check a report before you paste it into a public issue.

### Analyzing a dump from another machine

The usual case is a dump from a server or container that you open on your workstation. Bring:

- **A full dump**, as above, and analyze it on the same operating system family and the same CPU architecture it came from (a Linux dump on Linux, a Windows dump on Windows).
- **The runtime's debugging library (the DAC)** for the exact runtime version the app ran on. If that version is installed on the analyzing machine, nothing more is needed. If it is not, pass `--dac`. On Linux and macOS HangSloth never downloads the DAC, so without it the analysis stops with "Could not load the debugging library (DAC)". The easiest fix is to install the runtime version named in that message. Otherwise take `libmscordaccore.so` (`mscordaccore.dll` on Windows) from the `shared/Microsoft.NETCore.App/<version>/` folder of that runtime, or from the `Microsoft.NETCore.App.Runtime.<rid>` NuGet package of that version. It has to be built for the analyzing machine's OS, architecture and C library: the file from an Alpine (musl) container does not load on a glibc workstation.
- **The app's binaries**, the build that was dumped, to get file and line numbers: pass `--symbols` with a folder containing the app's `.dll` and `.pdb` files (the publish folder). Without them you still get method names. HangSloth checks that the `.dll` and `.pdb` files it finds are the build that was dumped, and uses that build wherever it is: at the app's original path or in any `--symbols` folder. If it only finds another build (for example because the app was rebuilt after the dump was taken), it still shows file and line numbers, with a note that they may be off for that module.

```sh
hangsloth prod.dmp --dac ~/dac/10.0.15/libmscordaccore.so --symbols ./publish
```

### The output

The text report is always UTF-8, whatever the console's code page: written to a file or a pipe it is UTF-8 without a byte order mark, and on a Windows console whose code page is not UTF-8 HangSloth switches the console to UTF-8 for the run and puts it back afterwards. On Linux and macOS the terminal's own character set (from the locale) cannot be changed by a program, so a terminal that is not UTF-8 gets plain ASCII. The same happens on Windows when the switch is not possible (no console attached, or not allowed): HangSloth prints plain ASCII markers instead of the severity glyphs (`X`, `!`, `i`, `-`, `>`, `|`) and writes any other non-ASCII character as `\uXXXX`. `--json` is always UTF-8 too. In PowerShell, set `[Console]::OutputEncoding = [Text.Encoding]::UTF8` before piping or redirecting HangSloth's output, or it decodes the UTF-8 with the console's code page.

Names and text in a dump come from another machine, so the text report never passes them on as they are: control characters (escape sequences, bell, newlines, the C1 controls) and bidirectional overrides are shown as `\x1B`, `\u202E` and so on (tab, carriage return and newline become a space), so a dump cannot move the cursor, recolor the terminal or forge a line of the report. Numbers in sentences read the same on every machine (`12,345 rows`, `1.5 minutes`), whatever the machine's language settings.

Code is named the way the source names it, not by the names the compiler gives it: `Program (top-level statements)`, `Program (lambda in top-level statements)`, `Worker.Drain (local function in Run)`, and a lock on a local that a lambda captured is `orders (a captured local in Program)`. Two locks or semaphores that have the same name are told apart by their owner's id when it has one (`Warehouse.Lock (Name = "north")`).

Threads that wait in the same way at the same place (the same call at the same line, for the same chain, statement, server or object) are one finding that says how many they are and names the first five, `30 "import-worker" threads (Thread 5, Thread 6, Thread 7, Thread 8, Thread 9 and 25 more) are blocked in ...`; `threadIds` in `--json` lists all of them, and the text report shows their stack once.

A thread's stack always shows the app's own frames (up to 40 per thread, then `… N more frames`). The framework's frames are what gets shortened: a run of more than eight framework frames above the first app frame is cut to its first four and its last one, and a run of three or more framework frames between or below app frames is shown as `… N framework frames`. `--json` has every frame that was collected.

`--json` has a versioned shape. Fields are only ever added within a `schemaVersion`; a renamed, removed or re-purposed field raises it. A property with a fixed set of values, such as a task's `kind`, can get a new value without a new version (`not-started` and `queued` were added that way), so treat a value you don't know like a missing one.

| Field | Meaning |
|---|---|
| `schemaVersion` | The version of this shape, currently `1`. |
| `tool`, `version` | `"hangsloth"` and the version that wrote the report (for example `0.1.0+f182f67`; the command line passes its own version, a program that calls `JsonReporter.Serialize` passes `toolVersion` or gets the library's version, which is the same number). |
| `source`, `runtimeVersion` | What was analyzed (a dump path, or `process <pid>`), and the runtime that ran it. |
| `heapAvailable` | `false` for a dump without a readable managed heap: an empty `findings` list then means "could not tell". |
| `warnings` | Notes about a degraded analysis (a missing PDB, a build that is not the one dumped...); the same list as `snapshot.warnings`. |
| `failedDetectors` | Checks that failed: their findings are missing, so an empty `findings` list cannot be trusted. |
| `findings` | The findings: `id`, `severity` (`info`, `warning`, `critical`), `title`, `summary`, `details`, `advice`, `threadIds`, `location`. |
| `snapshot` | Everything HangSloth read from the dump: threads and their frames, locks, semaphores, the thread pool. Properties that are not set are left out, and addresses are decimal numbers. |

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
dotnet samples/HangApp/bin/Release/net10.0/HangApp.dll sync-over-async &   # prints READY once it is stuck
sleep 2; hangsloth $!
kill $!
```

The `dotnet build -c Release` above builds the samples too. Start the built app itself, not `dotnet run ... &`: with `dotnet run`, `$!` is the .NET SDK's launcher process and the app is its child, so HangSloth would analyze the launcher.

Modes: `sync-over-async`, `deadlock`, `contention`, `starvation`, `tiered-starvation`, `never-completes`, `healthy`, `lock-type-deadlock`, `sync-io-starvation`, `task-run-async`, `task-run-async-result`, `wait-async`, `unwrap`, `continue-with`, `lock-task-run-async`, `lock-task-run-async-start`, `semaphore-token`, `when-any`, `when-any-timeout`, `wait-any-timeout`, `task-run-returns-task`, `resumed-monitor-wait`, `parked-pool`, `pool-lock-convoy`, `pool-backlog`, `never-started`, `queued-task`, `when-any-two`, `when-any-two-timeout`, `async-main-shutdown`, `async-main-import`, `host-start-stuck`, `host-start-stuck-async`, `host-running`, `tiered-lock`, `tiered-two-results`, `parallel-then-result`, `minimal-api`, `tiered-ledger`.

For harder cases, [`samples/DeadlockLab`](samples/DeadlockLab) is a shop app with 61 deadlocks and hangs, from textbook lock ordering to hidden ones in callbacks, static constructors, `Lazy<T>`, semaphores, queues, `Parallel.ForEach`, UI dispatching, `HttpClient`, finalizers, child processes and real PostgreSQL (with and without EF Core) and SQL Server databases. `scripts/try-deadlock-lab.sh` runs every scenario and prints what HangSloth found next to what it should find.

To check it on code written by someone else, `scripts/try-diagnostic-scenarios.sh` builds Microsoft's [DiagnosticScenarios sample](https://learn.microsoft.com/en-us/samples/dotnet/samples/diagnostic-scenarios/) (the app from the official [deadlock](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-deadlock) and [thread pool starvation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation) tutorials) in a temp folder, puts each scenario under load, and runs HangSloth on it. `taskasyncwait` is the healthy control and should report nothing.

## How it works

```
dump / pid ──► ClrMD ──► SnapshotBuilder ──► ProcessSnapshot ──► detectors ──► findings ──► text / JSON
                         (+ PDB resolver)    (plain records)
```

- **`HangSloth.Core/Collection`** reads the dump with [ClrMD](https://github.com/microsoft/clrmd) and gathers facts: every thread's stack with file:line, held monitors and who is trying to enter them, each thread's `SynchronizationContext`, the task a blocked thread is waiting on and the async chain behind it (state machine state, awaited task, and where its continuation is registered or already queued), plus thread pool counters and queue length.
- **`HangSloth.Core/Analysis`** holds the detectors. They only read the snapshot, so each is unit-tested with hand-built snapshots. They run from most to least specific; a thread explained by one finding isn't reported again by a more generic one.
- **`tests/`** has unit tests for every detector and end-to-end tests that run `samples/HangApp` in each mode, every `samples/DeadlockLab` scenario and `samples/DriverVersions` built against older Npgsql and SqlClient versions, dump them, and check the explanation. The DeadlockLab code scenarios run a second time fully optimized (`DOTNET_TieredCompilation=0`), as a process looks after it has run for a while: `Thread.Join`, `Monitor.Wait`, `GC.WaitForPendingFinalizers` and `Process.WaitForExit` are then inlined into the app's method and only a P/Invoke stub or an internal method is left on the stack, and a `SynchronizationContext.Send` override no longer keeps its context alive; HangSloth recognises those waits and their targets either way. The DeadlockLab database scenarios use PostgreSQL (`DEADLOCKLAB_PG`) and SQL Server (`DEADLOCKLAB_SQLSERVER`); when the lab's login doesn't work there, the tests start them in containers with Testcontainers, or skip those scenarios when Docker isn't available. Each test run builds the samples into a folder of its own under the temp folder, works in a database of its own (`hangsloth_run_*`) on each server, and removes both when it ends; a run that was killed leaves no sample running, the next run removes its folder, and a later run drops its database once it is more than 3 hours old.

## Limitations

- .NET Core 3.1+ / .NET 5+ only (no .NET Framework yet).
- One dump is one moment. A thread that is merely slow can look stuck; if in doubt, take two dumps a few seconds apart.
- `ValueTask`-based awaits from custom `IValueTaskSource`s aren't followed yet.
- `SemaphoreSlim` has no recorded owner, so deadlocks through it are inferred ("most likely"), not proven. For a `SemaphoreSlim` HangSloth needs the waiting method's code in the dump (a full or heap dump has it). When the code doesn't show whether the semaphore is a lock or a "work available" signal and waits for it in a loop, for example a throttle that was passed in from elsewhere or one created closed with room for several (`new SemaphoreSlim(0, 4)`) and opened later, it reports the wait as Info, not as a hang, and says it can't tell. A throttle like that whose slots all leaked can then go without a warning; that is the price of not reporting idle workers as hangs. A single wait that doesn't show it keeps its Warning. When the waiting code can't be followed at all, for example because the runtime inlined the helper that waits, or doesn't show what the semaphore is, HangSloth reports what the semaphore shows: a Warning for a wait nothing else could end, and a possible deadlock (a Warning, never Critical) for threads waiting for each other's taken one-slot semaphores. Workers that each wait for a signal of their own through such a helper look the same, so they get that Warning too. Semaphores kept in locals or parameters (`first.Wait(); second.Wait();`) are never counted as held, so a deadlock through them is two Warnings, not a deadlock. The same goes for a method that works on two objects of its own class when the dump doesn't show for certain which of them is `this`. A release the waiting code doesn't show at all can't be seen: by another thread, through an object that was handed the semaphore or its owner somewhere else, or in an override of a virtual or interface method the code calls. The deadlock sentence names the line of the take, so that is the place to check. A wait with a timeout in such a circle is reported as a deadlock too, although it ends when the timeout runs out. Nor is one taken in a helper the method calls, such as a `using` scope struct whose constructor waits: threads crossing through such helpers are a possible deadlock (a Warning). A semaphore reached through a property or an indexer is not followed either, so a leak through one is a Warning, and threads crossing through two are a possible deadlock. `ReaderWriterLockSlim` readers are found from each thread's own lock counts; when the dump doesn't have those, they're only counted.
- For database waits HangSloth sees only the app's side. It names the blocked SQL, the sessions and any idle open transaction, and gives the query to confirm it on the server; it doesn't query the database itself. Transaction and pool details are read from Npgsql and Microsoft.Data.SqlClient; other drivers get the SQL text only.
- Waiting on a `WaitHandle` (e.g. `Timer.Dispose(WaitHandle)`) can't be linked to the thread that would signal it, so those cycles show up as lock contention with a hint.

## License

MIT
