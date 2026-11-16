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
    private readonly Dictionary<string, Guid?> _dumpedBuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dumpedBuildPdbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];

    public SourceResolver(IEnumerable<string>? searchDirectories = null)
    {
        _searchDirectories = (searchDirectories ?? []).Where(Directory.Exists).ToList();
    }

    /// One note per module whose files on this machine are not the build that was dumped.
    public IReadOnlyList<string> Warnings => _warnings;

    /// <param name="dumpedBuild">Gives the PDB id of the module as it is in the dump (ClrModule.Pdb?.Guid), or null
    /// when the dump does not say. Asked once per module, the first time one of its frames is resolved.</param>
    public SourceLocation? Resolve(string? modulePath, int methodToken, int ilOffset, Func<Guid?>? dumpedBuild = null)
    {
        var reader = GetReader(modulePath, dumpedBuild);
        if (reader is null || !TryGetMethodHandle(methodToken, out var handle)) return null;
        try
        {
            return FindSequencePoint(reader, handle, ilOffset);
        }
        catch (BadImageFormatException) { return null; }
    }

    /// For an async method's MoveNext, find the source of the await that state <paramref name="state"/> is suspended at.
    public SourceLocation? ResolveAwait(string? modulePath, int moveNextToken, int state, Func<Guid?>? dumpedBuild = null)
    {
        if (state < 0) return null;
        var reader = GetReader(modulePath, dumpedBuild);
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
    public BlockingCall? ResolveBlockingCall(string? modulePath, int methodToken, int ilOffset, Func<Guid?>? dumpedBuild = null)
    {
        var image = GetImage(modulePath, dumpedBuild);
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

            var pdb = GetReader(modulePath, dumpedBuild);
            var location = pdb is null ? null : FindSequencePoint(pdb, handle, offset);
            return new BlockingCall(call!, location);
        }
        catch (BadImageFormatException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// For a frame whose IL offset is unknown (optimized code that the dump has no line map for): the lines of its
    /// method where it can be calling the frame above it on the stack, <paramref name="calleeType"/>.<paramref name="calleeMethod"/>.
    /// That is each call to it, and each call to a method that the JIT may have inlined there and that leads to it: a
    /// method of the same module, or of the callee's own module (<paramref name="calleeModule"/>, as when SemaphoreSlim.Wait()
    /// is inlined and the frame above is SemaphoreSlim.WaitCore). Null when the IL can't be read.
    public CallSites? ResolveCall(string? modulePath, int methodToken, string calleeType, string calleeMethod, string? calleeModule = null, Func<Guid?>? dumpedBuild = null)
    {
        var type = PlainName(calleeType);
        var method = PlainName(calleeMethod);
        Func<MetadataReader, int, string?> match = (metadata, token) => TryGetCallee(metadata, token, out var t, out var m) && PlainName(m) == method && PlainName(t) == type ? "" : null;
        var viaCallee = calleeModule is not null && !string.Equals(calleeModule, modulePath, StringComparison.OrdinalIgnoreCase) ? ReachThrough(calleeModule, type, method, match) : null;
        var exact = FindCallSites(modulePath, methodToken, $"call {type}::{method} in {calleeModule}", dumpedBuild, match, viaCallee);
        if (exact is not { Lines.Count: 0, HasSymbols: true }) return exact;
        // A call through an interface or a base class names the method, but not the class of the frame above.
        var name = UnqualifiedName(method);
        return FindCallSites(modulePath, methodToken, $"call *::{name}", dumpedBuild,
            (metadata, token) => TryGetCallee(metadata, token, out _, out var m) && UnqualifiedName(PlainName(m)) == name ? "" : null) ?? exact;
    }

    /// "Dispose" for an explicit interface implementation such as "System.IDisposable.Dispose".
    private static string UnqualifiedName(string method) => method.LastIndexOf('.') is var dot and > 0 ? method[(dot + 1)..] : method;

    private readonly Dictionary<string, (PEReader Image, Dictionary<string, TypeDefinitionHandle> Types)?> _otherModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Module, string Type, string Method), Func<MetadataReader, int, bool>?> _reachThrough = new();

    /// Whether a call (in another module's IL) to a method of <paramref name="modulePath"/> leads to a call of
    /// <paramref name="type"/>.<paramref name="method"/>, once inlined. One per callee, so that what it has followed is
    /// kept for every frame that calls it. Only for following calls: no line is read from this module, so a build that
    /// differs from the dumped one is not worth a warning.
    private Func<MetadataReader, int, bool>? ReachThrough(string modulePath, string type, string method, Func<MetadataReader, int, string?> match)
    {
        if (!_reachThrough.TryGetValue((modulePath, type, method), out var reach))
        {
            reach = NewReachThrough(modulePath, match);
            _reachThrough[(modulePath, type, method)] = reach;
        }
        return reach;
    }

    private Func<MetadataReader, int, bool>? NewReachThrough(string modulePath, Func<MetadataReader, int, string?> match)
    {
        if (!_otherModules.TryGetValue(modulePath, out var module))
        {
            module = null;
            try
            {
                if (File.Exists(modulePath))
                {
                    var pe = new PEReader(File.OpenRead(modulePath));
                    if (pe.HasMetadata)
                    {
                        var md = pe.GetMetadataReader();
                        var byName = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
                        foreach (var t in md.TypeDefinitions)
                            if (TypeName(md, t) is { } name) byName.TryAdd(PlainName(name), t);
                        module = (pe, byName);
                    }
                    else pe.Dispose();
                }
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
            _otherModules[modulePath] = module;
        }
        if (module is not { } found) return null;
        var (image, types) = found;
        var metadata = image.GetMetadataReader();
        // Only as far as the JIT inlines: every method of a virtual call's name in the framework, or every method however
        // big, would make almost any call lead to a lock.
        var inlining = new Inlining(image, metadata, MethodsByName(modulePath, metadata), match, framework: true);
        var known = new Dictionary<(string, string, string?), bool>();
        return (caller, token) =>
        {
            if (!TryGetCallee(caller, token, out var type, out var method)) return false;
            // The overload that is called: new Dictionary<K, V>() doesn't run the constructor that copies another dictionary.
            var parameters = Parameters(caller, MetadataTokens.EntityHandle(token));
            if (!known.TryGetValue((type, method, parameters), out var reaches))
            {
                reaches = types.TryGetValue(PlainName(type), out var definition)
                          && metadata.GetTypeDefinition(definition).GetMethods().Any(m =>
                              metadata.GetString(metadata.GetMethodDefinition(m).Name) == method
                              && (parameters is null || Parameters(metadata, m) is not { } own || own == parameters)
                              && inlining.Reaches(m).Count > 0);
                known[(type, method, parameters)] = reaches;
            }
            return reaches;
        };
    }

    /// A method's parameter types as signatures name them ("System.Int32,!0"), the same in the module that calls it and
    /// the one that defines it; null when the signature can't be read.
    private static string? Parameters(MetadataReader metadata, EntityHandle handle)
    {
        try
        {
            if (handle.Kind == HandleKind.MethodSpecification)
                handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            var signature = handle.Kind switch
            {
                HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)handle).Signature,
                HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Signature,
                _ => default,
            };
            if (signature.IsNil) return null;
            var blob = metadata.GetBlobReader(signature);
            return string.Join(",", new SignatureDecoder<string, object?>(TypeNames.Instance, metadata, null).DecodeMethodSignature(ref blob).ParameterTypes);
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// For an app frame that blocks on a task at an IL offset that is unknown: the lines of its blocking calls that the
    /// wait can be, narrowed down by what the stack says (<paramref name="hint"/>), and how they block when they agree.
    public CallSites? ResolveBlockingCall(string? modulePath, int methodToken, TaskWaitHint hint, Func<Guid?>? dumpedBuild = null) =>
        FindCallSites(modulePath, methodToken, $"wait {hint.Call}|{hint.ResultType}", dumpedBuild, (metadata, token) =>
            DescribeBlockingCall(metadata, token) is { } call
            && (hint.Call is null || call == hint.Call)
            && (hint.ResultType is null || MayHaveResultType(metadata, token, hint.ResultType))
                ? call
                : null);

    /// Optimized code may inline small methods several levels deep; deeper than this is rare.
    private const int MaxInlineDepth = 4;

    /// The most IL the JIT considers inlining without [MethodImpl(AggressiveInlining)] and without profile data
    /// (JitExtDefaultPolicyMaxIL). A bigger framework method keeps its own frame, so it isn't followed.
    internal const int MaxInlineIL = 128;

    private readonly Dictionary<(string Module, int Token, string Query), CallSites?> _callSites = new();
    private readonly Dictionary<string, ILookup<string, MethodDefinitionHandle>> _methodsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="match">Says whether a call (by its token) is one that is looked for, and how it blocks ("" when that doesn't matter).</param>
    /// <param name="external">Says whether a call into another module leads to one, once inlined.</param>
    private CallSites? FindCallSites(string? modulePath, int methodToken, string query, Func<Guid?>? dumpedBuild, Func<MetadataReader, int, string?> match,
        Func<MetadataReader, int, bool>? external = null)
    {
        var image = GetImage(modulePath, dumpedBuild);
        if (image is null || !TryGetMethodHandle(methodToken, out var handle)) return null;
        if (_callSites.TryGetValue((modulePath!, methodToken, query), out var cached)) return cached;
        CallSites? result = null;
        try
        {
            var metadata = image.GetMetadataReader();
            if (MethodBody(image, metadata, handle) is { } il)
            {
                var inlining = new Inlining(image, metadata, MethodsByName(modulePath!, metadata), match, external);
                var found = new List<(int Offset, string Call)>();
                var instructions = IlReader.Instructions(il).ToList();
                // A delegate that optimized code devirtualized and inlined runs where it is invoked, not where it is created.
                var delegateReaches = instructions.Any(x => x.OpCode is IlReader.Ldftn or IlReader.Ldvirtftn && inlining.Leads(x.Token).Count > 0);
                for (var i = 0; i < instructions.Count; i++)
                {
                    var (offset, opcode, token) = instructions[i];
                    if (!IlReader.IsCall(opcode)) continue;
                    if (delegateReaches && TryGetCallee(metadata, token, out _, out var name) && name == "Invoke") found.Add((offset, ""));
                    else found.AddRange(inlining.Calls(token, inlined: !IlReader.IsThrown(instructions, i)).Select(c => (offset, c)));
                }
                var pdb = GetReader(modulePath, dumpedBuild);
                var lines = pdb is null
                    ? []
                    : found.Select(f => FindSequencePoint(pdb, handle, f.Offset)).OfType<SourceLocation>()
                        .GroupBy(l => (l.File, l.Line)).Select(g => g.First()).OrderBy(l => l.Line).ToList();
                var calls = found.Select(f => f.Call).Where(c => c.Length > 0).Distinct().ToList();
                result = new CallSites(lines, calls.Count == 1 && found.All(f => f.Call.Length > 0) ? calls[0] : null, pdb is not null);
            }
        }
        catch (BadImageFormatException) { }
        catch (InvalidOperationException) { }
        _callSites[(modulePath!, methodToken, query)] = result;
        return result;
    }

    /// Which of the calls that are looked for a call is, or leads to once the JIT inlines what it calls: through methods
    /// of the same module, whose IL is at hand, and through another module when <paramref name="external"/> can tell.
    /// <param name="framework">True for the module of the frame above (the framework), whose code is only followed as far as
    /// the JIT can inline it: a virtual call only to the method it names, and no method bigger than the inline budget. In
    /// the app's own, smaller code a virtual call can be any method of its name, and size isn't judged: a guess that is
    /// too wide there costs a line, while one that is too narrow could give a wrong one.</param>
    private sealed class Inlining(PEReader image, MetadataReader metadata, ILookup<string, MethodDefinitionHandle> byName, Func<MetadataReader, int, string?> match,
        Func<MetadataReader, int, bool>? external = null, bool framework = false)
    {
        private readonly Dictionary<(MethodDefinitionHandle, int), HashSet<string>> _reached = new();

        /// The calls looked for that the call with this token is, or leads to unless it is never <paramref name="inlined"/>.
        public IReadOnlyCollection<string> Calls(int token, bool inlined = true) =>
            inlined ? Calls(token, MaxInlineDepth) : match(metadata, token) is { } call ? [call] : [];

        /// The calls looked for that the call with this token leads to, once inlined.
        public IReadOnlyCollection<string> Leads(int token)
        {
            var result = new HashSet<string>();
            foreach (var target in Targets(token))
                result.UnionWith(Reached(target, MaxInlineDepth));
            return result;
        }

        /// The calls looked for that this method makes, itself or through what it calls, once inlined.
        public IReadOnlyCollection<string> Reaches(MethodDefinitionHandle method) => Reached(method, MaxInlineDepth);

        private HashSet<string> Calls(int token, int depth)
        {
            if (match(metadata, token) is { } call) return [call];
            if (external?.Invoke(metadata, token) == true) return [""];
            var result = new HashSet<string>();
            foreach (var target in Targets(token))
                result.UnionWith(Reached(target, depth));
            return result;
        }

        private HashSet<string> Reached(MethodDefinitionHandle method, int depth)
        {
            if (_reached.TryGetValue((method, depth), out var known)) return known;
            var result = new HashSet<string>();
            _reached[(method, depth)] = result; // also ends recursion
            // A method that is never inlined keeps its own frame, so it can't be what the frame above was called from.
            var impl = metadata.GetMethodDefinition(method).ImplAttributes;
            if ((impl & (System.Reflection.MethodImplAttributes.NoInlining | System.Reflection.MethodImplAttributes.Synchronized)) != 0) return result;
            if (MethodBody(image, metadata, method) is not { } il) return result;
            if (framework && il.Length > MaxInlineIL && (impl & System.Reflection.MethodImplAttributes.AggressiveInlining) == 0) return result;
            var instructions = IlReader.Instructions(il).ToList();
            for (var i = 0; i < instructions.Count; i++)
            {
                var (_, opcode, token) = instructions[i];
                if (!IlReader.IsCall(opcode)) continue;
                if (match(metadata, token) is { } call) result.Add(call);
                else if (IlReader.IsThrown(instructions, i)) continue;
                else if (external?.Invoke(metadata, token) == true) result.Add("");
                else if (depth > 1) result.UnionWith(Calls(token, depth - 1));
            }
            return result;
        }

        /// The methods of this module a call can run: the method itself, and for a virtual one every method of the
        /// module with its name, since devirtualized code can inline any override.
        private IEnumerable<MethodDefinitionHandle> Targets(int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MethodSpecification)
                handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            if (handle.Kind == HandleKind.MemberReference)
            {
                // A method of a generic type of this module, called through a reference to the type's instantiation:
                // that overload only (new Dictionary<K, V>() doesn't run the constructor for deserialization).
                var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                var parameters = Parameters(metadata, handle);
                return member.Parent.Kind == HandleKind.TypeSpecification && GenericTypeDefinition(metadata, member.Parent) is { } type
                    ? byName[metadata.GetString(member.Name)].Where(m => metadata.GetMethodDefinition(m).GetDeclaringType() == type
                                                                         && (parameters is null || Parameters(metadata, m) is not { } own || own == parameters))
                    : [];
            }
            if (handle.Kind != HandleKind.MethodDefinition) return [];
            var definition = (MethodDefinitionHandle)handle;
            var method = metadata.GetMethodDefinition(definition);
            return framework || (method.Attributes & System.Reflection.MethodAttributes.Virtual) == 0 ? [definition] : byName[metadata.GetString(method.Name)];
        }
    }

    private ILookup<string, MethodDefinitionHandle> MethodsByName(string modulePath, MetadataReader metadata)
    {
        if (!_methodsByName.TryGetValue(modulePath, out var lookup))
        {
            lookup = metadata.MethodDefinitions.ToLookup(m => metadata.GetString(metadata.GetMethodDefinition(m).Name), StringComparer.Ordinal);
            _methodsByName[modulePath] = lookup;
        }
        return lookup;
    }

    private static byte[]? MethodBody(PEReader image, MetadataReader metadata, MethodDefinitionHandle handle)
    {
        var rva = metadata.GetMethodDefinition(handle).RelativeVirtualAddress;
        return rva == 0 ? null : image.GetMethodBody(rva).GetILBytes();
    }

    private static TypeDefinitionHandle? GenericTypeDefinition(MetadataReader metadata, EntityHandle typeSpec)
    {
        var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)typeSpec).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
        blob.ReadCompressedInteger(); // CLASS or VALUETYPE
        return blob.ReadTypeHandle() is { Kind: HandleKind.TypeDefinition } type ? (TypeDefinitionHandle)type : null;
    }

    /// Whether a blocking call can be the one on a task with this result type ("" for a Task without one): Task&lt;string&gt;.Result
    /// can't be waiting for a Task&lt;int&gt;, and no .Result waits for a plain Task. A call declared on a type without type
    /// arguments (Task.Wait, TaskAwaiter.GetResult) can be waiting for any task.
    private static bool MayHaveResultType(MetadataReader metadata, int token, string resultType)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
            handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        if (handle.Kind != HandleKind.MemberReference) return true;
        var parent = metadata.GetMemberReference((MemberReferenceHandle)handle).Parent;
        if (parent.Kind != HandleKind.TypeSpecification) return true;
        var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return true;
        blob.ReadCompressedInteger(); // CLASS or VALUETYPE
        blob.ReadTypeHandle();
        if (blob.ReadCompressedInteger() < 1) return true;
        if (resultType.Length == 0) return false;
        var argument = new SignatureDecoder<string, object?>(TypeNames.Instance, metadata, null).DecodeType(ref blob);
        // A type parameter of the app's own generic code: could be anything.
        return argument.StartsWith('!') || PlainName(argument) == PlainName(resultType);
    }

    /// A type or method name without generic arity or type arguments, so that the metadata's "Task`1" and ClrMD's
    /// "Task&lt;System.Int32&gt;" or "ForEach[[System.__Canon, System.Private.CoreLib]]" compare equal. Angle brackets
    /// at the start of a name, as in "&lt;&gt;c" or "&lt;Run&gt;b__0_0", belong to the name.
    internal static string PlainName(string name)
    {
        if (name.IndexOf("[[", StringComparison.Ordinal) is var instantiation and >= 0) name = name[..instantiation];
        var plain = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '`')
            {
                while (i + 1 < name.Length && char.IsDigit(name[i + 1])) i++;
                continue;
            }
            if (c == '<' && i > 0 && (char.IsLetterOrDigit(name[i - 1]) || name[i - 1] == '_'))
            {
                for (var depth = 1; depth > 0 && i + 1 < name.Length;)
                {
                    i++;
                    if (name[i] == '<') depth++;
                    else if (name[i] == '>') depth--;
                }
                continue;
            }
            plain.Append(c);
        }
        return plain.ToString();
    }

    /// Type names from signatures, written the way ClrMD writes them ("System.Collections.Generic.List&lt;System.String&gt;").
    private sealed class TypeNames : ISignatureTypeProvider<string, object?>
    {
        public static readonly TypeNames Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => $"System.{typeCode}";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, handle) ?? "?";
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(reader, handle) ?? "?";
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(shape.Rank - 1, 0))}]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "?";
        public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) =>
            $"{PlainName(genericType)}<{string.Join(", ", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    }

    private readonly Dictionary<(string Module, int Token), BlockingCallers?> _blockingCallers = new();
    private readonly Dictionary<string, ILookup<(string Type, string Method), MethodDefinitionHandle>> _callers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _references = new(StringComparer.OrdinalIgnoreCase);

    /// The app's methods that call an async method and block on its task (.Result, .GetAwaiter().GetResult()...) in the
    /// same statement, for a thread that waits for that async method with no app frame left to say where: a compiled
    /// delegate (lambda_method) had them inlined. The async method is found from its state machine's MoveNext, its callers
    /// in the IL of every app module that references it. A method that just returns the task is followed to its own callers.
    /// Null when the PDB or the IL can't be read. Kept per state machine: every thread of a starved pool asks the same.
    public BlockingCallers? ResolveBlockingCallers(string? modulePath, int moveNextToken, IReadOnlyList<(string Path, Func<Guid?>? DumpedBuild)> appModules, Func<Guid?>? dumpedBuild = null)
    {
        if (string.IsNullOrEmpty(modulePath)) return null;
        if (_blockingCallers.TryGetValue((modulePath, moveNextToken), out var known)) return known;
        var result = FindBlockingCallers(modulePath, moveNextToken, appModules, dumpedBuild);
        _blockingCallers[(modulePath, moveNextToken)] = result;
        return result;
    }

    private BlockingCallers? FindBlockingCallers(string modulePath, int moveNextToken, IReadOnlyList<(string Path, Func<Guid?>? DumpedBuild)> appModules, Func<Guid?>? dumpedBuild)
    {
        var pdb = GetReader(modulePath, dumpedBuild);
        var image = GetImage(modulePath, dumpedBuild);
        if (pdb is null || image is null || !TryGetMethodHandle(moveNextToken, out var moveNext)) return null;
        try
        {
            var kickoff = pdb.GetMethodDebugInformation(moveNext).GetStateMachineKickoffMethod();
            if (kickoff.IsNil) return null;
            var metadata = image.GetMetadataReader();
            var definition = metadata.GetMethodDefinition(kickoff);
            var found = new List<BlockingCaller>();
            var complete = true;
            var pending = new Queue<(string Type, string Method, string? Assembly, string Module, int Depth)>();
            pending.Enqueue((PlainName(TypeName(metadata, definition.GetDeclaringType()) ?? ""), metadata.GetString(definition.Name), AssemblyName(metadata), modulePath, 0));
            var seen = new HashSet<(string, string)>();
            while (pending.Count > 0)
            {
                var target = pending.Dequeue();
                if (!seen.Add((target.Type, target.Method))) continue;
                foreach (var (path, build) in appModules)
                {
                    if (!string.Equals(path, target.Module, StringComparison.OrdinalIgnoreCase) && !References(path, target.Assembly)) continue;
                    if (GetImage(path, build) is not { } module) continue;
                    var md = module.GetMetadataReader();
                    var symbols = GetReader(path, build);
                    var inlining = new Inlining(module, md, MethodsByName(path, md), (m, t) => DescribeBlockingCall(m, t));
                    foreach (var handle in Callers(path, module, md)[(target.Type, target.Method)])
                    {
                        if (MethodBody(module, md, handle) is not { } il) continue;
                        var instructions = IlReader.Instructions(il).ToList();
                        for (var i = 0; i < instructions.Count; i++)
                        {
                            var (offset, opcode, token) = instructions[i];
                            if (!IlReader.IsCall(opcode) || !TryGetCallee(md, token, out var type, out var name) || name != target.Method || PlainName(type) != target.Type) continue;
                            // Without line information the call can't be told apart from the statements around it.
                            if (symbols is null || FindSequencePoint(symbols, handle, offset) is not { } statement)
                            {
                                complete = false;
                                continue;
                            }
                            var blocking = instructions.Skip(i + 1)
                                .TakeWhile(x => Equals(FindSequencePoint(symbols, handle, x.Offset), statement))
                                .Where(x => IlReader.IsCall(x.OpCode))
                                .Select(x => (x.Offset, Call: DescribeBlockingCall(md, x.Token) ?? inlining.Leads(x.Token).FirstOrDefault()))
                                .FirstOrDefault(x => x.Call is not null);
                            var method = md.GetMethodDefinition(handle);
                            if (blocking.Call is { } call)
                            {
                                // A method that is never inlined would have its own frame on the stack.
                                if ((method.ImplAttributes & System.Reflection.MethodImplAttributes.NoInlining) == 0)
                                    found.Add(new BlockingCaller(TypeName(md, method.GetDeclaringType()) ?? "", md.GetString(method.Name), path,
                                        new BlockingCall(call, FindSequencePoint(symbols, handle, blocking.Offset))));
                            }
                            else if (i + 1 < instructions.Count && instructions[i + 1].OpCode == IlReader.Ret && target.Depth < 2)
                                pending.Enqueue((PlainName(TypeName(md, method.GetDeclaringType()) ?? ""), md.GetString(method.Name), AssemblyName(md), path, target.Depth + 1));
                            else
                                // The task is kept or passed on in a way that isn't followed here: someone else may block on it.
                                complete = false;
                        }
                    }
                }
            }
            var distinct = found.GroupBy(c => (c.ModulePath, c.TypeName, c.MethodName, c.Call.Location?.Line)).Select(g => g.First()).ToList();
            return new BlockingCallers(distinct, complete);
        }
        catch (BadImageFormatException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// The methods of a module by what they call (type without arity, method name), read once per module. Async methods'
    /// MoveNext are left out: their awaits end in GetResult() too, but don't block.
    private ILookup<(string Type, string Method), MethodDefinitionHandle> Callers(string path, PEReader image, MetadataReader metadata)
    {
        if (_callers.TryGetValue(path, out var known)) return known;
        var calls = new List<((string, string) Callee, MethodDefinitionHandle Caller)>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            byte[]? il;
            try
            {
                il = MethodBody(image, metadata, handle);
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
            {
                continue;
            }
            if (il is null) continue;
            var callees = new HashSet<(string, string)>();
            var awaits = false;
            foreach (var x in IlReader.Instructions(il))
            {
                if (!IlReader.IsCall(x.OpCode) || !TryGetCallee(metadata, x.Token, out var type, out var name)) continue;
                if (name is "AwaitUnsafeOnCompleted" or "AwaitOnCompleted") awaits = true;
                callees.Add((PlainName(type), name));
            }
            if (!awaits) calls.AddRange(callees.Select(c => (c, handle)));
        }
        known = calls.ToLookup(c => c.Callee, c => c.Caller);
        _callers[path] = known;
        return known;
    }

    private static string? AssemblyName(MetadataReader metadata) =>
        metadata.IsAssembly ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : null;

    /// Whether a module references an assembly, which it must to call into it. The references are read once per module.
    private bool References(string path, string? assembly)
    {
        if (assembly is null) return false;
        if (!_references.TryGetValue(path, out var names))
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(path))
                {
                    using var pe = new PEReader(File.OpenRead(path));
                    if (pe.HasMetadata)
                    {
                        var metadata = pe.GetMetadataReader();
                        foreach (var r in metadata.AssemblyReferences) names.Add(metadata.GetString(metadata.GetAssemblyReference(r).Name));
                    }
                }
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Unreadable: it references nothing that can be followed.
            }
            _references[path] = names;
        }
        return names.Contains(assembly);
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

    private MetadataReader? GetReader(string? modulePath, Func<Guid?>? dumpedBuild)
    {
        if (string.IsNullOrEmpty(modulePath)) return null;
        if (!_cache.TryGetValue(modulePath, out var provider))
        {
            provider = OpenPdb(modulePath, DumpedBuild(modulePath, dumpedBuild));
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

    private PEReader? GetImage(string? modulePath, Func<Guid?>? dumpedBuild)
    {
        if (string.IsNullOrEmpty(modulePath)) return null;
        if (_images.TryGetValue(modulePath, out var cached)) return cached;
        var expected = DumpedBuild(modulePath, dumpedBuild);
        PEReader? image = null;
        var isDumpedBuild = false;
        var fileName = Path.GetFileName(modulePath.Replace('\\', '/'));
        foreach (var candidate in new[] { modulePath }.Concat(_searchDirectories.Select(d => Path.Combine(d, fileName))).Where(File.Exists))
        {
            try
            {
                var pe = new PEReader(File.OpenRead(candidate));
                if (pe.HasMetadata)
                {
                    if (expected is not { } id) { image = pe; break; }
                    // The build that was dumped wins wherever it is; failing that, the first binary found.
                    if (BuildIds(pe).Contains(id))
                    {
                        image?.Dispose();
                        image = pe;
                        isDumpedBuild = true;
                        break;
                    }
                    if (image is null) { image = pe; continue; }
                }
                pe.Dispose();
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException) { }
        }
        if (image is not null && expected is not null && !isDumpedBuild)
        {
            // IL from another build next to the dumped build's PDB would point at the wrong statement.
            try
            {
                GetReader(modulePath, dumpedBuild);
            }
            catch (Exception)
            {
                // A PDB that cannot be read is not the dumped build's; the image is still cached below.
            }
            if (_dumpedBuildPdbs.Contains(modulePath))
            {
                image.Dispose();
                image = null;
            }
            else
            {
                WarnNotTheDumpedBuild(fileName);
            }
        }
        _images[modulePath] = image;
        return image;
    }

    /// The PDB id of the module in the dump, or null when it is unknown: then any build found is used, unchecked.
    private Guid? DumpedBuild(string modulePath, Func<Guid?>? dumpedBuild)
    {
        if (_dumpedBuilds.TryGetValue(modulePath, out var known)) return known;
        Guid? id = null;
        try
        {
            id = dumpedBuild?.Invoke();
        }
        catch (Exception)
        {
            // Whatever reading the dump threw: unknown.
        }
        if (id == Guid.Empty) id = null;
        _dumpedBuilds[modulePath] = id;
        return id;
    }

    /// An old dump analysed after a rebuild would otherwise get the new build's lines without a word.
    /// The lines are kept: the same source built in another folder has another id and the right lines.
    private void WarnNotTheDumpedBuild(string moduleFileName)
    {
        if (_warned.Add(moduleFileName))
            _warnings.Add($"{moduleFileName} on this machine is not the build that was dumped; file and line numbers for it may be off. Pass --symbols <folder with the dumped build>.");
    }

    /// The PDB ids a binary was built with (its CodeView debug directory entries).
    private static List<Guid> BuildIds(PEReader pe)
    {
        var ids = new List<Guid>();
        try
        {
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.CodeView)
                    ids.Add(pe.ReadCodeViewDebugDirectoryData(entry).Guid);
            }
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException) { }
        return ids;
    }

    /// The id of a portable PDB file: the first 16 bytes of its header id are the GUID that the binary's
    /// CodeView entry, and so the dump, records.
    private static Guid? PdbId(string path)
    {
        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path));
            var id = provider.GetMetadataReader().DebugMetadataHeader?.Id ?? default;
            return id.IsDefault || id.Length < 16 ? null : new Guid(id.AsSpan(0, 16));
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private MetadataReaderProvider? OpenPdb(string modulePath, Guid? dumpedBuild)
    {
        if (dumpedBuild is not { } id) return OpenPdb(modulePath);
        if (OpenDumpedBuildPdb(modulePath, id) is { } match)
        {
            _dumpedBuildPdbs.Add(modulePath);
            return match;
        }

        // Nothing here is the dumped build: use what is found, as without an id, and say so.
        var first = OpenPdb(modulePath);
        try
        {
            if (first?.GetMetadataReader() is not null)
                WarnNotTheDumpedBuild(Path.GetFileName(modulePath.Replace('\\', '/')));
        }
        catch (BadImageFormatException)
        {
            // Not a portable PDB: nothing will be read from it, so there is nothing to warn about.
        }
        return first;
    }

    /// The PDB of the build that was dumped, wherever it is among the candidates: a stale build that is
    /// found first must not hide the right one in a later folder.
    private MetadataReaderProvider? OpenDumpedBuildPdb(string modulePath, Guid dumpedBuild)
    {
        var fileName = Path.GetFileName(modulePath.Replace('\\', '/'));
        var pdbName = Path.ChangeExtension(fileName, ".pdb");
        var pdbFiles = new[] { Path.Combine(Path.GetDirectoryName(modulePath) ?? "", pdbName) }
            .Concat(_searchDirectories.Select(d => Path.Combine(d, pdbName)))
            .ToList();

        foreach (var image in new[] { modulePath }.Concat(_searchDirectories.Select(d => Path.Combine(d, fileName))).Where(File.Exists))
        {
            try
            {
                using var stream = File.OpenRead(image);
                using var pe = new PEReader(stream);
                var ids = BuildIds(pe);
                if (!ids.Contains(dumpedBuild)) continue;
                // The right binary: its PDB is next to it, in a search directory, or embedded in it.
                if (pe.TryOpenAssociatedPortablePdb(image, path => OpenPdbFile(pdbFiles.Prepend(path), ids), out var provider, out _) && provider is not null)
                    return provider;
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException) { }
        }

        // The binary isn't available on this machine: a PDB alone will do when it is the dumped build's.
        try
        {
            return OpenPdbFile(pdbFiles, [dumpedBuild]) is { } pdb ? MetadataReaderProvider.FromPortablePdbStream(pdb) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The first of these PDB files that has one of these ids.
    private static Stream? OpenPdbFile(IEnumerable<string> candidates, IReadOnlyCollection<Guid> ids)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Length > 0 && File.Exists(candidate) && PdbId(candidate) is { } id && ids.Contains(id))
                return File.OpenRead(candidate);
        }
        return null;
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
        foreach (var m in _otherModules.Values) m?.Image.Dispose();
        _otherModules.Clear();
    }
}

