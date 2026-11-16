using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HangSloth.Analysis;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

// A SemaphoreSlim counts its free slots; it doesn't record who took them. The code around a wait says more: the method
// that waits also releases it (a lock), waits for it in a loop and never releases it (a worker waiting for a signal), or
// took another semaphore earlier on the same path and is still inside that region (held while it waits). All of it is
// read from the IL and metadata in the dump, so it is the code that was running, whatever build is on this machine.
public sealed partial class SnapshotBuilder
{
    private const string SemaphoreType = "System.Threading.SemaphoreSlim";

    /// Semaphore address -> the free slots it was created with; null when unknown or when two places disagree.
    private readonly Dictionary<ulong, int?> _initialCounts = [];

    /// Adds <see cref="ThreadSnapshot.SemaphoreUse"/> to every thread that waits on a SemaphoreSlim, synchronously or at
    /// the end of the task chain it is blocked on.
    private List<ThreadSnapshot> ReadSemaphoreUses(List<(ClrThread Clr, ThreadSnapshot Snapshot, List<ClrStackRoot> Roots)> partial, List<ThreadSnapshot> threads)
    {
        using var code = new CodeReader(_runtime, _sources);
        var byId = new Dictionary<int, (ClrThread Clr, List<ClrStackRoot> Roots)>();
        foreach (var p in partial) byId.TryAdd(p.Snapshot.ManagedId, (p.Clr, p.Roots));
        var result = new List<ThreadSnapshot>(threads.Count);
        foreach (var thread in threads)
        {
            var updated = thread;
            try
            {
                var (clr, roots) = byId[thread.ManagedId];
                if (thread.WaitingForSemaphore is { } semaphore && thread.WaitingOnCollection is null)
                    updated = thread with { SemaphoreUse = SyncSemaphoreUse(clr, roots, semaphore, code) };
                else if (thread.BlockedOnTask is { } task && LastOfChain(task) is { Kind: TaskKind.SemaphoreWait, Semaphore: { } awaited })
                    updated = thread with { SemaphoreUse = AsyncSemaphoreUse(task, awaited, code) };
            }
            catch
            {
                // How the semaphore is used stays unknown; the wait itself is still reported.
            }
            result.Add(updated);
        }
        return result;
    }

    private static TaskInfo LastOfChain(TaskInfo task)
    {
        while (task.Awaiting is { } next) task = next;
        return task;
    }

    /// SemaphoreSlim.Wait: the app method that called it, and the app methods that called that one.
    private SemaphoreUse? SyncSemaphoreUse(ClrThread thread, List<ClrStackRoot> roots, ulong semaphore, CodeReader code)
    {
        var frames = thread.EnumerateStackTrace(includeContext: false, MaxFramesPerThread)
            .Where(f => f.Kind == ClrStackFrameKind.ManagedMethod && f.Method is not null)
            .ToList();
        var inner = frames.FindIndex(f => f.Method!.Type?.Name == SemaphoreType);
        if (inner < 0) return null;
        var waiter = frames.FindIndex(inner, f => f.Method!.Type?.Name != SemaphoreType);
        if (waiter < 0 || code.Read(frames[waiter].Method!) is not { } body) return null;

        var candidates = ThisCandidates(frames[waiter], roots);
        var offset = CallerIlOffset(frames[waiter]);
        // Without `this` first: the waits that can be the one in progress tell whether the frame still reports `this`.
        var calls = body.CallsWithoutThis ??= SemaphoreCalls(body, default);
        var everySlot = candidates.Count > 0 && body.SlotsFor(candidates) == candidates.Count - 1;
        var reportsThis = everySlot || candidates.Count > 0 && UsesThisAfterEach(body, calls.Where(c => c.Kind == SemaphoreCallKind.Wait && c.Offset >= offset).Select(c => c.Offset));
        if (reportsThis && candidates is [var only]) calls = SemaphoreCalls(body, only);
        var current = CurrentWait(calls.Where(c => c.Kind == SemaphoreCallKind.Wait).ToList(), semaphore, offset);
        if (current is null) return null;
        // Several objects of the method's type on the frame (`this` and an `other` of the same class): the wait in
        // progress tells which one is `this`, and with it which semaphore `this` took.
        if (reportsThis && candidates.Count > 1 && WhichIsThis(body, candidates, current, semaphore, everySlot) is { } self)
        {
            calls = SemaphoreCalls(body, self);
            current = CurrentWait(calls.Where(c => c.Kind == SemaphoreCallKind.Wait).ToList(), semaphore, offset);
            if (current is null) return null;
        }
        // A semaphore reached through a property, an indexer or a call can't be matched with its Release or with the
        // field it was created in: the code that waits says nothing about it.
        if (current.Key is null) return null;
        RememberInitialCount(semaphore, current, body, code);

        var releases = calls.Any(c => c.Kind == SemaphoreCallKind.Release && SameSemaphore(c, current, semaphore));
        var holds = HeldBefore(calls, current.Offset, body, body.Name).ToList();
        // The methods between a caller's take and the wait, with how far each has run: one of them may have released it.
        var furtherIn = new List<RanSoFar> { new(calls, current.Offset, null, body) };
        for (var i = waiter + 1; i < frames.Count; i++)
        {
            // A caller that took a semaphore before the call that led here holds it during the wait too.
            if (code.Read(frames[i].Method!) is not { } caller) continue;
            var site = CallSite(caller, frames[i - 1].Method!, CallerIlOffset(frames[i]));
            var callerCalls = site is { } call ? SemaphoreCalls(caller, ThisOf(frames[i], roots, caller, call)) : caller.CallsWithoutThis ??= SemaphoreCalls(caller, default);
            if (site is { } at)
            {
                releases |= callerCalls.Any(c => c.Kind == SemaphoreCallKind.Release && c.Address == semaphore);
                holds.AddRange(HeldBefore(callerCalls, at, caller, caller.Name, furtherIn: furtherIn));
            }
            furtherIn.Add(new(callerCalls, site, null, caller));
        }
        return new SemaphoreUse { ReleasesIt = releases, WaitsInLoop = WaitsInLoop(body, current, semaphore), Holds = holds.DistinctBy(h => h.Address).ToList() };
    }

    /// A method on the way from a take to the wait: its calls on SemaphoreSlim, where it is (<paramref name="Point"/>:
    /// the wait, the call it is making or the await it is paused at; null when that isn't known), and for an await the
    /// call whose task it waits for (<paramref name="InProgress"/>).
    private readonly record struct RanSoFar(List<SemaphoreCall> Calls, int? Point, Started? InProgress, IlBody Body);

    /// The call an async method awaits (its IL offset), with the method further in that the await waits for: the
    /// MethodDef token of its MoveNext. That method is still running, and what it has released so far is in its own
    /// code. The call started it when it is a call to that method, or to a method that isn't async itself and returns
    /// its task (`Task FlushAsync() => FlushAsync(CancellationToken.None);`): whatever else such a method does has run.
    internal readonly record struct Started(int Call, int MoveNext);

    /// The wait runs again and again: a worker waiting for its next signal. Retrying a wait with a timeout until it gets
    /// the slot (`while (!gate.Wait(t)) Log(...)`) is one wait for the slot, unless the semaphore was created with no
    /// free slot, where it is a worker that wakes up now and then until it is told to stop.
    private bool WaitsInLoop(IlBody body, SemaphoreCall wait, ulong semaphore) =>
        body.Loops(wait.Offset, ignore: wait.RetryBranch is { } retry && !(_initialCounts.TryGetValue(semaphore, out var created) && created == 0) ? retry : null);

