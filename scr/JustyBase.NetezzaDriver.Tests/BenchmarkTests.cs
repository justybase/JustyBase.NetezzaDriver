using System.Diagnostics;
using System.Text;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Integration")]
[Trait("Category", "Benchmark")]
public class BenchmarkTests
{
    private const int RowCount = 10000;

    private static readonly string[] TestQueries =
    [
        $"SELECT * FROM JUST_DATA.ADMIN.DIMDATE ORDER BY ROWID LIMIT {RowCount}",
        $"SELECT * FROM JUST_DATA.ADMIN.FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT {RowCount}",
        $"""
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
        """
    ];

    private static readonly string[] QueryNames =
    [
        "DIMDATE (10k rows)",
        "FACTPRODUCTINVENTORY (10k rows)",
        "Many Types (10k rows)"
    ];

    private static string FormatBytes(long bytes)
    {
        string[] units = ["Bytes", "KB", "MB", "GB"];
        double n = bytes;
        foreach (var unit in units)
        {
            if (n < 1024)
                return $"{n:F2} {unit}";
            n /= 1024;
        }
        return $"{n:F2} TB";
    }

    private static string FormatTime(double ms)
    {
        if (ms < 1000)
            return $"{ms:F2}ms";
        return $"{ms / 1000:F2}s";
    }

    public static IEnumerable<object[]> BenchmarkData()
    {
        for (int i = 0; i < TestQueries.Length; i++)
            yield return [TestQueries[i], QueryNames[i]];
    }

    [Theory]
    [MemberData(nameof(BenchmarkData))]
    public async Task TestBenchmarkThroughput(string sql, string queryName)
    {
        await using var conn = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand(sql);

        var sw = Stopwatch.StartNew();
        await using var reader = await cmd.ExecuteReaderAsync();

        long rows = 0;
        long dataSize = 0;

        while (await reader.ReadAsync())
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

        sw.Stop();
        var elapsed = sw.Elapsed.TotalMilliseconds;
        var throughput = elapsed > 0 ? dataSize / (elapsed / 1000) : 0;
        var rowsPerSec = elapsed > 0 ? rows / (elapsed / 1000) : 0;

        Console.WriteLine(
            $"\n  Query:     {queryName}" +
            $"\n  Time:      {FormatTime(elapsed)}" +
            $"\n  Rows:      {rows:N0}" +
            $"\n  Data:      {FormatBytes(dataSize)}" +
            $"\n  Throughput: {FormatBytes((long)throughput)}/s" +
            $"\n  Rows/sec:  {rowsPerSec:N0}");

        Assert.True(rows > 0, "Query must return at least one row");
    }
}