/// A call in the app's code that blocks on a task, found in the method's IL.
/// <param name="Call">".Result", ".Wait()", ".GetAwaiter().GetResult()"...</param>
public sealed record BlockingCall(string Call, SourceLocation? Location);

/// Where in a method a frame can be, read from its IL when the dump has no line map for its code.
/// <param name="Lines">Every line it can be on; the line is known only when there is one.</param>
/// <param name="Call">For a task wait, how it blocks (".Result"...), when all the candidates agree.</param>
/// <param name="HasSymbols">False when the PDB can't be read, so there are no lines to tell.</param>
public sealed record CallSites(IReadOnlyList<SourceLocation> Lines, string? Call = null, bool HasSymbols = true)
{
    public SourceLocation? Line => Lines.Count == 1 ? Lines[0] : null;
}

/// What the stack says about a task wait whose IL offset is unknown.
/// <param name="Call">The kind of blocking call when its frame survived (".Result" for Task&lt;T&gt;.get_Result), else null.</param>
/// <param name="ResultType">The waited task's result type ("System.Int32"; "" for a Task without one), when known.</param>
public sealed record TaskWaitHint(string? Call, string? ResultType);

/// An app method that blocks on an async method's task, found in its IL.
public sealed record BlockingCaller(string TypeName, string MethodName, string ModulePath, BlockingCall Call);

