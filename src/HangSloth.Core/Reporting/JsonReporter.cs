using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HangSloth.Analysis;

namespace HangSloth.Reporting;

/// Machine-readable report: findings plus the full snapshot they were derived from.
public static class JsonReporter
{
    /// Bumped when a field is renamed, removed or changes meaning. Adding a field does not change it.
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    /// <param name="toolVersion">The version of the program that produced the report; this library's own when omitted.</param>
    public static string Serialize(AnalysisReport report, string? toolVersion = null) => EscapeInvisible(JsonSerializer.Serialize(new
    {
        schemaVersion = SchemaVersion,
        tool = "hangsloth",
        version = toolVersion ?? LibraryVersion(),
        source = report.Snapshot.Source,
        runtimeVersion = report.Snapshot.RuntimeVersion,
        // False for a dump without the managed heap: an empty findings list then means "could not tell", not "healthy".
        heapAvailable = report.Snapshot.HeapAvailable,
        // The same list as snapshot.warnings, and the checks that failed: findings from those are missing.
        warnings = report.Snapshot.Warnings,
        failedDetectors = report.FailedDetectors,
        findings = report.Findings,
        snapshot = report.Snapshot,
    }, Options));

    /// The serializer already writes control characters below U+0080 as \uXXXX. It leaves the C1 controls (U+0080
    /// to U+009F, which include the one-byte CSI), the line and paragraph separators and the bidirectional
    /// marks and overrides as they are, and a viewer that prints the JSON would act on them. Outside a string
    /// JSON has only ASCII, so every such character found in the text is inside one and can be escaped as it is.
    private static string EscapeInvisible(string json) => Invisible.Replace(json, m => $"\\u{(int)m.Value[0]:X4}");

    private static readonly System.Text.RegularExpressions.Regex Invisible =
        new("[\u0080-\u009F\u061C\u200E\u200F\u2028\u2029\u202A-\u202E\u2066-\u2069]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string LibraryVersion()
    {
        var assembly = typeof(JsonReporter).Assembly;
        var v = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..Math.Min(v.Length, plus + 1 + 7)];
    }
}
