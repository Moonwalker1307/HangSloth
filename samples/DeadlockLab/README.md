# DeadlockLab

A small, deliberately broken "shop" app with 61 ways to hang: textbook lock-ordering deadlocks, the
hidden kind that hide in callbacks, libraries, initializers, semaphores, queues, UI dispatching, HTTP
clients, finalizers and child processes, and deadlocks that involve a real database: PostgreSQL (with and
without EF Core, including large selects that hold locks) and SQL Server. It's HangSloth's test bed: every scenario declares which finding HangSloth
should report, and the end-to-end tests (`tests/HangSloth.Tests/DeadlockLabTests.cs`) run them all.

```sh
dotnet build -c Release                                                # once, from the repository root
dotnet samples/DeadlockLab/bin/Release/net10.0/DeadlockLab.dll transfer &   # prints READY once it's stuck
sleep 2; hangsloth $!
kill $!

dotnet run --project samples/DeadlockLab -- --list                  # every scenario, tab-separated
scripts/try-deadlock-lab.sh                                         # run them all, print a scoreboard
```

Database scenarios need PostgreSQL. Set `DEADLOCKLAB_PG` to an Npgsql connection string (default
`Host=localhost;Username=postgres;Password=postgres;Database=postgres`). Each scenario creates its own
`lab_*` table. The quickest way to get one: `docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:16`.

The `sqlserver-*` scenarios need SQL Server. Set `DEADLOCKLAB_SQLSERVER` to a SqlClient connection string
(default `Server=localhost;User Id=sa;Password=Lab_Passw0rd!;TrustServerCertificate=true`), or run
`docker run -d -p 1433:1433 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Lab_Passw0rd!' mcr.microsoft.com/mssql/server:2022-latest`.
`scripts/try-deadlock-lab.sh` skips them with `SKIP_SQLSERVER=1`.

