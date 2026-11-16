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
        return new HttpPoolInfo(server, max.Value, open.Value, self.ReadInt("_pendingHttp11ConnectionCount"));
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
                    && !(value.TryReadField<bool>("_disposed", out var disposed) && disposed)
                    && StillOwnsConnection(value))
                    return true;
            }
            catch
            {
                // Keep looking.
            }
        }
        return false;
    }

    /// A response keeps its connection only while its body is unread: the content still reads from the
    /// connection's stream (HttpConnectionResponseContent._stream._connection) and hasn't been buffered.
    /// With the default ResponseContentRead the body is buffered and the connection is back in the pool.
    /// Without that evidence (other content types, fields that differ), the response isn't counted.
    private static bool StillOwnsConnection(ClrObject response)
    {
        if (!response.TryReadObjectField("_content", out var content) || content.IsNull
            || content.Type?.Name != "System.Net.Http.HttpConnectionResponseContent") return false;
        if (!content.TryReadObjectField("_bufferedContent", out var buffered) || !buffered.IsNull) return false;
        if (!content.TryReadObjectField("_stream", out var stream) || stream.IsNull) return false;
        return stream.TryReadObjectField("_connection", out var connection) && !connection.IsNull;
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

    private const string ProcessType = "System.Diagnostics.Process";

    private static ProcessWait? FindProcessWait(ClrThread thread, List<ClrStackRoot> roots, IReadOnlyList<StackFrameInfo> frames)
    {
        ClrObject p;
        if (frames.Any(f => f.TypeName == ProcessType && FrameClassifier.FrameworkWait(f) == BlockingKind.ProcessExit))
            p = roots.Select(r => r.Object).FirstOrDefault(IsProcess);
        else if (frames.Any(f => FrameClassifier.FrameworkWait(f) == BlockingKind.ProcessExit))
        {
            // Process.WaitForExit inlined into the caller (Unix leaves ProcessWaitState.WaitForExit): the process is
            // the one Process object the caller's frame holds.
            var inCaller = WaitCallerObjects(thread, roots, BlockingKind.ProcessExit).Where(IsProcess).ToList();
            p = inCaller.Count == 1 ? inCaller[0] : default;
        }
        else if (IsBareWaitHandleWait(frames) && roots.Any(r => IsProcess(r.Object)))
            p = ProcessOfExitWait(thread, roots);
        else
            return null;
        if (p.IsNull) return null;
        var unread = new List<string>();
        if (Unread(p, "_standardOutput", "_outputStreamReadMode")) unread.Add("standard output");
        if (Unread(p, "_standardError", "_errorStreamReadMode")) unread.Add("standard error");
        return new ProcessWait(p.ReadInt("_processId") ?? 0, CommandLine(p), unread);
    }

    private static bool IsProcess(ClrObject o) => !o.IsNull && DerivesFrom(o.Type, ProcessType);

    /// Only WaitHandle's own frames above the app's code: a warmed-up process (tiered PGO) also inlines
    /// ProcessWaitState.WaitForExit (Unix) or Process.WaitForExitCore (Windows) into the app's method.
    private static bool IsBareWaitHandleWait(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameClassifier.FrameworkPrefix(frames);
        return prefix.Count > 0 && prefix.Count < frames.Count && prefix.All(f => f.TypeName == WaitHandleType);
    }

    private const string WaitHandleType = "System.Threading.WaitHandle";

    /// The process whose exit a bare WaitHandle wait is for: the one Process object in the app frame that called
    /// it, when the handle waited on is that process's own exit handle (Unix: its ProcessWaitState's exited event;
    /// Windows: the ProcessWaitHandle that WaitForExitCore makes). Anything else is some other wait.
    private static ClrObject ProcessOfExitWait(ClrThread thread, List<ClrStackRoot> roots)
    {
        try
        {
            var objects = WaitCallerObjects(thread, roots, m => m.Type?.Name == WaitHandleType);
            var processes = objects.Where(IsProcess).ToList();
            if (processes.Count != 1) return default;
            var p = processes[0];
            if (objects.Any(o => o.Type?.Name == "System.Diagnostics.ProcessWaitHandle")) return p;
            if (p.TryReadObjectField("_waitStateHolder", out var holder) && !holder.IsNull
                && holder.TryReadObjectField("_state", out var state) && !state.IsNull
                && state.TryReadObjectField("_exitedEvent", out var exited) && !exited.IsNull)
            {
                var handle = exited.TryReadObjectField("_waitHandle", out var h) && !h.IsNull ? h.Address : 0;
                if (objects.Any(o => o.Address == exited.Address || (handle != 0 && o.Address == handle))) return p;
            }
        }
        catch
        {
            // Fields of another runtime version: the process stays unknown.
        }
        return default;
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
