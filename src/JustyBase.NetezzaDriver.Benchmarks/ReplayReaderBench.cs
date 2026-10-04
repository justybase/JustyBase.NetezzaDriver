using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver.TestSupport;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Measures the reader path against a byte-exact replay of a recorded Netezza
/// response served over loopback by <see cref="NzReplayServer"/>. No database
/// and no real network latency, so client-side read/decode cost is isolated.
/// One connection is opened once and reused across invocations.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ReplayReaderBench
{
    public readonly record struct ReplayScenario(string Name, string FixtureFile)
    {
        public override string ToString() => Name;
    }

    public IEnumerable<ReplayScenario> Scenarios
    {
        get
        {
            yield return new ReplayScenario("DimDate_3652x19", "dimdate.nzreplay.gz");
            yield return new ReplayScenario("Fact_200000x7", "fact200k.nzreplay.gz");
        }
    }

    [ParamsSource(nameof(Scenarios))]
    public ReplayScenario Scenario { get; set; }

    private NzReplayFixture _fixture = null!;
    private NzReplayServer _server = null!;
    private NzConnection _connection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = NzReplayFixture.LoadShipped(Scenario.FixtureFile);
        _server = NzReplayServer.Start(_fixture);

        _connection = new NzConnection(
            "replay",
            "replay",
            "127.0.0.1",
            "JUST_DATA",
            _server.Port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
        };
        _connection.Open();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public int Sync_GetValue()
    {
        _connection.UseLazyColumnDecoding = false;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        int fields = reader.FieldCount;
        while (reader.Read())
        {
            for (int i = 0; i < fields; i++)
            {
                _ = reader.GetValue(i);
            }
            rows++;
        }
        return rows;
    }

    [Benchmark]
    public int Sync_GetValue_Lazy()
    {
        _connection.UseLazyColumnDecoding = true;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        int fields = reader.FieldCount;
        while (reader.Read())
        {
            for (int i = 0; i < fields; i++)
            {
                _ = reader.GetValue(i);
            }
            rows++;
        }
        return rows;
    }

    [Benchmark]
    public int Sync_FirstColumn()
    {
        _connection.UseLazyColumnDecoding = false;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            _ = reader.GetValue(0);
            rows++;
        }
        return rows;
    }

    [Benchmark]
    public int Sync_FirstColumn_Lazy()
    {
        _connection.UseLazyColumnDecoding = true;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            _ = reader.GetValue(0);
            rows++;
        }
        return rows;
    }

    [Benchmark]
    public int Sync_TwoColumns()
    {
        _connection.UseLazyColumnDecoding = false;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int columns = Math.Min(2, reader.FieldCount);
        int rows = 0;
        while (reader.Read())
        {
            for (int i = 0; i < columns; i++)
            {
                _ = reader.GetValue(i);
            }
            rows++;
        }
        return rows;
    }

    [Benchmark]
    public int Sync_TwoColumns_Lazy()
    {
        _connection.UseLazyColumnDecoding = true;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int columns = Math.Min(2, reader.FieldCount);
        int rows = 0;
        while (reader.Read())
        {
            for (int i = 0; i < columns; i++)
            {
                _ = reader.GetValue(i);
            }
            rows++;
        }
        return rows;
    }

    [Benchmark]
    public async Task<int> Async_GetValue()
    {
        await using var command = _connection.CreateCommand(_fixture.Query);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        int rows = 0;
        int fields = reader.FieldCount;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            for (int i = 0; i < fields; i++)
            {
                _ = reader.GetValue(i);
            }
            rows++;
        }
        return rows;
    }

    [Benchmark(Description = "read 1 row + Dispose (fast drain)")]
    public int Sync_Read1_Dispose()
    {
        _connection.UseLazyColumnDecoding = false;
        _connection.DiscardedRows = 0;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        if (reader.Read())
        {
            _ = reader.GetValue(0);
            rows = 1;
        }
        // Dispose drains the remaining rows via the discard path.
        return rows;
    }

    [Benchmark(Description = "read 10 rows + Dispose (fast drain)")]
    public int Sync_Read10_Dispose()
    {
        _connection.UseLazyColumnDecoding = false;
        _connection.DiscardedRows = 0;
        using var command = _connection.CreateCommand(_fixture.Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (rows < 10 && reader.Read())
        {
            _ = reader.GetValue(0);
            rows++;
        }
        return rows;
    }

    [Benchmark(Description = "ExecuteScalar over full result (SingleRow)")]
    public object? Sync_ExecuteScalar()
    {
        _connection.UseLazyColumnDecoding = false;
        using var command = _connection.CreateCommand(_fixture.Query);
        return command.ExecuteScalar();
    }
}
