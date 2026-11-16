using HangSloth.Model;

namespace HangSloth.Analysis;

/// Who a sentence is about: one thread ("Thread 5 is") or several in the same wait ("30 threads (...) are").
internal readonly record struct Subject(string Who, bool Plural)
{
    public string Is => Plural ? "are" : "is";

    public static Subject Of(IReadOnlyList<ThreadSnapshot> threads) => new(Describe.Many(threads), threads.Count > 1);
}

/// Threads that wait in the same way at the same place get one finding that says how many they are, instead of one
/// identical finding each. "The same" is strict: the finding reads the same word for word apart from who it is about
/// (same id and severity, the same call at the same line, the same chain, statement or host), and the detector's key
/// says it is the same object where the words can't (two semaphores with one name).
internal static class SameWait
{
    private static readonly Subject Placeholder = new("\u0001", true);

    /// <param name="key">What the words don't show and must be the same too, such as the address of the object waited for.</param>
    /// <param name="make">The finding, worded for its subject; the ThreadIds are filled in here.</param>
    public static IEnumerable<Finding> Group<T>(IEnumerable<T> waits, Func<T, ThreadSnapshot> thread, Func<T, string> key, Func<T, Subject, Finding> make)
    {
        foreach (var group in waits.GroupBy(w => $"{key(w)}\u0002{Text(make(w, Placeholder))}"))
        {
            var members = group.ToList();
            var threads = members.Select(thread).ToList();
            yield return make(members[0], Subject.Of(threads)) with { ThreadIds = threads.Select(t => t.ManagedId).ToList() };
        }
    }

    /// One sentence for each group of threads whose sentences read the same apart from who they are about.
    public static List<string> Lines<T>(IEnumerable<T> items, Func<T, ThreadSnapshot> thread, Func<T, Subject, string> line) =>
        Lines(items, thread, item => line(item, Placeholder), line);

    /// <param name="key">What makes two sentences the same: their words without the subject, or less than that when a
    /// detail only fits one thread (a session id).</param>
    /// <param name="line">The sentence for a group, from its first member.</param>
    public static List<string> Lines<T>(IEnumerable<T> items, Func<T, ThreadSnapshot> thread, Func<T, string> key, Func<T, Subject, string> line) =>
        items.GroupBy(key).Select(g => g.ToList()).Select(g => line(g[0], Subject.Of(g.Select(thread).ToList()))).ToList();

    private static string Text(Finding f) =>
        string.Join("\u0002", [f.Id, f.Severity.ToString(), f.Title, f.Summary, .. f.Details, f.Advice ?? "", f.Location?.ToString() ?? ""]);
}
