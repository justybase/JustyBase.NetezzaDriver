using JustyBase.NetezzaDriver.TestSupport;
using System.Data;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Exercises the typed ADO.NET getter surface and its invalid-cast guards
/// against a real captured binary result set.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataReaderSurfaceTests
{
    [Fact]
    public async Task TypedGetters_ReturnValuesAndRejectWrongTypes()
    {
        WireFixture wire = WireFixture.LoadShipped("dbos_types.bin");
        NzReplayFixture startup = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        var fixture = new NzReplayFixture
        {
            Query = wire.Records[^1].Sql,
            ExpectedRows = 2,
            ExpectedColumns = 11,
            Segments = startup.Segments
                .Take(8)
                .Concat(wire.Records.Select(record => record.Response))
                .ToArray(),
        };

        await using var server = NzReplayServer.Start(fixture);
        using var connection = Open(server.Port);
        for (int i = 0; i < wire.Records.Count - 1; i++)
        {
            using var setup = connection.CreateCommand(wire.Records[i].Sql);
            setup.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand(wire.Records[^1].Sql);
        using var reader = (NzDataReader)command.ExecuteReader();

        Assert.Equal(0, reader.Depth);
        Assert.True(reader.HasRows);
        Assert.False(reader.IsClosed);
        Assert.Equal(11, reader.FieldCount);
        Assert.Equal("I", reader.GetName(0));
        Assert.Equal("INT32", reader.GetDataTypeName(0).ToUpperInvariant());
        Assert.Equal(0, reader.GetOrdinal("I"));

        Assert.True(reader.Read());
        Assert.Equal(-2147483648, reader.GetInt32(0));
        Assert.Equal("Zażółć gęślą jaźń", reader.GetString(1));
        Assert.Equal(3.1400m, reader.GetDecimal(2));
        Assert.Equal(new DateTime(2024, 2, 29), reader.GetDateTime(3));
        Assert.Equal(new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(1_234_560), reader.GetDateTime(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal(1.5, reader.GetDouble(6));
        Assert.Equal(new TimeSpan(12, 34, 56), reader.GetFieldValue<TimeSpan>(7));
        Assert.Equal(-9223372036854775807, reader.GetInt64(8));
        Assert.Equal((short)-32768, reader.GetInt16(9));
        Assert.Equal(-2147483648, reader.GetFieldValue<int>(0));
        Assert.Equal("Zażółć gęślą jaźń", reader.GetFieldValue<string>(1));

        Assert.Equal(-2147483648, reader[0]);
        Assert.Equal(-2147483648, reader["I"]);
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(23, reader.GetProviderType(0));
        Assert.Equal("INTEGER", reader.GetDeclaredTypeName(0));

        Assert.Throws<InvalidCastException>(() => reader.GetBoolean(0));
        Assert.Throws<InvalidCastException>(() => reader.GetByte(0));
        Assert.Throws<InvalidCastException>(() => reader.GetChar(1));
        Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
        Assert.Throws<InvalidCastException>(() => reader.GetDecimal(0));
        Assert.Throws<InvalidCastException>(() => reader.GetDouble(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFloat(0));
        Assert.Throws<InvalidCastException>(() => reader.GetGuid(1));
        Assert.Throws<InvalidCastException>(() => reader.GetInt16(0));
        Assert.Throws<InvalidCastException>(() => reader.GetInt32(1));
        Assert.Throws<InvalidCastException>(() => reader.GetInt64(0));
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("MISSING"));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetName(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetValue(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetFieldValue<int>(99));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetFieldValue<int>("MISSING"));

        Assert.True(reader.Read());
        Assert.False(reader.Read());
        Assert.False(reader.NextResult());
        Assert.Equal(0, reader.Depth);
    }

    [Fact]
    public async Task ReadAsyncCloseAsyncAndEmptyGetterGuards_AreConsistent()
    {
        WireFixture wire = WireFixture.LoadShipped("text_types.bin");
        NzReplayFixture startup = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        var fixture = new NzReplayFixture
        {
            Query = wire.Records[0].Sql,
            ExpectedRows = 1,
            ExpectedColumns = 8,
            Segments = startup.Segments
                .Take(8)
                .Concat(wire.Records.Select(record => record.Response))
                .ToArray(),
        };

        await using var server = NzReplayServer.Start(fixture);
        using var connection = Open(server.Port);
        using var command = connection.CreateCommand(wire.Records[0].Sql);
        using var reader = command.ExecuteReader();

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(await reader.NextResultAsync(TestContext.Current.CancellationToken));
        await reader.CloseAsync();
        Assert.True(reader.IsClosed);
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        Assert.False(reader.Read());

        Assert.Equal(-1, reader.RecordsAffected);
        Assert.Equal(8, reader.FieldCount);
    }

    private static NzConnection Open(int port)
    {
        var connection = new NzConnection(
            "surface",
            "surface",
            "127.0.0.1",
            "JUST_DATA",
            port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
            ConnectionTimeoutDuration = TimeSpan.FromSeconds(5),
        };
        connection.Open();
        return connection;
    }
}
