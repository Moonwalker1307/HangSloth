using System.Diagnostics;

namespace DeadlockLab.Code;

/// Runs a "report generator" child process with redirected output and waits for it to exit
/// before reading the output. The child writes more than the pipe holds and blocks; the parent
/// waits for it to exit. Each waits for the other.
public static class ReportExporter
{
    public static string Export()
    {
        var self = Environment.ProcessPath!;
        var dll = typeof(ReportExporter).Assembly.Location;
        var usesHost = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false };
        if (usesHost) info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--spew");

        using var child = Process.Start(info)!;
        child.WaitForExit(); // BUG: reads the output only after the child exits
        return child.StandardOutput.ReadToEnd();
    }

    /// The child side: a report much bigger than a pipe buffer (64 KB on Linux, 4 KB on Windows).
    public static void Spew()
    {
        var line = new string('x', 1000);
        for (var i = 0; i < 2000; i++) Console.WriteLine(line);
    }
}

internal static class ChildProcessScenario
{
    public static void Run()
    {
        Ready.SignalLater(1500);
        ReportExporter.Export();
    }
}
