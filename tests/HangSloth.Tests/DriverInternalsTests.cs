using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;

namespace HangSloth.Tests;

/// Driver internals read under more than one name (fields renamed between driver versions), the warning
/// when none of them is there, and the pool frames of older drivers.
public class DriverInternalsTests
{
    private static AlternativeNames.TryRead<int> Fields(Dictionary<string, int> fields) =>
        (string name, out int value) => fields.TryGetValue(name, out value);

    [Fact]
    public void The_first_name_is_read_when_it_is_there()
    {
        var name = AlternativeNames.First(["<MaxConnections>k__BackingField", "_max"], Fields(new() { ["<MaxConnections>k__BackingField"] = 3, ["_max"] = 9 }), out var value);

        Assert.Equal("<MaxConnections>k__BackingField", name);
        Assert.Equal(3, value);
    }

    [Fact]
    public void An_older_name_is_read_when_the_newer_one_is_missing()
    {
        var name = AlternativeNames.First(["<MaxConnections>k__BackingField", "_max"], Fields(new() { ["_max"] = 3 }), out var value);

        Assert.Equal("_max", name);
        Assert.Equal(3, value);
    }

    [Fact]
    public void No_name_read_gives_null_and_the_default_value()
    {
        var name = AlternativeNames.First(["<MaxConnections>k__BackingField", "_max"], Fields(new() { ["_maxPoolSize"] = 3 }), out var value);

        Assert.Null(name);
        Assert.Equal(0, value);
    }

    [Fact]
    public void A_reader_that_throws_counts_as_a_missing_name()
    {
        AlternativeNames.TryRead<int> read = (string name, out int value) =>
        {
            if (name == "_dataSource") throw new InvalidOperationException("odd field");
            value = 7;
            return true;
        };

        Assert.Equal("_pool", AlternativeNames.First(["_dataSource", "_pool"], read, out var value));
        Assert.Equal(7, value);
        Assert.Null(AlternativeNames.First(["_dataSource"], read, out _));
    }

    [Fact]
    public void Each_old_Npgsql_name_is_listed_after_the_current_one()
    {
        Assert.Equal(["Npgsql.PoolingDataSource", "Npgsql.ConnectorPool"], SnapshotBuilder.NpgsqlPoolTypes);
        Assert.Equal(["<MaxConnections>k__BackingField", "_max"], SnapshotBuilder.NpgsqlPoolMax);
        Assert.Equal(["_dataSource", "_pool"], SnapshotBuilder.NpgsqlConnectionPool);
        Assert.Equal(["<InternalConnection>k__BackingField", "_connection"], SnapshotBuilder.NpgsqlCommandConnection);
        Assert.Equal(["Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "Microsoft.Data.ProviderBase.DbConnectionPool"], SnapshotBuilder.SqlClientPoolTypes);
    }

    [Fact]
    public void The_warning_names_the_driver_version_and_what_was_not_recognised()
    {
        Assert.Equal("Npgsql 4.1.9: connection pool internals not recognised, so pool exhaustion cannot be detected.",
            SnapshotBuilder.UnrecognisedWarning("Npgsql 4.1.9", ["connection pool internals"]));
        Assert.Equal("Npgsql 12.0.0: connection internals and connection pool internals not recognised, so pool exhaustion cannot be detected.",
            SnapshotBuilder.UnrecognisedWarning("Npgsql 12.0.0", ["connection internals", "connection pool internals"]));
    }

    [Fact]
    public void The_warning_is_printed_with_the_report()
    {
        var warning = SnapshotBuilder.UnrecognisedWarning("Npgsql 4.1.9", ["connection pool internals"]);
        var report = new HangAnalyzer().Analyze(SnapshotFactory.Process() with { Warnings = [warning] });

        using var writer = new StringWriter();
        HangSloth.Reporting.TextReporter.Write(report, writer);
        Assert.Contains("note: " + warning, writer.ToString());
    }

    [Theory]
    [InlineData("Npgsql.ConnectorPool", "Get")] // Npgsql 6
    [InlineData("Npgsql.ConnectorPool", "Rent")] // Npgsql 5
    [InlineData("Npgsql.PoolingDataSource", "Get")] // Npgsql 7 and later
    [InlineData("Microsoft.Data.ProviderBase.DbConnectionPool", "TryGetConnection")] // SqlClient 5
    [InlineData("Microsoft.Data.SqlClient.ConnectionPool.WaitHandleDbConnectionPool", "TryGetConnection")] // SqlClient 6 and later
    public void Old_and_new_pool_frames_are_pool_waits(string type, string method)
    {
        Assert.True(FrameClassifier.IsConnectionPoolFrame(new StackFrameInfo { TypeName = type, MethodName = method }));
        Assert.Equal(BlockingKind.ConnectionPool, FrameClassifier.Classify(
        [
            new StackFrameInfo { TypeName = "System.Threading.ManualResetEventSlim", MethodName = "Wait" },
            new StackFrameInfo { TypeName = type, MethodName = method },
            new StackFrameInfo { TypeName = "Shop.Orders", MethodName = "Save" },
        ]));
    }

    [Theory]
    [InlineData("Microsoft.Data.ProviderBase.DbConnectionFactory", "TryGetConnection")]
    [InlineData("Npgsql.ConnectorPool", "Return")]
    [InlineData("Shop.ConnectorPool", "Get")]
    public void Other_driver_frames_are_not_pool_waits(string type, string method) =>
        Assert.False(FrameClassifier.IsConnectionPoolFrame(new StackFrameInfo { TypeName = type, MethodName = method }));
}
