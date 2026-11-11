using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Live benchmark over <c>JUST_DATA.PERF.EVENTS_400K</c> (400,000 rows,
/// 9 columns: BIGINT, INTEGER, TIMESTAMPTZ, two NUMERIC, SMALLINT and three
/// VARCHARs). Unlike the replay fact fixture, the SKU/NOTE columns are
/// high-cardinality, so this table exercises the string-pool cap and the
/// numeric decode path under a realistic mix. Requires a live Netezza
/// instance (<c>NZ_DEV_HOST</c> etc.).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class LiveEvents400kBench
{
    private NzConnection _connection = null!;

    private const string Query = "SELECT * FROM JUST_DATA.PERF.EVENTS_400K";

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

    /// <summary>
    /// Decodes every column but never boxes the values, isolating the wire
    /// parse/decode cost from <see cref="Sync_GetValue"/>'s boxing.
    /// </summary>
    [Benchmark]
    public int Sync_FullDecode_NoGetValue()
    {
        using var command = _connection.CreateCommand(Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }
        return rows;
    }

    /// <summary>Typed accessors for the exact EVENTS_400K schema.</summary>
    [Benchmark]
    public int Sync_Typed()
    {
        using var command = _connection.CreateCommand(Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            _ = reader.GetInt64(0);
            _ = reader.GetInt32(1);
            _ = reader.GetDateTime(2);
            _ = reader.GetDecimal(3);
            _ = reader.GetDecimal(4);
            _ = reader.GetInt16(5);
            _ = reader.GetString(6);
            _ = reader.GetString(7);
            _ = reader.GetString(8);
            rows++;
        }
        return rows;
    }
}
