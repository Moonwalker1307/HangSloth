using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using HangSloth.Reporting;
using System.Reflection;

namespace HangSloth.Cli;

public static class Program
{
    private const string Usage = """
        HangSloth: find out why a .NET app is stuck.

        Usage:
          hangsloth <dump-file>   [options]   Analyze a memory dump (dotnet-dump, createdump, procdump, Task Manager...)
          hangsloth <pid>         [options]   Capture a dump of a running .NET process and analyze it

        Options:
          --json               Print the findings and the full snapshot as JSON
          --all-threads        Show every thread's stack, not just the ones involved
          --symbols <dir>      Extra folder with the app's .dll/.pdb files (repeatable)
          --dac <path>         The runtime's debugging library (libmscordaccore.so / mscordaccore.dll),
                               for dumps from another machine whose DAC can't be downloaded
          --keep-dump <path>   With a pid: keep the captured dump at this path
          --no-color           Disable colored output
          --version            Show the version
          -h, --help           Show this help

        Exit codes: 0 = nothing wrong found, 2 = a hang pattern was found,
                    1 = error, or nothing found but the dump has no managed heap or a check failed.
        """;

    public static int Main(string[] args) => Run(args);

    /// <param name="loadDump">Replaces reading a dump file; lets tests drive the CLI with a synthetic snapshot.</param>
    /// <param name="analyzer">Replaces the default detector list; lets tests inject a failing detector.</param>
    internal static int Run(string[] args, Func<string, LoadOptions, ProcessSnapshot>? loadDump = null, Func<HangAnalyzer>? analyzer = null)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"hangsloth {Version()}");
            return 0;
        }

        if (options.ShowHelp || options.Target is null)
        {
            Console.WriteLine(Usage);
            return options.ShowHelp ? 0 : 1;
        }

        var isPid = int.TryParse(options.Target, out var pid) && !File.Exists(options.Target);
        if (options.KeepDump is not null && !isPid)
        {
            Console.Error.WriteLine("--keep-dump only works with a process id.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 1;
        }

        try
        {
            if (options.SymbolDirectories.FirstOrDefault(d => !Directory.Exists(d)) is { } missing)
                throw new HangSlothException($"Symbols folder not found: {missing}");

            var load = new LoadOptions { SymbolDirectories = options.SymbolDirectories, DacPath = options.DacPath };
            var snapshot = isPid
                ? DumpLoader.LoadProcess(pid, load, options.KeepDump)
                : (loadDump ?? DumpLoader.LoadDump)(options.Target, load);

            var report = (analyzer?.Invoke() ?? new HangAnalyzer()).Analyze(snapshot);
            if (options.Json)
            {
                Console.WriteLine(JsonReporter.Serialize(report));
            }
            else
            {
                var color = !options.NoColor && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
                TextReporter.Write(report, Console.Out, new TextReportOptions { UseColor = color, AllThreads = options.AllThreads });
            }
            if (report.Findings.Any(f => f.Severity >= Severity.Warning)) return 2;
            var incomplete = false;
            if (!report.Snapshot.HeapAvailable)
            {
                // Without the heap most hangs cannot be seen, so finding nothing is not a clean result either.
                Console.Error.WriteLine(report.Snapshot.Warnings.Contains(SnapshotBuilder.HeapNotWalkableWarning)
                    ? "hangsloth: the managed heap in this dump could not be read, so HangSloth cannot tell whether the app is hung. Take another dump a moment later."
                    : "hangsloth: this dump has no managed heap, so HangSloth cannot tell whether the app is hung. Take a full dump (hangsloth <pid>, dotnet-dump collect --type Full or createdump --full).");
                incomplete = true;
            }
            if (report.FailedDetectors.Count > 0)
            {
                // A failed check must not look like a healthy app. Without the heap the report has no
                // "No hang patterns found" to distrust, so the line only adds the failed check.
                var many = report.FailedDetectors.Count > 1;
                var checks = $"the {string.Join(", ", report.FailedDetectors)} {(many ? "checks" : "check")} failed";
                Console.Error.WriteLine(report.Snapshot.HeapAvailable
                    ? $"hangsloth: {checks}, so \"No hang patterns found\" cannot be trusted."
                    : $"hangsloth: {checks} too.");
                incomplete = true;
            }
            return incomplete ? 1 : 0;
        }
        catch (HangSlothException e)
        {
            Console.Error.WriteLine($"hangsloth: {e.Message}");
            if (e.InnerException is not null && Environment.GetEnvironmentVariable("HANGSLOTH_DEBUG") == "1") Console.Error.WriteLine(e.InnerException);
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"hangsloth: unexpected error ({e.GetType().Name}): {e.Message.ReplaceLineEndings(" ")}");
            if (Environment.GetEnvironmentVariable("HANGSLOTH_DEBUG") == "1") Console.Error.WriteLine(e);
            return 1;
        }
    }

    /// The assembly's version; the SDK appends the commit after '+', cut to 7 characters.
    private static string Version()
    {
        var v = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..Math.Min(v.Length, plus + 1 + 7)];
    }

    internal sealed record Options
    {
        public string? Target { get; init; }
        public bool Json { get; init; }
        public bool AllThreads { get; init; }
        public bool NoColor { get; init; }
        public bool ShowHelp { get; init; }
        public bool ShowVersion { get; init; }
        public List<string> SymbolDirectories { get; } = [];
        public string? DacPath { get; init; }
        public string? KeepDump { get; init; }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
                o = a switch
                {
                    "--json" => o with { Json = true },
                    "--all-threads" => o with { AllThreads = true },
                    "--no-color" => o with { NoColor = true },
                    "--version" => o with { ShowVersion = true },
                    "-h" or "--help" or "-?" => o with { ShowHelp = true },
                    "--dac" => o with { DacPath = Value() },
                    "--keep-dump" => o with { KeepDump = Value() },
                    "--symbols" => AddSymbols(o, Value()),
                    _ when a.StartsWith('-') => throw new ArgumentException($"Unknown option: {a}"),
                    _ when o.Target is null => o with { Target = a },
                    _ => throw new ArgumentException($"Unexpected argument: {a}"),
                };
            }
            return o;
        }

        private static Options AddSymbols(Options o, string dir)
        {
            o.SymbolDirectories.Add(dir);
            return o;
        }
    }
}
