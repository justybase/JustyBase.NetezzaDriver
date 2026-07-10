using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JustyBase.NetezzaDriver.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class AsyncReaderBench
{
    public readonly record struct ReaderScenario(string Name, string Query)
    {
        public override string ToString() => Name;
    }

    private NzConnection _connection = null!;

    public IEnumerable<ReaderScenario> Scenarios
    {
        get
        {
            yield return new ReaderScenario(
                "LargeMixed_500k",
                "SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY FI ORDER BY ROWID LIMIT 500000");
            yield return new ReaderScenario(
                "NumericScalars_300k",
                "SELECT ROWID::BIGINT AS ID64, (RANDOM() * 1000000)::NUMERIC(18,4) AS AMOUNT, RANDOM()::DOUBLE PRECISION AS RATE, (RANDOM() * 1000)::INT AS COUNT_INT FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 300000");
            yield return new ReaderScenario(
                "Textual_250k",
                "SELECT ('code-' || ((RANDOM()*100000)::INT))::VARCHAR(32) AS CODE, ('category-' || ((RANDOM()*1000)::INT))::CHAR(16) AS CATEGORY, ('note-' || ((RANDOM()*1000000)::INT))::VARCHAR(48) AS NOTE FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 250000");
            yield return new ReaderScenario(
                "TemporalNulls_300k",
                "SELECT '2024-01-01'::DATE AS D1, '12:34:56'::TIME AS T1, '2024-01-01 12:34:56'::TIMESTAMP AS TS1, CASE WHEN MOD(ROWID,10) = 0 THEN NULL ELSE (RANDOM()*100)::NUMERIC(10,2) END AS OPTIONAL_NUM FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 300000");
        }
    }

    [ParamsSource(nameof(Scenarios))]
    public ReaderScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _connection.Open();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int ReadLargeDataReaderSync()
    {
        using var command = _connection.CreateCommand(Scenario.Query);
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

    [Benchmark]
    public async Task<int> ReadLargeDataReaderAsync()
    {
        await using var command = _connection.CreateCommand(Scenario.Query);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        int rows = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
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
