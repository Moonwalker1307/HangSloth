using System.Text.Json;
using System.Text.Json.Serialization;
using HangSloth.Analysis;

namespace HangSloth.Reporting;

/// Machine-readable report: findings plus the full snapshot they were derived from.
public static class JsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static string Serialize(AnalysisReport report) => JsonSerializer.Serialize(new
    {
        tool = "hangsloth",
        source = report.Snapshot.Source,
        runtimeVersion = report.Snapshot.RuntimeVersion,
        findings = report.Findings,
        snapshot = report.Snapshot,
    }, Options);
}
