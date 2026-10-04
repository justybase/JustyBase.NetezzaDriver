using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver.TestSupport;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Measures read throughput against the application-level read buffer size.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ReadBufferSizeBench
{
    public readonly record struct ReadScenario(string Name, string FixtureFile)
    {
        public override string ToString() => Name;
    }

    public IEnumerable<ReadScenario> Scenarios
    {
        get
        {
            yield return new ReadScenario("DimDate_3652x19", "dimdate.nzreplay.gz");
            yield return new ReadScenario("Fact_200000x7", "fact200k.nzreplay.gz");
        }
    }

    [ParamsSource(nameof(Scenarios))]
    public ReadScenario Scenario { get; set; }

    [Params(8 * 1024, 16 * 1024, 32 * 1024, 64 * 1024, 128 * 1024)]
    public int BufferSize { get; set; }

    private NzReplayFixture _fixture = null!;
    private NzReplayServer _server = null!;
    private NzConnection _connection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = NzReplayFixture.LoadShipped(Scenario.FixtureFile);
        _server = NzReplayServer.Start(_fixture);
        _connection = new NzConnection(
            "replay", "replay", "127.0.0.1", "JUST_DATA", _server.Port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
            ReadBufferSize = BufferSize,
        };
        _connection.Open();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public int Read_All()
    {
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
}