    /// SemaphoreSlim.WaitAsync at the end of an async chain: the method that awaits it and the methods that await that one.
    private SemaphoreUse? AsyncSemaphoreUse(TaskInfo blocked, ulong semaphore, CodeReader code)
    {
        // As the report tells it: WaitAsync with a token or a timeout waits through plumbing of its own, and the method
        // that awaits it is the link before that.
        var chain = Describe.ReadableChain(blocked);
        if (chain.Count < 2 || chain[^2].Kind != TaskKind.AsyncMethod) return null;

        var releases = false;
        var loops = false;
        var holds = new List<HeldSemaphore>();
        var furtherIn = new List<RanSoFar>();
        // The method of the link further in, when that is an async method whose code was read.
        MethodCode? inner = null;
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            var task = chain[i];
            var waits = i == chain.Count - 2;
            if (task.Kind != TaskKind.AsyncMethod || ReadAsyncMethod(task, code) is not { } method)
            {
                if (waits) return null;
                inner = null;
                continue;
            }
            var calls = SemaphoreCalls(method.Body, method.This);
            var paused = method.Body.SuspensionPoint(method.State);
            var awaited = paused is { } at ? AwaitedCall(method.Body.Instructions, method.Body.CalleeName, at) : null;
            // The method further in is still running: what it has released so far is in its own code.
            Started? running = awaited is { } call && inner is { } next && next.Module.Address == method.Body.Module.Address ? new(call, next.Token) : null;
            if (waits)
            {
                var current = CurrentAwaitedWait(calls, semaphore, paused, awaited);
                if (current?.Key is null) return null;
                RememberInitialCount(semaphore, current, method.Body, code);
                releases = calls.Any(c => c.Kind == SemaphoreCallKind.Release && SameSemaphore(c, current, semaphore));
                loops = WaitsInLoop(method.Body, current, semaphore);
                holds.AddRange(HeldBefore(calls, current.Offset, method.Body, task.Description));
                furtherIn.Add(new(calls, current.Offset, null, method.Body));
            }
            else
            {
                releases |= calls.Any(c => c.Kind == SemaphoreCallKind.Release && c.Address == semaphore);
                // Without the point it is paused at, nothing can be said about what it holds.
                if (paused is { } p) holds.AddRange(HeldBefore(calls, p, method.Body, task.Description, running, furtherIn));
                furtherIn.Add(new(calls, paused, running, method.Body));
            }
            inner = method.Body;
        }
        return new SemaphoreUse { ReleasesIt = releases, WaitsInLoop = loops, Holds = holds.DistinctBy(h => h.Address).ToList() };
    }

    private (MethodCode Body, int State, ClrObject This)? ReadAsyncMethod(TaskInfo task, CodeReader code)
    {
        var box = _heap.GetObject(task.Address);
        if (!box.IsValid || !TryReadStateMachine(box, out var stateMachine) || stateMachine.Type is not { } smType) return null;
        if (smType.Methods.FirstOrDefault(m => m.Name == "MoveNext") is not { } moveNext || code.Read(moveNext) is not { } body) return null;
        var self = task.ThisAddress is { } a ? _heap.GetObject(a) : default;
        return (body, stateMachine.ReadInt("<>1__state") ?? -1, self);
    }

    private static int CallerIlOffset(ClrStackFrame frame)
    {
        try
        {
            // Not the innermost frame: the instruction pointer is a return address, which belongs to the next instruction.
            return IlOffsetAt(frame.Method!.ILOffsetMap, frame.InstructionPointer - 1);
        }
        catch
        {
            return -1;
        }
    }

    /// The IL offset of the map entry that covers the address, else -1. GetILOffset falls back to an offset near the start
    /// when none does, as for code the JIT recompiled after warm-up, whose map ClrMD reads from another version of the code.
    internal static int IlOffsetAt(IEnumerable<ILToNativeMap> map, ulong address)
    {
        foreach (var entry in map)
            if (entry.StartAddress <= address && address < entry.EndAddress) return entry.ILOffset >= 0 ? entry.ILOffset : -1;
        return -1;
    }

    /// The `this` of a frame that is making the call at <paramref name="call"/>: the one object of the method's own type
    /// the frame keeps alive, when that surely is `this` (see <see cref="UsesThisAfterEach"/>).
    private static ClrObject ThisOf(ClrStackFrame frame, List<ClrStackRoot> roots, MethodCode body, int call) =>
        ThisCandidates(frame, roots) is [var only] candidates && (body.SlotsFor(candidates) == 0 || UsesThisAfterEach(body, [call])) ? only : default;

    /// Whether a frame surely still reports its `this` while it makes one of these calls. Optimized code stops reporting
    /// `this` after its last use, and an argument or a local of the same class can then be the only object of the class
    /// left on the frame: read as `this`, its semaphores would pass for the ones this thread took. The frame does report
    /// it when the code reads a field of `this` on some way on from the call, whichever of the calls it is. (The other
    /// proof is in the frame itself: one object for `this` and for every parameter and local that can hold one.)
    private static bool UsesThisAfterEach(MethodCode body, IEnumerable<int> calls)
    {
        var any = false;
        foreach (var call in calls)
        {
            if (!body.ReadsFieldOfThisAfter(call)) return false;
            any = true;
        }
        return any;
    }

    /// The objects of the method's own type that the frame keeps alive; `this` is one of them.
    private static List<ClrObject> ThisCandidates(ClrStackFrame frame, List<ClrStackRoot> roots)
    {
        if (frame.Method is not { } method || (method.Attributes & MethodAttributes.Static) != 0 || method.Type is not { } type) return [];
        return roots
            .Where(r => r.StackFrame?.StackPointer == frame.StackPointer && !r.Object.IsNull && IsOfType(r.Object.Type, type))
            .Select(r => r.Object)
            .DistinctBy(o => o.Address)
            .ToList();
    }

    /// Which candidate is `this`, from the wait in progress, given that `this` is among them. In `Lock.Wait()` it is the
    /// one whose Lock the thread waits for. In `other.Lock.Wait()` it is the one left once the object whose Lock the
    /// thread waits for is set aside; `other` can be `this` itself under another name, so that only when the frame
    /// reports one object for `this` and for every parameter and local that can hold one
    /// (<paramref name="everySlot"/>). Null when two of them have the semaphore: nothing singles one out then.
    private static ClrObject? WhichIsThis(MethodCode body, List<ClrObject> candidates, SemaphoreCall current, ulong semaphore, bool everySlot)
    {
        if (current.Field is not { } token || body.IsStaticField(token) || body.Field(token) is not { } field) return null;
        var owners = candidates.Where(c => IsOfType(c.Type, field.DeclaringType)).ToList();
        if (owners.Count != candidates.Count) return null;
        var waitedOn = owners.Where(c => FieldValue(c, field) == semaphore).ToList();
        if (waitedOn.Count != 1) return null;
        var left = current.OnThis ? waitedOn
            : everySlot ? owners.Except(waitedOn).ToList()
            : [];
        return left is [var self] ? self : null;
    }

    private static ulong? FieldValue(ClrObject obj, MetadataNames.FieldName field)
    {
        try
        {
            return InstanceField(obj.Type, field) is { IsObjectReference: true } f && obj.ReadObjectField(f) is { IsNull: false } value ? value.Address : null;
        }
        catch
        {
            return null;
        }
    }

    /// The field the IL names, on an object's type: by its name and by the type that declares it, since a class and a
    /// class derived from it can each have a private field of the same name. Null when that doesn't pick out one.
    private static ClrInstanceField? InstanceField(ClrType? type, MetadataNames.FieldName field)
    {
        if (type is null) return null;
        var fields = type.Fields.ToList();
        return PickField(fields.Select(f => (f.Name, f.ContainingType?.Name)).ToList(), field) is { } i ? fields[i] : null;
    }

    /// Of an object's fields (name, declaring type), the one the IL names: the only one of that name, or of several the
    /// one its declaring type has.
    internal static int? PickField(IReadOnlyList<(string? Name, string? Owner)> fields, MetadataNames.FieldName field)
    {
        var named = Enumerable.Range(0, fields.Count).Where(i => fields[i].Name == field.Name).ToList();
        if (named is [var only]) return only;
        var declared = named.Where(i => fields[i].Owner is { } owner && WithoutGenerics(owner) == WithoutGenerics(field.DeclaringType)).ToList();
        return declared is [var match] ? match : null;
    }

    /// The type or one of its base types has this name; a generic type matches its definition ("Pump`1" for "Pump&lt;int&gt;").
    private static bool IsOfType(ClrType? type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (WithoutGenerics(t.Name) == WithoutGenerics(name)) return true;
        return false;
    }

    /// Generic arguments aside: shared generic code names its type Cache&lt;System.__Canon&gt;, the object Cache&lt;System.String&gt;.
    private static bool IsOfType(ClrType? type, ClrType expected) => IsOfType(type, expected.Name ?? "");

    // ---------------------------------------------------------------- the calls on SemaphoreSlim in a method

    internal enum SemaphoreCallKind { Wait, WaitAsync, Release }

    /// <param name="Key">How the receiver is loaded ("this M:_gate", "static M:Gate"...), to match a Release with its Wait in one method.</param>
    /// <param name="Address">The semaphore it is called on, when the receiver could be read from the dump.</param>
    /// <param name="Field">The field token the receiver is loaded from, when it is a field.</param>
    /// <param name="OnThis">The receiver is a field of `this`.</param>
    /// <param name="HeldIn">For a Wait or WaitAsync, the IL offsets [From, To) where the code has the slot it took; null
    /// when that can't be told (a wait with a timeout whose result isn't tested right away), so it doesn't count as a take.</param>
    /// <param name="RetryBranch">For a wait with a timeout retried until it gets the slot (`while (!gate.Wait(t)) ...`),
    /// the IL offset of the branch that goes back to it.</param>
    internal sealed record SemaphoreCall(int Offset, SemaphoreCallKind Kind, string? Key, ulong? Address, int? Field, bool OnThis, (int From, int To)? HeldIn, int? RetryBranch = null);

    private List<SemaphoreCall> SemaphoreCalls(MethodCode body, ClrObject self)
    {
        var calls = new List<SemaphoreCall>();
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var ins = body.Instructions[i];
            if (ins.Code != OpCodes.Call.Value && ins.Code != OpCodes.Callvirt.Value) continue;
            if (body.Callee(ins.Operand) is not ({ } type, { } name) || type != SemaphoreType) continue;
            SemaphoreCallKind? kind = name switch
            {
                "Wait" => SemaphoreCallKind.Wait,
                "WaitAsync" => SemaphoreCallKind.WaitAsync,
                "Release" => SemaphoreCallKind.Release,
                _ => null,
            };
            if (kind is null) continue;
            var (key, field, onThis) = Receiver(body, i);
            var address = field is { } f ? ResolveField(body, f, onThis, self) : null;
            var heldIn = kind == SemaphoreCallKind.Release ? null : HeldAfter(body, i);
            calls.Add(new SemaphoreCall(ins.Offset, kind.Value, key, address, field, onThis, heldIn, kind == SemaphoreCallKind.Release ? null : RetryBranch(body, i)));
        }
        return calls;
    }

    /// Where the code has the slot a Wait or WaitAsync took. One without a timeout returns once it has it: everything after
    /// it. One with a timeout (Wait(0), WaitAsync(TimeSpan)...) returns whether it got it, so only the code its true result
    /// leads to, as the branch that tests the result right away shows: `if (!gate.Wait(t)) throw ...;` holds after the
    /// throw, `if (gate.Wait(0)) { ... }` inside the block, and `while (!gate.Wait(100)) { }` after the loop. A WaitAsync
    /// only starts to wait, so it counts when the code waits for its task right there
    /// (<see cref="WaitsForTaskOf"/>). Null otherwise.
    internal static (int From, int To)? HeldAfter(IlBody body, int call)
    {
        var ins = body.Instructions;
        if (body.ReturnsResult(ins[call].Operand) is not { } timed) return null;
        if (body.Callee(ins[call].Operand).Name == "WaitAsync" && WaitsForTaskOf(body, call) < 0) return null;
        if (!timed) return (ins[call].Offset + 1, int.MaxValue);
        if (ResultTest(body, call) is not var (j, jumpsIfTaken)) return null;
        var target = ins[j].Target!.Value;
        var fallThrough = ins[j + 1].Offset;

        // The code the true result leads to has the slot only if nothing else leads there: `if (!g.Wait(t) && strict)
        // throw ...;` lands after the throw without the slot too, and so does a `break` out of `while (!g.Wait(t))`.
        if (!jumpsIfTaken)
        {
            if (body.OtherWayInto(fallThrough, j)) return null;
            return target <= ins[call].Offset ? (fallThrough, int.MaxValue) : (fallThrough, target);
        }
        // if (!taken) { ... } and on: held from the target on, as long as the code for "not taken" can't get there itself:
        // it runs straight to a throw, a return or a leave.
        if (target <= ins[j].Offset) return null;
        var last = ins.FindLastIndex(i => i.Offset < target);
        if (last <= j || !Exits(ins, last)) return null;
        for (var k = j + 1; k < last; k++)
            if (ins[k].Target is not null || ins[k].Switch is not null) return null;
        return body.OtherWayInto(target, j) ? null : (target, int.MaxValue);
    }

    /// The branch back to a wait with a timeout that its false result takes, `while (!gate.Wait(t)) Log(...)`: a retry
    /// until it gets the slot, not a loop around the wait. Null for any other shape.
    internal static int? RetryBranch(IlBody body, int call)
    {
        var ins = body.Instructions;
        if (body.ReturnsResult(ins[call].Operand) is not true || ResultTest(body, call) is not (var j, false)) return null;
        return ins[j].Target <= ins[call].Offset ? ins[j].Offset : null;
    }

    /// The branch that tests a timed wait's result right away (Wait's bool, or the GetResult() of the awaiter of
    /// WaitAsync's Task&lt;bool&gt;), and whether it jumps when the slot was taken. Null for any other shape.
    private static (int Branch, bool JumpsIfTaken)? ResultTest(IlBody body, int call)
    {
        var ins = body.Instructions;
        var result = call;
        if (body.Callee(ins[call].Operand).Name == "WaitAsync")
        {
            // The result of the await made on its task right there: the GetResult() of that awaiter comes first.
            if (WaitsForTaskOf(body, call) is not (>= 0 and var awaiter) || body.CalleeName(ins[awaiter].Operand) != "GetAwaiter") return null;
            result = ins.FindIndex(awaiter + 1, i => (i.Code == OpCodes.Call.Value || i.Code == OpCodes.Callvirt.Value)
                                                     && body.Callee(i.Operand) is ({ } t, "GetResult") && t.Contains("Awaiter", StringComparison.Ordinal));
            if (result < 0) return null;
        }
        // Skip what a debug build puts in between: `ldc.i4.0; ceq` for a `!`, a store to a local read right back, and in a
        // state machine a store to a field of its own read right back.
        var negated = false;
        var j = result + 1;
        while (j + 1 < ins.Count)
        {
            if (ConstantInt(ins[j]) == 0 && ins[j + 1].Code == OpCodes.Ceq.Value) negated = !negated;
            else if (LocalIndex(ins[j], load: false) is { } local && LocalIndex(ins[j + 1], load: true) == local) { }
            else if (j + 2 < ins.Count && ins[j].Code == OpCodes.Stfld.Value && ins[j + 1].Code == OpCodes.Ldarg_0.Value
                     && ins[j + 2].Code == OpCodes.Ldfld.Value && ins[j + 2].Operand == ins[j].Operand) j++;
            else break;
            j += 2;
        }
        if (j + 1 >= ins.Count || ins[j].Target is null) return null;
        // A jump into what was skipped brings a value from somewhere else (`flag || g.Wait(0)`).
        for (var k = result + 1; k <= j; k++)
            if (body.IsJumpTarget(ins[k].Offset)) return null;
        if (ins[j].Code == OpCodes.Brtrue.Value || ins[j].Code == OpCodes.Brtrue_S.Value) return (j, !negated);
        if (ins[j].Code == OpCodes.Brfalse.Value || ins[j].Code == OpCodes.Brfalse_S.Value) return (j, negated);
        return null;
    }

    /// The call that waits for the task a WaitAsync returns, made on it right where the code gets it (its index, else
    /// -1): the GetAwaiter() of `await gate.WaitAsync()` or of .GetAwaiter().GetResult(), past a ConfigureAwait(...) and
    /// the store and address-load of a struct in between, or a .Wait(). A task that is kept in a local, handed to
    /// Task.WhenAny or given a continuation may still be waiting for its slot long after the call: the call took nothing.
    internal static int WaitsForTaskOf(IlBody body, int call)
    {
        static bool IsCall(Instruction i) => i.Code == OpCodes.Call.Value || i.Code == OpCodes.Callvirt.Value;
        var ins = body.Instructions;
        var j = call + 1;
        if (j + 1 < ins.Count && ConstantInt(ins[j]) is not null && IsCall(ins[j + 1]) && body.CalleeName(ins[j + 1].Operand) == "ConfigureAwait") j += 2;
        if (j + 1 < ins.Count && LocalIndex(ins[j], load: false) is { } local
            && (ins[j + 1].Code == OpCodes.Ldloca_S.Value || ins[j + 1].Code == OpCodes.Ldloca.Value) && ins[j + 1].Operand == local) j += 2;
        if (j >= ins.Count || !IsCall(ins[j])) return -1;
        for (var k = call + 1; k <= j; k++)
            if (body.IsJumpTarget(ins[k].Offset)) return -1;
        var (type, name) = body.Callee(ins[j].Operand);
        return name == "GetAwaiter" || name == "Wait" && type == "System.Threading.Tasks.Task" && body.ParameterCount(ins[j].Operand) == 0 ? j : -1;
    }

    /// The instruction leaves the method or the try block it is in: throw, return, leave, or a jump to a return.
    private static bool Exits(List<Instruction> ins, int i)
    {
        var code = ins[i].Code;
        if (code == OpCodes.Throw.Value || code == OpCodes.Rethrow.Value || code == OpCodes.Ret.Value
            || code == OpCodes.Leave.Value || code == OpCodes.Leave_S.Value) return true;
        if ((code != OpCodes.Br.Value && code != OpCodes.Br_S.Value) || ins[i].Target is not { } target) return false;
        // A debug build's `return x;` jumps to the method's one `ldloc; ret`.
        var at = ins.FindIndex(x => x.Offset == target);
        return at >= 0 && (ins[at].Code == OpCodes.Ret.Value || at + 1 < ins.Count && LocalIndex(ins[at], load: true) is not null && ins[at + 1].Code == OpCodes.Ret.Value);
    }

    /// The SemaphoreSlim a call is made on: the value under its arguments on the stack, followed back through
    /// straight-line code. A field of `this` or a static field can be read from the dump; a local or a parameter only
    /// matches a Release with its Wait. When the code merges two values there (`var gate = flag ? _a : _b;`) or the value
    /// comes from a call, the receiver is unknown, and it never counts as a semaphore the code holds.
    internal static (string? Key, int? Field, bool OnThis) Receiver(IlBody body, int call)
    {
        var ins = body.Instructions;
        var args = body.ParameterCount(ins[call].Operand);
        if (args < 0 || body.Producer(call, args) is not (>= 0 and var j)) return (null, null, false);
        if (IsLoadOfLocalOrArgument(ins[j].Code)) return ($"{ins[j].Code:x}:{ins[j].Operand:x}", null, false);
        if (ins[j].Code != OpCodes.Ldfld.Value && ins[j].Code != OpCodes.Ldsfld.Value) return (null, null, false);
        if (body.Field(ins[j].Operand) is not { FieldType: SemaphoreType }) return (null, null, false);
        var token = ins[j].Operand;
        if (ins[j].Code == OpCodes.Ldsfld.Value) return ($"static {token:x}", token, false);

        // this._gate; in an async method's MoveNext this.<>4__this._gate, or the local the compiler copies <>4__this into.
        var owner = body.Producer(j, 0);
        if (owner < 0) return (null, token, false);
        var loadsThis = ins[owner].Code == OpCodes.Ldarg_0.Value
                        || owner >= 1 && IsLoadOfAsyncThis(body, owner - 1) && body.Producer(owner, 0) == owner - 1
                        || LocalIndex(ins[owner], load: true) is { } local && body.AsyncThisLocals.Contains(local);
        if (loadsThis) return ($"this {token:x}", token, true);
        return IsLoadOfLocalOrArgument(ins[owner].Code) ? ($"{ins[owner].Code:x}:{ins[owner].Operand:x} {token:x}", token, false) : (null, token, false);
    }

    /// ldarg.0; ldfld &lt;&gt;4__this: the object an async method was called on, in its MoveNext.
    private static bool IsLoadOfAsyncThis(IlBody body, int i) =>
        i + 1 < body.Instructions.Count && body.Instructions[i].Code == OpCodes.Ldarg_0.Value
        && body.Instructions[i + 1].Code == OpCodes.Ldfld.Value && body.Field(body.Instructions[i + 1].Operand)?.Name == "<>4__this";

    /// The local an ldloc reads or an stloc writes.
    private static int? LocalIndex(Instruction i, bool load) => (i.Code, load) switch
    {
        ( >= 0x06 and <= 0x09, true) => i.Code - 0x06, // ldloc.0-3
        ( >= 0x0A and <= 0x0D, false) => i.Code - 0x0A, // stloc.0-3
        (0x11, true) or (0x13, false) => i.Operand, // ldloc.s, stloc.s
        (unchecked((short)0xFE0C), true) or (unchecked((short)0xFE0E), false) => i.Operand, // ldloc, stloc
        _ => null,
    };

    private static bool IsLoadOfLocalOrArgument(short code) =>
        code is >= 0x02 and <= 0x09 // ldarg.0-3, ldloc.0-3
        || code == OpCodes.Ldarg_S.Value || code == OpCodes.Ldloc_S.Value || code == OpCodes.Ldarg.Value || code == OpCodes.Ldloc.Value;

    /// The semaphore a static field, or a field of `this`, holds in the dump.
    private ulong? ResolveField(MethodCode body, int token, bool onThis, ClrObject self)
    {
        if (body.Field(token) is not { } field) return null;
        try
        {
            ClrObject value = default;
            if (!onThis && body.IsStaticField(token))
            {
                if (body.Module.GetTypeByName(field.DeclaringType) is not { } type || type.GetStaticFieldByName(field.Name) is not { IsObjectReference: true } s) return null;
                var domain = body.Module.AppDomain ?? _runtime.AppDomains.FirstOrDefault();
                if (domain is null || !s.IsInitialized(domain)) return null;
                value = s.ReadObject(domain);
            }
            else if (onThis && !self.IsNull && InstanceField(self.Type, field) is { IsObjectReference: true } f)
            {
                value = self.ReadObjectField(f);
            }
            return !value.IsNull && value.Type?.Name == SemaphoreType ? value.Address : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool SameSemaphore(SemaphoreCall call, SemaphoreCall wait, ulong semaphore) =>
        call.Address == semaphore || (call.Address is null && call.Key is not null && call.Key == wait.Key);

    /// Which of a method's waits is the one in progress: the first at or after the frame's IL offset (optimized code often
    /// maps a return address to the start of the statement). A wait before it has completed, and one on another semaphore
    /// isn't this one; when none is left, the thread waits somewhere this method's code doesn't show (a helper the JIT
    /// inlined), and null says so. Without the offset only a single wait known to be on this semaphore will do.
    internal static SemaphoreCall? CurrentWait(List<SemaphoreCall> waits, ulong semaphore, int ilOffset)
    {
        var candidates = waits.Where(c => c.Address is null || c.Address == semaphore).ToList();
        if (ilOffset < 0) return candidates.Where(c => c.Address == semaphore).ToList() is [var only] ? only : null;
        return candidates.FirstOrDefault(c => c.Offset >= ilOffset);
    }

    /// The WaitAsync an async method is paused on: the call whose task the await it is suspended at waits for
    /// (<paramref name="awaited"/>). Null when that await is on something else (a helper that returns the semaphore's
    /// task) or on another semaphore. Without the suspension point only a single wait known to be on it will do.
    internal static SemaphoreCall? CurrentAwaitedWait(List<SemaphoreCall> calls, ulong semaphore, int? paused, int? awaited)
    {
        if (paused is null)
            return calls.Where(c => c.Kind == SemaphoreCallKind.WaitAsync && c.Address == semaphore).ToList() is [var only] ? only : null;
        return calls.FirstOrDefault(c => c.Offset == awaited && c.Kind == SemaphoreCallKind.WaitAsync && (c.Address is null || c.Address == semaphore));
    }

    /// The call whose result the await suspended at <paramref name="paused"/> waits for: the one right before its
    /// GetAwaiter(), past a ConfigureAwait(...) and the store and address-load of a struct in between. Null for any
    /// other shape, such as a task kept in a local first.
    internal static int? AwaitedCall(List<Instruction> ins, Func<int, string?> calleeName, int paused)
    {
        static bool IsCall(Instruction i) => i.Code == OpCodes.Call.Value || i.Code == OpCodes.Callvirt.Value;
        var at = ins.FindIndex(i => i.Offset == paused);
        var j = -1;
        for (var i = at - 1; i >= 0 && i >= at - 12; i--)
        {
            if (IsCall(ins[i]) && calleeName(ins[i].Operand) == "GetAwaiter") { j = i - 1; break; }
        }
        for (var guard = 0; guard < 4 && j >= 1; guard++)
        {
            var address = ins[j].Code == OpCodes.Ldloca_S.Value || ins[j].Code == OpCodes.Ldloca.Value ? ins[j].Operand : (int?)null;
            if (address is not null && LocalIndex(ins[j - 1], load: false) == address) j -= 2;
            else if (IsCall(ins[j]) && calleeName(ins[j].Operand) == "ConfigureAwait" && ConstantInt(ins[j - 1]) is not null) j -= 2;
            else break;
        }
        return j >= 0 && IsCall(ins[j]) ? ins[j].Offset : null;
    }

    /// Semaphores a method took before <paramref name="offset"/> and doesn't release on the way there: still held there,
    /// unless a method further in (<paramref name="furtherIn"/>) may have released it since. The method that the call
    /// at <paramref name="inProgress"/> started is one of those.
    private static IEnumerable<HeldSemaphore> HeldBefore(List<SemaphoreCall> calls, int offset, MethodCode body, string method, Started? inProgress = null, List<RanSoFar>? furtherIn = null)
    {
        foreach (var take in calls.Where(c => c.Kind != SemaphoreCallKind.Release && c.Offset < offset && c.Address is not null && HasSlotAt(body, c, offset)))
        {
            if (MayRelease(calls, take, offset, body, inProgress)) continue;
            var field = take.Field is { } token ? body.Field(token) : null;
            if (furtherIn?.Any(x => MayHaveReleased(x.Calls, take.Address!.Value, field, x.Point, x.InProgress, x.Body)) == true) continue;
            yield return new HeldSemaphore(take.Address!.Value, method, body.Location(take.Offset));
        }
    }

    /// Whether the code may release what <paramref name="take"/> took before it gets to <paramref name="offset"/>. A
    /// release counts when it lies between them, or when the point can be reached from it without taking the semaphore
    /// again (a loop that lets go of a setup lock after its first turn). It counts when it is on the same semaphore, or
    /// on one the code can't name (`_gate?.Release()`, a parameter); so does a call to a method of the same class whose
    /// code releases it or can't be read (`finally { Unlock(); }`). Of the call at <paramref name="inProgress"/> only
    /// what has run counts: the method it started is still running (see <see cref="Started"/>). The semaphore handed to
    /// other code there, or copied, may be released where this code doesn't show it (`_gate.SafeRelease()`,
    /// `Locks.Release(_gate)`, `new Releaser(_gate)`), and a lambda of the method that releases it may run at any time.
    internal static bool MayRelease(List<SemaphoreCall> calls, SemaphoreCall take, int offset, IlBody body, Started? inProgress = null) =>
        MayRelease(calls, take.Address, take.Key, take.Field is { } token ? body.Field(token) : null, take.Offset, offset, inProgress, body);

    /// <see cref="MayRelease(List{SemaphoreCall}, SemaphoreCall, int, IlBody, int?)"/> for a semaphore by its address,
    /// the way the code names it and the field it is in, on the ways from <paramref name="from"/> to
    /// <paramref name="offset"/>. For a method that didn't take it itself but has run since (a method the taker called
    /// and waits in), <paramref name="from"/> is -1, its start, and <paramref name="offset"/> is how far it has got;
    /// null when that isn't known, and a release anywhere in it counts.
    private static bool MayRelease(List<SemaphoreCall> calls, ulong? address, string? key, MetadataNames.FieldName? field, int from, int? offset, Started? inProgress, IlBody body)
    {
        bool before(int at) => offset is not { } to || at > from && at < to || body.ReachesWithout(at, to, from >= 0 ? from : null);
        foreach (var r in calls)
        {
            if (r.Kind != SemaphoreCallKind.Release || !before(r.Offset)) continue;
            if (r.Key is not null && r.Key == key) return true;
            if (r.Address is not null ? r.Address == address : !GivesBackItsOwn(calls, r, body)) return true;
        }
        // Code of another class that has run since the take, in a frame between the taker and the wait, has the object.
        var outsider = field is not null && !body.OwnTypes.Contains(WithoutGenerics(field.DeclaringType) ?? "");
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var ins = body.Instructions[i];
            if (ins.Code == OpCodes.Call.Value || ins.Code == OpCodes.Callvirt.Value || ins.Code == OpCodes.Newobj.Value)
            {
                if (body.OwnMethod(ins.Operand) is { } own)
                {
                    if (!before(ins.Offset)) continue;
                    // In a loop that doesn't take the semaphore again, an earlier turn made the call in progress too, and
                    // the method that one started has run to its end.
                    var started = inProgress is { } running && running.Call == ins.Offset && !body.ReachesWithout(ins.Offset, ins.Offset, from >= 0 ? from : null) ? running.MoveNext : 0;
                    if (OwnMethodMayRelease(body, own, field, started)) return true;
                }
                else if (field is not null && body.Callee(ins.Operand).Type != SemaphoreType && before(ins.Offset) && OtherCodeMayRelease(body, i, field, outsider))
                {
                    return true;
                }
            }
            else if (ins.Code == OpCodes.Ldftn.Value || ins.Code == OpCodes.Ldvirtftn.Value)
            {
                if (body.OwnMethod(ins.Operand) is { } target && OwnMethodMayRelease(body, target, field)) return true;
            }
            else if (field is not null && (Loads(body, ins, field) && !body.SemaphoreReceivers.Contains(i) || Stores(body, ins, field)) && before(ins.Offset))
            {
                return true;
            }
        }
        return false;
    }

    /// A method that didn't take the semaphore itself but has run since the take (the taker called it and waits in it)
    /// may have released it in the part of its code that has run: from its start to <paramref name="point"/>, the wait,
    /// the call it is making or the await it is paused at; null when that isn't known, and a release anywhere in it
    /// counts. <paramref name="field"/> is the field the taker has it in.
    internal static bool MayHaveReleased(List<SemaphoreCall> calls, ulong address, MetadataNames.FieldName? field, int? point, Started? inProgress, IlBody body) =>
        MayRelease(calls, address, key: null, field, from: -1, point, inProgress, body);

    /// A release on a semaphore the code can't read from the dump (a local, a parameter, a field of another object) gives
    /// back a slot the method took itself: a take on the same name whose slot the method has there, with no release of
    /// it in between. Whatever semaphore that is, the method holds no less of it afterwards than before that take.
    private static bool GivesBackItsOwn(List<SemaphoreCall> calls, SemaphoreCall release, IlBody body) =>
        release.Key is not null
        && calls.Any(c => c.Kind != SemaphoreCallKind.Release && c.Key == release.Key && HasSlotAt(body, c, release.Offset)
                          && !calls.Any(o => o.Kind == SemaphoreCallKind.Release && o.Key == release.Key && o.Offset > c.Offset && o.Offset < release.Offset));

    /// The instruction loads the field (or its address): by name and declaring type, since one field can have several tokens.
    private static bool Loads(IlBody body, Instruction ins, MetadataNames.FieldName field) =>
        (ins.Code == OpCodes.Ldfld.Value || ins.Code == OpCodes.Ldsfld.Value || ins.Code == OpCodes.Ldflda.Value || ins.Code == OpCodes.Ldsflda.Value)
        && body.Field(ins.Operand) is { } loaded && loaded.Name == field.Name && WithoutGenerics(loaded.DeclaringType) == WithoutGenerics(field.DeclaringType);

    /// The instruction puts another object into the field (`_gate = new SemaphoreSlim(1, 1);`): the semaphore the dump
    /// shows there is then not the one that was taken.
    private static bool Stores(IlBody body, Instruction ins, MetadataNames.FieldName field) =>
        (ins.Code == OpCodes.Stfld.Value || ins.Code == OpCodes.Stsfld.Value)
        && body.Field(ins.Operand) is { } stored && stored.Name == field.Name && WithoutGenerics(stored.DeclaringType) == WithoutGenerics(field.DeclaringType);

    private static bool SameField(MetadataNames.FieldName one, MetadataNames.FieldName other) =>
        one.Name == other.Name && WithoutGenerics(one.DeclaringType) == WithoutGenerics(other.DeclaringType);

    /// What a call to code outside the class may do to the field. Code of the module counts for what it shows, a few
    /// levels deep: a call into the class that may release the field, the field itself (a static semaphore is in
    /// anyone's reach, `Cleanup.ReleaseDb()`). Code that is passed `this` (`Courier.Finish(this)`) has the object: then
    /// what can't be read counts too, as a handler of an event that gets it as its sender. In the code of a frame of
    /// another class between the taker and the wait (<paramref name="outsider"/>), a call into the class is such a call.
    private static bool OtherCodeMayRelease(IlBody body, int call, MetadataNames.FieldName field, bool outsider)
    {
        var ins = body.Instructions[call];
        var owner = outsider ? body.OwnerOf(field) : body;
        if (outsider && owner.OwnMethod(ins.Operand) is { } into) return OwnMethodMayRelease(owner, into, field);
        var handed = !outsider && !body.IsStatic && PassesThis(body, call);
        if (MetadataNames.MethodDefinition(body.Metadata, ins.Operand) is { } other) return OtherMethodMayRelease(owner, other, field, handed);
        // Code of another module can't be read: a method of the field's class there, or anything that is passed `this`.
        var type = body.Callee(ins.Operand).Type;
        if (outsider) return WithoutGenerics(type) is { } t && WithoutGenerics(field.DeclaringType) is { } declaring
                             && (t == declaring || t.StartsWith(declaring + "+", StringComparison.Ordinal));
        return handed && !IsDelegateConstructor(body, call) && !(type is not null && KeepNothing.Contains(type));
    }

    /// Types of the runtime whose methods do nothing with an object they are passed but look at it or lock on it.
    private static readonly HashSet<string> KeepNothing = new(StringComparer.Ordinal)
    {
        "System.Object", "System.GC", "System.Threading.Monitor", "System.ObjectDisposedException", "System.ArgumentNullException",
    };

    /// `new Action(this.Run)`: the delegate's constructor is passed `this`, and the method it will run is looked at where
    /// the code names it (ldftn).
    private static bool IsDelegateConstructor(IlBody body, int call) =>
        body.Instructions[call].Code == OpCodes.Newobj.Value && call >= 1
        && (body.Instructions[call - 1].Code == OpCodes.Ldftn.Value || body.Instructions[call - 1].Code == OpCodes.Ldvirtftn.Value);

    /// The call is passed the object the method runs on as an argument, not as the object it is made on.
    private static bool PassesThis(IlBody body, int call)
    {
        var parameters = body.ParameterCount(body.Instructions[call].Operand);
        for (var k = 0; k < parameters; k++)
            if (body.Producer(call, k) is >= 0 and var j && LoadsThis(body, j)) return true;
        return false;
    }

    /// The instruction loads the object the method runs on: `this`, or what the MoveNext of an async method and a lambda
    /// keep it in (<>4__this, or the local it was copied to).
    private static bool LoadsThis(IlBody body, int j)
    {
        var ins = body.Instructions;
        if (j >= 1 && IsLoadOfAsyncThis(body, j - 1) && body.Producer(j, 0) == j - 1) return true;
        if (LocalIndex(ins[j], load: true) is { } local && body.AsyncThisLocals.Contains(local)) return true;
        return ins[j].Code == OpCodes.Ldarg_0.Value && !body.KeepsThisInAField;
    }

    private const int Handed = -1, NotHanded = -2;

    /// <see cref="OtherMethodMayRelease(IlBody, int, MetadataNames.FieldName, bool, int, HashSet{int})"/>, worked out
    /// once per method and field for all the threads that wait in the caller.
    private static bool OtherMethodMayRelease(IlBody owner, int method, MetadataNames.FieldName field, bool handed)
    {
        var key = (method, (string?)field.Name, WithoutGenerics(field.DeclaringType), handed ? Handed : NotHanded);
        if (!owner.ReleasesFound.TryGetValue(key, out var found))
            owner.ReleasesFound[key] = found = OtherMethodMayRelease(owner, method, field, handed, depth: 0, visited: []);
        return found;
    }

    /// A method of the module outside the class (the MethodDef token <paramref name="method"/>) may release the field:
    /// its code calls Release() on it, loads it or stores to it, calls a method of the class
    /// (<paramref name="owner"/>'s own methods) that may release it, or calls on into code of the module that does,
    /// a few levels deep. With <paramref name="handed"/> it has the object: then a Release() on a semaphore it can't
    /// name counts too, and so does what can't be read. Without it, code that can't be read is taken to leave the
    /// field alone, as code of other modules is.
    private static bool OtherMethodMayRelease(IlBody owner, int method, MetadataNames.FieldName field, bool handed, int depth, HashSet<int> visited)
    {
        if (!visited.Add(handed ? -method : method)) return false;
        if (depth > 2 || owner.ReadMethod?.Invoke(method) is not { } code) return handed;
        for (var i = 0; i < code.Instructions.Count; i++)
        {
            var ins = code.Instructions[i];
            if (ins.Code == OpCodes.Call.Value || ins.Code == OpCodes.Callvirt.Value || ins.Code == OpCodes.Newobj.Value)
            {
                if (code.Callee(ins.Operand) is (SemaphoreType, var name))
                {
                    if (name != "Release") continue;
                    var released = Receiver(code, i).Field is { } token ? code.Field(token) : null;
                    if (released is null ? handed : SameField(released, field)) return true;
                }
                else if (owner.OwnMethod(ins.Operand) is { } into)
                {
                    if (OwnMethodMayRelease(owner, into, field)) return true;
                }
                else if (MetadataNames.MethodDefinition(code.Metadata, ins.Operand) is { } next && OtherMethodMayRelease(owner, next, field, handed, depth + 1, visited))
                {
                    return true;
                }
            }
            else if (ins.Code == OpCodes.Ldftn.Value || ins.Code == OpCodes.Ldvirtftn.Value)
            {
                if (owner.OwnMethod(ins.Operand) is { } target
                        ? OwnMethodMayRelease(owner, target, field)
                        : MetadataNames.MethodDefinition(code.Metadata, ins.Operand) is { } made && OtherMethodMayRelease(owner, made, field, handed, depth + 1, visited))
                    return true;
            }
            else if (ins.Code == OpCodes.Stfld.Value && code.Field(ins.Operand)?.Name == "<>1__state")
            {
                // An async method: its code is the MoveNext of its state machine.
                if (MetadataNames.MoveNextOf(code.Metadata, ins.Operand) is not { } moveNext) return handed;
                if (OtherMethodMayRelease(owner, moveNext, field, handed, depth, visited)) return true;
            }
            else if (Loads(code, ins, field) && !code.SemaphoreReceivers.Contains(i) || Stores(code, ins, field))
            {
                return true;
            }
        }
        return false;
    }

    /// <see cref="OwnMethodMayRelease(IlBody, int, MetadataNames.FieldName?, int, HashSet{int}, ref int)"/>, worked out
    /// once per method and field for all the threads that wait in the caller. <paramref name="started"/> is the MoveNext
    /// of the method the call started that is still running, or 0 for a call that has returned.
    private static bool OwnMethodMayRelease(IlBody caller, int method, MetadataNames.FieldName? field, int started = 0)
    {
        var key = (method, field?.Name, WithoutGenerics(field?.DeclaringType), started);
        if (!caller.ReleasesFound.TryGetValue(key, out var found))
            caller.ReleasesFound[key] = found = OwnMethodMayRelease(caller, method, field, depth: 0, visited: [], ref started);
        return found;
    }

    /// A method of the caller's own class (the MethodDef token <paramref name="method"/>) may release the field: its code
    /// calls Release() on it or on a semaphore it can't name, hands the field to other code, makes a delegate of a method
    /// that may, calls on into the class past a few levels, or can't be read. An async method's code is the MoveNext of
    /// its state machine; the one at <paramref name="started"/> is still running and is left out, once: its code so far
    /// is looked at where it is paused. A method already looked at adds nothing.
    private static bool OwnMethodMayRelease(IlBody caller, int method, MetadataNames.FieldName? field, int depth, HashSet<int> visited, ref int started)
    {
        if (!visited.Add(method)) return false;
        if (depth > 2 || caller.ReadMethod?.Invoke(method) is not { } callee) return true;
        for (var i = 0; i < callee.Instructions.Count; i++)
        {
            var ins = callee.Instructions[i];
            if (ins.Code == OpCodes.Call.Value || ins.Code == OpCodes.Callvirt.Value || ins.Code == OpCodes.Newobj.Value)
            {
                if (callee.Callee(ins.Operand) is (SemaphoreType, "Release"))
                {
                    if (field is null || Receiver(callee, i).Field is not { } token || callee.Field(token) is not { } released
                        || released.Name == field.Name && WithoutGenerics(released.DeclaringType) == WithoutGenerics(field.DeclaringType))
                        return true;
                }
                else if (caller.OwnMethod(ins.Operand) is { } next)
                {
                    // A call in a loop has returned on the turns before: none of those is the one still running.
                    var none = 0;
                    if (callee.Loops(ins.Offset) ? OwnMethodMayRelease(caller, next, field, depth + 1, visited, ref none)
                                                 : OwnMethodMayRelease(caller, next, field, depth + 1, visited, ref started)) return true;
                }
                else if (field is not null && callee.Callee(ins.Operand).Type is var type && type != SemaphoreType)
                {
                    // Code outside the class, as in the method that took the semaphore (see OtherCodeMayRelease). Its
                    // `this` is the object only in code of the class the field is in.
                    var handed = !callee.IsStatic && caller.OwnTypes.Contains(WithoutGenerics(field.DeclaringType) ?? "") && PassesThis(callee, i);
                    if (MetadataNames.MethodDefinition(callee.Metadata, ins.Operand) is { } other
                            ? OtherMethodMayRelease(caller, other, field, handed)
                            : handed && !IsDelegateConstructor(callee, i) && !(type is not null && KeepNothing.Contains(type)))
                        return true;
                }
            }
            else if (ins.Code == OpCodes.Ldftn.Value || ins.Code == OpCodes.Ldvirtftn.Value)
            {
                // A delegate may run at any time, and more than once.
                var none = 0;
                if (caller.OwnMethod(ins.Operand) is { } target && OwnMethodMayRelease(caller, target, field, depth + 1, visited, ref none)) return true;
            }
            else if (ins.Code == OpCodes.Stfld.Value && callee.Field(ins.Operand)?.Name == "<>1__state")
            {
                // The method only starts a state machine: `async Task UnlockLaterAsync() { await ...; _gate.Release(); }`.
                if (MetadataNames.MoveNextOf(callee.Metadata, ins.Operand) is not { } moveNext) return true;
                if (moveNext == started)
                {
                    // The one still running. Another call that starts this method has run like any other call.
                    started = 0;
                    visited.Remove(method);
                    continue;
                }
                if (OwnMethodMayRelease(caller, moveNext, field, depth, visited, ref started)) return true;
            }
            else if (field is not null && (Loads(callee, ins, field) && !callee.SemaphoreReceivers.Contains(i) || Stores(callee, ins, field)))
            {
                return true;
            }
        }
        return false;
    }

    /// A Wait or WaitAsync has the slot at <paramref name="offset"/>: the offset is where the code holds it, and every way
    /// there goes through the take, and for a wait with a timeout through the branch its true result takes. A take inside
    /// an `if` that may not have run (`if (exclusive) _table.Wait();`) holds nothing after the `if`.
    internal static bool HasSlotAt(IlBody body, SemaphoreCall take, int offset) =>
        take.HeldIn is { } held && held.From <= offset && offset < held.To
        && body.OnEveryPathTo(take.Offset, offset)
        && (held.From == take.Offset + 1 || body.OnEveryPathTo(held.From, offset));

    /// The call in a caller's IL that led to the next frame in: a call to that method, the first at or after the frame's IL
    /// offset. A call before it has returned; none left means the call went through code inlined here, so null.
    private static int? CallSite(MethodCode caller, ClrMethod callee, int ilOffset)
    {
        var type = WithoutGenerics(callee.Type?.Name);
        var sites = caller.Instructions
            .Where(i => (i.Code == OpCodes.Call.Value || i.Code == OpCodes.Callvirt.Value || i.Code == OpCodes.Newobj.Value)
                        && caller.Callee(i.Operand) is ({ } t, { } n) && n == callee.Name && WithoutGenerics(t) == type)
            .Select(i => i.Offset)
            .ToList();
        return CallInProgress(sites, ilOffset);
    }

    /// Of a caller's calls to the next frame's method (IL offsets), the one in progress.
    internal static int? CallInProgress(List<int> sites, int ilOffset)
    {
        if (sites.Count == 0) return null;
        if (ilOffset < 0) return sites.Count == 1 ? sites[0] : null;
        return sites.Where(o => o >= ilOffset).Cast<int?>().FirstOrDefault();
    }

    private static string? WithoutGenerics(string? typeName) =>
        typeName?.IndexOfAny(['<', '`']) is { } i and >= 0 ? typeName[..i] : typeName;

    // ---------------------------------------------------------------- how a semaphore was created

    /// The initial count a semaphore was created with as the same value as its maximum, `new SemaphoreSlim(n, n)`.
    internal const int EqualsMax = int.MinValue;

    /// `new SemaphoreSlim(0, 1)` stored in the field the wait is made on: the field's type has the code that creates it.
    private void RememberInitialCount(ulong semaphore, SemaphoreCall wait, MethodCode body, CodeReader code)
    {
        if (wait.Address != semaphore || wait.Field is not { } token) return;
        var count = code.InitialCount(body, token);
        if (count == EqualsMax) count = _heap.GetObject(semaphore).ReadInt("m_maxCount");
        _initialCounts[semaphore] = _initialCounts.TryGetValue(semaphore, out var known) && known != count ? null : count;
    }

    /// The initial count of `new SemaphoreSlim(n)` with n a value that isn't a constant: a throttle sized from a setting
    /// or from the machine, made with free slots. A signal is made with a constant 0.
    internal const int FromVariable = int.MinValue + 1;

    /// The initial count a `new SemaphoreSlim(...)` at <paramref name="newobj"/> passes: a constant first argument,
    /// <see cref="EqualsMax"/> for (maxJobs, maxJobs), the same value loaded twice, or <see cref="FromVariable"/> for
    /// (maxJobs). Null otherwise, also when a jump lands among the arguments (`paused ? 0 : 1`).
    internal static int? CreatedWith(List<Instruction> ins, int newobj, int arguments, Func<int, (string? Type, string? Name)> callee)
    {
        bool landed(int from) => Enumerable.Range(from, newobj - from).Any(k => ins.Any(x => x.Target == ins[k].Offset || x.Switch?.Contains(ins[k].Offset) == true));
        bool same(int a, int b, int length) => Enumerable.Range(0, length).All(k => ins[a + k].Code == ins[b + k].Code && ins[a + k].Operand == ins[b + k].Operand);
        // One load: an argument, a local, a static field or Environment.ProcessorCount.
        bool load(Instruction i) => IsLoadOfLocalOrArgument(i.Code) || i.Code == OpCodes.Ldsfld.Value
                                    || i.Code == OpCodes.Call.Value && callee(i.Operand) is ("System.Environment", "get_ProcessorCount");
        if (arguments == 1)
        {
            if (newobj < 1 || landed(newobj - 1)) return null;
            if (ConstantInt(ins[newobj - 1]) is { } count) return count;
            if (load(ins[newobj - 1])) return FromVariable;
            return newobj >= 2 && !landed(newobj - 2) && ins[newobj - 2].Code == OpCodes.Ldarg_0.Value && ins[newobj - 1].Code == OpCodes.Ldfld.Value ? FromVariable : null;
        }
        if (arguments != 2 || newobj < 2) return null;
        var one = ins[newobj - 1];
        // A constant first argument, when the second one is a single value of its own.
        if (ConstantInt(ins[newobj - 2]) is { } constant)
            return !landed(newobj - 2) && (ConstantInt(one) is not null || load(one)) ? constant : null;
        if (same(newobj - 2, newobj - 1, 1) && load(one) && !landed(newobj - 2)) return EqualsMax;
        // this._max twice.
        if (newobj >= 4 && ins[newobj - 2].Code == OpCodes.Ldarg_0.Value && one.Code == OpCodes.Ldfld.Value && same(newobj - 4, newobj - 2, 2) && !landed(newobj - 4)) return EqualsMax;
        return null;
    }

    // ---------------------------------------------------------------- IL and metadata from the dump

    /// <param name="Switch">The targets of a switch.</param>
    internal readonly record struct Instruction(int Offset, short Code, int Operand, int? Target, int[]? Switch = null);

    /// A protected block and its handler, as IL offsets [start, end), with the clause's kind as the method header gives
    /// it: 0 catch, 1 filter (<paramref name="FilterStart"/> is where its test starts), 2 finally, 4 fault.
    internal readonly record struct ExceptionClause(int Kind, int TryStart, int TryEnd, int HandlerStart, int HandlerEnd, int FilterStart = 0)
    {
        public const int Catch = 0, Filter = 1, Finally = 2, Fault = 4;
    }

    /// A method's IL, decoded, with the metadata of its module to name what it calls and to follow its values and paths.
    /// <paramref name="clauses"/> are its try blocks; null when they couldn't be read.
    internal class IlBody(List<Instruction> instructions, MetadataReader metadata, IReadOnlyList<ExceptionClause>? clauses = null)
    {
        public List<Instruction> Instructions => instructions;
        public MetadataReader Metadata => metadata;
        public IReadOnlyList<ExceptionClause>? Clauses => clauses;

        /// The method's own type, the types it is nested in (an async method's state machine, a lambda's closure) and their
        /// base types, without generic arguments: a call to one of their methods is a call into the same class.
        public IReadOnlySet<string> OwnTypes { get; init; } = new HashSet<string>();

        /// The IL of another method of this module, by its MethodDef token; null when it can't be read.
        public Func<int, IlBody?>? ReadMethod { get; init; }

        /// A static method: its first argument (ldarg.0) is a parameter, not `this`.
        public bool IsStatic { get; init; }

        /// The MoveNext of an async method, or a lambda that captured `this`: its own ldarg.0 is the state machine or
        /// the closure, and the object the method runs on is in their &lt;&gt;4__this.
        public bool KeepsThisInAField => _keepsThis ??= StateLocal >= 0 || Enumerable.Range(0, Math.Max(0, instructions.Count - 1)).Any(i =>
            instructions[i].Code == OpCodes.Ldarg_0.Value && instructions[i + 1].Code == OpCodes.Ldfld.Value && Field(instructions[i + 1].Operand)?.Name == "<>4__this");
        private bool? _keepsThis;

        /// The module as the class that declares <paramref name="field"/> sees it: its own methods are the ones of that
        /// class and of the classes nested in it. For code of another class that calls into it.
        public IlBody OwnerOf(MetadataNames.FieldName field)
        {
            var name = WithoutGenerics(field.DeclaringType) ?? "";
            if (!_owners.TryGetValue(name, out var owner))
                _owners[name] = owner = new IlBody([], metadata) { OwnTypes = new HashSet<string>(StringComparer.Ordinal) { name }, ReadMethod = ReadMethod };
            return owner;
        }
        private readonly Dictionary<string, IlBody> _owners = [];

        /// Whether a method of the class this one calls may release a field, by method token and field, and by the
        /// method the call started that is still running (0 for none).
        public Dictionary<(int Method, string? Field, string? Owner, int Started), bool> ReleasesFound { get; } = [];

        /// The MethodDef token of a method of this module that the call goes to, when it is declared in one of
        /// <see cref="OwnTypes"/> or in a type nested in one (the class the compiler makes for a lambda that captures a
        /// local, a private helper class): code that can reach the class's private fields. Null for anything else.
        public int? OwnMethod(int token)
        {
            if (OwnTypes.Count == 0 || MetadataNames.MethodDefinition(metadata, token) is not { } def || Callee(token).Type is not { } type) return null;
            return WithoutGenerics(type) is { } plain && OwnTypes.Contains(plain)
                   || OwnTypes.Any(own => type.Length > own.Length && type[own.Length] == '+' && type.StartsWith(own, StringComparison.Ordinal))
                ? def
                : null;
        }

        /// The instructions (by index) that load the object a call on SemaphoreSlim is made on: `_gate` in `_gate.Wait(100)`.
        public HashSet<int> SemaphoreReceivers
        {
            get
            {
                if (_receivers is not null) return _receivers;
                var receivers = new HashSet<int>();
                for (var i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].Code != OpCodes.Call.Value && instructions[i].Code != OpCodes.Callvirt.Value) continue;
                    if (Callee(instructions[i].Operand).Type != SemaphoreType || ParameterCount(instructions[i].Operand) is not (>= 0 and var arguments)) continue;
                    if (Producer(i, arguments) is >= 0 and var receiver) receivers.Add(receiver);
                }
                return _receivers = receivers;
            }
        }
        private HashSet<int>? _receivers;

        /// The code reads a field of `this` (ldarg.0; ldfld) on some way on from the call at <paramref name="offset"/>:
        /// `this` is still needed while that call runs. A call on `this` doesn't show that, since the method it calls
        /// may be inlined and never touch it. False when the ways through the code aren't known.
        public bool ReadsFieldOfThisAfter(int offset)
        {
            if (_readsThisAfter.TryGetValue(offset, out var known)) return known;
            var found = false;
            if (PathsKnown && Index.TryGetValue(offset, out var call))
            {
                var next = _next ??= Graph();
                var seen = new bool[instructions.Count];
                var work = new Stack<int>();
                foreach (var n in next[call])
                {
                    if (seen[n]) continue;
                    seen[n] = true;
                    work.Push(n);
                }
                while (!found && work.TryPop(out var i))
                {
                    found = instructions[i].Code == OpCodes.Ldarg_0.Value && i + 1 < instructions.Count && !IsJumpTarget(instructions[i + 1].Offset)
                            && (instructions[i + 1].Code == OpCodes.Ldfld.Value || instructions[i + 1].Code == OpCodes.Ldflda.Value);
                    foreach (var n in next[i])
                    {
                        if (seen[n]) continue;
                        seen[n] = true;
                        work.Push(n);
                    }
                }
            }
            return _readsThisAfter[offset] = found;
        }
        private readonly Dictionary<int, bool> _readsThisAfter = [];

        /// Locals that MoveNext copies &lt;&gt;4__this into (ldarg.0; ldfld &lt;&gt;4__this; stloc), as release builds do.
        public HashSet<int> AsyncThisLocals => _asyncThisLocals ??= instructions
            .Select((_, i) => i)
            .Where(i => i + 2 < instructions.Count && IsLoadOfAsyncThis(this, i) && LocalIndex(instructions[i + 2], load: false) is not null)
            .Select(i => LocalIndex(instructions[i + 2], load: false)!.Value)
            .ToHashSet();
        private HashSet<int>? _asyncThisLocals;

        /// A backward branch from after the offset to it or before it, other than the one at <paramref name="ignore"/>:
        /// the instruction there runs in a loop.
        public bool Loops(int offset, int? ignore = null) =>
            instructions.Any(i => i.Target is { } target && i.Offset > offset && target <= offset && i.Offset != ignore);

        /// Where an async method's MoveNext suspends for an await state: the compiler stores the state
        /// (ldc.i4 state; dup; stloc; stfld &lt;&gt;1__state) right before it hands its continuation to the awaiter.
        public int? SuspensionPoint(int state)
        {
            if (state < 0) return null;
            for (var i = 0; i + 3 < instructions.Count; i++)
            {
                if (ConstantInt(instructions[i]) != state || instructions[i + 1].Code != OpCodes.Dup.Value) continue;
                var store = instructions[i + 3];
                if (store.Code == OpCodes.Stfld.Value && Field(store.Operand)?.Name == "<>1__state") return instructions[i].Offset;
            }
            return null;
        }

        public (string? Type, string? Name) Callee(int token) => MetadataNames.Callee(metadata, token);
        public string? CalleeName(int token) => Callee(token).Name;
        public MetadataNames.FieldName? Field(int token) => _fields.TryGetValue(token, out var known) ? known : _fields[token] = MetadataNames.Field(metadata, token);
        private readonly Dictionary<int, MetadataNames.FieldName?> _fields = [];
        public bool IsStaticField(int token) => MetadataNames.IsStaticField(metadata, token);
        public int ParameterCount(int token) => MetadataNames.ParameterCount(metadata, token);
        public bool? ReturnsResult(int token) => MetadataNames.ReturnsResult(metadata, token);

        private Dictionary<int, int>? _index;
        private HashSet<int>? _jumpTargets;
        private List<int>[]? _next;
        private List<int>[]? _nextThrowing;

        /// Instruction index by IL offset.
        private Dictionary<int, int> Index => _index ??= instructions.Select((ins, i) => (ins.Offset, i)).ToDictionary(x => x.Offset, x => x.i);

        /// Offsets some branch or switch lands on: the stack and the path there can come from more than one place.
        public bool IsJumpTarget(int offset) =>
            (_jumpTargets ??= instructions.SelectMany(i => i.Switch ?? (i.Target is { } t ? [t] : [])).ToHashSet()).Contains(offset);

        /// The instruction that pushed the value <paramref name="below"/> places under the top of the stack right before
        /// instruction <paramref name="at"/>, as straight-line code shows it: the receiver of a call is the value under its
        /// arguments. -1 when a jump lands in between (`flag ? _a : _b`), the code branches, or the value comes from a dup.
        public int Producer(int at, int below)
        {
            if (at <= 0 || at >= instructions.Count || IsJumpTarget(instructions[at].Offset)) return -1;
            var want = below + 1;
            for (var j = at - 1; j >= 0 && j >= at - 48; j--)
            {
                if (StackEffect(instructions[j]) is not ({ } pops, { } pushes)) return -1;
                if (want <= pushes) return pushes == 1 ? j : -1;
                want += pops - pushes;
                if (IsJumpTarget(instructions[j].Offset)) return -1;
            }
            return -1;
        }

        /// How many values an instruction takes off the stack and puts on it; null for control flow and what can't be read.
        private (int? Pops, int? Pushes) StackEffect(Instruction i)
        {
            if (i.Target is not null || i.Switch is not null || !Behaviours.TryGetValue(i.Code, out var b) || b.ControlFlow) return (null, null);
            if (b.Pops is { } pops && b.Pushes is { } pushes) return (pops, pushes);
            if (i.Code != OpCodes.Call.Value && i.Code != OpCodes.Callvirt.Value && i.Code != OpCodes.Newobj.Value) return (null, null);
            if (MetadataNames.CallShape(metadata, i.Operand) is not var (parameters, hasThis, returnsValue)) return (null, null);
            return i.Code == OpCodes.Newobj.Value ? (parameters, 1) : (parameters + (hasThis ? 1 : 0), returnsValue ? 1 : 0);
        }

        /// Every way through the method's code to <paramref name="point"/> goes through the instruction at
        /// <paramref name="through"/>: a take inside an `if` that may not have run doesn't hold the semaphore after it,
        /// and one inside a try block doesn't hold it after a catch that carries on, since what throws before the take
        /// lands there. False when the point can't be reached at all as far as the code shows, or when the method's try
        /// blocks couldn't be read, so nothing is claimed there.
        public bool OnEveryPathTo(int through, int point)
        {
            if (!PathsKnown || !Index.TryGetValue(point, out var p) || !Index.TryGetValue(through, out var t)) return false;
            return Reaches(0, p, blocked: -1) && !Reaches(0, p, blocked: t);
        }

        /// Code the method can run, other than the instruction at index <paramref name="except"/>, goes on to
        /// <paramref name="offset"/>: a jump, a fall-through, or the end of a finally block. The jumps of an async method's
        /// MoveNext on its state are not ways in (see <see cref="Successors"/>). True when that can't be told.
        public bool OtherWayInto(int offset, int except)
        {
            if (!PathsKnown || !Index.TryGetValue(offset, out var to)) return true;
            var next = _next ??= Graph();
            var run = _run ??= RunFromStart(next);
            for (var k = 0; k < next.Length; k++)
                if (k != except && run[k] && next[k].Contains(to)) return true;
            return false;
        }

        private bool[]? _run;

        /// The instructions some way from the method's start reaches.
        private static bool[] RunFromStart(List<int>[] next)
        {
            var seen = new bool[next.Length];
            if (next.Length == 0) return seen;
            var work = new Stack<int>();
            work.Push(0);
            seen[0] = true;
            while (work.TryPop(out var i))
                foreach (var n in next[i])
                    if (!seen[n])
                    {
                        seen[n] = true;
                        work.Push(n);
                    }
            return seen;
        }

        /// The code can get on from the instruction at <paramref name="from"/> to <paramref name="point"/> without passing
        /// <paramref name="without"/>; from the point itself, whether it comes back round to it (true when that can't be
        /// told). The call a frame is making is not run before itself: only an earlier turn of a loop around it is.
        public bool ReachesWithout(int from, int point, int? without)
        {
            var w = -1;
            if (!PathsKnown || !Index.TryGetValue(point, out var p) || !Index.TryGetValue(from, out var f) || without is { } offset && !Index.TryGetValue(offset, out w)) return true;
            // The same for every thread that waits at this point.
            if (_reachesWithout.TryGetValue((f, p, w), out var known)) return known;
            // What has run when something throws is done: `_gate.Release(); throw ...;` in a try block whose catch
            // carries on has released. So here a handler is entered from every instruction of its try block.
            var next = _nextThrowing ??= Graph(throwsAnywhere: true);
            return _reachesWithout[(f, p, w)] = f != p ? Reaches(next, f, p, blocked: w) : next[f].Any(n => Reaches(next, n, p, blocked: w));
        }
        private readonly Dictionary<(int From, int Point, int Without), bool> _reachesWithout = [];

        /// The ways through the code are known: the method has no try blocks, or their clauses were read.
        private bool PathsKnown => _pathsKnown ??= clauses is { Count: <= MaxClauses } || clauses is null && !instructions.Any(i => IsLeave(i) || i.Code == OpCodes.Endfinally.Value);
        private bool? _pathsKnown;

        private const int MaxClauses = 256;

        private static bool IsLeave(Instruction i) => i.Code == OpCodes.Leave.Value || i.Code == OpCodes.Leave_S.Value;

        private bool Reaches(int from, int target, int blocked) => Reaches(_next ??= Graph(), from, target, blocked);

        private bool Reaches(List<int>[] next, int from, int target, int blocked)
        {
            if (instructions.Count == 0 || from == blocked) return false;
            var seen = new bool[instructions.Count];
            var work = new Stack<int>();
            work.Push(from);
            seen[from] = true;
            while (work.TryPop(out var i))
            {
                if (i == target) return true;
                foreach (var n in next[i])
                {
                    if (n == blocked || seen[n]) continue;
                    seen[n] = true;
                    work.Push(n);
                }
            }
            return false;
        }

        private List<int>[] Graph(bool throwsAnywhere = false)
        {
            var handlers = HandlerEdges(throwsAnywhere);
            return instructions.Select((_, i) => handlers.TryGetValue(i, out var more) ? [.. Successors(i), .. more] : Successors(i)).ToArray();
        }

        /// The ways into handlers, which no branch shows. A catch, filter or fault block can be entered from the start of
        /// its try block, as if what throws came first: the way that leaves out the most, for what every way goes
        /// through. A finally block runs on each leave out of its try block, and carries on where those leaves go.
        /// With <paramref name="throwsAnywhere"/> every handler, a finally block too, can also be entered from each
        /// instruction of its try block, with what ran before it done: the ways that leave out the least, for what
        /// may have run. (What a finally block entered that way goes on to is the handler of a try block around it,
        /// which its own instructions lead to the same way.)
        private Dictionary<int, List<int>> HandlerEdges(bool throwsAnywhere = false)
        {
            var edges = new Dictionary<int, List<int>>();
            if (clauses is null) return edges;
            void add(int from, int offset)
            {
                if (!Index.TryGetValue(offset, out var to)) return;
                if (!edges.TryGetValue(from, out var list)) edges[from] = list = [];
                list.Add(to);
            }
            var leaves = instructions.Select((ins, i) => (ins, i)).Where(x => IsLeave(x.ins) && x.ins.Target is not null).Select(x => x.i).ToList();
            var ends = instructions.Select((ins, i) => (ins, i)).Where(x => x.ins.Code == OpCodes.Endfinally.Value).Select(x => x.i).ToList();
            foreach (var c in clauses)
            {
                if (!Index.TryGetValue(c.TryStart, out var start)) continue;
                for (var k = start; k < instructions.Count && instructions[k].Offset < c.TryEnd && (throwsAnywhere || k == start && c.Kind != ExceptionClause.Finally); k++)
                {
                    add(k, c.HandlerStart);
                    if (c.Kind == ExceptionClause.Filter) add(k, c.FilterStart);
                }
                if (c.Kind != ExceptionClause.Finally) continue;
                var targets = new List<int>();
                foreach (var l in leaves.Where(l => instructions[l].Offset >= c.TryStart && instructions[l].Offset < c.TryEnd))
                {
                    var to = instructions[l].Target!.Value;
                    if (to >= c.TryStart && to < c.TryEnd) continue;
                    add(l, c.HandlerStart);
                    targets.Add(to);
                }
                // The endfinally of this finally block, not of one nested in it.
                foreach (var e in ends.Where(e => Within(instructions[e].Offset, c.HandlerStart, c.HandlerEnd)
                                                  && !clauses.Any(n => n != c && n.Kind is ExceptionClause.Finally or ExceptionClause.Fault
                                                                       && n.HandlerStart >= c.HandlerStart && n.HandlerEnd <= c.HandlerEnd
                                                                       && Within(instructions[e].Offset, n.HandlerStart, n.HandlerEnd))))
                    foreach (var to in targets.Distinct()) add(e, to);
            }
            return edges;
        }

        private static bool Within(int offset, int start, int end) => offset >= start && offset < end;

        /// Where the code can go after an instruction. The jumps an async method's MoveNext makes on its state to resume
        /// after an await are left out: a resume continues the path that reached the await, as the code that runs when
        /// the awaited task had already completed shows, not a new way in from the start.
        private List<int> Successors(int i)
        {
            var ins = instructions[i];
            var list = new List<int>(2);
            if (!Behaviours.TryGetValue(ins.Code, out var b) || !b.EndsPath)
                if (i + 1 < instructions.Count) list.Add(i + 1);
            if (TestsState(i)) return list;
            foreach (var target in ins.Switch ?? (ins.Target is { } t ? [t] : []))
                if (Index.TryGetValue(target, out var to) && !IsResume(to)) list.Add(to);
            return list;
        }

        /// The local MoveNext copies its state into first (ldarg.0; ldfld &lt;&gt;1__state; stloc), or -1.
        private int StateLocal => _stateLocal ??= instructions.Count > 2 && instructions[0].Code == OpCodes.Ldarg_0.Value && instructions[1].Code == OpCodes.Ldfld.Value
                                                  && Field(instructions[1].Operand)?.Name == "<>1__state" && LocalIndex(instructions[2], load: false) is { } local
            ? local
            : -1;
        private int? _stateLocal;

        /// A branch or switch on MoveNext's state: `switch (num)`, `if (num == 1)`, `if ((uint)(num - 1) <= 1)`. The values
        /// it tests are made right before it from the state and constants only.
        private bool TestsState(int i)
        {
            var ins = instructions[i];
            if ((ins.Target is null && ins.Switch is null) || StateLocal < 0 || !Behaviours.TryGetValue(ins.Code, out var b) || b.Pops is not ({ } tested and > 0)) return false;
            var stack = 0;
            var state = false;
            for (var j = i - 1; j >= 0 && j >= i - 8 && stack < tested; j--)
            {
                var at = instructions[j];
                if (LocalIndex(at, load: true) == StateLocal) { state = true; stack++; }
                else if (ConstantInt(at) is not null) stack++;
                else if (at.Code == OpCodes.Sub.Value || at.Code == OpCodes.Add.Value) stack--;
                else return false;
                if (j < i - 1 && IsJumpTarget(instructions[j + 1].Offset)) return false;
            }
            return state && stack == tested;
        }

        /// Code a MoveNext resumes at after an await: it sets the state back to -1 (ldc.i4.m1 ... stfld &lt;&gt;1__state)
        /// before it branches or calls anything.
        private bool IsResume(int at)
        {
            for (var i = at; i < instructions.Count && i < at + 14; i++)
            {
                var ins = instructions[i];
                if (ins.Target is not null || ins.Switch is not null || ins.Code == OpCodes.Call.Value || ins.Code == OpCodes.Callvirt.Value) return false;
                if (ConstantInt(ins) == -1)
                    return instructions.Skip(i + 1).Take(3).Any(s => s.Code == OpCodes.Stfld.Value && Field(s.Operand)?.Name == "<>1__state");
            }
            return false;
        }
    }

    /// One method's IL with where it comes from, to name the method and the source lines of its calls.
    private sealed class MethodCode(ClrMethod method, ClrModule module, MetadataReader metadata, List<Instruction> instructions, IReadOnlyList<ExceptionClause>? clauses,
                                    SourceResolver sources, int localsToken)
        : IlBody(instructions, metadata, clauses)
    {
        public ClrModule Module => module;
        public int Token => method.MetadataToken;
        public string Name { get; } = Names.ShortMethod(method.Type?.Name ?? "", method.Name ?? "?");

        /// See <see cref="IlBody.OwnTypes"/>: the type, the types it is nested in, and their base types.
        public static HashSet<string> OwnTypesOf(ClrType? type)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (type?.Name is not { } name) return names;
            var types = new List<ClrType> { type };
            for (var plus = name.IndexOf('+'); plus > 0; plus = name.IndexOf('+', plus + 1))
            {
                if (WithoutGenerics(name[..plus]) is { } outer) names.Add(outer);
                if (type.Module?.GetTypeByName(name[..plus]) is { } t) types.Add(t);
            }
            foreach (var first in types)
                for (var t = first; t is not null && t.Name is not ("System.Object" or "System.ValueType"); t = t.BaseType)
                    if (WithoutGenerics(t.Name) is { } n) names.Add(n);
            return names;
        }

        public SourceLocation? Location(int offset)
        {
            try { return sources.Resolve(module.Name, method.MetadataToken, offset, () => module.Pdb?.Guid); }
            catch { return null; }
        }

        /// The method's calls on SemaphoreSlim read without a `this`, the same for every thread that waits in it.
        public List<SemaphoreCall>? CallsWithoutThis { get; set; }

        /// How many parameters and locals could hold one of these objects, which the frame keeps alive: declared as the
        /// object's class, a base class or an interface of it (the parameter of a derived class holds an object the
        /// method's own class says nothing about), object or a type parameter, passed by reference, or a struct that
        /// may have such a field. Null when the signatures or an object's type can't be read.
        public int? SlotsFor(IReadOnlyList<ClrObject> objects)
        {
            if (!_slotsRead)
            {
                _slotTypes = MetadataNames.ParameterAndLocalTypes(Metadata, method.MetadataToken, localsToken);
                _slotsRead = true;
            }
            if (_slotTypes is not { } slots) return null;
            var names = new List<HashSet<string?>>();
            foreach (var o in objects)
            {
                if (NamesOf(o.Type) is not { } known) return null;
                names.Add(known);
            }
            return slots.Count(slot => names.Any(n => MetadataNames.CanHold(slot, n)));
        }
        private List<string?>? _slotTypes;
        private bool _slotsRead;

        /// What an object of the type can be declared as: its class, its base classes and its interfaces, without
        /// generic arguments. Null when they can't be read.
        private HashSet<string?>? NamesOf(ClrType? type)
        {
            if (type is null) return null;
            if (_namesOf.TryGetValue(type.MethodTable, out var known)) return known;
            HashSet<string?>? names = new(StringComparer.Ordinal) { "System.Object" };
            try
            {
                for (var t = type; t is not null; t = t.BaseType) names.Add(WithoutGenerics(t.Name));
                foreach (var i in type.EnumerateInterfaces()) names.Add(WithoutGenerics(i.Name));
            }
            catch
            {
                names = null;
            }
            return _namesOf[type.MethodTable] = names;
        }
        private readonly Dictionary<ulong, HashSet<string?>?> _namesOf = [];
    }

    /// Reads IL and metadata of app modules from the dump, once per method and module.
    private sealed class CodeReader(ClrRuntime runtime, SourceResolver sources) : IDisposable
    {
        private const int MaxMetadataBytes = 64 * 1024 * 1024;
        private const int MaxImageBytes = 64 * 1024 * 1024;
        private readonly Dictionary<ulong, MetadataReaderProvider?> _metadata = [];
        private readonly Dictionary<ulong, PEReader?> _images = [];
        private readonly Dictionary<ulong, MethodCode?> _methods = [];
        private readonly Dictionary<(ulong Module, int Field), int?> _initialCounts = [];
        private readonly Dictionary<(ulong Module, int Method), IlBody?> _imageBodies = [];

        public MethodCode? Read(ClrMethod method)
        {
            if (_methods.TryGetValue(method.MethodDesc, out var cached)) return cached;
            MethodCode? code = null;
            try
            {
                if (method.Type?.Module is { IsDynamic: false } module && !FrameClassifier.IsFrameworkModule(module.Name)
                    && Metadata(module) is { } metadata && method.GetILInfo() is { Length: > 0 and < 1_000_000 } il)
                {
                    var bytes = new byte[il.Length];
                    if (runtime.DataTarget.DataReader.Read(il.Address, bytes) == bytes.Length && Decode(bytes) is { } instructions)
                        code = new MethodCode(method, module, metadata, instructions, Clauses(il.Address, il.Length), sources, unchecked((int)il.LocalVarSignatureToken))
                        {
                            OwnTypes = MethodCode.OwnTypesOf(method.Type),
                            ReadMethod = token => ImageBody(module, metadata, token),
                            IsStatic = (method.Attributes & MethodAttributes.Static) != 0,
                        };
                }
            }
            catch
            {
                // Unreadable code is unknown code.
            }
            _methods[method.MethodDesc] = code;
            return code;
        }

        /// The try blocks of the method whose IL starts at <paramref name="code"/>: the method header right before the IL
        /// says whether sections follow it, and the exception section lists them. Null when that can't be read.
        private List<ExceptionClause>? Clauses(ulong code, int length)
        {
            try
            {
                var reader = runtime.DataTarget.DataReader;
                var header = new byte[12];
                if (code >= 12 && reader.Read(code - 12, header) == header.Length)
                {
                    var flags = BitConverter.ToUInt16(header, 0);
                    // A fat header: format bits 3, a size of 3 dwords, and the code size of this IL.
                    if ((flags & 0x3) == 0x3 && flags >> 12 == 3 && BitConverter.ToInt32(header, 4) == length)
                        return (flags & 0x8) == 0 ? [] : ReadSections(code + (ulong)length, length);
                }
                // A tiny header has no sections.
                var tiny = new byte[1];
                return code >= 1 && reader.Read(code - 1, tiny) == 1 && (tiny[0] & 0x3) == 0x2 && tiny[0] >> 2 == length ? [] : null;
            }
            catch
            {
                return null;
            }
        }

        private List<ExceptionClause>? ReadSections(ulong end, int length)
        {
            var reader = runtime.DataTarget.DataReader;
            var clauses = new List<ExceptionClause>();
            var at = (end + 3) & ~3UL;
            for (var section = 0; section < 8; section++)
            {
                var head = new byte[4];
                if (reader.Read(at, head) != head.Length) return null;
                var fat = (head[0] & 0x40) != 0;
                var size = fat ? head[1] | head[2] << 8 | head[3] << 16 : head[1];
                if (size < 4 || size > 1 << 20) return null;
                if ((head[0] & 0x3F) == 0x01) // the exception table
                {
                    var data = new byte[size];
                    if (reader.Read(at, data) != data.Length) return null;
                    for (var k = 4; k + (fat ? 24 : 12) <= size; k += fat ? 24 : 12)
                    {
                        var c = fat
                            ? new ExceptionClause(BitConverter.ToInt32(data, k), BitConverter.ToInt32(data, k + 4), BitConverter.ToInt32(data, k + 4) + BitConverter.ToInt32(data, k + 8),
                                                  BitConverter.ToInt32(data, k + 12), BitConverter.ToInt32(data, k + 12) + BitConverter.ToInt32(data, k + 16), BitConverter.ToInt32(data, k + 20))
                            : new ExceptionClause(BitConverter.ToUInt16(data, k), BitConverter.ToUInt16(data, k + 2), BitConverter.ToUInt16(data, k + 2) + data[k + 4],
                                                  BitConverter.ToUInt16(data, k + 5), BitConverter.ToUInt16(data, k + 5) + data[k + 7], BitConverter.ToInt32(data, k + 8));
                        if (c.Kind is not (ExceptionClause.Catch or ExceptionClause.Filter or ExceptionClause.Finally or ExceptionClause.Fault)
                            || c.TryStart < 0 || c.TryEnd > length || c.HandlerStart < 0 || c.HandlerEnd > length) return null;
                        clauses.Add(c.Kind == ExceptionClause.Filter ? c : c with { FilterStart = 0 });
                    }
                }
                if ((head[0] & 0x80) == 0) return clauses;
                at = (at + (ulong)size + 3) & ~3UL;
            }
            return null;
        }

        /// The IL of a method of the module by its MethodDef token, read from the module's image in the dump.
        private IlBody? ImageBody(ClrModule module, MetadataReader metadata, int token)
        {
            var key = (module.Address, token);
            if (_imageBodies.TryGetValue(key, out var cached)) return cached;
            IlBody? body = null;
            try
            {
                var handle = MetadataTokens.EntityHandle(token);
                if (token != 0 && handle.Kind == HandleKind.MethodDefinition && Image(module) is { } pe)
                {
                    var definition = pe.GetMetadataReader().GetMethodDefinition((MethodDefinitionHandle)handle);
                    var rva = definition.RelativeVirtualAddress;
                    if (rva != 0 && pe.GetMethodBody(rva).GetILBytes() is { Length: > 0 } il && Decode(il) is { } instructions)
                        body = new IlBody(instructions, metadata) { IsStatic = (definition.Attributes & MethodAttributes.Static) != 0 };
                }
            }
            catch
            {
                // Unknown code, which may release anything.
            }
            _imageBodies[key] = body;
            return body;
        }

        /// The constant initial count of every `new SemaphoreSlim(...)` stored in the field, if they all agree.
        public int? InitialCount(MethodCode user, int fieldToken)
        {
            var key = (user.Module.Address, fieldToken);
            if (_initialCounts.TryGetValue(key, out var cached)) return cached;
            int? result = null;
            try
            {
                // Every method of the type, from the module's image; the JIT's list lacks those it only inlined (a constructor
                // inlined into its caller). Without the image, the methods the JIT compiled on their own.
                var bodies = ImageMethods(user, fieldToken)
                             ?? (user.Field(fieldToken) is { } field && user.Module.GetTypeByName(field.DeclaringType) is { } type
                                 ? type.Methods.Where(m => m.Type?.Name == type.Name).DistinctBy(m => m.MetadataToken).Select(m => Read(m)?.Instructions).OfType<List<Instruction>>().ToList()
                                 : null);
                var counts = new List<int?>();
                foreach (var ins in bodies ?? [])
                {
                    for (var i = 0; i + 1 < ins.Count; i++)
                    {
                        if (ins[i].Code != OpCodes.Newobj.Value || user.Callee(ins[i].Operand) is not (SemaphoreType, ".ctor")) continue;
                        var store = ins[i + 1];
                        if ((store.Code != OpCodes.Stfld.Value && store.Code != OpCodes.Stsfld.Value) || store.Operand != fieldToken) continue;
                        counts.Add(SnapshotBuilder.CreatedWith(ins, i, user.ParameterCount(ins[i].Operand), user.Callee));
                    }
                }
                if (counts.Count > 0 && counts.All(c => c is not null && c == counts[0])) result = counts[0];
            }
            catch
            {
                // Unknown.
            }
            _initialCounts[key] = result;
            return result;
        }

        /// The IL of every method of the type that declares the field (a field of this module), read from the module's
        /// image in the dump. Null when the image isn't there or can't be read.
        private List<List<Instruction>>? ImageMethods(MethodCode user, int fieldToken)
        {
            try
            {
                var handle = MetadataTokens.EntityHandle(fieldToken);
                if (handle.Kind != HandleKind.FieldDefinition || Image(user.Module) is not { } pe) return null;
                var metadata = pe.GetMetadataReader();
                var type = metadata.GetTypeDefinition(metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType());
                var bodies = new List<List<Instruction>>();
                foreach (var m in type.GetMethods())
                {
                    var rva = metadata.GetMethodDefinition(m).RelativeVirtualAddress;
                    if (rva != 0 && pe.GetMethodBody(rva).GetILBytes() is { Length: > 0 } il && Decode(il) is { } instructions) bodies.Add(instructions);
                }
                return bodies;
            }
            catch
            {
                // A short or odd image: the methods the JIT compiled are all there is to go on.
                return null;
            }
        }

        private PEReader? Image(ClrModule module)
        {
            if (_images.TryGetValue(module.Address, out var cached)) return cached;
            PEReader? pe = null;
            try
            {
                if (module.ImageBase != 0 && module.Size is > 0 and < MaxImageBytes)
                {
                    var bytes = new byte[(int)module.Size];
                    if (runtime.DataTarget.DataReader.Read(module.ImageBase, bytes) == bytes.Length)
                    {
                        pe = new PEReader(new MemoryStream(bytes), module.Layout == ModuleLayout.Flat ? PEStreamOptions.Default : PEStreamOptions.IsLoadedImage);
                        if (!pe.HasMetadata) { pe.Dispose(); pe = null; }
                    }
                }
            }
            catch
            {
                pe?.Dispose();
                pe = null;
            }
            _images[module.Address] = pe;
            return pe;
        }

        private MetadataReader? Metadata(ClrModule module)
        {
            if (!_metadata.TryGetValue(module.Address, out var provider))
            {
                provider = null;
                if (module.MetadataAddress != 0 && module.MetadataLength is > 0 and < MaxMetadataBytes)
                {
                    var bytes = new byte[(int)module.MetadataLength];
                    if (runtime.DataTarget.DataReader.Read(module.MetadataAddress, bytes) == bytes.Length)
                        provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(bytes));
                }
                _metadata[module.Address] = provider;
            }
            return provider?.GetMetadataReader();
        }

        public void Dispose()
        {
            foreach (var p in _metadata.Values) p?.Dispose();
            _metadata.Clear();
            foreach (var pe in _images.Values) pe?.Dispose();
            _images.Clear();
        }
    }

    private static int? ConstantInt(Instruction i) => i.Code switch
    {
        0x15 => -1, // ldc.i4.m1
        >= 0x16 and <= 0x1E => i.Code - 0x16, // ldc.i4.0 - ldc.i4.8
        0x1F or 0x20 => i.Operand, // ldc.i4.s, ldc.i4
        _ => null,
    };

    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First().OperandType);

    /// What an instruction does to the stack and to the path through the code. Pops or Pushes is null for a call, whose
    /// signature says it.
    private readonly record struct Behaviour(int? Pops, int? Pushes, bool ControlFlow, bool EndsPath);

    private static readonly Dictionary<short, Behaviour> Behaviours = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g =>
        {
            var o = g.First();
            static int? Count(string name, string none, string variable) =>
                name == none ? 0 : name.StartsWith(variable, StringComparison.Ordinal) ? null : name.Split('_').Length;
            var ends = o.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw || o.Value == OpCodes.Jmp.Value;
            return new Behaviour(Count(o.StackBehaviourPop.ToString(), "Pop0", "Varpop"), Count(o.StackBehaviourPush.ToString(), "Push0", "Varpush"),
                                 ends || o.FlowControl == FlowControl.Cond_Branch, ends);
        });

    /// Every instruction with its offset, its token or constant operand, and where it branches to. Null if the IL is odd.
    internal static List<Instruction>? Decode(byte[] il)
    {
        var list = new List<Instruction>();
        var i = 0;
        while (i < il.Length)
        {
            var start = i;
            short code = il[i++];
            if (code == 0xFE)
            {
                if (i >= il.Length) return null;
                code = unchecked((short)(0xFE00 | il[i++]));
            }
            if (!Operands.TryGetValue(code, out var kind)) return null;
            var size = kind switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => i + 4 <= il.Length && BitConverter.ToInt32(il, i) is var count && count >= 0 && count <= (il.Length - i - 4) / 4 ? 4 + 4 * count : -1,
                _ => 4,
            };
            if (size < 0 || i + size > il.Length) return null;
            var operand = kind switch
            {
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI => (sbyte)il[i],
                OperandType.ShortInlineVar => il[i],
                OperandType.InlineVar => BitConverter.ToUInt16(il, i),
                OperandType.InlineNone or OperandType.InlineI8 or OperandType.InlineR or OperandType.InlineSwitch => 0,
                _ => BitConverter.ToInt32(il, i),
            };
            var operandAt = i;
            i += size;
            int? target = kind is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget ? i + operand : null;
            int[]? cases = null;
            if (kind == OperandType.InlineSwitch)
            {
                cases = new int[(size - 4) / 4];
                for (var k = 0; k < cases.Length; k++) cases[k] = i + BitConverter.ToInt32(il, operandAt + 4 + 4 * k);
            }
            list.Add(new Instruction(start, code, operand, target, cases));
        }
        return list;
    }
}

