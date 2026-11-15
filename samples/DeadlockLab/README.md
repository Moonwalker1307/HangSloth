# DeadlockLab

A small, deliberately broken "shop" app with 29 ways to hang: textbook lock-ordering deadlocks, the
hidden kind that hide in callbacks, libraries, initializers, semaphores, queues and UI dispatching, and
deadlocks that involve a real PostgreSQL database. It's HangSloth's test bed: every scenario declares which finding HangSloth
should report, and the end-to-end tests (`tests/HangSloth.Tests/DeadlockLabTests.cs`) run them all.

```sh
dotnet run --project samples/DeadlockLab -c Release -- transfer &   # prints READY once it's stuck
hangsloth $!

dotnet run --project samples/DeadlockLab -- --list                  # every scenario, tab-separated
scripts/try-deadlock-lab.sh                                         # run them all, print a scoreboard
```

Database scenarios need PostgreSQL. Set `DEADLOCKLAB_PG` to an Npgsql connection string (default
`Host=localhost;Username=postgres;Password=postgres;Database=postgres`). Each scenario creates its own
`lab_*` table. The quickest way to get one: `docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:16`.

## Scenarios

| Scenario | Kind | What's wrong | HangSloth reports |
|---|---|---|---|
| `transfer` | obvious | Two transfers lock the same two accounts in opposite order. | `lock-deadlock`, naming each lock by its account (`Account.Sync (Id = "alice")`) |
| `dining-philosophers` | obvious | Three threads, three forks, each holds one and waits for the next. | `lock-deadlock` (3-thread cycle) |
| `event-callback` | hidden | `OrderBook` locks itself and calls `Inventory`; `Inventory` raises `StockChanged` under its own lock and the handler locks `OrderBook`. | `lock-deadlock` across the two classes |
| `library-sync-over-async` | hidden | A "library" sync wrapper blocks on async code; `ConfigureAwait(false)` is only on the last await, three calls deep. | `sync-over-async-deadlock`, listing every method that captures the context |
| `semaphore-order` | hidden | Two `SemaphoreSlim(1, 1)` "locks" taken in opposite order. | `semaphore-deadlock` |
| `async-semaphore-reentry` | hidden | An async cache holds its `SemaphoreSlim` and calls a refresh method that waits on it again. | `semaphore-wait`, pointing out the same object earlier in the chain |
| `rwlock-vs-lock` | hidden | `ConfigStore` holds its `ReaderWriterLockSlim` write lock while logging; `AuditLog` holds its lock while reading the config. | `lock-deadlock` between a write lock and a monitor |
| `lazy-init` | hidden | A `Lazy<T>` factory waits for a `Task.Run` that reads the same `Lazy<T>.Value`. | `wait-cycle` (lock + task) |
| `static-constructor` | hidden | A static constructor waits for a `Task.Run` that touches the same class. | `type-initializer-deadlock` |
| `lock-then-wait` | obvious | A thread holds a lock and `.Result`s a `Task.Run` that needs that lock. | `wait-cycle` (lock + task) |
| `thread-join-under-lock` | hidden | `Stop()` joins the worker while holding the lock the worker needs to finish. | `wait-cycle` (lock + join) |
| `timer-dispose` | hidden | `Stop()` holds a lock while waiting for the timer to drain; the in-flight callback waits for that lock. | `lock-contention`, flagging the timer callback (the link between a `WaitHandle` and a timer isn't in the dump) |
| `parallel-foreach-under-lock` | hidden | A thread holds a lock and runs `Parallel.ForEach`; the loop body, on other threads, takes the same lock. | `wait-cycle` (lock + Parallel worker) |
| `ui-invoke` | hidden | The UI thread `.Wait()`s for a background export, which `Send`s a status update to the UI thread (`Control.Invoke` style). | `wait-cycle` (task + `SynchronizationContext.Send`) |
| `inline-continuation` | hidden | `TaskCompletionSource.SetResult` under a lock runs the awaiting method inline; that continuation needs a lock another thread holds while it waits for the first. | `lock-deadlock`, pointing out the continuation that ran inside `SetResult` |
| `throttle-reentry` | hidden | A `SemaphoreSlim(3)` throttle: each call holds a slot and makes a nested call through the same throttle. Three concurrent calls use every slot. | `semaphore-wait` (Critical: every slot held by a nested waiter) |
| `bounded-queue` | hidden | A publisher holds a lock while adding to a full `BlockingCollection`; the only consumer needs that lock before it takes the next item. | `bounded-queue-deadlock` |
| `lost-pulse` | hidden | A hand-rolled queue pulses only when the consumer is already waiting; the item arrived first, so the consumer waits forever. | `monitor-wait`, noting the queue already holds an item |
| `healthy` | control | Short locks and a timer, nothing wrong. | nothing |
| `db-forgotten-transaction` | obvious | One thread updates a row and waits for an approval without committing; another updates the same row. | `database-wait` naming the idle transaction and the blocked SQL |
| `db-deadlock-retry` | obvious | Two transactions update two rows in opposite order; PostgreSQL aborts one, the app retries the same way. | `database-wait` (possible database deadlock) |
| `db-app-lock-cycle` | hidden | Thread A holds an app lock and waits for a row lock; thread B holds that row lock in a transaction and waits for the app lock. Neither .NET nor PostgreSQL sees the cycle. | `database-deadlock` |
| `db-pool-exhaustion` | hidden | Each request holds a connection and opens a second one for audit logging; with more requests than pool slots, nobody gets one. | `connection-pool-exhausted` (pool deadlock) |
| `db-async-row-lock` | hidden | Like the forgotten transaction, but the blocked update is async and the caller blocks on it. | `database-wait`, following the async chain into the driver to get the SQL |
| `db-migration-lock` | hidden | A migration runs `ALTER TABLE` in a transaction and waits before committing; plain `SELECT`s queue behind its table lock. | `database-wait` (reads blocked by a table lock) |
| `db-advisory-lock` | hidden | A job takes a session-level `pg_advisory_lock`, never unlocks, and keeps the connection in a field; the next scheduler waits forever. No transaction involved. | `database-wait`, naming the idle connection in `NightlyJob._leaderConnection` |
| `db-starvation` | obvious | Controller actions block on async database calls with `.Result`; requests hold every pool thread. | `threadpool-starvation`, with the SQL they wait for |
| `db-async-pool-exhaustion` | hidden | The async version of the nested-connection pool deadlock: no thread is blocked on the database, every request awaits a second connection while holding one. | `connection-pool-exhausted`, found from the paused async methods on the heap |
| `db-healthy` | control | An idle app with an open pooled connection. | nothing |

A database deadlock that stays inside the database (`db-deadlock-retry`) is detected by the database
itself, which aborts one transaction. HangSloth only sees the app's side, so it reports the two
transactions waiting on each other's rows as a *possible* deadlock and shows the SQL to confirm it on
the server (`pg_blocking_pids`).

## Before and after

When the lab was written, HangSloth explained 7 of the first 19 scenarios, and after the first round
of fixes, 4 of the 10 added later. Most misses were reported as plain
lock contention ("held by Thread 1, which is blocked on .Wait()"), which is true but doesn't say
it's a deadlock, or not at all (semaphores, static constructors, everything involving the database).
It now explains all 29. The changes are described in the pull requests that added the scenarios.
