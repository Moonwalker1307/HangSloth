using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// "Took X earlier and doesn't release it before this wait" has to be true of the code, on every path: the semaphore a
/// call is made on is the value under its arguments, not the nearest field load, and a take only counts where every way
/// to the wait goes through it. Checked on the IL the compiler made for the methods below.
public class SemaphoreCodeEvidenceTests
{
    private static readonly PEReader Image = new(File.OpenRead(typeof(SemaphoreCodeEvidenceTests).Assembly.Location));

    internal static SnapshotBuilder.IlBody Body(MethodBase method) =>
        new(SnapshotBuilder.Decode(method.GetMethodBody()!.GetILAsByteArray()!)!, Image.GetMetadataReader(), Clauses(method.GetMethodBody()!)) { IsStatic = method.IsStatic };

    /// The method's try blocks as the method header lists them.
    internal static List<SnapshotBuilder.ExceptionClause> Clauses(MethodBody body) =>
        body.ExceptionHandlingClauses.Select(c => new SnapshotBuilder.ExceptionClause((int)c.Flags, c.TryOffset, c.TryOffset + c.TryLength, c.HandlerOffset,
                                                                                     c.HandlerOffset + c.HandlerLength,
                                                                                     c.Flags == ExceptionHandlingClauseOptions.Filter ? c.FilterOffset : 0))
            .ToList();

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static SnapshotBuilder.IlBody Method(string name) => Body(typeof(OrderBook).GetMethod(name, Any)!);

    private static SnapshotBuilder.IlBody MoveNext(string name) =>
        Body(typeof(OrderBook).GetNestedTypes(Any).Single(t => t.Name.StartsWith($"<{name}>", StringComparison.Ordinal)).GetMethod("MoveNext", Any)!);

    /// The calls on SemaphoreSlim in the method, in order: their instruction index.
    private static List<int> Calls(SnapshotBuilder.IlBody body, string name) =>
        body.Instructions.Select((ins, i) => (ins, i))
            .Where(x => (x.ins.Code == OpCodes.Call.Value || x.ins.Code == OpCodes.Callvirt.Value)
                        && body.Callee(x.ins.Operand) is ("System.Threading.SemaphoreSlim", var n) && n == name)
            .Select(x => x.i)
            .ToList();

    private static string? FieldOf(SnapshotBuilder.IlBody body, int call) =>
        SnapshotBuilder.Receiver(body, call).Field is { } token ? body.Field(token)?.Name : null;

    private static bool Holds(SnapshotBuilder.IlBody body, int take, int offset) =>
        SnapshotBuilder.HasSlotAt(body, new SnapshotBuilder.SemaphoreCall(body.Instructions[take].Offset, SnapshotBuilder.SemaphoreCallKind.Wait, null, 0x100, null, true,
                                                                          SnapshotBuilder.HeldAfter(body, take)), offset);

    // ---------------------------------------------------------------- which semaphore a call is made on

    [Fact]
    public void The_semaphore_a_call_is_made_on_is_the_value_under_its_arguments()
    {
        var plain = Method(nameof(OrderBook.Plain));
        Assert.Equal(["_table", "_row"], Calls(plain, "Wait").Select(c => FieldOf(plain, c)));
        Assert.All(Calls(plain, "Wait"), c => Assert.True(SnapshotBuilder.Receiver(plain, c).OnThis));

        var token = Method(nameof(OrderBook.WithToken));
        Assert.Equal("_row", FieldOf(token, Assert.Single(Calls(token, "Wait"))));

        var open = Method(nameof(OrderBook.Open));
        Assert.Equal("_closed", FieldOf(open, Assert.Single(Calls(open, "Release"))));

        var async = MoveNext(nameof(OrderBook.Get));
        var wait = Assert.Single(Calls(async, "WaitAsync"));
        Assert.Equal("_table", FieldOf(async, wait));
        Assert.True(SnapshotBuilder.Receiver(async, wait).OnThis);
    }

    [Fact]
    public void A_field_passed_to_another_call_is_not_the_receiver()
    {
        // Track(_primary); batch.Wait();  -- the wait is on the parameter.
        var body = Method(nameof(OrderBook.Passed));
        var (key, field, onThis) = SnapshotBuilder.Receiver(body, Assert.Single(Calls(body, "Wait")));
        Assert.Null(field);
        Assert.False(onThis);
        Assert.NotNull(key);
    }

    [Fact]
    public void A_semaphore_picked_by_a_condition_is_not_known()
    {
        // var gate = primary ? _primary : _fallback; gate.Wait();
        var body = Method(nameof(OrderBook.Either));
        Assert.Null(SnapshotBuilder.Receiver(body, Assert.Single(Calls(body, "Wait"))).Field);
    }

    // ---------------------------------------------------------------- a take holds only where every path went through it

