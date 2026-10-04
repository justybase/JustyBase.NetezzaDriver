using JustyBase.NetezzaDriver.TestSupport;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Replays a byte-exact recorded server response through the real driver over
/// loopback, with no database. Validates that the fixture is usable and that the
/// client parses the recorded result set correctly.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NzReplayTests
{
    [Fact]
    public async Task Replay_DimDate_ReadsExpectedRowsAndColumns()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        using var command = connection.CreateCommand(fixture.Query);
        using var reader = command.ExecuteReader();

        Assert.Equal(fixture.ExpectedColumns, reader.FieldCount);

        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }

        Assert.Equal(fixture.ExpectedRows, rows);
    }

    [Fact]
    public async Task Replay_RepeatedQueries_OnSingleConnection_StayInSync()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        for (int iteration = 0; iteration < 3; iteration++)
        {
            using var command = connection.CreateCommand(fixture.Query);
            using var reader = command.ExecuteReader();

            int rows = 0;
            while (reader.Read())
            {
                rows++;
            }

            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_DimDate_LazyDecoding_MatchesEagerDecoding()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);

        using var eagerConnection = OpenConnection(server.Port);
        var eager = ReadAll(eagerConnection, fixture.Query, lazy: false);

        using var lazyConnection = OpenConnection(server.Port);
        var lazy = ReadAll(lazyConnection, fixture.Query, lazy: true);

        Assert.Equal(fixture.ExpectedRows, eager.Count);
        Assert.Equal(eager.Count, lazy.Count);
        for (int row = 0; row < eager.Count; row++)
        {
            Assert.Equal(eager[row].Length, lazy[row].Length);
            for (int col = 0; col < eager[row].Length; col++)
            {
                Assert.Equal(eager[row][col], lazy[row][col]);
            }
        }
    }

    private static List<object?[]> ReadAll(NzConnection connection, string query, bool lazy)
    {
        connection.UseLazyColumnDecoding = lazy;
        using var command = connection.CreateCommand(query);
        using var reader = command.ExecuteReader();

        int fields = reader.FieldCount;
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[fields];
            for (int i = 0; i < fields; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    private static NzReplayServer StartServer(NzReplayFixture fixture) => NzReplayServer.Start(fixture);

    private static NzConnection OpenConnection(int port)
    {
        var connection = new NzConnection(
            "replay",
            "replay",
            "127.0.0.1",
            "JUST_DATA",
            port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
        };
        connection.Open();
        return connection;
    }
}
