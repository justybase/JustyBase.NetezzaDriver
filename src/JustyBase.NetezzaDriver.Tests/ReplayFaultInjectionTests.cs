using JustyBase.NetezzaDriver.TestSupport;
using System.Data;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Drives the real socket protocol through the replay server with injected
/// errors, notices, delays and truncated responses. These tests assert both the
/// exception surface and that the connection remains usable afterwards.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReplayFaultInjectionTests
{
    private const int StartupQueries = 7;

    [Fact]
    public async Task Transcript_RecordsStartupAndExactUtf8CommandText()
    {
        var observed = new List<string>();
        var gate = new object();
        var options = new NzReplayOptions
        {
            OnQuery = (sql, _) =>
            {
                lock (gate)
                {
                    observed.Add(sql);
                }
            },
        };

        await using var server = Start(options);
        using var connection = OpenConnection(server.Port);

        Assert.Equal(StartupQueries, server.Queries.Count);
        Assert.Equal("set nz_encoding to 'utf8'", server.Queries[0]);

        const string sql = "SELECT * FROM JUST_DATA.ADMIN.DIMDATE /* Zażółć gęślą jaźń */";
        using (var command = connection.CreateCommand(sql))
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
        }

        Assert.Equal(sql, server.Queries[^1]);
        Assert.Equal(StartupQueries + 1, server.Queries.Count);
        lock (gate)
        {
            Assert.Equal(sql, observed[^1]);
        }
    }

    [Fact]
    public async Task InjectedError_SurfacesDiagnosticsAndSessionRecovers()
    {
        var options = new NzReplayOptions
        {
            ResponseOverride = (sql, _) => sql.Contains("INJECT_ERROR", StringComparison.Ordinal)
                ? BackendTestMessages.ErrorThenReady(
                    "synthetic failure",
                    sqlState: "42S02",
                    detail: "synthetic detail",
                    hint: "synthetic hint")
                : null,
        };

        await using var server = Start(options);
        using var connection = OpenConnection(server.Port);
        using var command = connection.CreateCommand("SELECT 1 /* INJECT_ERROR */");

        var error = Assert.Throws<NetezzaException>(() => command.ExecuteNonQuery());
        Assert.Contains("synthetic failure", error.Message, StringComparison.Ordinal);
        Assert.Equal("42S02", error.SqlState);
        Assert.Equal("synthetic detail", error.Detail);
        Assert.Equal("synthetic hint", error.Hint);
        Assert.Equal("synthetic failure", error.Diagnostics['M']);

        // The injected response ended with ReadyForQuery, so the session must
        // be reusable without reconnecting.
        using var followUp = connection.CreateCommand("SELECT * FROM JUST_DATA.ADMIN.DIMDATE");
        using var reader = followUp.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task InjectedNotice_RaisesEventAndAttachesToCommand()
    {
        var options = new NzReplayOptions
        {
            ResponseOverride = (sql, _) => sql.Contains("INJECT_NOTICE", StringComparison.Ordinal)
                ? BackendTestMessages.NoticeThenSuccess("synthetic notice")
                : null,
        };

        await using var server = Start(options);
        using var connection = OpenConnection(server.Port);
        var events = new List<string>();
        connection.NoticeReceived += (_, args) => events.Add(args.Message);

        using var command = connection.CreateCommand("SELECT 1 /* INJECT_NOTICE */");
        command.ExecuteNonQuery();

        Assert.Contains("synthetic notice", command.Notices);
        Assert.Contains("synthetic notice", events);
    }

    [Fact]
    public async Task InjectedDelay_TripsCommandTimeoutAndConnectionRecovers()
    {
        const string slowSql = "SELECT 1 /* INJECT_TIMEOUT */";
        var options = new NzReplayOptions
        {
            ResponseOverride = (sql, _) => sql == slowSql ? BackendTestMessages.Success() : null,
            DelayBeforeResponse = (sql, _) => sql == slowSql
                ? TimeSpan.FromSeconds(1.5)
                : TimeSpan.Zero,
        };

        await using var server = Start(options);
        using var connection = OpenConnection(server.Port);
        connection.CommandTimeout = TimeSpan.FromMilliseconds(300);
        using (var command = connection.CreateCommand(slowSql))
        {
            var error = Assert.Throws<NetezzaException>(() => command.ExecuteNonQuery());
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        connection.CommandTimeout = Timeout.InfiniteTimeSpan;

        // The delayed response is drained by the timeout path; wait until the
        // server has actually written it before reusing the connection.
        long deadline = Environment.TickCount64 + 10_000;
        while (server.ResponsesSent < StartupQueries + 1 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(25);
        }

        using var followUp = connection.CreateCommand("SELECT * FROM JUST_DATA.ADMIN.DIMDATE");
        using var reader = followUp.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData("text_types.bin", 0)]
    [InlineData("dbos_types.bin", 3)]
    [InlineData("numeric_edges.bin", 5)]
    [InlineData("temporal_boundaries.bin", 3)]
    [InlineData("varchar_32767.bin", 2)]
    public async Task TruncatedResponse_FailsClosedWithoutHanging(string fixtureName, int recordIndex)
    {
        WireFixture wire = WireFixture.LoadShipped(fixtureName);
        WireRecord target = wire.Records[recordIndex];
        byte[] truncated = target.Response.AsSpan(0, Math.Max(1, target.Response.Length / 2)).ToArray();
        int targetQueryIndex = StartupQueries + recordIndex;

        NzReplayFixture startup = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        var combined = new NzReplayFixture
        {
            Query = target.Sql,
            ExpectedRows = 0,
            ExpectedColumns = 0,
            Segments = startup.Segments
                .Take(8)
                .Concat(wire.Records.Select(record => record.Response))
                .ToArray(),
        };
        var options = new NzReplayOptions
        {
            ResponseOverride = (_, index) => index == targetQueryIndex ? truncated : null,
            CloseAfterResponse = (_, index) => index == targetQueryIndex,
        };

        await using var server = NzReplayServer.Start(combined, options);
        using var connection = OpenConnection(server.Port);

        bool failed = false;
        for (int i = 0; i <= recordIndex; i++)
        {
            try
            {
                using var command = connection.CreateCommand(wire.Records[i].Sql);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                }
            }
            catch (Exception error) when (error is NetezzaException or IOException or EndOfStreamException or InvalidDataException)
            {
                failed = true;
                break;
            }
        }

        Assert.True(failed, $"{fixtureName}[{recordIndex}]: a truncated response was accepted as success.");
    }

    private static NzReplayServer Start(NzReplayOptions? options = null)
        => NzReplayServer.Start(NzReplayFixture.LoadShipped("dimdate.nzreplay.gz"), options);

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
            ConnectionTimeoutDuration = TimeSpan.FromSeconds(5),
        };
        connection.Open();
        return connection;
    }
}
