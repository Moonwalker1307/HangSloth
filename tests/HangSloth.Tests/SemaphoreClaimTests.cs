using System.Reflection;
using System.Reflection.Emit;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;
using Call = HangSloth.Collection.SnapshotBuilder.SemaphoreCall;
using Kind = HangSloth.Collection.SnapshotBuilder.SemaphoreCallKind;

namespace HangSloth.Tests;

/// "Took X earlier and doesn't release it before this wait" claims two things of the code, and each is said only when
/// the code shows it: that every way to the wait went through the take (exceptions too: a catch that carries on is a way
/// around a take in its try block), and that no way from the take to the wait goes through a release (also one later in
/// a loop, on a semaphore the code can't name, or in a method of the same class). Checked on the IL the compiler made
/// for the methods of <see cref="StockRoom"/>.
public class SemaphoreClaimTests
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly HashSet<string> OwnTypes = ["HangSloth.Tests.StockRoom"];

    /// The method's IL with what collection knows of its class: the methods it can call there.
    private static SnapshotBuilder.IlBody Body(MethodBase method, HashSet<string>? ownTypes = null)
    {
        var body = SemaphoreCodeEvidenceTests.Body(method);
        return new(body.Instructions, body.Metadata, body.Clauses)
        {
            OwnTypes = ownTypes ?? OwnTypes,
            ReadMethod = token => typeof(StockRoom).Module.ResolveMethod(token) is { } m && m.GetMethodBody() is not null ? SemaphoreCodeEvidenceTests.Body(m) : null,
            IsStatic = method.IsStatic,
        };
    }

    private static SnapshotBuilder.IlBody Method(string name) => Body(typeof(StockRoom).GetMethod(name, Any)!);

    /// A method of Porter, a class outside StockRoom, as collection reads the frame of another class.
    private static SnapshotBuilder.IlBody Outside(string name) => Body(typeof(Porter).GetMethod(name, Any)!, ["HangSloth.Tests.Porter"]);

    private static SnapshotBuilder.IlBody MoveNext(string name) =>
        Body(typeof(StockRoom).GetNestedTypes(Any).Single(t => t.Name.StartsWith($"<{name}>", StringComparison.Ordinal)).GetMethod("MoveNext", Any)!);

    /// The calls on SemaphoreSlim, as collection reads them; each field is a semaphore of its own.
    private static List<Call> Calls(SnapshotBuilder.IlBody body)
    {
        var calls = new List<Call>();
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var ins = body.Instructions[i];
            if ((ins.Code != OpCodes.Call.Value && ins.Code != OpCodes.Callvirt.Value) || body.Callee(ins.Operand) is not ("System.Threading.SemaphoreSlim", var name)) continue;
            Kind? kind = name switch { "Wait" => Kind.Wait, "WaitAsync" => Kind.WaitAsync, "Release" => Kind.Release, _ => null };
            if (kind is null) continue;
            var (key, field, onThis) = SnapshotBuilder.Receiver(body, i);
            var address = field is { } f && body.Field(f) is { } named ? Address(named.Name) : (ulong?)null;
            calls.Add(new Call(ins.Offset, kind.Value, key, address, field, onThis,
                               kind == Kind.Release ? null : SnapshotBuilder.HeldAfter(body, i), kind == Kind.Release ? null : SnapshotBuilder.RetryBranch(body, i)));
        }
        return calls;
    }

    private static ulong Address(string field) => field switch { "_a" => 0xA0, "_b" => 0xB0, _ => 0xF0 };

    /// Whether the claim is made: the first take of <paramref name="taken"/> is held at <paramref name="point"/>.
    private static bool Held(SnapshotBuilder.IlBody body, string taken, int point)
    {
        var calls = Calls(body);
        var take = calls.First(c => c.Kind != Kind.Release && c.Address == Address(taken));
        return SnapshotBuilder.HasSlotAt(body, take, point) && !SnapshotBuilder.MayRelease(calls, take, point, body);
    }

    /// Every way to <paramref name="point"/> goes through the first take of <paramref name="taken"/>; releases aside.
    private static bool Dominates(SnapshotBuilder.IlBody body, string taken, int point) =>
        SnapshotBuilder.HasSlotAt(body, Calls(body).First(c => c.Kind != Kind.Release && c.Address == Address(taken)), point);

    /// The IL offset of the n-th wait on the field.
    private static int WaitOn(SnapshotBuilder.IlBody body, string field, int nth = 0) =>
        Calls(body).Where(c => c.Kind != Kind.Release && c.Address == Address(field)).ElementAt(nth).Offset;

    /// The IL offset of the first call to the method.
    private static int CallTo(SnapshotBuilder.IlBody body, string method) =>
        body.Instructions.First(i => (i.Code == OpCodes.Call.Value || i.Code == OpCodes.Callvirt.Value) && body.CalleeName(i.Operand) == method).Offset;

    /// The MoveNext of an async method of the class, as collection names it: by its MethodDef token.
    private static int MoveNextToken(string name) =>
        typeof(StockRoom).GetNestedTypes(Any).Single(t => t.Name.StartsWith($"<{name}>", StringComparison.Ordinal)).GetMethod("MoveNext", Any)!.MetadataToken;

    /// Where MoveNext suspends for the await of the task that the first call to the method returns.
    private static int AwaitOfCallTo(SnapshotBuilder.IlBody body, string method)
    {
        var call = CallTo(body, method);
        return Enumerable.Range(0, 8).Select(s => body.SuspensionPoint(s)).First(p => p is not null && SnapshotBuilder.AwaitedCall(body.Instructions, body.CalleeName, p.Value) == call)!.Value;
    }

    /// Where MoveNext suspends for the await of the n-th WaitAsync on the field.
    private static int AwaitOf(SnapshotBuilder.IlBody body, string field, int nth = 0)
    {
        var wait = WaitOn(body, field, nth);
        var state = Enumerable.Range(0, 8).First(s => body.SuspensionPoint(s) is { } p && p > wait && SnapshotBuilder.AwaitedCall(body.Instructions, body.CalleeName, p) == wait);
        return body.SuspensionPoint(state)!.Value;
    }

    // ---------------------------------------------------------------- the claims that stay

    [Fact]
    public void Crossed_takes_are_held_also_with_a_call_into_the_class_and_a_using_block_in_between()
    {
        var crossed = Method(nameof(StockRoom.Crossed));
        Assert.True(Held(crossed, "_a", WaitOn(crossed, "_b")));

        // A worker loop takes _a again on every turn, and the release in the finally comes after the wait on that turn.
        var loop = Method(nameof(StockRoom.CrossedInLoop));
        Assert.True(Held(loop, "_a", WaitOn(loop, "_b")));

        var helper = Method(nameof(StockRoom.CountInBetween));
        Assert.True(Held(helper, "_a", WaitOn(helper, "_b")));

        var usingBlock = Method(nameof(StockRoom.UsingBetween));
        Assert.True(Held(usingBlock, "_a", WaitOn(usingBlock, "_b")));

        // The catch rethrows: what throws there never gets to the wait.
        var rethrows = Method(nameof(StockRoom.CatchRethrows));
        Assert.True(Held(rethrows, "_a", WaitOn(rethrows, "_b")));

        var reentry = MoveNext(nameof(StockRoom.ReentryAsync));
        Assert.True(Held(reentry, "_a", reentry.SuspensionPoint(1)!.Value));
    }

    // ---------------------------------------------------------------- releases

    [Fact]
    public void A_release_later_in_a_loop_is_a_release_before_the_wait_on_the_next_turn()
    {
        // _a.Wait(); while (true) { _b.Wait(); ... if (setup) { setup = false; _a.Release(); } }
        var sync = Method(nameof(StockRoom.ImportOrders));
        Assert.True(SnapshotBuilder.HasSlotAt(sync, Calls(sync).First(c => c.Address == 0xA0), WaitOn(sync, "_b")));
        Assert.False(Held(sync, "_a", WaitOn(sync, "_b")));

        // The same with the release in a finally block, which carries on where its try block's leave goes.
        var inFinally = Method(nameof(StockRoom.ReleaseInFinallyOnce));
        Assert.False(Held(inFinally, "_a", WaitOn(inFinally, "_b")));

        // await _a.WaitAsync(); while (true) { if (!setup) await _a.WaitAsync(); try { } finally { _a.Release(); } ... }
        var pump = MoveNext(nameof(StockRoom.PumpAsync));
        Assert.True(Dominates(pump, "_a", AwaitOf(pump, "_a", nth: 1)));
        Assert.False(Held(pump, "_a", AwaitOf(pump, "_a", nth: 1)));
    }

    [Fact]
    public void A_release_on_a_semaphore_the_code_cant_name_may_be_this_one()
    {
        // finally { _a?.Release(); }
        var nullConditional = Method(nameof(StockRoom.ReleaseNullConditional));
        Assert.True(Dominates(nullConditional, "_a", WaitOn(nullConditional, "_b")));
        Assert.Contains(Calls(nullConditional), c => c.Kind == Kind.Release && c.Key is null);
        Assert.False(Held(nullConditional, "_a", WaitOn(nullConditional, "_b")));
    }

    [Fact]
    public void A_method_of_the_same_class_that_releases_it_is_a_release()
    {
        // finally { UnlockA(); }
        var helper = Method(nameof(StockRoom.ReleaseThroughHelper));
        Assert.True(Dominates(helper, "_a", WaitOn(helper, "_b")));
        Assert.False(Held(helper, "_a", WaitOn(helper, "_b")));

        // finally { Let(_a); } with static void Let(SemaphoreSlim s) => s.Release();
        var parameter = Method(nameof(StockRoom.ReleaseThroughParameter));
        Assert.False(Held(parameter, "_a", WaitOn(parameter, "_b")));

        // await _a.WaitAsync(); try { } finally { UnlockA(); } await Task.Yield(); await _b.WaitAsync();
        var async = MoveNext(nameof(StockRoom.ExportAsync));
        Assert.True(Dominates(async, "_a", AwaitOf(async, "_b")));
        Assert.False(Held(async, "_a", AwaitOf(async, "_b")));

        // Its code can't be read: it may release anything.
        var unread = new SnapshotBuilder.IlBody(helper.Instructions, helper.Metadata, helper.Clauses) { OwnTypes = OwnTypes, ReadMethod = _ => null };
        var unreadCalls = Calls(unread);
        Assert.True(SnapshotBuilder.MayRelease(unreadCalls, unreadCalls.First(c => c.Address == 0xA0), WaitOn(unread, "_b"), unread));
    }

    [Fact]
    public void The_call_a_frame_is_waiting_in_is_no_release_before_itself()
    {
        // _a.Wait(); try { other.AddA(); } finally { _a.Release(); }  -- the thread waits inside other.AddA(), which takes
        // and releases other's _a: this frame still holds its own _a there.
        var move = Method(nameof(StockRoom.MoveTo));
        Assert.True(Held(move, "_a", CallTo(move, nameof(StockRoom.AddA))));

        // In a loop an earlier turn of the call may have released it: the same field of another object reads alike.
        var loop = Method(nameof(StockRoom.MoveAllTo));
        Assert.True(Dominates(loop, "_a", CallTo(loop, nameof(StockRoom.AddA))));
        Assert.False(Held(loop, "_a", CallTo(loop, nameof(StockRoom.AddA))));
    }

    [Fact]
    public void What_the_class_s_methods_release_is_read_once_for_all_the_threads_waiting_in_the_caller()
    {
        // _a.Wait(); CountTwice(); CountTwice(); _b.Wait();  with CountTwice() { Count(); Count(); }
        var reads = 0;
        var method = Method(nameof(StockRoom.CountTwiceTwice));
        var body = new SnapshotBuilder.IlBody(method.Instructions, method.Metadata, method.Clauses)
        {
            OwnTypes = OwnTypes,
            ReadMethod = token =>
            {
                reads++;
                return typeof(StockRoom).Module.ResolveMethod(token) is { } m && m.GetMethodBody() is not null ? SemaphoreCodeEvidenceTests.Body(m) : null;
            },
        };
        var calls = Calls(body);
        var take = calls.First(c => c.Address == 0xA0);
        for (var thread = 0; thread < 1000; thread++)
            Assert.False(SnapshotBuilder.MayRelease(calls, take, WaitOn(body, "_b"), body));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_call_into_a_generic_class_of_the_module_is_found_by_its_definition()
    {
        var take = typeof(Shelf<>).GetMethod(nameof(Shelf<int>.Take))!;
        var body = SemaphoreCodeEvidenceTests.Body(take);
        var call = body.Instructions.Single(i => i.Code == OpCodes.Call.Value && body.CalleeName(i.Operand) == "Unlock");
        Assert.Equal(typeof(Shelf<>).GetMethod("Unlock", Any)!.MetadataToken, MetadataNames.MethodDefinition(body.Metadata, call.Operand));

        // A method of another module is not a method of the class.
        var wait = body.Instructions.First(i => i.Code == OpCodes.Callvirt.Value && body.CalleeName(i.Operand) == "Wait");
        Assert.Null(MetadataNames.MethodDefinition(body.Metadata, wait.Operand));
    }

    [Fact]
    public void A_semaphore_handed_to_other_code_after_the_take_may_be_released_there()
    {
        // finally { _a.ReleaseQuietly(); }   an extension method of another class
        // finally { Gates.Release(_a); }     a static helper of another class
        // using (new Releaser(_a)) ...       an object whose Dispose() releases it
        // var gate = _a; ... finally { gate.Release(); }
        foreach (var name in new[] { nameof(StockRoom.ReleaseThroughExtension), nameof(StockRoom.ReleaseThroughOtherClass), nameof(StockRoom.ReleaseThroughReleaser), nameof(StockRoom.ReleaseThroughCopy) })
        {
            var method = Method(name);
            Assert.True(Dominates(method, "_a", WaitOn(method, "_b")), name);
            Assert.False(Held(method, "_a", WaitOn(method, "_b")), name);
        }

        // A method of the class that hands it on: finally { HandOnA(); } with void HandOnA() => Gates.Release(_a);
        var handsOn = Method(nameof(StockRoom.ReleaseThroughHelperOfHelper));
        Assert.False(Held(handsOn, "_a", WaitOn(handsOn, "_b")));

        // Calling something on it is not handing it on: if (_a.CurrentCount > 0) Log("odd");
        var reads = Method(nameof(StockRoom.ReadsCountInBetween));
        Assert.True(Held(reads, "_a", WaitOn(reads, "_b")));

        // Nor is handing on the other semaphore, or this one after the wait.
        var other = Method(nameof(StockRoom.HandsOnTheOtherOne));
        Assert.True(Held(other, "_a", WaitOn(other, "_b")));
    }

    [Fact]
    public void A_lambda_of_the_method_that_releases_it_may_run_before_the_wait()
    {
        // finally { Gates.Run(() => _a.Release()); }: a method of the class that nothing calls by name.
        var lambda = Method(nameof(StockRoom.ReleaseThroughLambda));
        Assert.True(Dominates(lambda, "_a", WaitOn(lambda, "_b")));
        Assert.False(Held(lambda, "_a", WaitOn(lambda, "_b")));

        // With a captured local it is a method of a class the compiler nests in this one.
        var closure = Method(nameof(StockRoom.ReleaseThroughClosure));
        Assert.False(Held(closure, "_a", WaitOn(closure, "_b")));

        // A helper class nested in this one reaches the private field too: new Unlocker(this).UnlockA();
        var nested = Method(nameof(StockRoom.ReleaseThroughNestedClass));
        Assert.False(Held(nested, "_a", WaitOn(nested, "_b")));

        // A lambda that releases nothing takes nothing away: Gates.Run(() => _count++);
        var counts = Method(nameof(StockRoom.LambdaThatOnlyCounts));
        Assert.True(Held(counts, "_a", WaitOn(counts, "_b")));
    }

    [Fact]
    public void An_async_method_of_the_class_releases_in_the_MoveNext_of_its_state_machine()
    {
        // finally { UnlockALaterAsync().Wait(); } with async Task UnlockALaterAsync() { await Task.Yield(); _a.Release(); }
        var sync = Method(nameof(StockRoom.ReleaseThroughAsyncMethod));
        Assert.True(Dominates(sync, "_a", WaitOn(sync, "_b")));
        Assert.False(Held(sync, "_a", WaitOn(sync, "_b")));

        // await _a.WaitAsync(); _count++; await UnlockALaterAsync(); await _b.WaitAsync();
        var awaited = MoveNext(nameof(StockRoom.ReleaseThroughAwaitedMethodAsync));
        Assert.True(Dominates(awaited, "_a", AwaitOf(awaited, "_b")));
        Assert.False(Held(awaited, "_a", AwaitOf(awaited, "_b")));

        // finally { Gates.RunAsync(async () => { await Task.Yield(); _a.Release(); }).Wait(); }
        var lambda = Method(nameof(StockRoom.ReleaseThroughAsyncLambda));
        Assert.False(Held(lambda, "_a", WaitOn(lambda, "_b")));

        // An async method of the class that releases nothing takes nothing away: await CountLaterAsync();
        var counts = MoveNext(nameof(StockRoom.CountsThroughAwaitedMethodAsync));
        Assert.True(Held(counts, "_a", AwaitOf(counts, "_b")));
    }

    // ---------------------------------------------------------------- the methods further in

    [Fact]
    public void A_method_the_taker_waits_in_may_have_released_it_before_its_own_wait()
    {
        // _a.Wait(); _count++; CommitAndRelease();  with  CommitAndRelease() { _a.Release(); _b.Wait(); ... }
        // The thread waits in CommitAndRelease for _b. Save's own code shows no release before that call...
        var save = Method(nameof(StockRoom.SaveThenHandOff));
        Assert.True(Held(save, "_a", CallTo(save, "CommitAndRelease")));
        // ...but the method it is in has released it on its way to the wait.
        var commit = Method("CommitAndRelease");
        var field = commit.Field(Calls(commit).First(c => c.Address == 0xA0).Field!.Value);
        Assert.True(SnapshotBuilder.MayHaveReleased(Calls(commit), 0xA0, field, WaitOn(commit, "_b"), inProgress: null, commit));
        // Also when the dump doesn't show which object that method runs on: the release gives back nothing it took itself.
        Assert.True(SnapshotBuilder.MayHaveReleased(Unnamed(commit), 0xA0, field, WaitOn(commit, "_b"), inProgress: null, commit));

        // _a.Wait(); try { _count++; } finally { _a.Release(); }: the release comes after the wait it is in.
        var add = Method(nameof(StockRoom.AddA));
        Assert.False(SnapshotBuilder.MayHaveReleased(Calls(add), 0xA0, field, WaitOn(add, "_a"), inProgress: null, add));
        // Where it is isn't known: a release anywhere in it counts, when it is this semaphore...
        Assert.True(SnapshotBuilder.MayHaveReleased(Calls(add), 0xA0, field, point: null, inProgress: null, add));
        // ...but not the _a of another object, and not a release that gives back what the method took itself.
        Assert.False(SnapshotBuilder.MayHaveReleased(Calls(add).Select(c => c with { Address = 0xA1 }).ToList(), 0xA0, field, point: null, inProgress: null, add));
        Assert.False(SnapshotBuilder.MayHaveReleased(Unnamed(add), 0xA0, field, point: null, inProgress: null, add));

        static List<Call> Unnamed(SnapshotBuilder.IlBody body) => Calls(body).Select(c => c with { Address = null }).ToList();
    }

    [Fact]
    public void The_call_an_async_method_awaits_is_still_running_and_has_released_only_what_its_code_so_far_shows()
    {
        // await _a.WaitAsync(); try { await InnerAsync(); } finally { _a.Release(); }
        // with  async Task InnerAsync() { await _a.WaitAsync(); try { _count++; } finally { _a.Release(); } }
        var outer = MoveNext(nameof(StockRoom.OuterAsync));
        var call = CallTo(outer, "InnerAsync");
        var paused = AwaitOfCallTo(outer, "InnerAsync");
        var calls = Calls(outer);
        var take = calls.First(c => c.Kind == Kind.WaitAsync);
        Assert.True(SnapshotBuilder.HasSlotAt(outer, take, paused));

        // InnerAsync releases _a in its state machine: as a call that has returned it would be a release.
        Assert.True(SnapshotBuilder.MayRelease(calls, take, paused, outer));
        // As the call the await waits for it hasn't got there: it is paused at its own wait for _a.
        Assert.False(SnapshotBuilder.MayRelease(calls, take, paused, outer, inProgress: new(call, MoveNextToken("InnerAsync"))));
        var inner = MoveNext("InnerAsync");
        Assert.False(SnapshotBuilder.MayHaveReleased(Calls(inner), 0xA0, outer.Field(take.Field!.Value), WaitOn(inner, "_a"), inProgress: null, inner));
        // When the await waits for some other method, the call started and finished InnerAsync like any other call.
        Assert.True(SnapshotBuilder.MayRelease(calls, take, paused, outer, inProgress: new(call, MoveNextToken("CountLaterAsync"))));
    }

    [Fact]
    public void Only_the_method_the_awaited_call_started_is_still_running()
    {
        var innerAsync = MoveNextToken("InnerAsync");
        static bool Released(SnapshotBuilder.IlBody body, string awaited, int running)
        {
            var calls = Calls(body);
            var take = calls.First(c => c.Kind == Kind.WaitAsync);
            var paused = AwaitOfCallTo(body, awaited);
            Assert.True(SnapshotBuilder.HasSlotAt(body, take, paused), awaited);
            return SnapshotBuilder.MayRelease(calls, take, paused, body, inProgress: new(CallTo(body, awaited), running));
        }

        // await _a.WaitAsync(); _count++; await HandBackThenRetake();  with  Task HandBackThenRetake() { _a.Release(); return InnerAsync(); }
        // The awaited method isn't async: all of it has run, the release too, before it returned InnerAsync's task.
        Assert.True(Released(MoveNext(nameof(StockRoom.OuterThroughWrapperAsync)), "HandBackThenRetake", innerAsync));

        // try { await InnerOverload(); } finally { _a.Release(); }  with  Task InnerOverload() => InnerAsync();
        // An overload that forwards has done nothing but start it.
        Assert.False(Released(MoveNext(nameof(StockRoom.OuterThroughOverloadAsync)), "InnerOverload", innerAsync));

        // ...  with  Task InnerTwice() { InnerAsync().Wait(); return InnerAsync(); }: one of the two has run to its end.
        Assert.True(Released(MoveNext(nameof(StockRoom.OuterThroughTwoCallsAsync)), "InnerTwice", innerAsync));

        // ...  with  Task InnerOfMany() { Task last = Task.CompletedTask; for (...) last = InnerAsync(); return last; }
        Assert.True(Released(MoveNext(nameof(StockRoom.OuterThroughLoopingWrapperAsync)), "InnerOfMany", innerAsync));

        // await _a.WaitAsync(); for (...) await InnerAsync();: the call of an earlier turn has run to its end.
        Assert.True(Released(MoveNext(nameof(StockRoom.OuterAwaitsInLoopAsync)), "InnerAsync", innerAsync));

        // for (...) { await _a.WaitAsync(); try { await InnerAsync(); } finally { _a.Release(); } }: every turn takes it
        // again, so an earlier turn's call says nothing about this turn's take.
        Assert.False(Released(MoveNext(nameof(StockRoom.OuterTakesInLoopAsync)), "InnerAsync", innerAsync));
    }

    // ---------------------------------------------------------------- a WaitAsync that isn't awaited

    [Fact]
    public void A_WaitAsync_takes_the_slot_only_where_the_code_waits_for_its_task()
    {
        static Call Take(SnapshotBuilder.IlBody body) => Calls(body).First(c => c.Kind == Kind.WaitAsync);

        // var entered = _a.WaitAsync();
        // if (await Task.WhenAny(entered, Task.Delay(50)) != entered) { await InnerAsync(); return; }
        // The method gave up waiting: the task is still waiting for the slot while InnerAsync waits for it too.
        Assert.Null(Take(MoveNext(nameof(StockRoom.TakeOrGiveUpAsync))).HeldIn);

        // var entered = _a.WaitAsync(); await InnerAsync(); await entered;
        Assert.Null(Take(MoveNext(nameof(StockRoom.TakeLaterAsync))).HeldIn);

        // _ = _a.WaitAsync().ContinueWith(...);
        Assert.Null(Take(MoveNext(nameof(StockRoom.TakeThenContinueAsync))).HeldIn);

        // var entered = _a.WaitAsync(100); await CountLaterAsync(); if (!await entered) return;
        // The result tested first is that of another await.
        Assert.Null(Take(MoveNext(nameof(StockRoom.TimedTakeLaterAsync))).HeldIn);

        // Waited for right there, it has the slot when the code goes on: await, with ConfigureAwait, and blocking.
        foreach (var name in new[] { nameof(StockRoom.OuterAsync), nameof(StockRoom.TakeConfiguredAsync), nameof(StockRoom.TakeBlockingAsync), nameof(StockRoom.TakeGetResultAsync) })
        {
            var body = MoveNext(name);
            var take = Take(body);
            Assert.True(take.HeldIn is not null, name);
            Assert.True(SnapshotBuilder.HasSlotAt(body, take, AwaitOfCallTo(body, "InnerAsync")), name);
        }

        // if (!await _a.WaitAsync(100).ConfigureAwait(false)) return;: held after the test of its own result.
        var timed = MoveNext(nameof(StockRoom.TimedTakeConfiguredAsync));
        Assert.True(SnapshotBuilder.HasSlotAt(timed, Take(timed), AwaitOfCallTo(timed, "InnerAsync")));
    }

    // ---------------------------------------------------------------- which object is `this`

    [Fact]
    public void A_constructor_of_a_class_nested_in_this_one_that_releases_it_is_a_release()
    {
        // using (new Unlocked(this)) { _b.Wait(); ... }  with  Unlocked(StockRoom room) { room._a.Release(); }
        var scope = Method(nameof(StockRoom.ReleaseThroughConstructor));
        Assert.True(Dominates(scope, "_a", WaitOn(scope, "_b")));
        Assert.False(Held(scope, "_a", WaitOn(scope, "_b")));
        // A scope whose constructor only counts.
        var counted = Method(nameof(StockRoom.CountThroughConstructor));
        Assert.True(Held(counted, "_a", WaitOn(counted, "_b")));
        // A method of the class that returns the scope: using (Unlock()) { ... }  with  Unlock() => new Unlocked(this);
        var returned = Method(nameof(StockRoom.ReleaseThroughReturnedScope));
        Assert.False(Held(returned, "_a", WaitOn(returned, "_b")));
    }

    [Fact]
    public void Code_outside_the_class_that_is_passed_this_may_release_it()
    {
        // _a.Wait(); _count++; Porter.Finish(this); _b.Wait();  with  Porter.Finish(room) => room.GiveBackA();
        var handed = Method(nameof(StockRoom.HandsItselfToAHelper));
        Assert.True(Dominates(handed, "_a", WaitOn(handed, "_b")));
        Assert.False(Held(handed, "_a", WaitOn(handed, "_b")));
        // Porter.Note(this) only counts: its code is read, and the claim stays.
        var shown = Method(nameof(StockRoom.ShowsItselfToAHelper));
        Assert.True(Held(shown, "_a", WaitOn(shown, "_b")));
        // Code of another module can't be read: whoever handles the event gets the room as its sender.
        var raised = Method(nameof(StockRoom.RaisesAnEvent));
        Assert.False(Held(raised, "_a", WaitOn(raised, "_b")));
        // lock (this), Monitor.Enter(this) and a delegate made of one of the class's own methods hand it to nobody.
        var locked = Method(nameof(StockRoom.LocksOnItself));
        Assert.True(Held(locked, "_a", WaitOn(locked, "_b")));
        // A method of the class that hands `this` on: Finish() => Porter.Finish(this);
        var through = Method(nameof(StockRoom.HandsItselfOnThroughAHelper));
        Assert.False(Held(through, "_a", WaitOn(through, "_b")));
        // await _a.WaitAsync(); _count++; await Porter.FinishAsync(this); await _b.WaitAsync();
        var async = MoveNext(nameof(StockRoom.HandsItselfToAHelperAsync));
        Assert.True(Dominates(async, "_a", AwaitOf(async, "_b")));
        Assert.False(Held(async, "_a", AwaitOf(async, "_b")));
    }

    [Fact]
    public void A_static_semaphore_is_in_reach_of_any_code_of_the_module()
    {
        // Shared.Wait(); Porter.ReleaseShared(); _b.Wait();  with  Porter.ReleaseShared() => StockRoom.Shared.Release();
        var shared = Method(nameof(StockRoom.SharedThenB));
        Assert.True(Dominates(shared, "Shared", WaitOn(shared, "_b")));
        Assert.False(Held(shared, "Shared", WaitOn(shared, "_b")));
        // Shared.Wait(); Porter.Idle(); _b.Wait();: code that is read and leaves it alone.
        var idle = Method(nameof(StockRoom.SharedThenIdle));
        Assert.True(Held(idle, "Shared", WaitOn(idle, "_b")));
    }

    [Fact]
    public void A_frame_of_another_class_between_the_take_and_the_wait_may_have_released_it()
    {
        // StockRoom: _a.Wait(); _count++; Porter.Deliver(this);  Porter: room.GiveBackA(); room.PackStep();  PackStep waits for _b.
        var save = Method(nameof(StockRoom.HandsItselfToAHelper));
        var field = save.Field(Calls(save).First(c => c.Address == 0xA0).Field!.Value);
        var deliver = Outside(nameof(Porter.Deliver));
        Assert.True(SnapshotBuilder.MayHaveReleased(Calls(deliver), 0xA0, field, CallTo(deliver, "PackStep"), inProgress: null, deliver));
        // The same frame when it only looks at the room on its way...
        var held = Outside(nameof(Porter.DeliverHeld));
        Assert.False(SnapshotBuilder.MayHaveReleased(Calls(held), 0xA0, field, CallTo(held, "PackStep"), inProgress: null, held));
        // ...and when the release comes after the call it is in.
        var later = Outside(nameof(Porter.DeliverThenGiveBack));
        Assert.False(SnapshotBuilder.MayHaveReleased(Calls(later), 0xA0, field, CallTo(later, "PackStep"), inProgress: null, later));
    }

    [Fact]
    public void A_field_given_another_semaphore_after_the_take_no_longer_names_what_was_taken()
    {
        // _swapped.Wait(); _swapped = new SemaphoreSlim(1, 1); _b.Wait();: the dump shows the new one, taken by nobody here.
        var swap = Method(nameof(StockRoom.SwapsTheLock));
        Assert.True(Dominates(swap, "_swapped", WaitOn(swap, "_b")));
        Assert.False(Held(swap, "_swapped", WaitOn(swap, "_b")));
        // The same in a method of the class: _swapped.Wait(); Reset(); _b.Wait();
        var helper = Method(nameof(StockRoom.SwapsTheLockInAHelper));
        Assert.False(Held(helper, "_swapped", WaitOn(helper, "_b")));
    }

    [Fact]
    public void A_release_before_a_throw_has_run_when_a_catch_carries_on()
    {
        // _a.Wait(); for (...) { try { if (attempt > 0) { _b.Wait(); ...; return; } _a.Release(); throw ...; } catch (TimeoutException) { } }
        var retry = Method(nameof(StockRoom.ReleaseThenThrowInRetry));
        Assert.True(Dominates(retry, "_a", WaitOn(retry, "_b")));
        Assert.False(Held(retry, "_a", WaitOn(retry, "_b")));
        // Without the release the second turn still holds it.
        var held = Method(nameof(StockRoom.ThrowInRetry));
        Assert.True(Held(held, "_a", WaitOn(held, "_b")));
        // The release in the finally block of a try block that only ever throws: try { ...; throw ...; } finally { _a.Release(); }
        var inFinally = Method(nameof(StockRoom.ReleaseInFinallyOfAFailedTry));
        Assert.True(Dominates(inFinally, "_a", WaitOn(inFinally, "_b")));
        Assert.False(Held(inFinally, "_a", WaitOn(inFinally, "_b")));
    }

    [Fact]
    public void A_frame_still_reports_this_during_a_call_when_the_code_reads_a_field_of_it_afterwards()
    {
        static List<int> Waits(SnapshotBuilder.IlBody body) => body.Instructions
            .Where(i => (i.Code == OpCodes.Call.Value || i.Code == OpCodes.Callvirt.Value) && body.Callee(i.Operand) is ("System.Threading.SemaphoreSlim", "Wait"))
            .Select(i => i.Offset).ToList();

        // _a.Wait(); try { other.AddA(); } finally { _a.Release(); }: the release needs `this`.
        var move = Method(nameof(StockRoom.MoveTo));
        Assert.True(move.ReadsFieldOfThisAfter(Waits(move)[0]));
        Assert.True(move.ReadsFieldOfThisAfter(CallTo(move, nameof(StockRoom.AddA))));

        // _a.Wait(); to._a.Wait(); to._count++; to._a.Release();: nothing reads `this` after its own take. Optimized code
        // may then report only `to`, whose _a is not the one this thread took.
        var pass = Method(nameof(StockRoom.PassTo));
        Assert.Equal(2, Waits(pass).Count);
        Assert.All(Waits(pass), wait => Assert.False(pass.ReadsFieldOfThisAfter(wait)));

        // A call on `this` proves nothing, since the method it calls may be inlined and never touch it:
        // _a.Wait(); to._a.Wait(); Log(ToString());
        var calls = Method(nameof(StockRoom.PassThenLog));
        Assert.All(Waits(calls), wait => Assert.False(calls.ReadsFieldOfThisAfter(wait)));

        // In a loop the next turn reads it: while (!_stop) { _a.Wait(); try { _b.Wait(); ... } finally { _a.Release(); } }
        var loop = Method(nameof(StockRoom.CrossedInLoop));
        Assert.All(Waits(loop), wait => Assert.True(loop.ReadsFieldOfThisAfter(wait)));

        // Without the method's try blocks the ways on from the call aren't known.
        var blind = new SnapshotBuilder.IlBody(move.Instructions, move.Metadata, clauses: null);
        Assert.False(blind.ReadsFieldOfThisAfter(Waits(move)[0]));
    }

    // ---------------------------------------------------------------- exceptions

    [Fact]
    public void A_take_in_a_try_block_whose_catch_carries_on_is_not_held_after_it()
    {
        // try { Validate(); _a.Wait(); } catch (InvalidOperationException) { } _b.Wait();
        var skip = Method(nameof(StockRoom.CatchSkip));
        Assert.False(Held(skip, "_a", WaitOn(skip, "_b")));
        // Without the way into the catch block the take looked like it was on every path.
        var noCatch = new SnapshotBuilder.IlBody(skip.Instructions, skip.Metadata, []);
        Assert.True(Held(noCatch, "_a", WaitOn(noCatch, "_b")));

        // catch (Exception e) when (e is InvalidOperationException) { }
        var filter = Method(nameof(StockRoom.FilterSkip));
        Assert.False(Held(filter, "_a", WaitOn(filter, "_b")));

        // try { using (...) { Validate(); _a.Wait(); } } catch (InvalidOperationException) { }: the outer try block counts.
        var nested = Method(nameof(StockRoom.UsingInsideCatch));
        Assert.False(Held(nested, "_a", WaitOn(nested, "_b")));

        // try { Validate(); await _a.WaitAsync(); } catch (InvalidOperationException) { } await _b.WaitAsync();
        var async = MoveNext(nameof(StockRoom.CatchSkipAsync));
        Assert.False(Held(async, "_a", AwaitOf(async, "_b")));
    }

    [Fact]
    public void Without_the_try_blocks_a_take_before_a_leave_proves_nothing()
    {
        var known = Method(nameof(StockRoom.UsingBetween));
        var unknown = new SnapshotBuilder.IlBody(known.Instructions, known.Metadata, clauses: null);
        Assert.True(Held(known, "_a", WaitOn(known, "_b")));
        Assert.False(Held(unknown, "_a", WaitOn(unknown, "_b")));

        // Code without try blocks needs none.
        var plain = Method(nameof(StockRoom.Plain));
        Assert.True(Held(new SnapshotBuilder.IlBody(plain.Instructions, plain.Metadata, clauses: null), "_a", WaitOn(plain, "_b")));
    }

    // ---------------------------------------------------------------- waits with a timeout

    [Fact]
    public void A_timed_wait_whose_failure_can_also_carry_on_took_nothing()
    {
        // if (!_a.Wait(50) && _strict) throw new TimeoutException(); _b.Wait();
        var lenient = Method(nameof(StockRoom.TimedLenient));
        Assert.True(lenient.OnEveryPathTo(WaitOn(lenient, "_a"), WaitOn(lenient, "_b")));
        Assert.Null(SnapshotBuilder.HeldAfter(lenient, lenient.Instructions.FindIndex(i => i.Offset == WaitOn(lenient, "_a"))));
        Assert.False(Held(lenient, "_a", WaitOn(lenient, "_b")));

        // var got = _a.Wait(50); if (!got && _strict) return; _b.Wait();
        var local = Method(nameof(StockRoom.TimedLenientLocal));
        Assert.False(Held(local, "_a", WaitOn(local, "_b")));

        // while (!_a.Wait(50)) { if (_stop) break; } _b.Wait();
        var stopped = Method(nameof(StockRoom.RetryUntilStopped));
        Assert.False(Held(stopped, "_a", WaitOn(stopped, "_b")));

        // if (!_a.Wait(50)) throw new TimeoutException(); _b.Wait();  -- still held.
        var strict = Method(nameof(StockRoom.TimedStrict));
        Assert.True(Held(strict, "_a", WaitOn(strict, "_b")));
    }

    [Fact]
    public void A_timed_async_take_holds_across_an_await_in_the_try_block_after_it()
    {
        // if (!await _a.WaitAsync(timeout)) throw new TimeoutException(); try { await Task.Yield(); } finally { _a.Release(); }
        // MoveNext's jump on its state into the try block, to resume there, is no way past the throw.
        var body = MoveNext(nameof(StockRoom.TimedReentryAsync));
        var wait = body.Instructions.FindIndex(i => i.Offset == WaitOn(body, "_a"));
        Assert.NotNull(SnapshotBuilder.HeldAfter(body, wait));
        Assert.True(Held(body, "_a", body.SuspensionPoint(1)!.Value));
    }

    [Fact]
    public void A_debug_state_machine_that_keeps_the_awaited_result_in_a_field_still_shows_the_test()
    {
        // A Debug build's MoveNext for `if (!await _a.WaitAsync(timeout)) throw new TimeoutException();` stores the awaited
        // bool in a field of the state machine and reads it right back (call GetResult; stfld <>s__1; ldarg.0;
        // ldfld <>s__1; ldc.i4.0; ceq; stloc.2; ldloc.2; brfalse.s). The tokens come from this assembly's own MoveNext.
        var release = MoveNext(nameof(StockRoom.TimedReentryAsync));
        int token(string name) => release.Instructions.First(i => (i.Code == OpCodes.Call.Value || i.Code == OpCodes.Callvirt.Value) && release.CalleeName(i.Operand) == name).Operand;
        var gate = release.Instructions.First(i => i.Code == OpCodes.Ldfld.Value && release.Field(i.Operand)?.Name == "_a").Operand;
        var spill = release.Instructions.First(i => i.Code == OpCodes.Stfld.Value && release.Field(i.Operand)?.Name == "<>1__state").Operand;
        var il = new IlWriter()
            .Op(0x02).Op(0x7B, gate).Op(0x06).Op(0x6F, token("WaitAsync")).Op(0x6F, token("GetAwaiter")).Op(0x0B) // ldarg.0; ldfld _a; ldloc.0; callvirt WaitAsync; GetAwaiter; stloc.1
            .Op(0x02).Op8(0x12, 1).Op(0x28, token("GetResult")).Op(0x7D, spill)                           // ldarg.0; ldloca.s 1; call GetResult; stfld <>s__1
            .Op(0x02).Op(0x7B, spill).Op(0x16).Op2(0xFE, 0x01).Op(0x0C).Op(0x08).Op8(0x2C, 6)             // ldarg.0; ldfld; ldc.i4.0; ceq; stloc.2; ldloc.2; brfalse.s +6
            .Op(0x73, release.Instructions.First(i => i.Code == OpCodes.Newobj.Value).Operand).Op(0x7A)     // newobj TimeoutException; throw
            .Op(0x00).Op(0x2A)                                                                            // nop; ret
            .Bytes();
        var debug = new SnapshotBuilder.IlBody(SnapshotBuilder.Decode(il)!, release.Metadata, []);
        var wait = debug.Instructions.FindIndex(i => i.Code == OpCodes.Callvirt.Value && debug.CalleeName(i.Operand) == "WaitAsync");
        var nop = debug.Instructions.Single(i => i.Code == OpCodes.Nop.Value).Offset;
        Assert.Equal((nop, int.MaxValue), SnapshotBuilder.HeldAfter(debug, wait));
    }

    // ---------------------------------------------------------------- loops

    [Fact]
    public void Retrying_a_wait_with_a_timeout_until_it_gets_the_slot_is_not_a_loop_around_it()
    {
        // while (!_a.Wait(TimeSpan.FromSeconds(2))) Log("still waiting");
        var retry = Method(nameof(StockRoom.Retry));
        var wait = Calls(retry).Single(c => c.Kind == Kind.Wait);
        Assert.NotNull(wait.RetryBranch);
        Assert.True(retry.Loops(wait.Offset));
        Assert.False(retry.Loops(wait.Offset, ignore: wait.RetryBranch));

        // The same inside a worker's loop: still a loop.
        var worker = Method(nameof(StockRoom.RetryInWorker));
        var workerWait = Calls(worker).Single(c => c.Kind == Kind.Wait);
        Assert.True(worker.Loops(workerWait.Offset, ignore: workerWait.RetryBranch));

        // while (!await _a.WaitAsync(TimeSpan.FromSeconds(2))) Log("still waiting");
        var async = MoveNext(nameof(StockRoom.RetryAsync));
        var awaited = Calls(async).Single(c => c.Kind == Kind.WaitAsync);
        Assert.NotNull(awaited.RetryBranch);
        Assert.False(async.Loops(awaited.Offset, ignore: awaited.RetryBranch));

        // A wait that runs once per turn of a loop: no retry.
        var plain = Method(nameof(StockRoom.ImportOrders));
        Assert.All(Calls(plain).Where(c => c.Kind == Kind.Wait), c => Assert.Null(c.RetryBranch));
    }

    // ---------------------------------------------------------------- receivers and fields

    [Fact]
    public void A_semaphore_reached_through_a_property_or_an_indexer_has_no_name()
    {
        // PickLock.Wait(); and _locks[id].Wait(): call results, which collection can't tie to a field or to a Release.
        var property = Method(nameof(StockRoom.ThroughProperty));
        Assert.All(Calls(property), c => Assert.Null(c.Key));
        var indexer = Method(nameof(StockRoom.ThroughIndexer));
        Assert.All(Calls(indexer), c => Assert.Null(c.Key));
    }

    [Fact]
    public void A_field_is_picked_by_its_declaring_type_when_a_base_class_has_one_of_the_same_name()
    {
        var lockInBase = new MetadataNames.FieldName("Shop.Ledger", "_lock", "System.Threading.SemaphoreSlim");
        var lockInDerived = new MetadataNames.FieldName("Shop.AuditedLedger", "_lock", "System.Threading.SemaphoreSlim");
        (string?, string?)[] fields = [("_lock", "Shop.Ledger"), ("_journal", "Shop.Ledger"), ("_lock", "Shop.AuditedLedger")];
        Assert.Equal(0, SnapshotBuilder.PickField(fields, lockInBase));
        Assert.Equal(2, SnapshotBuilder.PickField(fields, lockInDerived));
        Assert.Null(SnapshotBuilder.PickField(fields, lockInBase with { DeclaringType = "Shop.Other" }));
        Assert.Equal(1, SnapshotBuilder.PickField(fields, new MetadataNames.FieldName("Shop.Ledger", "_journal", null)));

        // Generic arguments aside: the IL names Cache`1, the object is a Cache<System.String>.
        (string?, string?)[] generic = [("_read", "Shop.Cache<System.String>"), ("_read", "Shop.Base")];
        Assert.Equal(0, SnapshotBuilder.PickField(generic, new MetadataNames.FieldName("Shop.Cache`1", "_read", null)));
    }

    // ---------------------------------------------------------------- how a semaphore was created

    [Fact]
    public void A_throttle_sized_when_the_app_starts_is_made_with_free_slots()
    {
        var ctor = SemaphoreCodeEvidenceTests.Body(typeof(Throttles).GetConstructors(Any).Single());
        var ins = ctor.Instructions;
        var created = new Dictionary<string, int?>();
        for (var i = 0; i + 1 < ins.Count; i++)
        {
            if (ins[i].Code != OpCodes.Newobj.Value || ctor.Callee(ins[i].Operand).Type != "System.Threading.SemaphoreSlim") continue;
            created[ctor.Field(ins[i + 1].Operand)!.Name] = SnapshotBuilder.CreatedWith(ins, i, ctor.ParameterCount(ins[i].Operand), ctor.Callee);
        }

        Assert.Equal(SnapshotBuilder.FromVariable, created["_jobs"]);
        Assert.Equal(SnapshotBuilder.FromVariable, created["_cores"]);
        Assert.Equal(SnapshotBuilder.FromVariable, created["_sized"]);
        Assert.Equal(0, created["_closed"]);
        Assert.Equal(1, created["_bounded"]);
        // The last argument is more than one load, or a jump lands among the arguments: nothing is read.
        Assert.Null(created["_double"]);
        Assert.Null(created["_paused"]);
        Assert.Null(created["_pausedOne"]);
    }

    [Fact]
    public void A_dispatcher_waiting_in_a_loop_on_a_throttle_sized_from_a_setting_is_a_warning()
    {
        // new SemaphoreSlim(maxJobs), every slot leaked, the dispatcher waiting for one in its loop.
        var dispatcher = Thread(4, [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "WaitCore"),
                                    Fx("System.Threading.SemaphoreSlim", "Wait"), App("MyApp.Jobs", "Dispatch", "Jobs.cs", 22)], "dispatcher")
            with { WaitingForSemaphore = 0x100, SemaphoreUse = new SemaphoreUse { WaitsInLoop = true } };
        var snapshot = Process(Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 9)]), dispatcher) with
        {
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x100, "System.Threading.SemaphoreSlim"), FieldName = "Jobs._throttle", CurrentCount = 0, MaxCount = int.MaxValue, SizedAtRunTime = true, WaiterThreadIds = [4] }],
        };

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);
        Assert.Equal(("semaphore-wait", Severity.Warning), (f.Id, f.Severity));
        Assert.Equal(SemaphoreRole.Lock, SemaphoreFacts.Role(dispatcher.SemaphoreUse, snapshot.Semaphores[0]));
    }

    // ---------------------------------------------------------------- cost

    [Fact]
    public void Five_thousand_threads_whose_code_could_not_be_followed_cost_little()
    {
        var wait = new[] { Fx("System.Threading.SemaphoreSlim", "Wait"), App("MyApp.Orders", "Run", "Orders.cs", 5) };
        var threads = Enumerable.Range(10, 5000).Select(id => Thread(id, wait, $"worker-{id}") with { WaitingForSemaphore = (ulong)id * 0x10 }).ToList();
        var snapshot = Process([Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 9)]), .. threads]) with
        {
            Semaphores = threads.Select(t => new SemaphoreInfo
            {
                Object = new ObjectRef(t.WaitingForSemaphore!.Value, "System.Threading.SemaphoreSlim"), FieldName = $"Orders.Gate{t.ManagedId}", CurrentCount = 0, MaxCount = 1,
                WaiterThreadIds = [t.ManagedId],
            }).ToList(),
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var f = Assert.Single(new SemaphoreDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Equal(5000, f.ThreadIds.Count);
        Assert.Equal("Possible deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
    }

    /// Writes IL bytes.
    private sealed class IlWriter
    {
        private readonly List<byte> _bytes = [];
        public IlWriter Op(byte code) { _bytes.Add(code); return this; }
        public IlWriter Op2(byte code, byte second) { _bytes.Add(code); _bytes.Add(second); return this; }
        public IlWriter Op(byte code, int token) { _bytes.Add(code); _bytes.AddRange(BitConverter.GetBytes(token)); return this; }
        public IlWriter Op8(byte code, sbyte operand) { _bytes.Add(code); _bytes.Add(unchecked((byte)operand)); return this; }
        public byte[] Bytes() => [.. _bytes];
    }
}

/// The code the tests above read. It runs nowhere.
internal sealed class StockRoom
{
    private readonly SemaphoreSlim _a = new(1, 1);
    private readonly SemaphoreSlim _b = new(1, 1);
    private readonly Dictionary<int, SemaphoreSlim> _locks = [];
    private bool _strict;
    private bool _stop;
    private int _count;

    public SemaphoreSlim PickLock { get; } = new(1, 1);

    public void Configure(bool strict, bool stop) => (_strict, _stop) = (strict, stop);

    public void Plain()
    {
        _a.Wait();
        _b.Wait();
        _count++;
    }

    public void Crossed()
    {
        _a.Wait();
        try
        {
            _b.Wait();
            try { _count++; }
            finally { _b.Release(); }
        }
        finally { _a.Release(); }
    }

    public void CrossedInLoop()
    {
        while (!_stop)
        {
            _a.Wait();
            try
            {
                _b.Wait();
                try { _count++; }
                finally { _b.Release(); }
            }
            finally { _a.Release(); }
        }
    }

    public void CountInBetween()
    {
        _a.Wait();
        Count();
        _b.Wait();
        _count++;
    }

    public void CountTwiceTwice()
    {
        _a.Wait();
        CountTwice();
        CountTwice();
        _b.Wait();
        _count++;
    }

    private void CountTwice()
    {
        Count();
        Count();
    }

    public void UsingBetween()
    {
        _a.Wait();
        using (var writer = new StringWriter()) writer.Write(_count);
        _b.Wait();
        _count++;
    }

    public void CatchRethrows()
    {
        try { _a.Wait(); }
        catch (ObjectDisposedException) { Log("closed"); throw; }
        _b.Wait();
        _count++;
    }

    public void ImportOrders()
    {
        _a.Wait();
        var setup = true;
        while (true)
        {
            _b.Wait();
            try { _count++; }
            finally { _b.Release(); }
            if (setup) { setup = false; _a.Release(); }
        }
    }

    public void ReleaseInFinallyOnce()
    {
        _a.Wait();
        for (var i = 0; !_stop; i++)
        {
            _b.Wait();
            try { _count++; }
            finally
            {
                if (i == 0) _a.Release();
                _b.Release();
            }
        }
    }

    public void ReleaseNullConditional()
    {
        _a.Wait();
        try { _count++; }
        finally { _a?.Release(); }
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughHelper()
    {
        _a.Wait();
        try { _count++; }
        finally { UnlockA(); }
        _b.Wait();
        _count++;
    }

    public void MoveTo(StockRoom other)
    {
        _a.Wait();
        try { other.AddA(); }
        finally { _a.Release(); }
    }

    public void MoveAllTo(StockRoom other, int times)
    {
        _a.Wait();
        try
        {
            for (var i = 0; i < times; i++) other.AddA();
        }
        finally { _a.Release(); }
    }

    public void AddA()
    {
        _a.Wait();
        try { _count++; }
        finally { _a.Release(); }
    }

    public void ReleaseThroughParameter()
    {
        _a.Wait();
        try { _count++; }
        finally { Let(_a); }
        _b.Wait();
        _count++;
    }

    public void CatchSkip()
    {
        try
        {
            Validate();
            _a.Wait();
        }
        catch (InvalidOperationException) { }
        _b.Wait();
        _count++;
    }

    public void FilterSkip()
    {
        try
        {
            Validate();
            _a.Wait();
        }
        catch (Exception e) when (e is InvalidOperationException) { }
        _b.Wait();
        _count++;
    }

    public void UsingInsideCatch()
    {
        try
        {
            using var writer = new StringWriter();
            Validate();
            _a.Wait();
        }
        catch (InvalidOperationException) { }
        _b.Wait();
        _count++;
    }

    public void TimedLenient()
    {
        if (!_a.Wait(50) && _strict) throw new TimeoutException();
        _b.Wait();
        _count++;
    }

    public void TimedLenientLocal()
    {
        var got = _a.Wait(50);
        if (!got && _strict) return;
        _b.Wait();
        _count++;
        if (got) _a.Release();
    }

    public void RetryUntilStopped()
    {
        while (!_a.Wait(50))
        {
            if (_stop) break;
        }
        _b.Wait();
        _count++;
    }

    public void TimedStrict()
    {
        if (!_a.Wait(50)) throw new TimeoutException();
        _b.Wait();
        _count++;
    }

    public void Retry()
    {
        while (!_a.Wait(TimeSpan.FromSeconds(2))) Log("still waiting");
    }

    public void RetryInWorker()
    {
        while (!_stop)
        {
            while (!_a.Wait(TimeSpan.FromSeconds(2))) Log("still waiting");
            _count++;
        }
    }

    public void ThroughProperty()
    {
        PickLock.Wait();
        try { _count++; }
        finally { PickLock.Release(); }
    }

    public void ThroughIndexer(int id)
    {
        _locks[id].Wait();
        try { _count++; }
        finally { _locks[id].Release(); }
    }

    public async Task ReentryAsync()
    {
        await _a.WaitAsync();
        try { await Task.Yield(); }
        finally { _a.Release(); }
    }

    public async Task PumpAsync()
    {
        await _a.WaitAsync();
        var setup = true;
        while (!_stop)
        {
            if (!setup) await _a.WaitAsync();
            try { _count++; }
            finally { _a.Release(); }
            if (setup) { setup = false; await Task.Yield(); }
        }
    }

    public async Task ExportAsync()
    {
        await _a.WaitAsync();
        try { _count++; }
        finally { UnlockA(); }
        await Task.Yield();
        await _b.WaitAsync();
        _b.Release();
    }

    public async Task CatchSkipAsync()
    {
        try
        {
            Validate();
            await _a.WaitAsync();
        }
        catch (InvalidOperationException) { }
        await _b.WaitAsync();
        _b.Release();
    }

    public async Task RetryAsync()
    {
        while (!await _a.WaitAsync(TimeSpan.FromSeconds(2))) Log("still waiting");
    }

    public async Task TimedReentryAsync()
    {
        if (!await _a.WaitAsync(TimeSpan.FromMinutes(10))) throw new TimeoutException();
        try { await Task.Yield(); }
        finally { _a.Release(); }
    }

    public void ReleaseThroughExtension()
    {
        _a.Wait();
        try { _count++; }
        finally { _a.ReleaseQuietly(); }
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughOtherClass()
    {
        _a.Wait();
        try { _count++; }
        finally { Gates.Release(_a); }
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughReleaser()
    {
        _a.Wait();
        using (new Releaser(_a)) _count++;
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughCopy()
    {
        _a.Wait();
        var gate = _a;
        try { _count++; }
        finally { gate.Release(); }
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughHelperOfHelper()
    {
        _a.Wait();
        try { _count++; }
        finally { HandOnA(); }
        _b.Wait();
        _count++;
    }

    public void ReadsCountInBetween()
    {
        _a.Wait();
        if (_a.CurrentCount > 0) Log("odd");
        _b.Wait();
        _count++;
    }

    public void HandsOnTheOtherOne()
    {
        _a.Wait();
        try
        {
            Gates.Look(_b);
            _b.Wait();
            try { _count++; }
            finally { _b.Release(); }
        }
        finally { Gates.Release(_a); }
    }

    public void ReleaseThroughLambda()
    {
        _a.Wait();
        try { _count++; }
        finally { Gates.Run(() => _a.Release()); }
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughClosure(int added)
    {
        _a.Wait();
        try { _count++; }
        finally { Gates.Run(() => { _count += added; _a.Release(); }); }
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughNestedClass()
    {
        _a.Wait();
        try { _count++; }
        finally { new Unlocker(this).UnlockA(); }
        _b.Wait();
        _count++;
    }

    public void LambdaThatOnlyCounts()
    {
        _a.Wait();
        Gates.Run(() => _count++);
        _b.Wait();
        _count++;
    }

    public void ReleaseThroughAsyncMethod()
    {
        _a.Wait();
        try { _count++; }
        finally { UnlockALaterAsync().Wait(); }
        _b.Wait();
        _count++;
    }

    public async Task ReleaseThroughAwaitedMethodAsync()
    {
        await _a.WaitAsync();
        _count++;
        await UnlockALaterAsync();
        await _b.WaitAsync();
        _b.Release();
    }

    public void ReleaseThroughAsyncLambda()
    {
        _a.Wait();
        try { _count++; }
        finally { Gates.RunAsync(async () => { await Task.Yield(); _a.Release(); }).Wait(); }
        _b.Wait();
        _count++;
    }

    public async Task CountsThroughAwaitedMethodAsync()
    {
        await _a.WaitAsync();
        await CountLaterAsync();
        await _b.WaitAsync();
        _b.Release();
    }

    public void PassTo(StockRoom to)
    {
        _a.Wait();
        to._a.Wait();
        to._count++;
        to._a.Release();
    }

    public void PassThenLog(StockRoom to)
    {
        _a.Wait();
        to._a.Wait();
        Log(ToString()!);
    }

    public void SaveThenHandOff()
    {
        _a.Wait();
        _count++;
        CommitAndRelease();
    }

    private void CommitAndRelease()
    {
        _a.Release();
        _b.Wait();
        try { _count++; }
        finally { _b.Release(); }
    }

    public async Task OuterAsync()
    {
        await _a.WaitAsync();
        try { await InnerAsync(); }
        finally { _a.Release(); }
    }

    private async Task InnerAsync()
    {
        await _a.WaitAsync();
        try { _count++; }
        finally { _a.Release(); }
    }

    public async Task OuterThroughWrapperAsync()
    {
        await _a.WaitAsync();
        _count++;
        await HandBackThenRetake();
    }

    private Task HandBackThenRetake()
    {
        _a.Release();
        return InnerAsync();
    }

    public async Task OuterThroughOverloadAsync()
    {
        await _a.WaitAsync();
        try { await InnerOverload(); }
        finally { _a.Release(); }
    }

    private Task InnerOverload() => InnerAsync();

    public async Task OuterThroughTwoCallsAsync()
    {
        await _a.WaitAsync();
        try { await InnerTwice(); }
        finally { _a.Release(); }
    }

    private Task InnerTwice()
    {
        InnerAsync().Wait();
        return InnerAsync();
    }

    public async Task OuterThroughLoopingWrapperAsync()
    {
        await _a.WaitAsync();
        try { await InnerOfMany(); }
        finally { _a.Release(); }
    }

    private Task InnerOfMany()
    {
        var last = Task.CompletedTask;
        for (var i = 0; i < 2; i++) last = InnerAsync();
        return last;
    }

    public async Task OuterAwaitsInLoopAsync()
    {
        await _a.WaitAsync();
        for (var turn = 0; turn < 2; turn++) await InnerAsync();
    }

    public async Task OuterTakesInLoopAsync()
    {
        for (var turn = 0; turn < 2; turn++)
        {
            await _a.WaitAsync();
            try { await InnerAsync(); }
            finally { _a.Release(); }
        }
    }

    public async Task TakeOrGiveUpAsync()
    {
        var entered = _a.WaitAsync();
        if (await Task.WhenAny(entered, Task.Delay(50)) != entered)
        {
            await InnerAsync();
            return;
        }
        try { _count++; }
        finally { _a.Release(); }
    }

    public async Task TakeLaterAsync()
    {
        var entered = _a.WaitAsync();
        await InnerAsync();
        await entered;
        try { _count++; }
        finally { _a.Release(); }
    }

    public async Task TakeThenContinueAsync()
    {
        _ = _a.WaitAsync().ContinueWith(_ => _count++);
        await InnerAsync();
    }

    public async Task TimedTakeLaterAsync()
    {
        var entered = _a.WaitAsync(100);
        await CountLaterAsync();
        if (!await entered) return;
        try { await InnerAsync(); }
        finally { _a.Release(); }
    }

    public async Task TakeConfiguredAsync()
    {
        await _a.WaitAsync().ConfigureAwait(false);
        try { await InnerAsync().ConfigureAwait(false); }
        finally { _a.Release(); }
    }

    public async Task TakeBlockingAsync()
    {
        _a.WaitAsync().Wait();
        try { await InnerAsync(); }
        finally { _a.Release(); }
    }

    public async Task TakeGetResultAsync()
    {
        _a.WaitAsync().GetAwaiter().GetResult();
        try { await InnerAsync(); }
        finally { _a.Release(); }
    }

    public async Task TimedTakeConfiguredAsync()
    {
        if (!await _a.WaitAsync(100).ConfigureAwait(false)) return;
        try { await InnerAsync().ConfigureAwait(false); }
        finally { _a.Release(); }
    }

    private async Task UnlockALaterAsync()
    {
        await Task.Yield();
        _a.Release();
    }

    private async Task CountLaterAsync()
    {
        await Task.Yield();
        _count++;
    }

    private void HandOnA() => Gates.Release(_a);

    // ---- a scope object whose constructor gives the lock back

    public void ReleaseThroughConstructor()
    {
        _a.Wait();
        try
        {
            using (new Unlocked(this))
            {
                _b.Wait();
                _count++;
            }
        }
        finally { _a.Release(); }
    }

    public void CountThroughConstructor()
    {
        _a.Wait();
        try
        {
            using (new Counted(this))
            {
                _b.Wait();
                _count++;
            }
        }
        finally { _a.Release(); }
    }

    public void ReleaseThroughReturnedScope()
    {
        _a.Wait();
        try
        {
            using (Unlock())
            {
                _b.Wait();
                _count++;
            }
        }
        finally { _a.Release(); }
    }

    private IDisposable Unlock() => new Unlocked(this);

    private sealed class Unlocked : IDisposable
    {
        private readonly StockRoom _room;

        public Unlocked(StockRoom room)
        {
            _room = room;
            room._a.Release();
        }

        public void Dispose() => _room._a.Wait();
    }

    private sealed class Counted : IDisposable
    {
        private readonly StockRoom _room;

        public Counted(StockRoom room)
        {
            _room = room;
            room._count++;
        }

        public void Dispose() => _room._count--;
    }

    // ---- `this` handed to code outside the class

    internal static readonly SemaphoreSlim Shared = new(1, 1);

    public event EventHandler? Changed;

    public void HandsItselfToAHelper()
    {
        _a.Wait();
        _count++;
        Porter.Finish(this);
        _b.Wait();
        _count++;
    }

    public void ShowsItselfToAHelper()
    {
        _a.Wait();
        _count++;
        Porter.Note(this);
        _b.Wait();
        _count++;
    }

    public void RaisesAnEvent()
    {
        _a.Wait();
        Changed?.Invoke(this, EventArgs.Empty);
        _b.Wait();
        _count++;
    }

    public void LocksOnItself()
    {
        _a.Wait();
        lock (this) { _count++; }
        Monitor.Enter(this);
        Monitor.Exit(this);
        var count = new Action(Count);
        count();
        _b.Wait();
        _count++;
    }

    public void HandsItselfOnThroughAHelper()
    {
        _a.Wait();
        Finish();
        _b.Wait();
        _count++;
    }

    private void Finish() => Porter.Finish(this);

    public async Task HandsItselfToAHelperAsync()
    {
        await _a.WaitAsync();
        _count++;
        await Porter.FinishAsync(this);
        await _b.WaitAsync();
        _count++;
    }

    public void SharedThenB()
    {
        Shared.Wait();
        Porter.ReleaseShared();
        _b.Wait();
        _count++;
    }

    public void SharedThenIdle()
    {
        Shared.Wait();
        Porter.Idle();
        _b.Wait();
        _count++;
    }

    internal void GiveBackA() => _a.Release();

    internal void Touch() => _count++;

    internal void PackStep()
    {
        _b.Wait();
        try { _count++; }
        finally { _b.Release(); }
    }

    // ---- the field is given another semaphore after the take

    private SemaphoreSlim _swapped = new(1, 1);

    public void SwapsTheLock()
    {
        _swapped.Wait();
        _swapped = new SemaphoreSlim(1, 1);
        _b.Wait();
        _count++;
    }

    public void SwapsTheLockInAHelper()
    {
        _swapped.Wait();
        Reset();
        _b.Wait();
        _count++;
    }

    private void Reset() => _swapped = new SemaphoreSlim(1, 1);

    // ---- a release before a throw that a catch block carries on from

    public void ReleaseThenThrowInRetry()
    {
        _a.Wait();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    _b.Wait();
                    try { _count++; }
                    finally { _b.Release(); }
                    return;
                }
                _count++;
                _a.Release();
                throw new TimeoutException();
            }
            catch (TimeoutException) { }
        }
    }

    public void ThrowInRetry()
    {
        _a.Wait();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    _b.Wait();
                    try { _count++; }
                    finally { _b.Release(); }
                    return;
                }
                _count++;
                throw new TimeoutException();
            }
            catch (TimeoutException) { }
        }
    }

    public void ReleaseInFinallyOfAFailedTry()
    {
        _a.Wait();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    _b.Wait();
                    _count++;
                    return;
                }
                try
                {
                    _count++;
                    throw new TimeoutException();
                }
                finally { _a.Release(); }
            }
            catch (TimeoutException) { }
        }
    }

    private sealed class Unlocker(StockRoom room)
    {
        public void UnlockA() => room._a.Release();
    }

    private void UnlockA() => _a.Release();

    private void Count() => _count++;

    private static void Let(SemaphoreSlim semaphore) => semaphore.Release();

    private void Validate()
    {
        if (_count < 0) throw new InvalidOperationException();
    }

    private static void Log(string line) => GC.KeepAlive(line);
}

