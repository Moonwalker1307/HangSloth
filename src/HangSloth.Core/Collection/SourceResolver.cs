using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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
    }
}
