using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

/// Which collection a Monitor.Wait is about, read from the waiting method's IL in the dump: the first of the
/// object's collection fields that the code reads after the Wait call, before it leaves the lock. In
/// `while (_jobs.Count == 0) Monitor.Wait(_sync); job = _jobs.Dequeue();` that is _jobs, whatever else
/// the object holds (results, history...).
internal sealed class WaitCondition(ClrRuntime runtime)
{
    private readonly Dictionary<ulong, MetadataReaderProvider?> _metadata = [];

    /// The candidate the code reads first after the Wait call; <see cref="IlInstructions.Outcome.NoneRead"/> when the
    /// code leaves the lock without reading any of them (the condition is a counter or a flag); and
    /// <see cref="IlInstructions.Outcome.Unknown"/> when the IL or metadata can't be read from the dump, or the code
    /// calls app code that can't be followed before it reads one.
    public (IlInstructions.Outcome Outcome, ClrInstanceField? Field) FirstReadAfterWait(ClrMethod method, ulong returnAddress, ClrType owner, IReadOnlyList<ClrInstanceField> candidates)
    {
        try
        {
            var module = method.Type?.Module;
            if (module is null || module.IsDynamic || Metadata(module) is not { } metadata || ReadIl(method) is not { } il) return (IlInstructions.Outcome.Unknown, null);
            var code = IlInstructions.Decode(il);
            var waits = code.Where(i => i.IsCall && IsMonitorWait(metadata, i.Operand)).ToList();
            if (waits.Count == 0) return (IlInstructions.Outcome.Unknown, null);
            // The Wait at or after the frame's offset; failing that, the last one before it.
            var at = method.GetILOffset(returnAddress - 1);
            var wait = waits.FirstOrDefault(w => w.Offset >= at) ?? waits[^1];
            return IlInstructions.FirstRead(code, wait.Next,
                load => FieldOf(metadata, module, owner, candidates, load.Operand),
                call => CalleeOf(metadata, module, owner, call.Operand));
        }
        catch
        {
            return (IlInstructions.Outcome.Unknown, null);
        }
    }

    /// The code of a call to one of the object's own methods; <see cref="IlInstructions.Opaque"/> for other app code,
    /// which may read the collection some other way; null for the framework's, which doesn't read the object's fields.
    private List<IlInstructions.Instruction>? CalleeOf(MetadataReader metadata, ClrModule module, ClrType owner, int token)
    {
        if (MethodOf(metadata, module, owner, token) is { } callee)
            return ReadIl(callee) is { } il ? IlInstructions.Decode(il) : IlInstructions.Opaque;
        return IsFrameworkMethod(metadata, MetadataTokens.EntityHandle(token)) ? null : IlInstructions.Opaque;
    }

    private static ClrInstanceField? FieldOf(MetadataReader metadata, ClrModule module, ClrType owner, IReadOnlyList<ClrInstanceField> candidates, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        // Tokens are per module: an inherited field declared in another assembly can carry the same number.
        if (handle.Kind == HandleKind.FieldDefinition)
            return candidates.FirstOrDefault(f => f.Token == token && f.ContainingType?.Module?.Address == module.Address);
        if (handle.Kind != HandleKind.MemberReference) return null;
        // A field of a generic class, or of a class in another assembly, is referenced by name.
        var name = metadata.GetString(metadata.GetMemberReference((MemberReferenceHandle)handle).Name);
        return candidates.FirstOrDefault(f => f.Name == name);
    }

