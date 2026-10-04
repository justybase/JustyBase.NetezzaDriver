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

    [Fact]
    public async Task LargeCommandText_DoesNotGrowRetainedTmpBuffer()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        int initialCapacity = connection.TmpBufferCapacity;
        string largeQuery = "SELECT 1 /* " + new string('x', 256 * 1024) + " */";

        using (var command = connection.CreateCommand(largeQuery))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
            }
        }

        // The 256 KB command must not be pinned as the retained scratch buffer.
        Assert.True(connection.TmpBufferCapacity <= NzConnection.TmpBufferRetainCap);
        Assert.True(initialCapacity <= NzConnection.TmpBufferRetainCap);
    }

    [Fact]
    public async Task Replay_EarlyDispose_AfterOneRow_ConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        connection.DiscardedRows = 0;
        using (var command = connection.CreateCommand(fixture.Query))
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            // Dispose without consuming the rest: must drain via discard path.
        }

        Assert.True(connection.DiscardedRows > 0);

        // Connection must be immediately reusable with correct results.
        using (var command2 = connection.CreateCommand(fixture.Query))
        using (var reader2 = command2.ExecuteReader())
        {
            int rows = 0;
            while (reader2.Read())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_EarlyDispose_AfterTenRows_ConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        using (var command = connection.CreateCommand(fixture.Query))
        using (var reader = command.ExecuteReader())
        {
            for (int i = 0; i < 10; i++)
                Assert.True(reader.Read());
        }

        using (var command2 = connection.CreateCommand(fixture.Query))
        using (var reader2 = command2.ExecuteReader())
        {
            int rows = 0;
            while (reader2.Read())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_SingleRow_ReturnsFirstRow_AndConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        object? firstViaFullRead;
        using (var full = connection.CreateCommand(fixture.Query))
        using (var r = full.ExecuteReader())
        {
            Assert.True(r.Read());
            firstViaFullRead = r.GetValue(0);
        }

        using (var single = connection.CreateCommand(fixture.Query))
        using (var r = single.ExecuteReader(System.Data.CommandBehavior.SingleRow))
        {
            Assert.True(r.Read());
            Assert.Equal(firstViaFullRead, r.GetValue(0));
            // Second read must report no more rows without decoding the tail.
            Assert.False(r.Read());
        }

        // ExecuteScalar (which uses SingleRow internally) over the same fixture.
        using (var scalarCmd = connection.CreateCommand(fixture.Query))
        {
            var scalar = scalarCmd.ExecuteScalar();
            Assert.Equal(firstViaFullRead, scalar);
        }

        // Connection still in sync for the next query.
        using (var again = connection.CreateCommand(fixture.Query))
        using (var r = again.ExecuteReader())
        {
            int rows = 0;
            while (r.Read())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_EarlyDisposeAsync_AfterOneRow_ConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        await using (var command = connection.CreateCommand(fixture.Query))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
        }

        await using (var command2 = connection.CreateCommand(fixture.Query))
        await using (var reader2 = await command2.ExecuteReaderAsync())
        {
            int rows = 0;
            while (await reader2.ReadAsync())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
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
