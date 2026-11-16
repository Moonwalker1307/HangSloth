using System.Text.Json;
using HangSloth.Analysis;
using HangSloth.Model;
using HangSloth.Reporting;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Pins the JSON report: people parse it, so a renamed or removed field must fail a test and be a decision.
/// Adding a field is allowed, and does not change schemaVersion.
public class JsonShapeTests
{
    private static AnalysisReport Report()
    {
        var snapshot = new ProcessSnapshot
        {
            Source = "dump.dmp",
            RuntimeVersion = "10.0.1",
            Threads = [Thread(1, [Fx("System.Threading.Monitor", "Wait"), App("MyApp.OrderController", "Get", "OrderController.cs", 10)], "main") with { OSThreadId = 0x2a }],
            Warnings = ["PDB for MyApp.dll was not found."],
        };
        var finding = new Finding
        {
            Id = "demo", Severity = Severity.Warning, Title = "Demo", Summary = "Thread 1 waits.", Details = ["A detail."], Advice = "Stop.",
            ThreadIds = [1], Location = new SourceLocation("/src/OrderController.cs", 10),
        };
        return new AnalysisReport(snapshot, [finding]) { FailedDetectors = ["demo-check"] };
    }

    // The exact text; line endings are compared as "\n" because the serializer writes the platform's.
    private const string Expected = """
        {
          "schemaVersion": 1,
          "tool": "hangsloth",
          "version": "1.2.3",
          "source": "dump.dmp",
          "runtimeVersion": "10.0.1",
          "heapAvailable": true,
          "warnings": [
            "PDB for MyApp.dll was not found."
          ],
          "failedDetectors": [
            "demo-check"
          ],
          "findings": [
            {
              "id": "demo",
              "severity": "warning",
              "title": "Demo",
              "summary": "Thread 1 waits.",
              "details": [
                "A detail."
              ],
              "advice": "Stop.",
              "threadIds": [
                1
              ],
              "location": {
                "file": "/src/OrderController.cs",
                "line": 10,
                "column": 0,
                "fileName": "OrderController.cs"
              }
            }
          ],
          "snapshot": {
            "source": "dump.dmp",
            "runtimeVersion": "10.0.1",
            "threads": [
              {
                "managedId": 1,
                "osThreadId": 42,
                "name": "main",
                "isThreadPoolWorker": false,
                "isFinalizer": false,
                "isBackground": false,
                "frames": [
                  {
                    "typeName": "System.Threading.Monitor",
                    "methodName": "Wait",
                    "moduleName": "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll",
                    "isRuntimeFrame": false,
                    "fullName": "System.Threading.Monitor.Wait"
                  },
                  {
                    "typeName": "MyApp.OrderController",
                    "methodName": "Get",
                    "moduleName": "/app/MyApp.dll",
                    "source": {
                      "file": "/src/OrderController.cs",
                      "line": 10,
                      "column": 0,
                      "fileName": "OrderController.cs"
                    },
                    "isRuntimeFrame": false,
                    "fullName": "MyApp.OrderController.Get"
                  }
                ],
                "label": "Thread 1 (\"main\")"
              }
            ],
            "locks": [],
            "semaphores": [],
            "asyncPoolWaits": [],
            "warnings": [
              "PDB for MyApp.dll was not found."
            ],
            "heapAvailable": true
          }
        }
        """;

    [Fact]
    public void The_json_report_has_this_exact_shape()
    {
        Assert.Equal(Expected.ReplaceLineEndings("\n"), JsonReporter.Serialize(Report(), "1.2.3").ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_top_level_has_the_versions_the_verdict_inputs_and_the_warnings_before_the_bulk()
    {
        using var doc = JsonDocument.Parse(JsonReporter.Serialize(Report(), "1.2.3"));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["schemaVersion", "tool", "version", "source", "runtimeVersion", "heapAvailable", "warnings", "failedDetectors", "findings", "snapshot"], names);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonReporter.SchemaVersion, 1);
        Assert.Equal("hangsloth", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("1.2.3", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal("PDB for MyApp.dll was not found.", doc.RootElement.GetProperty("warnings")[0].GetString());
        Assert.Equal("demo-check", doc.RootElement.GetProperty("failedDetectors")[0].GetString());
    }

    [Fact]
    public void Without_a_version_the_report_still_names_one()
    {
        using var doc = JsonDocument.Parse(JsonReporter.Serialize(Report()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("version").GetString()));
    }

    [Fact]
    public void The_library_and_the_command_line_have_the_same_version()
    {
        // The version is set once for every project (Directory.Build.props), so a program that calls the library
        // gets the version the command line would report.
        static string? Of(System.Reflection.Assembly assembly) => assembly.GetName().Version?.ToString(3);
        Assert.Equal(Of(typeof(HangSloth.Cli.Program).Assembly), Of(typeof(HangSloth.Reporting.JsonReporter).Assembly));

        using var doc = JsonDocument.Parse(JsonReporter.Serialize(Report()));
        Assert.StartsWith(Of(typeof(HangSloth.Cli.Program).Assembly)!, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void A_clean_report_has_empty_lists_not_missing_ones()
    {
        using var doc = JsonDocument.Parse(JsonReporter.Serialize(new AnalysisReport(Process(Thread(1, [Fx("A", "B")])), []), "1.0.0"));
        Assert.Equal(0, doc.RootElement.GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("failedDetectors").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void Non_ascii_text_is_kept_as_it_is()
    {
        var json = JsonReporter.Serialize(new AnalysisReport(Process(Thread(1, [Fx("A", "B")], "заказ-0 ✓")), []), "1.0.0");
        Assert.Contains("\"name\": \"заказ-0 ✓\"", json);
    }
}
