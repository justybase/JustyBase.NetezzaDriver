using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Benchmarks matching the Python test_benchmark.py queries exactly.
/// Run: dotnet run -c Release -- --filter *PythonBench*
/// Compare results with: pytest tests/test_benchmark.py -m benchmark
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
public class PythonBench
{
    private const int RowCount = 10000;
    private NzConnection _connection = null!;
    private string _dimdateSql = null!;
    private string _factSql = null!;
    private string _manyTypesSql = null!;

    // Data size (bytes) captured during the last benchmark iteration
    // These are informational only, not used for timing.
    public static long LastDataSize { get; private set; }
    public static long LastRowCount { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        _connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _connection.Open();

        _dimdateSql = $"SELECT * FROM JUST_DATA.ADMIN.DIMDATE ORDER BY ROWID LIMIT {RowCount}";
        _factSql = $"SELECT * FROM JUST_DATA.ADMIN.FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT {RowCount}";
        _manyTypesSql = $"""
            SELECT
                10::bigint, null::bigint, true::Boolean, false::Boolean,
                null::Boolean, 5::Byteint, null::Byteint, 'a'::Char,
                null::Char, current_date::Date, null::Date, 0.5::float,
                null::float, 10::integer, null::integer, '02:00:00'::TIME,
                'abc'::nchar(10), null::nchar(10), 1.54::numeric(30, 6),
                null::numeric(30, 6), 'abc'::Nvarchar(10), null::Nvarchar(10),
                1.54::real, null::real, 5::smallint, null::smallint,
                '10:12:13'::TIME, null::time,
                DATE_TRUNC('hour', current_timestamp)::Timestamp,
                null::Timestamp, 'abc'::varchar(10), null::varchar(10)
            FROM JUST_DATA..FACTPRODUCTINVENTORY
            ORDER BY ROWID ASC
            LIMIT {RowCount}
            """;
    }

    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    [Benchmark]
    public long Dimdate10k() => ExecuteAndCount(_dimdateSql);

    [Benchmark]
    public long FactProductInventory10k() => ExecuteAndCount(_factSql);

    [Benchmark]
    public long ManyTypes10k() => ExecuteAndCount(_manyTypesSql);

    private long ExecuteAndCount(string sql)
    {
        using var cmd = _connection.CreateCommand(sql);
        using var reader = cmd.ExecuteReader();

        long rows = 0;
        long dataSize = 0;

        while (reader.Read())
        {
            rows++;
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.IsDBNull(i))
                {
                    var val = reader.GetValue(i);
                    dataSize += Encoding.UTF8.GetByteCount(val.ToString() ?? string.Empty);
                }
            }
        }

        LastRowCount = rows;
        LastDataSize = dataSize;
        return rows;
    }
}
