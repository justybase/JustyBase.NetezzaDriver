using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Live counterpart of the shared fact200k replay fixture: the exact query
/// recorded in <c>fact200k.nzreplay.gz</c>, so live numbers are comparable
/// with replay numbers and with the Rust harness.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class LiveFact200kBench
{
    private NzConnection _connection = null!;

    private const string Query =
        "SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 200000";

    [GlobalSetup]
    public void Setup()
    {
        _connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _connection.Open();
    }

    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    [Benchmark(Baseline = true)]
    public int Sync_GetValue()
    {
        using var command = _connection.CreateCommand(Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                _ = reader.GetValue(i);
            }
            rows++;
        }
        return rows;
    }
}
