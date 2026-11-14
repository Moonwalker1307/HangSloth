using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Reporting;

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
          -h, --help           Show this help

        Exit codes: 0 = nothing wrong found, 2 = a hang pattern was found, 1 = error.
        """;

    public static int Main(string[] args)
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

        if (options.ShowHelp || options.Target is null)
        {
            Console.WriteLine(Usage);
            return options.ShowHelp ? 0 : 1;
        }

        try
        {
            var load = new LoadOptions { SymbolDirectories = options.SymbolDirectories, DacPath = options.DacPath };
            var snapshot = int.TryParse(options.Target, out var pid) && !File.Exists(options.Target)
                ? DumpLoader.LoadProcess(pid, load, options.KeepDump)
                : DumpLoader.LoadDump(options.Target, load);

            var report = new HangAnalyzer().Analyze(snapshot);
            if (options.Json)
            {
                Console.WriteLine(JsonReporter.Serialize(report));
            }
            else
            {
                var color = !options.NoColor && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
                TextReporter.Write(report, Console.Out, new TextReportOptions { UseColor = color, AllThreads = options.AllThreads });
            }
            return report.Findings.Any(f => f.Severity >= Severity.Warning) ? 2 : 0;
        }
        catch (HangSlothException e)
        {
            Console.Error.WriteLine($"hangsloth: {e.Message}");
            return 1;
        }
    }

    internal sealed record Options
    {
        public string? Target { get; init; }
        public bool Json { get; init; }
        public bool AllThreads { get; init; }
        public bool NoColor { get; init; }
        public bool ShowHelp { get; init; }
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
