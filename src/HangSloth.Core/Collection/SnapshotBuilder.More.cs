using HangSloth.Analysis;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

// HTTP requests in async chains, cancellation callbacks, and child processes.
public sealed partial class SnapshotBuilder
{
    // ---------------------------------------------------------------- HttpClient

    /// HttpClient's own async methods keep the request in a local (or parameter) field; the client is their `this`.
    private static HttpCall? ReadHttpCall(FieldReader stateMachine, ClrType smType, ClrObject self)
    {
        if (smType.Name?.StartsWith("System.Net.Http.HttpClient+", StringComparison.Ordinal) != true) return null;
        foreach (var field in smType.Fields)
        {
            if (!field.IsObjectReference) continue;
            ClrObject value;
            try { value = stateMachine.ReadObject(field); }
            catch { continue; }
            if (value.IsNull || value.Type?.Name != "System.Net.Http.HttpRequestMessage") continue;

            var method = value.TryReadObjectField("_method", out var m) && !m.IsNull && m.TryReadObjectField("_method", out var name) ? name.AsString() : null;
            var uri = value.TryReadObjectField("_requestUri", out var u) && !u.IsNull && u.TryReadObjectField("_string", out var s) ? s.AsString() : null;
            TimeSpan? timeout = null;
            var infinite = false;
            if (!self.IsNull && self.Type?.Name == "System.Net.Http.HttpClient" && self.Type.GetFieldByName("_timeout") is { } tf)
            {
                try
                {
                    var ticks = self.ReadValueTypeField(tf).ReadField<long>("_ticks");
                    infinite = ticks == Timeout.InfiniteTimeSpan.Ticks;
                    if (!infinite) timeout = TimeSpan.FromTicks(ticks);
                }
                catch
                {
                    // Unknown timeout.
                }
            }
            return new HttpCall(method ?? "?", uri ?? "?", timeout, infinite);
        }
        return null;
    }

    private static HttpPoolInfo? ReadHttpPool(ClrObject self)
    {
        if (self.Type?.Name != "System.Net.Http.HttpConnectionPool") return null;
        var max = self.ReadInt("_maxHttp11Connections");
        var open = self.ReadInt("_associatedHttp11ConnectionCount");
        if (max is null || open is null) return null;
        var server = "the server";
        if (self.TryReadObjectField("_originAuthority", out var authority) && !authority.IsNull)
        {
            var host = authority.TryReadObjectField("<IdnHost>k__BackingField", out var h) && !h.IsNull ? h.AsString() : null;
            var port = authority.ReadInt("<Port>k__BackingField");
            if (host is not null) server = port is { } p ? $"{host}:{p}" : host;
        }
        return new HttpPoolInfo(server, max.Value, open.Value);
    }

    private static bool HoldsHttpResponse(FieldReader stateMachine, ClrType smType)
    {
        foreach (var field in smType.Fields)
        {
            if (!field.IsObjectReference) continue;
            try
            {
                var value = stateMachine.ReadObject(field);
                if (!value.IsNull && value.Type?.Name == "System.Net.Http.HttpResponseMessage"
                    && !(value.TryReadField<bool>("_disposed", out var disposed) && disposed))
                    return true;
            }
            catch
            {
                // Keep looking.
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- cancellation callbacks

    /// CancellationTokenRegistration.Dispose waits in Registrations.WaitForCallbackToComplete for the
    /// thread that is running the callback right now, and the registrations record which thread that is.
    private static int? FindCallbackThread(List<ClrStackRoot> roots, IReadOnlyList<StackFrameInfo> frames, int self)
    {
        if (!frames.Any(f => f.MethodName == "WaitForCallbackToComplete" && f.TypeName.StartsWith("System.Threading.CancellationTokenSource", StringComparison.Ordinal)))
            return null;
        foreach (var root in roots)
        {
            var obj = root.Object;
            if (obj.IsNull) continue;
            var registrations = obj.Type?.Name == "System.Threading.CancellationTokenSource+Registrations" ? obj
                : obj.Type?.Name == "System.Threading.CancellationTokenSource" && obj.TryReadObjectField("_registrations", out var r) ? r
                : default;
            if (registrations.IsNull) continue;
            var thread = registrations.ReadInt("ThreadIDExecutingCallbacks");
            if (thread is > 0 && thread != self) return thread;
        }
        return null;
    }

    // ---------------------------------------------------------------- child processes

    private const int StreamReadModeUndefined = 0;

    private static ProcessWait? FindProcessWait(List<ClrStackRoot> roots, IReadOnlyList<StackFrameInfo> frames)
    {
        if (!frames.Any(f => f.TypeName == "System.Diagnostics.Process" && f.MethodName.StartsWith("WaitForExit", StringComparison.Ordinal))) return null;
        foreach (var root in roots)
        {
            var p = root.Object;
            if (p.IsNull || p.Type?.Name != "System.Diagnostics.Process") continue;
            var unread = new List<string>();
            if (Unread(p, "_standardOutput", "_outputStreamReadMode")) unread.Add("standard output");
            if (Unread(p, "_standardError", "_errorStreamReadMode")) unread.Add("standard error");
            return new ProcessWait(p.ReadInt("_processId") ?? 0, CommandLine(p), unread);
        }
        return null;
    }

    /// Redirected (the reader exists) but nobody has touched it: neither StandardOutput nor BeginOutputReadLine.
    private static bool Unread(ClrObject process, string readerField, string modeField) =>
        process.TryReadObjectField(readerField, out var reader) && !reader.IsNull && process.ReadInt(modeField) == StreamReadModeUndefined;

    private static string CommandLine(ClrObject process)
    {
        if (!process.TryReadObjectField("_startInfo", out var info) || info.IsNull) return "a child process";
        var file = info.TryReadObjectField("_fileName", out var f) && !f.IsNull ? f.AsString() ?? "" : "";
        var args = new List<string>();
        if (info.TryReadObjectField("_arguments", out var a) && !a.IsNull && a.AsString() is { Length: > 0 } text) args.Add(text);
        if (info.TryReadObjectField("_argumentList", out var list) && !list.IsNull && list.TryReadObjectField("items", out var items) && !items.IsNull
            && items.TryReadObjectField("_items", out var array) && !array.IsNull)
        {
            var size = items.ReadInt("_size") ?? 0;
            var arr = array.AsArray();
            for (var i = 0; i < Math.Min(size, Math.Min(arr.Length, 20)); i++)
                if (arr.GetObjectValue(i) is { IsNull: false } item) args.Add(item.AsString() ?? "");
        }
        return string.Join(" ", new[] { Path.GetFileName(file) }.Concat(args.Select(x => Path.IsPathRooted(x) ? Path.GetFileName(x) : x)));
    }
}