    [Fact]
    public void A_take_that_every_path_goes_through_is_held()
    {
        var plain = Method(nameof(OrderBook.Plain));
        var waits = Calls(plain, "Wait");
        Assert.True(Holds(plain, waits[0], plain.Instructions[waits[1]].Offset));

        var loop = Method(nameof(OrderBook.Retry));
        var table = Calls(loop, "Wait").First(c => FieldOf(loop, c) == "_table");
        var row = Calls(loop, "Wait").First(c => FieldOf(loop, c) == "_row");
        Assert.True(Holds(loop, table, loop.Instructions[row].Offset));

        var timed = Method(nameof(OrderBook.Timed));
        var timedWaits = Calls(timed, "Wait");
        Assert.True(Holds(timed, timedWaits[0], timed.Instructions[timedWaits[1]].Offset));
    }

    [Fact]
    public void A_take_inside_an_if_that_may_not_have_run_is_not_held()
    {
        // if (exclusive) _table.Wait(); _row.Wait();
        var body = Method(nameof(OrderBook.Optional));
        var waits = Calls(body, "Wait");
        Assert.Equal(["_table", "_row"], waits.Select(c => FieldOf(body, c)));
        Assert.False(Holds(body, waits[0], body.Instructions[waits[1]].Offset));
    }

    [Fact]
    public void An_async_take_holds_across_later_awaits_only_when_every_path_went_through_it()
    {
        // await _table.WaitAsync(); try { await Load(); } ...  -- paused at the second await (state 1).
        var always = MoveNext(nameof(OrderBook.Get));
        Assert.True(Holds(always, Assert.Single(Calls(always, "WaitAsync")), always.SuspensionPoint(1)!.Value));

        // if (locked) await _table.WaitAsync(); try { await Load(); } ...
        var optional = MoveNext(nameof(OrderBook.Refresh));
        Assert.False(Holds(optional, Assert.Single(Calls(optional, "WaitAsync")), optional.SuspensionPoint(1)!.Value));

        // await _table.WaitAsync(); try { await Load(); await Get(); } ...  -- MoveNext resumes states 1 and 2 inside the try.
        var nested = MoveNext(nameof(OrderBook.GetTwice));
        Assert.True(Holds(nested, Assert.Single(Calls(nested, "WaitAsync")), nested.SuspensionPoint(2)!.Value));
    }

    // ---------------------------------------------------------------- how a semaphore was created

    [Fact]
    public void The_initial_count_is_a_constant_or_the_maximum_passed_twice()
    {
        var ctor = Body(typeof(OrderBook).GetConstructors(Any).Single());
        var ins = ctor.Instructions;
        var created = new Dictionary<string, int?>();
        for (var i = 0; i + 1 < ins.Count; i++)
        {
            if (ins[i].Code != OpCodes.Newobj.Value || ctor.Callee(ins[i].Operand).Type != "System.Threading.SemaphoreSlim") continue;
            created[ctor.Field(ins[i + 1].Operand)!.Name] = SnapshotBuilder.CreatedWith(ins, i, ctor.ParameterCount(ins[i].Operand), ctor.Callee);
        }

        Assert.Equal(1, created["_table"]);
        Assert.Equal(4, created["_four"]);
        Assert.Equal(0, created["_closed"]);
        Assert.Equal(SnapshotBuilder.EqualsMax, created["_jobs"]);
        Assert.Equal(SnapshotBuilder.EqualsMax, created["_cores"]);
        Assert.Null(created["_half"]);
    }

    [Fact]
    public void The_slots_that_can_hold_an_object_of_a_type_come_from_the_signatures()
    {
        var shift = typeof(OrderBook).GetMethod(nameof(OrderBook.Shift), Any)!;
        var types = MetadataNames.ParameterAndLocalTypes(Image.GetMetadataReader(), shift.MetadataToken, shift.GetMethodBody()!.LocalSignatureMetadataToken);
        Assert.NotNull(types);
        Assert.Equal(["HangSloth.Tests.OrderBook", "HangSloth.Tests.OrderBook", "System.Int32"], types.Take(3));
    }

