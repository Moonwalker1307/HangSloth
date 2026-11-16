// DeadlockLab: a small "shop" app riddled with deadlocks, from the textbook kind to the
// kind that takes a senior engineer an afternoon. Each scenario hangs the process in one
// specific way and prints "READY" once the hang is in place, so HangSloth (or you, with
// dotnet-dump) can take a look.
//
//   DeadlockLab <scenario>     run one scenario
//   DeadlockLab --list         list scenarios as tab-separated: name, area, difficulty, expected finding ids, story,
//                              finding ids it must not report
//   DeadlockLab --lab-db postgres|sqlserver check|create <name>|drop <name>|list
//                              for the tests: log in with the lab's connection settings, or manage a test run's database
//
// Database scenarios need PostgreSQL. Set DEADLOCKLAB_PG to an Npgsql connection string
// (default: Host=localhost;Username=postgres;Password=postgres;Database=postgres).
using DeadlockLab;

if (args is ["--list"])
{
    foreach (var s in Scenarios.All)
        Console.WriteLine($"{s.Name}\t{s.Area}\t{s.Difficulty}\t{(s.Expected.Length == 0 ? "-" : string.Join(",", s.Expected))}\t{s.Story}\t{(s.MustNotReport is { Length: > 0 } no ? string.Join(",", no) : "-")}");
    return 0;
}

if (args is ["--lab-db", var kind, var command, .. var name] && name.Length <= 1)
    return LabDatabases.Run(kind, command, name.FirstOrDefault());

if (args is ["--spew"])
{
    DeadlockLab.Code.ReportExporter.Spew(); // the child process of the child-process-pipe scenario
    return 0;
}

var scenario = args.Length == 1 ? Scenarios.All.FirstOrDefault(s => s.Name == args[0]) : null;
if (scenario is null)
{
    Console.Error.WriteLine("usage: DeadlockLab <scenario> | --list");
    Console.Error.WriteLine();
    foreach (var s in Scenarios.All)
        Console.Error.WriteLine($"  {s.Name,-28} {s.Area,-9} {s.Difficulty,-8} {s.Story}");
    return 2;
}

scenario.Run();
return 0;
