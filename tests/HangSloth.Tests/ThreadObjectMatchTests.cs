using HangSloth.Collection;

namespace HangSloth.Tests;

/// Which System.Threading.Thread object belongs to which live thread (SnapshotBuilder.MatchThreadObjects).
public class ThreadObjectMatchTests
{
    private static readonly Dictionary<ulong, int> Live = new() { [0x7000] = 10, [0x7100] = 11 };

    [Fact]
    public void A_live_thread_gets_the_object_that_points_at_it_not_an_ended_thread_with_its_reused_id()
    {
        // The ended thread's object comes later, which is the one a match by id kept.
        var match = SnapshotBuilder.MatchThreadObjects([("live-worker-0", 10, 0x7000UL), ("old-import-2-6", 10, 0UL)], Live)!;
        Assert.Equal("live-worker-0", match[10]);
        Assert.Single(match);
    }

    [Fact]
    public void An_object_that_points_at_no_live_thread_is_ignored()
    {
        var match = SnapshotBuilder.MatchThreadObjects([("orders", 11, 0x7100UL), ("gone", 12, 0x9000UL)], Live)!;
        Assert.Equal(new Dictionary<int, string> { [11] = "orders" }, match);
    }

    [Fact]
    public void The_live_thread_id_is_used_even_when_the_object_says_otherwise()
    {
        Assert.Equal(new Dictionary<int, string> { [11] = "invoices" }, SnapshotBuilder.MatchThreadObjects([("invoices", 99, 0x7100UL)], Live));
        // Or when the object's id can't be read at all.
        Assert.Equal(new Dictionary<int, string> { [11] = "invoices" }, SnapshotBuilder.MatchThreadObjects([("invoices", null, 0x7100UL)], Live));
    }

    [Fact]
    public void Of_two_objects_on_one_thread_the_one_with_its_managed_id_wins()
    {
        Assert.Equal("mail-queue", SnapshotBuilder.MatchThreadObjects([("stale", 4, 0x7000UL), ("mail-queue", 10, 0x7000UL)], Live)![10]);
        Assert.Equal("mail-queue", SnapshotBuilder.MatchThreadObjects([("mail-queue", 10, 0x7000UL), ("stale", 4, 0x7000UL)], Live)![10]);
    }

    [Fact]
    public void An_object_whose_native_thread_cannot_be_read_is_skipped_and_the_others_still_match()
    {
        var match = SnapshotBuilder.MatchThreadObjects([("orders", 10, 0x7000UL), ("damaged", 11, null)], Live);
        Assert.Equal(new Dictionary<int, string> { [10] = "orders" }, match);
    }

    [Fact]
    public void Without_any_object_that_points_at_a_live_thread_the_heap_is_asked_instead()
    {
        Assert.Null(SnapshotBuilder.MatchThreadObjects(new List<(string, int?, ulong?)>(), Live));
        Assert.Null(SnapshotBuilder.MatchThreadObjects([("orders", 10, 0x5000UL), ("ended", 11, 0UL), ("damaged", 12, null)], Live));
    }

    [Fact]
    public void Threads_that_could_not_be_read_completely_get_one_note()
    {
        Assert.Equal("1 thread could not be read completely from the dump (Thread 5); the report shows what could be read of it.",
            SnapshotBuilder.IncompleteThreadsWarning([5]));
        Assert.Equal("2 threads could not be read completely from the dump (Thread 5, Thread 9); the report shows what could be read of them.",
            SnapshotBuilder.IncompleteThreadsWarning([5, 9]));
    }
}
