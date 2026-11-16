using System.Globalization;
using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// The sentences are built in the library, so their numbers must not follow the machine's language settings
/// there either: a program that calls HangAnalyzer itself gets the same text as the command line.
public class InvariantNumbersTests
{
    /// Runs with a culture that groups digits with '.' and writes decimals with ',', whatever the machine's ICU data says.
    private static T InGerman<T>(Func<T> act)
    {
        var before = CultureInfo.CurrentCulture;
        var german = (CultureInfo)new CultureInfo("en-US").Clone();
        german.NumberFormat.NumberGroupSeparator = ".";
        german.NumberFormat.NumberDecimalSeparator = ",";
        CultureInfo.CurrentCulture = german;
        try
        {
            return act();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Row_counts_read_the_same_in_every_culture()
    {
        var thread = Thread(10, [Fx("Npgsql.NpgsqlDataReader", "Read"), App("MyApp.ReportCache", "Refresh", "Cache.cs", 30)]) with
        {
            Database = new DatabaseActivity
            {
                Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ReadingRows, CommandText = "SELECT id FROM page_events", RowsRead = 1234567,
                Connections = [new DatabaseConnection(100, false)],
            },
        };

        Assert.Equal("reading the rows of `SELECT id FROM page_events` from PostgreSQL (1,234,567 rows so far) in Cache.cs:30", InGerman(() => Describe.Activity(thread)));
    }

    [Theory]
    [InlineData(90, "90 seconds")]
    [InlineData(100, "100 seconds")]
    [InlineData(150, "2.5 minutes")]
    [InlineData(30, "30 seconds")]
    [InlineData(2.5, "2.5 seconds")]
    public void Http_timeouts_read_the_same_in_every_culture(double seconds, string expected)
    {
        var reading = new TaskInfo { Address = 0x40, TypeName = "Box<System.Net.Http.HttpConnection+<InitialFillAsync>d>", Kind = TaskKind.AsyncMethod, Description = "HttpConnection.InitialFillAsync" };
        var pool = new TaskInfo { Address = 0x30, TypeName = "Box<System.Net.Http.HttpConnectionPool+<SendAsync>d>", Kind = TaskKind.AsyncMethod, Description = "HttpConnectionPool.SendAsync", Awaiting = reading };
        var client = new TaskInfo
        {
            Address = 0x20, TypeName = "Box<System.Net.Http.HttpClient+<SendAsync>d>", Kind = TaskKind.AsyncMethod, Description = "HttpClient.SendAsync",
            Http = new HttpCall("GET", "http://api/reviews/1", TimeSpan.FromSeconds(seconds), false), Awaiting = pool,
        };
        var page = new TaskInfo
        {
            Address = 0x10, TypeName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox<MyApp.Catalog+<GetPageAsync>d__2>",
            Kind = TaskKind.AsyncMethod, Description = "Catalog.GetPageAsync", Awaiting = client,
        };
        var snapshot = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = page });

        var f = InGerman(() => Assert.Single(new HttpDetector().Detect(snapshot, new HashSet<int>())));
        Assert.Equal("http-wait", f.Id);
        Assert.Contains($"The HttpClient's timeout is {expected}.", f.Summary);
    }
}
