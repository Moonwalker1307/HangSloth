using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection.Emit;
using HangSloth.Model;

namespace HangSloth.Collection;

/// Maps (module, method token, IL offset) to a source file and line using portable PDBs
/// (side-by-side or embedded). Also maps an async state machine's await state to the
/// line of that await.
public sealed class SourceResolver : IDisposable
{
    private static readonly Guid AsyncMethodSteppingInformation = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");

    private readonly IReadOnlyList<string> _searchDirectories;
    private readonly Dictionary<string, MetadataReaderProvider?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PEReader?> _images = new(StringComparer.OrdinalIgnoreCase);

    public SourceResolver(IEnumerable<string>? searchDirectories = null)
    {
        _searchDirectories = (searchDirectories ?? []).Where(Directory.Exists).ToList();
    }

    public SourceLocation? Resolve(string? modulePath, int methodToken, int ilOffset)
    {
        var reader = GetReader(modulePath);
        if (reader is null || !TryGetMethodHandle(methodToken, out var handle)) return null;
        try
        {
            return FindSequencePoint(reader, handle, ilOffset);
        }
        catch (BadImageFormatException) { return null; }
    }

    /// For an async method's MoveNext, find the source of the await that state <paramref name="state"/> is suspended at.
    public SourceLocation? ResolveAwait(string? modulePath, int moveNextToken, int state)
    {
        if (state < 0) return null;
        var reader = GetReader(modulePath);
        if (reader is null || !TryGetMethodHandle(moveNextToken, out var handle)) return null;
        try
        {
            foreach (var cdiHandle in reader.GetCustomDebugInformation(handle))
            {
                var cdi = reader.GetCustomDebugInformation(cdiHandle);
                if (reader.GetGuid(cdi.Kind) != AsyncMethodSteppingInformation) continue;

                var blob = reader.GetBlobReader(cdi.Value);
                blob.ReadUInt32(); // catch handler offset
                var index = 0;
                while (blob.RemainingBytes > 0)
                {
                    var yieldOffset = blob.ReadUInt32();
                    blob.ReadUInt32(); // resume offset
                    blob.ReadCompressedInteger(); // resume method
                    if (index++ == state)
                        return FindSequencePoint(reader, handle, (int)yieldOffset);
                }
            }
        }
        catch (BadImageFormatException) { }
        return null;
    }

    /// Finds the call that blocks on a task (.Result, .Wait(), GetAwaiter().GetResult()...) in a method's IL,
    /// at or after <paramref name="ilOffset"/>, and returns its source line and how it blocks.
    /// Optimized (tier-1) code often maps a return address to an earlier statement than the call, and inlines
    /// Task&lt;T&gt;.Result so that its frame is gone; the method's own IL still says exactly which call it was.
    public BlockingCall? ResolveBlockingCall(string? modulePath, int methodToken, int ilOffset)
    {
        var image = GetImage(modulePath);
        if (image is null || !TryGetMethodHandle(methodToken, out var handle)) return null;
        try
        {
            var metadata = image.GetMetadataReader();
            var rva = metadata.GetMethodDefinition(handle).RelativeVirtualAddress;
            if (rva == 0) return null;
            var il = image.GetMethodBody(rva).GetILBytes();
            if (il is null) return null;

            var calls = IlReader.Calls(il)
                .Select(c => (c.Offset, Call: DescribeBlockingCall(metadata, c.Token)))
                .Where(c => c.Call is not null)
                .ToList();
            if (calls.Count == 0) return null;
            // The first blocking call at or after the offset; failing that, the last one before it.
            var (offset, call) = calls.FirstOrDefault(c => c.Offset >= ilOffset) is { Call: not null } after ? after : calls[^1];

            var pdb = GetReader(modulePath);
            var location = pdb is null ? null : FindSequencePoint(pdb, handle, offset);
            return new BlockingCall(call!, location);
        }
        catch (BadImageFormatException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// How a call blocks on a task, in the words a developer would use, or null for any other call.
    internal static string? DescribeBlockingCall(MetadataReader metadata, int token)
    {
        if (!TryGetCallee(metadata, token, out var type, out var method)) return null;
        var simple = type[(type.LastIndexOfAny(['.', '+']) + 1)..];
        var inTasks = type.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal) || !type.Contains('.');
        return (simple, method) switch
        {
            ("Task`1" or "ValueTask`1", "get_Result") when inTasks => ".Result",
            ("Task", "Wait") when inTasks => ".Wait()",
            ("Task", "WaitAll") when inTasks => "Task.WaitAll(...)",
            ("Task", "WaitAny") when inTasks => "Task.WaitAny(...)",
            ("TaskAwaiter" or "TaskAwaiter`1" or "ConfiguredTaskAwaiter" or "ValueTaskAwaiter" or "ValueTaskAwaiter`1", "GetResult") => ".GetAwaiter().GetResult()",
            _ => null,
        };
    }

    private static bool TryGetCallee(MetadataReader metadata, int token, out string type, out string method)
    {
        type = method = "";
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
            handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                method = metadata.GetString(member.Name);
                type = TypeName(metadata, member.Parent) ?? "";
                return type.Length > 0;
            case HandleKind.MethodDefinition:
                var def = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                method = metadata.GetString(def.Name);
                type = TypeName(metadata, def.GetDeclaringType()) ?? "";
                return type.Length > 0;
            default:
                return false;
        }
    }