The tests don't need any of this set up: with no connection string and no server that accepts the default
login, they start PostgreSQL and SQL Server containers themselves with Testcontainers (when Docker can run
Linux containers), and skip those scenarios otherwise. Each test run creates a database of its own on the
server (`hangsloth_run_<time>_<id>`, dropped when the run ends), so two runs don't share the `lab_*` tables
(a login that may not create databases works in the database it names, as before).
They use `DeadlockLab --lab-db postgres|sqlserver check|create <name>|drop <name>|list` for that, with the
same connection settings as the scenarios.

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
| `rwlock-upgrade-slow-reader` | hidden | A price refresh in upgradeable mode waits to upgrade while a slow report still reads; the report finishes after two minutes. Slow, not deadlocked. | `lock-contention` naming the reader, and no `lock-deadlock` |
| `rwlock-readers-block-writer` | obvious | Three slow reports hold a `ReaderWriterLockSlim` for reading; a price update waits for the write lock. | `lock-contention` naming the three readers, not an orphaned lock |
| `lazy-init` | hidden | A `Lazy<T>` factory waits for a `Task.Run` that reads the same `Lazy<T>.Value`. | `wait-cycle` (lock + task) |
| `static-constructor` | hidden | A static constructor waits for a `Task.Run` that touches the same class. | `type-initializer-deadlock` |
| `lock-then-wait` | obvious | A thread holds a lock and `.Result`s a `Task.Run` that needs that lock. | `wait-cycle` (lock + task) |
| `thread-join-under-lock` | hidden | `Stop()` joins the worker while holding the lock the worker needs to finish. | `wait-cycle` (lock + join) |
| `timer-dispose` | hidden | `Stop()` holds a lock while waiting for the timer to drain; the in-flight callback waits for that lock. | `lock-contention`, flagging the timer callback (the link between a `WaitHandle` and a timer isn't in the dump) |
| `parallel-foreach-under-lock` | hidden | A thread holds a lock and runs `Parallel.ForEach`; the loop body, on other threads, takes the same lock. | `wait-cycle` (lock + Parallel worker) |
| `ui-invoke` | hidden | The UI thread `.Wait()`s for a background export, which `Send`s a status update to the UI thread (`Control.Invoke` style). | `wait-cycle` (task + `SynchronizationContext.Send`) |
| `inline-continuation` | hidden | `TaskCompletionSource.SetResult` under a lock runs the awaiting method inline; that continuation needs a lock another thread holds while it waits for the first. | `lock-deadlock`, pointing out the continuation that ran inside `SetResult` |
| `throttle-reentry` | hidden | A `SemaphoreSlim(3)` throttle: each call holds a slot and makes a nested call through the same throttle. Three concurrent calls use every slot. | `semaphore-wait` (Critical: every slot held by a nested waiter) |
| `semaphore-leak` | obvious | An export takes its `SemaphoreSlim(1, 1)` and returns early without `Release()`; the next two exports wait forever. | `semaphore-wait` (Warning: no free slots, nothing else running) |
| `semaphore-slow-holder` | hidden | A cache's `LoadAsync` holds its gate while a carrier call takes minutes; the next lookup blocks on `.Result` behind it. The outer `GetAsync` never takes the gate, so this is a slow holder, not a re-entry. | `semaphore-wait` (Warning, not "already holds") |
| `semaphore-transfer` | obvious | Two warehouses move stock to each other at once; each move takes its own warehouse's `SemaphoreSlim(1, 1)`, then the other one's. | `semaphore-deadlock`, with the semaphore each thread took |
| `semaphore-fast-path` | hidden | Freight quotes try their gate with `WaitAsync(0)` and wait for it on a slow path when it's busy; the first quote holds it for minutes. The failed `WaitAsync(0)` took nothing. | `semaphore-wait` (Warning, not "already holds") |
| `semaphore-lock-class` | hidden | Picking and packing take two locks of a small lock class around a `SemaphoreSlim(1, 1)` (`Enter()`, `Exit()`) in opposite order. | `semaphore-wait` (two Warnings: the wait is in `Enter()`, which took nothing). In optimized code, where `Enter()` is inlined, `semaphore-deadlock` as a possible deadlock (Warning) |
| `semaphore-injected-lock` | hidden | Billing takes two gates of a small lock class that is handed its `SemaphoreSlim(1, 1)` in opposite order. | `semaphore-deadlock` (Warning, "Possible deadlock": the wait is in `Enter()`, which took nothing, and the code that made the semaphores isn't in the class) |
| `semaphore-optional-lock` | hidden | A rebuild holds the row lock and waits for a table lock a truncate never released; a plain save, which takes the table lock only when it's exclusive, waits for the row lock. | `semaphore-wait`; must not report `semaphore-deadlock` |
| `semaphore-queued-moves` | hidden | Loading bays move pallets into two dispatch bays that are closed for a stocktake; each move keeps its own bay's `SemaphoreSlim(1, 1)` and waits for the dispatch bay's. Four moves queue behind two stocktakes. | `semaphore-wait`; must not report `semaphore-deadlock`, also not in optimized code, where the frame of the move keeps only the dispatch bay alive (two of the moves name it by its own class, derived from the loading bay's) |
| `semaphore-release-helper` | hidden | Counting and packing each take one of a depot's two locks and give it back through an extension method (`ReleaseQuietly()`) before they take the other one; the month-end closing then takes both and never finishes. | `semaphore-wait`; must not report `semaphore-deadlock` (a release through a method of another class is still a release) |
| `bounded-queue` | hidden | A publisher holds a lock while adding to a full `BlockingCollection`; the only consumer needs that lock before it takes the next item. | `bounded-queue-deadlock` |
| `lost-pulse` | hidden | A hand-rolled queue pulses only when the consumer is already waiting; the item arrived first, so the consumer waits forever. | `monitor-wait`, noting the queue already holds an item |
| `idle-monitor-consumer` | control | A mail sender waits in `Monitor.Wait` for its empty outbox, in the textbook loop, and a receipt printer waits while its counter of due receipts is 0; their logs of sent mails and printed receipts aren't empty. | nothing |
| `http-connection-limit` | hidden | `HttpClient` with `MaxConnectionsPerServer = 2`; each page request keeps its first response unread while it makes a second request to the same server. | `http-connection-limit`, naming the method that holds the response |
| `http-no-response` | obvious | `.Result` on a POST to a payment gateway that accepted the connection and never answers, with `Timeout = Infinite`. | `http-wait`, with the method and URL |
| `http-connect-stall` | hidden | `.Result` on an HTTPS call to a supplier that accepts the TCP connection and never answers the TLS handshake; no `ConnectTimeout`. | `http-connect`: a connection that is never established, not a full pool |
| `http-busy-connections` | hidden | A page keeps a buffered response in a local while it waits for a connection that two slow report downloads are using (`MaxConnectionsPerServer = 2`). | `http-connection-limit` as a warning, not a deadlock: the buffered response holds no connection |
| `db-connect-stall` | hidden | Startup opens the first PostgreSQL connection with `Timeout=0` to a server that accepts the TCP connection and never answers. No database needed. | `database-connect` with the host and the timeout, not a full pool |
| `sqlclient-connect-stall` | hidden | The same with SQL Server (`Connect Timeout=0`). No database needed. | `database-connect` |
| `timer-overlap` | hidden | A timer fires every 100 ms; its callback holds a lock for seconds, so callbacks pile up until the thread pool is full. | `threadpool-starvation`, pointing at the overlapping timer callback |
| `cancellation-dispose` | hidden | `CancellationTokenRegistration.Dispose` under a lock waits for the running callback, which waits for that lock. | `wait-cycle` (lock + cancellation callback) |
| `finalizer-wait` | hidden | `GC.WaitForPendingFinalizers` under a lock that a finalizer takes. | `wait-cycle` (lock + finalizer thread) |
| `child-process-pipe` | hidden | `WaitForExit` on a child with redirected, unread output; the child fills the pipe and blocks. | `process-pipe-deadlock`, with the child's command line and pid |
| `test-blocks-on-async` | control | Two tests block on a slow async call with `.Result` under xunit's `AsyncTestSyncContext`, one with `ConfigureAwait(false)` and one without; the context runs continuations on the thread pool. Slow, not deadlocked. | nothing at warning level, and no `sync-over-async-deadlock` or `sync-over-async-risk` |
| `healthy` | control | Short locks, a timer and idle `BlockingCollection` consumers waiting for work, nothing wrong. | nothing |
| `healthy-signals` | control | Two flushers wait in a loop on their own `SemaphoreSlim(0, 1)` wake-up signal, and a sender on a `SemaphoreSlim(0)` that an idle timer releases once per message. | nothing |
| `db-forgotten-transaction` | obvious | One thread updates a row and waits for an approval without committing; another updates the same row. | `database-wait` naming the idle transaction and the blocked SQL |
| `db-deadlock-retry` | obvious | Two transactions update two rows in opposite order; PostgreSQL aborts one, the app retries the same way. | `database-wait` (possible database deadlock) |
| `db-app-lock-cycle` | hidden | Thread A holds an app lock and waits for a row lock; thread B holds that row lock in a transaction and waits for the app lock. Neither .NET nor PostgreSQL sees the cycle. | `database-deadlock` |
| `db-pool-exhaustion` | hidden | Each request holds a connection and opens a second one for audit logging; with more requests than pool slots, nobody gets one. | `connection-pool-exhausted` (pool deadlock) |
| `db-async-row-lock` | hidden | Like the forgotten transaction, but the blocked update is async and the caller blocks on it. | `database-wait`, following the async chain into the driver to get the SQL |
| `db-migration-lock` | hidden | A migration runs `ALTER TABLE` in a transaction and waits before committing; plain `SELECT`s queue behind its table lock. | `database-wait` (reads blocked by a table lock) |
| `db-advisory-lock` | hidden | A job takes a session-level `pg_advisory_lock`, never unlocks, and keeps the connection in a field; the next scheduler waits forever. No transaction involved. | `database-wait`, naming the idle connection in `NightlyJob._leaderConnection` |
| `db-starvation` | obvious | Controller actions block on async database calls with `.Result`; requests hold every pool thread. | `threadpool-starvation`, with the SQL they wait for |
| `db-async-pool-exhaustion` | hidden | The async version of the nested-connection pool deadlock: no thread is blocked on the database, every request awaits a second connection while holding one. | `connection-pool-exhausted`, found from the paused async methods on the heap |
| `ef-row-lock` | hidden | EF Core: an approval saves an order in a transaction and waits for a fraud check; the warehouse's `SaveChanges` on the same order waits for the row lock. | `database-wait`, finding the open transaction inside EF Core's wrappers |
| `ef-nested-context` | hidden | EF Core: a checkout holds a `DbContext` transaction and logs through a second `DbContext`; three checkouts, two pooled connections. | `connection-pool-exhausted`, with EF-specific advice |
| `db-large-select` | obvious | A cache refresh holds its lock while reading two million rows one by one; every request waits for the lock. | `lock-contention`, with the query and the rows read so far |
| `db-select-blocks-alter` | hidden | An export keeps a reader open on a big `SELECT` and does slow work per row; a migration's `ALTER TABLE` waits for it, and dashboard `SELECT`s queue behind the `ALTER`. | `database-wait` (lock queue on the table) |
| `db-select-for-update` | hidden | A batch job locks a month of invoices with `SELECT ... FOR UPDATE` and processes them slowly; a payment for one of them waits. | `database-wait`, suggesting `SKIP LOCKED` batches |
| `db-healthy` | control | An idle app with an open pooled connection. | nothing |

A database deadlock that stays inside the database (`db-deadlock-retry`) is detected by the database
itself, which aborts one transaction. HangSloth only sees the app's side, so it reports the two
transactions waiting on each other's rows as a *possible* deadlock and shows the SQL to confirm it on
the server (`pg_blocking_pids`).

### SQL Server

| Scenario | Kind | What's wrong | HangSloth reports |
|---|---|---|---|
| `sqlserver-lock-escalation` | hidden | A month-end report sums 20,000 orders under `REPEATABLE READ` and keeps the transaction open; SQL Server escalates its row locks to a table lock, so an order outside the range can't be updated. | `database-wait` (lock escalation), suggesting snapshot isolation |
| `sqlserver-read-blocked` | obvious | A stock adjustment updates a row and waits before committing; under the default `READ COMMITTED` a product page's `SELECT` waits for it. | `database-wait`, pointing at `READ_COMMITTED_SNAPSHOT` |
| `sqlserver-pool-exhaustion` | hidden | Each shipment request holds a connection and opens a second one for its audit entry; with more requests than pool slots (`Max Pool Size=4`), nobody gets one. | `connection-pool-exhausted` (pool deadlock, with the pool's size and connections in use) |

## Before and after

When the lab was written, HangSloth explained 7 of the first 19 scenarios, and after the first round
of fixes, 4 of the 10 added in the second round, and 2 of the 8 in the third. SQL Server connections weren't read at all before the last 5 were added. Most misses were reported as plain
lock contention ("held by Thread 1, which is blocked on .Wait()"), which is true but doesn't say
it's a deadlock, or not at all (semaphores, static constructors, everything involving the database).
It now explains all 61. The changes are described in the pull requests that added the scenarios.