/// <param name="Complete">False when some call to the async method passes its task on in a way that isn't followed,
/// so that another method may be the one blocking on it.</param>
public sealed record BlockingCallers(IReadOnlyList<BlockingCaller> Methods, bool Complete);

/// Just enough of an IL decoder to find call instructions.
internal static class IlReader
{
    public const short Ret = 0x2A;
    public const short Throw = 0x7A;
    public const short Ldftn = unchecked((short)0xFE06);
    public const short Ldvirtftn = unchecked((short)0xFE07);

    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First().OperandType);

    /// The call and callvirt instructions in a method body, with their offsets and method tokens.
    public static IEnumerable<(int Offset, int Token)> Calls(byte[] il) =>
        Instructions(il).Where(x => (x.OpCode == OpCodes.Call.Value || x.OpCode == OpCodes.Callvirt.Value) && x.Token != 0).Select(x => (x.Offset, x.Token));

    /// call, callvirt and newobj: the instructions that run another method.
    public static bool IsCall(short opcode) => opcode == OpCodes.Call.Value || opcode == OpCodes.Callvirt.Value || opcode == OpCodes.Newobj.Value;

    /// The exception of a "throw new ...". Its constructor isn't followed: the JIT inlines little into a block that
    /// throws, as it rarely runs, and a thread doesn't wait in there.
    public static bool IsThrown(IReadOnlyList<(int Offset, short OpCode, int Token)> instructions, int index) =>
        instructions[index].OpCode == OpCodes.Newobj.Value && index + 1 < instructions.Count && instructions[index + 1].OpCode == Throw;

    /// Every instruction in a method body, with its offset and, for one that names a method (or other member), its token.
    public static IEnumerable<(int Offset, short OpCode, int Token)> Instructions(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            var start = i;
            short value = il[i++];
            if (value == 0xFE && i < il.Length) value = unchecked((short)(0xFE00 | il[i++]));
            if (!Operands.TryGetValue(value, out var operand)) yield break;
            var token = operand is OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType or OperandType.InlineField && i + 4 <= il.Length
                ? BitConverter.ToInt32(il, i)
                : 0;
            yield return (start, value, token);
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