    [Fact]
    public void A_slot_counts_when_it_can_hold_the_object_whatever_the_methods_own_class_is()
    {
        var move = typeof(Ledger).GetMethod(nameof(Ledger.Move), Any)!;
        var types = MetadataNames.ParameterAndLocalTypes(Image.GetMetadataReader(), move.MetadataToken, move.GetMethodBody()!.LocalSignatureMetadataToken);
        Assert.NotNull(types);
        Assert.Equal(
            ["HangSloth.Tests.Archive", "HangSloth.Tests.IAudited", "$HangSloth.Tests.Entry", "enum", "$System.Threading.CancellationToken", "$System.TimeSpan",
             "System.Int32", "&", "[]", "System.Action", "$System.TimeSpan", "System.Object"],
            types.Take(12));

        // What an Archive on the frame can be declared as: its class, its base class, its interfaces, object.
        HashSet<string?> archive = ["System.Object", "HangSloth.Tests.Ledger", "HangSloth.Tests.Archive", "HangSloth.Tests.IAudited"];
        // And a plain Ledger.
        HashSet<string?> ledger = ["System.Object", "HangSloth.Tests.Ledger"];
        bool[] holdsArchive = [.. types.Take(12).Select(t => MetadataNames.CanHold(t, archive))];
        bool[] holdsLedger = [.. types.Take(12).Select(t => MetadataNames.CanHold(t, ledger))];

        // The parameter of the derived class and the interface only the derived class has hold an Archive, not a Ledger.
        // A struct of the app may have a field for either; an enum, a CancellationToken, a TimeSpan (also as a
        // Nullable), an int, an array and a delegate have none; a `ref` may point into either; object holds anything.
        Assert.Equal([true, true, true, false, false, false, false, true, false, false, false, true], holdsArchive);
        Assert.Equal([false, false, true, false, false, false, false, true, false, false, false, true], holdsLedger);

        // A CancellationToken keeps its source alive, which matters only for a class derived from that.
        Assert.True(MetadataNames.CanHold("$System.Threading.CancellationToken", new HashSet<string?> { "System.Object", "System.Threading.CancellationTokenSource", "My.Source" }));
        // A type parameter, or a type that can't be named, may hold anything.
        Assert.True(MetadataNames.CanHold(null, ledger));
    }
}

internal interface IAudited;

internal enum EntryKind { Debit, Credit }

internal readonly record struct Entry(Ledger Book, int Amount);

/// A method with a slot of every kind. It runs nowhere.
internal class Ledger
{
    public int Total;

    public void Move(Archive to, IAudited audited, Entry entry, EntryKind kind, CancellationToken stop, TimeSpan wait, int units, ref int moved, int[] steps, Action done, TimeSpan? grace, object tag)
    {
        to.Total += units + entry.Amount + (int)kind + steps.Length + (int)wait.TotalSeconds + (grace?.Seconds ?? 0) + (stop.IsCancellationRequested ? 1 : 0) + (audited is null ? 0 : 1) + (tag is null ? 0 : 1);
        moved = to.Total;
        done();
    }
}

internal sealed class Archive : Ledger, IAudited;

/// The code the tests above read. It runs nowhere.
internal sealed class OrderBook
{
    private readonly SemaphoreSlim _table = new(1, 1);
    private readonly SemaphoreSlim _row = new(1, 1);
    private readonly SemaphoreSlim _primary = new(1, 1);
    private readonly SemaphoreSlim _fallback = new(1, 1);
    private readonly SemaphoreSlim _jobs;
    private readonly SemaphoreSlim _cores;
    private readonly SemaphoreSlim _half;
    private readonly SemaphoreSlim _four;
    private readonly SemaphoreSlim _closed;
    private int _rows;

    public OrderBook(int maxJobs)
    {
        _jobs = new SemaphoreSlim(maxJobs, maxJobs);
        _cores = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        _half = new SemaphoreSlim(maxJobs / 2, maxJobs);
        _four = new SemaphoreSlim(4);
        _closed = new SemaphoreSlim(0, 4);
    }

    public void Plain()
    {
        _table.Wait();
        _row.Wait();
        _rows++;
    }

    public void Optional(bool exclusive)
    {
        if (exclusive) _table.Wait();
        _row.Wait();
        _rows++;
    }

    public void Retry(int attempts)
    {
        _table.Wait();
        for (var i = 0; i < attempts; i++)
        {
            _row.Wait();
            _rows++;
            _row.Release();
        }
    }

    public void Timed()
    {
        if (!_table.Wait(100)) throw new TimeoutException();
        _row.Wait();
        _rows++;
    }

    public void Either(bool primary)
    {
        var gate = primary ? _primary : _fallback;
        gate.Wait();
        _rows++;
    }

    public void Passed(SemaphoreSlim batch)
    {
        Track(_primary);
        batch.Wait();
    }

    public void WithToken(CancellationToken token) => _row.Wait(token);

    public void Open() => _closed.Release(4);

    public int Shift(OrderBook to, OrderBook prev, int units) => to._rows + prev._rows + units + _half.CurrentCount + _four.CurrentCount + _jobs.CurrentCount + _cores.CurrentCount;

    public async Task Get()
    {
        await _table.WaitAsync();
        try { await Load(); }
        finally { _table.Release(); }
    }

    public async Task GetTwice()
    {
        await _table.WaitAsync();
        try
        {
            await Load();
            await Get();
        }
        finally { _table.Release(); }
    }

    public async Task Refresh(bool locked)
    {
        if (locked) await _table.WaitAsync();
        try { await Load(); }
        finally
        {
            if (locked) _table.Release();
        }
    }

    private static void Track(SemaphoreSlim semaphore) => GC.KeepAlive(semaphore);

    private static Task Load() => Task.Delay(1);
}
