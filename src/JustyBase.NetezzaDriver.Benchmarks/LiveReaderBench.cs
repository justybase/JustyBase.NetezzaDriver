using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Focused end-to-end reader benchmark against a live Netezza instance.
/// Small row counts keep before/after comparisons fast; the goal is to compare
/// the same read path before and after read-buffer/payload-decode changes.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class LiveReaderBench
{
    public readonly record struct LiveScenario(string Name, string Query)
    {
        public override string ToString() => Name;
    }

    private NzConnection _connection = null!;

    public IEnumerable<LiveScenario> Scenarios
    {
        get
        {
            yield return new LiveScenario(
                "MixedWide_50k",
                "SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 50000");
            yield return new LiveScenario(
                "Text_50k",
                "SELECT ('code-' || ((RANDOM()*100000)::INT))::VARCHAR(32) AS CODE, " +
                "('category-' || ((RANDOM()*1000)::INT))::CHAR(16) AS CATEGORY, " +
                "('note-' || ((RANDOM()*1000000)::INT))::VARCHAR(48) AS NOTE " +
                "FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 50000");
            yield return new LiveScenario(
                "Numeric_50k",
                "SELECT ROWID::BIGINT AS ID64, (RANDOM() * 1000000)::NUMERIC(18,4) AS AMOUNT, " +
                "RANDOM()::DOUBLE PRECISION AS RATE, (RANDOM() * 1000)::INT AS COUNT_INT " +
                "FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 50000");
        }
    }

    [ParamsSource(nameof(Scenarios))]
    public LiveScenario Scenario { get; set; }

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
    public async Task<int> Async_GetValue()
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
