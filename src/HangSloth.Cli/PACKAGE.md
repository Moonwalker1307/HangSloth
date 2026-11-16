# HangSloth

**HangSloth** is a hang explainer for .NET. Point it at a hung process or a memory dump and it tells you, in plain language, what's wrong and which line of your code to look at.

```sh
dotnet tool install -g HangSloth

hangsloth <pid>          # capture a dump of a running .NET process and analyze it
hangsloth <dump-file>    # analyze a memory dump (dotnet-dump, createdump, procdump, Task Manager)
```

```text
1. ✖ Deadlock: sync-over-async
   Thread 1 is blocking with .Result on a task that's waiting for the same SynchronizationContext.
   Classic sync-over-async, in OrderController.cs:10.
   Fix: Make the caller async and await the task instead of using .Result. ...
```

It explains sync-over-async and lock deadlocks, wait cycles across locks, tasks, threads and semaphores, lock contention, thread pool starvation, blocked HTTP calls and child processes, full queues, and database waits (PostgreSQL and SQL Server), with file and line numbers from your portable PDBs.

Exit codes: `0` nothing above informational, `2` a warning or critical finding, `1` an error, or nothing found in a dump without the managed heap or with a check that failed.

Useful options: `--symbols <dir>` (the app's `.dll` and `.pdb` files), `--dac <path>` (the runtime's debugging library, for a dump from another machine whose runtime version is not installed here), `--json`, `--all-threads`, `--keep-dump <path>`.

Needs the .NET 10 runtime. Dumps of .NET (Core) 3.1+ apps can be analyzed; .NET Framework is not supported.

The full documentation, what each finding means, and how to analyze a dump from another machine are in the [repository](https://github.com/Moonwalker1307/HangSloth).
