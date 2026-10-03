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