    /// A call to one of the object's own methods or properties, as in `while (IsEmpty) Monitor.Wait(_sync);`.
    private static ClrMethod? MethodOf(MetadataReader metadata, ClrModule module, ClrType owner, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodDefinition)
            return owner.Methods.FirstOrDefault(m => m.MetadataToken == token && m.Type?.Module?.Address == module.Address);
        if (handle.Kind != HandleKind.MemberReference) return null;
        var reference = metadata.GetMemberReference((MemberReferenceHandle)handle);
        // A method of a generic class defined in this module (WorkQueue<T>.IsEmpty), referenced by name.
        if (reference.Parent.Kind != HandleKind.TypeSpecification || GenericDefinition(metadata, (TypeSpecificationHandle)reference.Parent) is not { Kind: HandleKind.TypeDefinition }) return null;
        var name = metadata.GetString(reference.Name);
        return owner.Methods.FirstOrDefault(m => m.Name == name);
    }

    /// A method of a type from the framework's assemblies (List<T>.Count, Console.WriteLine...).
    private static bool IsFrameworkMethod(MetadataReader metadata, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MemberReference => IsFrameworkType(metadata, metadata.GetMemberReference((MemberReferenceHandle)handle).Parent, 0),
        HandleKind.MethodSpecification => IsFrameworkMethod(metadata, metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
        _ => false,
    };

    private static bool IsFrameworkType(MetadataReader metadata, EntityHandle handle, int depth)
    {
        if (depth > 8) return false;
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var scope = metadata.GetTypeReference((TypeReferenceHandle)handle).ResolutionScope;
                if (scope.Kind == HandleKind.TypeReference) return IsFrameworkType(metadata, (EntityHandle)scope, depth + 1);
                if (scope.Kind != HandleKind.AssemblyReference) return false;
                var assembly = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                return assembly is "System" or "mscorlib" or "netstandard"
                       || assembly.StartsWith("System.", StringComparison.Ordinal) || assembly.StartsWith("Microsoft.", StringComparison.Ordinal);
            case HandleKind.TypeSpecification:
                return GenericDefinition(metadata, (TypeSpecificationHandle)handle) is { } definition && IsFrameworkType(metadata, definition, depth + 1);
            default:
                return false;
        }
    }

    /// The generic type a type specification instantiates (List`1 in List<string>), or null for anything else.
    private static EntityHandle? GenericDefinition(MetadataReader metadata, TypeSpecificationHandle handle)
    {
        var blob = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
        return blob.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle ? blob.ReadTypeHandle() : null;
    }

    private static bool IsMonitorWait(MetadataReader metadata, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MemberReference) return false;
        var reference = metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (metadata.GetString(reference.Name) != "Wait" || reference.Parent.Kind != HandleKind.TypeReference) return false;
        var type = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
        return metadata.GetString(type.Name) == "Monitor" && metadata.GetString(type.Namespace) == "System.Threading";
    }

    private byte[]? ReadIl(ClrMethod method)
    {
        if (method.GetILInfo() is not { Length: > 0 and < 1_000_000 } info) return null;
        var il = new byte[info.Length];
        return runtime.DataTarget.DataReader.Read(info.Address, il) == il.Length ? il : null;
    }

    /// The module's metadata as it is mapped in the dump (a full dump has it), not the file on disk,
    /// which may be a different build by the time the dump is read.
    private MetadataReader? Metadata(ClrModule module)
    {
        if (!_metadata.TryGetValue(module.Address, out var provider))
        {
            provider = null;
            if (module.MetadataAddress != 0 && module.MetadataLength is > 0 and < 256 * 1024 * 1024)
            {
                var bytes = new byte[module.MetadataLength];
                if (runtime.DataTarget.DataReader.Read(module.MetadataAddress, bytes) == bytes.Length)
                    provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(bytes));
            }
            _metadata[module.Address] = provider;
        }
        return provider?.GetMetadataReader();
    }
}