/// Names of called methods and loaded fields from a module's metadata tables.
internal static class MetadataNames
{
    public sealed record FieldName(string DeclaringType, string Name, string? FieldType);

    public static (string? Type, string? Name) Callee(MetadataReader metadata, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MethodSpecification)
                handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                    return (TypeName(metadata, member.Parent), metadata.GetString(member.Name));
                case HandleKind.MethodDefinition:
                    var def = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return (TypeName(metadata, def.GetDeclaringType()), metadata.GetString(def.Name));
            }
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException) { }
        return (null, null);
    }

    /// The MethodDef token of the method of this module that a call names: the token itself, a generic method's
    /// instantiation, or a reference to a method of a generic type of this module (0 when that one can't be pinned
    /// down). Null for a method of another module or what can't be read.
    public static int? MethodDefinition(MetadataReader metadata, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MethodSpecification)
                handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            if (handle.Kind == HandleKind.MethodDefinition) return MetadataTokens.GetToken(handle);
            if (handle.Kind != HandleKind.MemberReference) return null;
            var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            var parent = member.Parent;
            if (parent.Kind == HandleKind.TypeSpecification)
            {
                var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                blob.ReadCompressedInteger(); // CLASS or VALUETYPE
                parent = blob.ReadTypeHandle();
            }
            if (parent.Kind != HandleKind.TypeDefinition) return null;
            var name = metadata.GetString(member.Name);
            var signature = metadata.GetBlobBytes(member.Signature);
            var matches = metadata.GetTypeDefinition((TypeDefinitionHandle)parent).GetMethods()
                .Where(m => metadata.GetMethodDefinition(m) is var d && metadata.GetString(d.Name) == name && metadata.GetBlobBytes(d.Signature).AsSpan().SequenceEqual(signature))
                .ToList();
            return matches is [var only] ? MetadataTokens.GetToken(only) : 0;
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    /// The MethodDef token of the MoveNext of the state machine type that declares the field (its &lt;&gt;1__state): where
    /// the code of an async method is. Null when the type isn't one of this module or has no single MoveNext.
    public static int? MoveNextOf(MetadataReader metadata, int fieldToken)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(fieldToken);
            EntityHandle type;
            if (handle.Kind == HandleKind.FieldDefinition) type = metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType();
            else if (handle.Kind == HandleKind.MemberReference) type = metadata.GetMemberReference((MemberReferenceHandle)handle).Parent;
            else return null;
            if (type.Kind == HandleKind.TypeSpecification)
            {
                var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)type).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                blob.ReadCompressedInteger(); // CLASS or VALUETYPE
                type = blob.ReadTypeHandle();
            }
            if (type.Kind != HandleKind.TypeDefinition) return null;
            var moveNext = metadata.GetTypeDefinition((TypeDefinitionHandle)type).GetMethods()
                .Where(m => metadata.GetString(metadata.GetMethodDefinition(m).Name) == "MoveNext")
                .ToList();
            return moveNext is [var only] ? MetadataTokens.GetToken(only) : null;
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    public static int ParameterCount(MetadataReader metadata, int token)
    {
        try
        {
            if (MethodSignature(metadata, token) is not { } blob) return -1;
            var header = blob.ReadSignatureHeader();
            if (header.IsGeneric) blob.ReadCompressedInteger();
            return blob.ReadCompressedInteger();
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException)
        {
            return -1;
        }
    }

    /// What a call does to the stack: its parameter count, whether it takes a `this`, and whether it returns a value.
    public static (int Parameters, bool HasThis, bool ReturnsValue)? CallShape(MetadataReader metadata, int token)
    {
        try
        {
            if (MethodSignature(metadata, token) is not { } blob) return null;
            var header = blob.ReadSignatureHeader();
            if (header.CallingConvention == SignatureCallingConvention.VarArgs) return null;
            if (header.IsGeneric) blob.ReadCompressedInteger();
            var parameters = blob.ReadCompressedInteger();
            for (var guard = 0; guard < 8; guard++)
            {
                var code = blob.ReadSignatureTypeCode();
                if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
                {
                    blob.ReadTypeHandle();
                    continue;
                }
                return (parameters, header.IsInstance, code != SignatureTypeCode.Void);
            }
            return null;
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    /// Whether a parameter or local declared as <paramref name="slot"/> (see <see cref="ParameterAndLocalTypes"/>) can
    /// keep alive an object that can be declared as one of <paramref name="names"/>. Whatever isn't surely unable to
    /// counts: a slot too many only means that nothing is claimed.
    public static bool CanHold(string? slot, IReadOnlySet<string?> names)
    {
        if (slot is null or ByReference) return true;
        if (slot[0] != Struct) return names.Contains(slot.IndexOfAny(['<', '`']) is >= 0 and var generics ? slot[..generics] : slot);
        var name = slot[1..];
        if (WithoutReferences.Contains(name)) return false;
        return name != "System.Threading.CancellationToken" || names.Contains("System.Threading.CancellationTokenSource");
    }

    private const string ByReference = "&";
    private const char Struct = '$';
    private const string Enum = "enum";

    /// Structs of the runtime that hold no reference to an object.
    private static readonly HashSet<string> WithoutReferences = new(StringComparer.Ordinal)
    {
        "System.TimeSpan", "System.DateTime", "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly", "System.Guid",
        "System.Decimal", "System.Int128", "System.UInt128", "System.Half", "System.Index", "System.Range",
    };

    /// The declared types of a method's parameters (not `this`) and locals, by name: a class or an interface as it is
    /// named, a struct with a '$' in front (an enum of this module as "enum"), a Nullable as what it wraps, anything
    /// passed by reference as "&amp;", an array as "[]", a pointer as "*"; null for a type parameter or a type that
    /// can't be named. Null when a signature can't be read.
    public static List<string?>? ParameterAndLocalTypes(MetadataReader metadata, int methodToken, int localsToken)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(methodToken);
            if (handle.Kind != HandleKind.MethodDefinition) return null;
            var provider = new TypeNames();
            var result = metadata.GetMethodDefinition((MethodDefinitionHandle)handle).DecodeSignature(provider, null).ParameterTypes.ToList();
            // A method without locals has no signature for them: token 0, or a signature token with row 0.
            if ((localsToken & 0x00FFFFFF) != 0)
            {
                var locals = MetadataTokens.EntityHandle(localsToken);
                if (locals.Kind != HandleKind.StandaloneSignature) return null;
                result.AddRange(metadata.GetStandaloneSignature((StandaloneSignatureHandle)locals).DecodeLocalSignature(provider, null));
            }
            return result;
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException or InvalidOperationException)
        {
            return null;
        }
    }

    /// Names a signature's types for <see cref="ParameterAndLocalTypes"/>.
    private sealed class TypeNames : ISignatureTypeProvider<string?, object?>
    {
        private const byte ValueType = 0x11, Class = 0x12;

        public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => null,
            _ => $"System.{typeCode}",
        };
        public string? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            rawTypeKind == ValueType && IsEnum(reader, handle) ? Enum : Named(TypeName(reader, handle), rawTypeKind);
        public string? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Named(TypeName(reader, handle), rawTypeKind);
        public string? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => Named(TypeName(reader, handle), rawTypeKind);
        public string? GetGenericInstantiation(string? genericType, ImmutableArray<string?> typeArguments) =>
            genericType == "$System.Nullable`1" && typeArguments.Length == 1 ? typeArguments[0] : genericType;
        public string? GetArrayType(string? elementType, ArrayShape shape) => "[]";
        public string? GetSZArrayType(string? elementType) => "[]";
        public string? GetByReferenceType(string? elementType) => ByReference;
        public string? GetPointerType(string? elementType) => "*";

        /// A class or an interface by its name, a struct with the mark in front; null when the signature doesn't say which.
        private static string? Named(string? name, byte kind) => name is null ? null : kind == Class ? name : kind == ValueType ? Struct + name : null;

        private static bool IsEnum(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var parent = reader.GetTypeDefinition(handle).BaseType;
            return !parent.IsNil && parent.Kind == HandleKind.TypeReference && TypeName(reader, parent) == "System.Enum";
        }
        public string? GetPinnedType(string? elementType) => elementType;
        public string? GetModifiedType(string? modifier, string? unmodifiedType, bool isRequired) => unmodifiedType;
        public string? GetFunctionPointerType(MethodSignature<string?> signature) => "*";
        public string? GetGenericMethodParameter(object? genericContext, int index) => null;
        public string? GetGenericTypeParameter(object? genericContext, int index) => null;
    }

    /// Whether the method returns a result: a bool or a generic Task&lt;T&gt; (SemaphoreSlim.Wait(0), WaitAsync(TimeSpan)),
    /// not void or a plain Task (Wait(), WaitAsync()). Null when the signature can't be read.
    public static bool? ReturnsResult(MetadataReader metadata, int token)
    {
        try
        {
            if (MethodSignature(metadata, token) is not { } blob) return null;
            var header = blob.ReadSignatureHeader();
            if (header.IsGeneric) blob.ReadCompressedInteger();
            blob.ReadCompressedInteger(); // parameter count
            for (var guard = 0; guard < 8; guard++)
            {
                var code = blob.ReadSignatureTypeCode();
                if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
                {
                    blob.ReadTypeHandle();
                    continue;
                }
                return code switch
                {
                    SignatureTypeCode.Void or SignatureTypeCode.TypeHandle => false,
                    SignatureTypeCode.Boolean or SignatureTypeCode.GenericTypeInstance => true,
                    _ => null,
                };
            }
            return null;
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    private static BlobReader? MethodSignature(MetadataReader metadata, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        var signature = handle.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)handle).Signature,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Signature,
            _ => default,
        };
        return signature.IsNil ? null : metadata.GetBlobReader(signature);
    }

    public static FieldName? Field(MetadataReader metadata, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.FieldDefinition:
                    var def = metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return TypeName(metadata, def.GetDeclaringType()) is { } owner
                        ? new FieldName(owner, metadata.GetString(def.Name), FieldType(metadata, def.Signature))
                        : null;
                case HandleKind.MemberReference:
                    var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                    return TypeName(metadata, member.Parent) is { } parent
                        ? new FieldName(parent, metadata.GetString(member.Name), FieldType(metadata, member.Signature))
                        : null;
            }
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException) { }
        return null;
    }

    public static bool IsStaticField(MetadataReader metadata, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            return handle.Kind == HandleKind.FieldDefinition
                && (metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Attributes & FieldAttributes.Static) != 0;
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidCastException)
        {
            return false;
        }
    }

    private static string? FieldType(MetadataReader metadata, BlobHandle signature)
    {
        var blob = metadata.GetBlobReader(signature);
        if (blob.ReadSignatureHeader().Kind != SignatureKind.Field) return null;
        for (var guard = 0; guard < 8; guard++)
        {
            var code = blob.ReadSignatureTypeCode();
            if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
            {
                blob.ReadTypeHandle(); // modreq(IsVolatile) and the like
                continue;
            }
            return code == SignatureTypeCode.TypeHandle ? TypeName(metadata, blob.ReadTypeHandle()) : null;
        }
        return null;
    }

    /// "Namespace.Outer+Inner", the way ClrMD names types; a generic instantiation is named after its generic type.
    /// The depth stops a walk through odd metadata that refers back to itself.
    private static string? TypeName(MetadataReader metadata, EntityHandle handle, int depth = 0)
    {
        if (depth > 16) return null;
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var r = metadata.GetTypeReference((TypeReferenceHandle)handle);
                var name = metadata.GetString(r.Name);
                if (r.ResolutionScope.Kind == HandleKind.TypeReference)
                    return TypeName(metadata, (EntityHandle)r.ResolutionScope, depth + 1) is { } scope ? $"{scope}+{name}" : null;
                return r.Namespace.IsNil ? name : $"{metadata.GetString(r.Namespace)}.{name}";
            case HandleKind.TypeDefinition:
                var d = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                var defName = metadata.GetString(d.Name);
                if (d.GetDeclaringType() is { IsNil: false } outer)
                    return TypeName(metadata, outer, depth + 1) is { } outerName ? $"{outerName}+{defName}" : null;
                return d.Namespace.IsNil ? defName : $"{metadata.GetString(d.Namespace)}.{defName}";
            case HandleKind.TypeSpecification:
                var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                blob.ReadCompressedInteger(); // CLASS or VALUETYPE
                return TypeName(metadata, blob.ReadTypeHandle(), depth + 1);
            default:
                return null;
        }
    }
}