    private static string? TypeName(MetadataReader metadata, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var r = metadata.GetTypeReference((TypeReferenceHandle)handle);
                var name = metadata.GetString(r.Name);
                if (r.ResolutionScope.Kind == HandleKind.TypeReference)
                    return $"{TypeName(metadata, (EntityHandle)r.ResolutionScope)}+{name}";
                return r.Namespace.IsNil ? name : $"{metadata.GetString(r.Namespace)}.{name}";
            case HandleKind.TypeDefinition:
                var d = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                var defName = metadata.GetString(d.Name);
                if (d.GetDeclaringType() is { IsNil: false } outer)
                    return $"{TypeName(metadata, outer)}+{defName}";
                return d.Namespace.IsNil ? defName : $"{metadata.GetString(d.Namespace)}.{defName}";
            case HandleKind.TypeSpecification:
                // Task<int> and friends: a generic instantiation of a type reference.
                var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                blob.ReadCompressedInteger(); // CLASS or VALUETYPE
                return TypeName(metadata, blob.ReadTypeHandle());
            default:
                return null;
        }
    }

    private static bool TryGetMethodHandle(int token, out MethodDefinitionHandle handle)
    {
        handle = default;
        if ((token >> 24) != 0x06 || (token & 0xFFFFFF) == 0) return false;
        handle = MetadataTokens.MethodDefinitionHandle(token & 0xFFFFFF);
        return true;
    }

    private static SourceLocation? FindSequencePoint(MetadataReader reader, MethodDefinitionHandle handle, int ilOffset)
    {
        var info = reader.GetMethodDebugInformation(handle);
        SequencePoint? best = null;
        foreach (var sp in info.GetSequencePoints())
        {
            if (sp.IsHidden) continue;
            if (best is null || sp.Offset <= ilOffset) best = sp;
            if (sp.Offset > ilOffset && best is not null) break;
        }
        if (best is not { } point) return null;
        var doc = reader.GetDocument(point.Document);
        return new SourceLocation(reader.GetString(doc.Name), point.StartLine, point.StartColumn);
    }

    private MetadataReader? GetReader(string? modulePath)
    {
        if (string.IsNullOrEmpty(modulePath)) return null;
        if (!_cache.TryGetValue(modulePath, out var provider))
        {
            provider = OpenPdb(modulePath);
            _cache[modulePath] = provider;
        }
        try
        {
            return provider?.GetMetadataReader();
        }
        catch (BadImageFormatException)
        {
            _cache[modulePath] = null;
            return null;
        }
    }

    private PEReader? GetImage(string? modulePath)
    {
        if (string.IsNullOrEmpty(modulePath)) return null;
        if (_images.TryGetValue(modulePath, out var cached)) return cached;
        PEReader? image = null;
        var fileName = Path.GetFileName(modulePath.Replace('\\', '/'));
        foreach (var candidate in new[] { modulePath }.Concat(_searchDirectories.Select(d => Path.Combine(d, fileName))).Where(File.Exists))
        {
            try
            {
                var pe = new PEReader(File.OpenRead(candidate));
                if (pe.HasMetadata) { image = pe; break; }
                pe.Dispose();
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException) { }
        }
        _images[modulePath] = image;
        return image;
    }

    private MetadataReaderProvider? OpenPdb(string modulePath)
    {
        var fileName = Path.GetFileName(modulePath.Replace('\\', '/'));
        var pdbName = Path.ChangeExtension(fileName, ".pdb");

        var imageCandidates = new List<string> { modulePath };
        imageCandidates.AddRange(_searchDirectories.Select(d => Path.Combine(d, fileName)));

        foreach (var image in imageCandidates.Where(File.Exists))
        {
            try
            {
                using var stream = File.OpenRead(image);
                using var pe = new PEReader(stream);
                if (pe.TryOpenAssociatedPortablePdb(image, OpenIfExists, out var provider, out _) && provider is not null)
                    return provider;
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException) { }
        }

        // The binary isn't available on this machine: fall back to a PDB with the same name.
        foreach (var dir in _searchDirectories)
        {
            var pdb = Path.Combine(dir, pdbName);
            if (OpenIfExists(pdb) is { } s)
            {
                try { return MetadataReaderProvider.FromPortablePdbStream(s); }
                catch (BadImageFormatException) { s.Dispose(); }
            }
        }
        return null;

        Stream? OpenIfExists(string path)
        {
            // The PDB path baked into the binary is from the build machine; also try next to the binary and search dirs.
            foreach (var candidate in new[] { path, Path.Combine(Path.GetDirectoryName(modulePath) ?? "", pdbName) }
                         .Concat(_searchDirectories.Select(d => Path.Combine(d, pdbName))))
            {
                if (candidate.Length > 0 && File.Exists(candidate))
                    return File.OpenRead(candidate);
            }
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var p in _cache.Values) p?.Dispose();
        _cache.Clear();
        foreach (var i in _images.Values) i?.Dispose();
        _images.Clear();
    }
}

/// A call in the app's code that blocks on a task, found in the method's IL.
/// <param name="Call">".Result", ".Wait()", ".GetAwaiter().GetResult()"...</param>
public sealed record BlockingCall(string Call, SourceLocation? Location);

/// Just enough of an IL decoder to find call instructions.
internal static class IlReader
{
    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First().OperandType);

    /// The call and callvirt instructions in a method body, with their offsets and method tokens.
    public static IEnumerable<(int Offset, int Token)> Calls(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            var start = i;
            short value = il[i++];
            if (value == 0xFE && i < il.Length) value = unchecked((short)(0xFE00 | il[i++]));
            if (!Operands.TryGetValue(value, out var operand)) yield break;
            if ((value == OpCodes.Call.Value || value == OpCodes.Callvirt.Value) && i + 4 <= il.Length)
                yield return (start, BitConverter.ToInt32(il, i));
            i += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch when i + 4 <= il.Length => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
    }
}
