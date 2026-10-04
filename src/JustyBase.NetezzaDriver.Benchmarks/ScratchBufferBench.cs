using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver.TestSupport;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Sends commands of increasing size over the replay server and reads the
/// result, exercising the capped scratch-buffer path. Retained capacity is
/// surfaced through <see cref="NzConnection.TmpBufferCapacity"/>.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ScratchBufferBench
{
    [Params(0, 1024, 10 * 1024, 100 * 1024, 512 * 1024)]
    public int QueryPadding { get; set; }

    private NzReplayFixture _fixture = null!;
    private NzReplayServer _server = null!;
    private NzConnection _connection = null!;
    private string _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        _server = NzReplayServer.Start(_fixture);
        _connection = new NzConnection(
            "replay", "replay", "127.0.0.1", "JUST_DATA", _server.Port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
        };
        _connection.Open();
        _query = QueryPadding == 0
            ? "SELECT 1"
            : "SELECT 1 /* " + new string('x', QueryPadding) + " */";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public int Send_And_Read()
    {
        using var command = _connection.CreateCommand(_query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            _ = reader.GetValue(0);
            rows++;
        }
        return rows;
    }
}