/// Just enough of an IL decoder to follow the code after a call: field loads, calls, jumps and exits.
internal static class IlInstructions
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First());

    /// <param name="Jump">Where an unconditional jump (br, leave) goes; null for every other instruction.</param>
    /// <param name="Ends">The code stops here: it returns, throws, or leaves the try block (the lock) it is in.</param>
    public sealed record Instruction(int Offset, int Next, short OpCode, int Operand, int? Jump, bool Ends)
    {
        public bool IsCall => OpCode == OpCodes.Call.Value || OpCode == OpCodes.Callvirt.Value;
        public bool IsFieldLoad => OpCode == OpCodes.Ldfld.Value || OpCode == OpCodes.Ldflda.Value || OpCode == OpCodes.Ldsfld.Value || OpCode == OpCodes.Ldsflda.Value;
    }

    private const int MaxSteps = 2_000;

    /// <param name="Read">The code reads one of the candidates.</param>
    /// <param name="NoneRead">The code leaves the lock without reading any of them.</param>
    /// <param name="Unknown">The walk couldn't tell: unreadable code, a call into app code it can't follow, or a loop.</param>
    public enum Outcome { Read, NoneRead, Unknown }

    /// What a callee resolver returns for app code that can't be followed.
    public static readonly List<Instruction> Opaque = [];

    /// The first field load at or after <paramref name="start"/> that <paramref name="fieldOf"/> knows, following jumps,
    /// and one level into the methods <paramref name="calleeOf"/> returns code for (a property such as IsEmpty).
    /// Conditional branches fall through: in `while (queue.Count == 0) Monitor.Wait(sync);` the code after the Wait
    /// is the condition, and after it the code that takes the work. It stops where the code returns, throws or
    /// leaves the try block it is in, which for a wait is the end of the lock. <paramref name="calleeOf"/> returns
    /// null for a call that reads none of the candidates (the framework's) and <see cref="Opaque"/> for one it can't follow.
    public static (Outcome Outcome, T? Field) FirstRead<T>(List<Instruction> code, int start, Func<Instruction, T?> fieldOf, Func<Instruction, List<Instruction>?>? calleeOf) where T : class
    {
        var index = code.ToDictionary(i => i.Offset);
        var visited = new HashSet<int>();
        for (var pc = start; ; )
        {
            if (visited.Count >= MaxSteps || !visited.Add(pc) || !index.TryGetValue(pc, out var ins)) return (Outcome.Unknown, null);
            if (ins.IsFieldLoad && fieldOf(ins) is { } field) return (Outcome.Read, field);
            if (ins.IsCall && calleeOf?.Invoke(ins) is { } callee)
            {
                if (ReferenceEquals(callee, Opaque)) return (Outcome.Unknown, null);
                // One level only: inside the callee, any call that would need following makes the answer unknown.
                var read = FirstRead(callee, 0, fieldOf, call => calleeOf(call) is null ? null : Opaque);
                if (read.Outcome != Outcome.NoneRead) return read;
            }
            if (ins.Ends) return (Outcome.NoneRead, null);
            pc = ins.Jump ?? ins.Next;
        }
    }

    public static List<Instruction> Decode(byte[] il)
    {
        var result = new List<Instruction>();
        var i = 0;
        while (i < il.Length)
        {
            var start = i;
            short value = il[i++];
            if (value == 0xFE && i < il.Length) value = unchecked((short)(0xFE00 | il[i++]));
            if (!OpCodesByValue.TryGetValue(value, out var op)) break;
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => i + 4 <= il.Length && BitConverter.ToInt32(il, i) is var count && count >= 0 && count <= (il.Length - i - 4) / 4 ? 4 + 4 * count : -1,
                _ => 4,
            };
            if (size < 0 || i + size > il.Length) break;
            var operand = size switch
            {
                1 => op.OperandType == OperandType.ShortInlineBrTarget ? (sbyte)il[i] : il[i],
                4 when op.OperandType != OperandType.InlineSwitch => BitConverter.ToInt32(il, i),
                _ => 0,
            };
            var next = i + size;
            var jumps = value == OpCodes.Br.Value || value == OpCodes.Br_S.Value;
            var leaves = value == OpCodes.Leave.Value || value == OpCodes.Leave_S.Value;
            var ends = leaves || value == OpCodes.Ret.Value || value == OpCodes.Throw.Value || value == OpCodes.Rethrow.Value
                       || value == OpCodes.Endfinally.Value || value == OpCodes.Endfilter.Value;
            result.Add(new Instruction(start, next, value, operand, jumps ? next + operand : null, ends));
            i = next;
        }
        return result;
    }
}