/// Code outside StockRoom that is handed a room, or reaches its static semaphore.
internal static class Porter
{
    public static void Finish(StockRoom room) => room.GiveBackA();

    public static void Note(StockRoom room) => room.Touch();

    public static async Task FinishAsync(StockRoom room)
    {
        await Task.Yield();
        room.GiveBackA();
    }

    public static void ReleaseShared() => StockRoom.Shared.Release();

    public static void Idle() => System.Threading.Thread.Yield();

    public static void Deliver(StockRoom room)
    {
        room.GiveBackA();
        room.PackStep();
    }

    public static void DeliverHeld(StockRoom room)
    {
        room.Touch();
        room.PackStep();
    }

    public static void DeliverThenGiveBack(StockRoom room)
    {
        room.PackStep();
        room.GiveBackA();
    }
}

/// A generic class whose calls into itself are references to its generic instantiation.
internal sealed class Shelf<T>
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _last;

    public T? Last => _last;

    public void Take(T item)
    {
        _gate.Wait();
        _last = item;
        Unlock();
    }

    private void Unlock() => _gate.Release();
}

/// Throttles made in the ways a class sizes them.
internal sealed class Throttles
{
    private readonly int _size = 3;
    private readonly SemaphoreSlim _jobs, _cores, _sized, _double, _paused, _pausedOne, _closed, _bounded;

