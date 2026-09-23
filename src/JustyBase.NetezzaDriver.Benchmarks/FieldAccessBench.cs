using BenchmarkDotNet.Attributes;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Measures field-access choices while streaming the same deterministic rows
/// from a live Netezza table. The returned checksum guards against accidentally
/// benchmarking a path that does not consume the values.
/// </summary>
[MemoryDiagnoser]
public class FieldAccessBench
{
    private const string Query = "SELECT ROWID::BIGINT AS ID64 FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 100000";
    private NzConnection _connection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _connection.Open();
    }

    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    [Benchmark(Baseline = true)]
    public long GetValueAndCast()
    {
        using var command = _connection.CreateCommand(Query);
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            checksum = unchecked(checksum + (long)reader.GetValue(0));
        }
        return checksum;
    }

    [Benchmark]
    public long GenericGetFieldValue()
    {
        using var command = _connection.CreateCommand(Query);
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            checksum = unchecked(checksum + reader.GetFieldValue<long>(0));
        }
        return checksum;
    }

    [Benchmark]
    public long TypedGetInt64()
    {
        using var command = _connection.CreateCommand(Query);
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            checksum = unchecked(checksum + reader.GetInt64(0));
        }
        return checksum;
    }
}