    public Throttles(int maxJobs, bool paused)
    {
        _jobs = new SemaphoreSlim(maxJobs);
        _cores = new SemaphoreSlim(Environment.ProcessorCount);
        _sized = new SemaphoreSlim(_size);
        _double = new SemaphoreSlim(maxJobs, maxJobs * 2);
        _paused = new SemaphoreSlim(paused ? 0 : 1, 1);
        _pausedOne = new SemaphoreSlim(paused ? 0 : 1);
        _closed = new SemaphoreSlim(0);
        _bounded = new SemaphoreSlim(1, maxJobs);
    }

    public int Free => _jobs.CurrentCount + _cores.CurrentCount + _sized.CurrentCount + _double.CurrentCount + _paused.CurrentCount + _pausedOne.CurrentCount
                       + _closed.CurrentCount + _bounded.CurrentCount;
}

/// Code of another class that is handed a semaphore.
internal static class Gates
{
    public static void ReleaseQuietly(this SemaphoreSlim semaphore) => semaphore.Release();

    public static void Release(SemaphoreSlim semaphore) => semaphore.Release();

    public static void Look(SemaphoreSlim semaphore) => GC.KeepAlive(semaphore);

    public static void Run(Action action) => action();

    public static Task RunAsync(Func<Task> action) => action();
}

internal sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
{
    public void Dispose() => semaphore.Release();
}
