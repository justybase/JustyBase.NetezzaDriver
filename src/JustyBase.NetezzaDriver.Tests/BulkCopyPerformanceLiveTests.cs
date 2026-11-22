using System.Diagnostics;
using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
[Trait("Category", "Benchmark")]
public sealed class BulkCopyPerformanceLiveTests
{
    private const int RowCount = 10_000;

    [Fact(Timeout = 300000)]
    public async Task CompareExternalTableBulkCopyWithRowByRowFallback()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NZ_BULK_COPY_BENCHMARK"),
                "1",
                StringComparison.Ordinal))
            return;

        object?[][] rows = Enumerable.Range(0, RowCount)
            .Select(i => new object?[] { i, $"bulk-copy-row-{i:D8}" })
            .ToArray();

        TimeSpan bulkElapsed;
        await using (var connection = new NzConnection(
                         Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port))
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await connection.OpenAsync(cancellationToken);
            await using var create = connection.CreateCommand(
                "CREATE TEMP TABLE CS_BULK_COPY_BENCH (ID INT, TXT VARCHAR(40))");
            await create.ExecuteNonQueryAsync(cancellationToken);

            var bulk = connection.CreateBulkCopy();
            bulk.DestinationTableName = "CS_BULK_COPY_BENCH";
            var timer = Stopwatch.StartNew();
            int count = await bulk.WriteToServerAsync(rows, cancellationToken);
            timer.Stop();
            Assert.Equal(RowCount, count);
            bulkElapsed = timer.Elapsed;
        }

        TimeSpan rowByRowElapsed;
        var builder = new NzConnectionStringBuilder
        {
            Host = Config.Host,
            Port = Config.Port,
            Database = Config.DbName,
            UserName = Config.UserName,
            Password = Config.Password,
            BulkInsert = false
        };
        await using (var connection = new NzConnection(builder.ConnectionString))
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await connection.OpenAsync(cancellationToken);
            await using var create = connection.CreateCommand(
                "CREATE TEMP TABLE CS_BULK_COPY_BENCH (ID INT, TXT VARCHAR(40))");
            await create.ExecuteNonQueryAsync(cancellationToken);

            var bulk = connection.CreateBulkCopy();
            bulk.DestinationTableName = "CS_BULK_COPY_BENCH";
            var timer = Stopwatch.StartNew();
            int count = await bulk.WriteToServerAsync(rows, cancellationToken);
            timer.Stop();
            Assert.Equal(RowCount, count);
            rowByRowElapsed = timer.Elapsed;
        }

        Console.WriteLine(
            $"BulkCopy rows={RowCount:N0}: external-table={bulkElapsed.TotalSeconds:F2}s, " +
            $"row-by-row={rowByRowElapsed.TotalSeconds:F2}s, " +
            $"speedup={rowByRowElapsed.TotalMilliseconds / bulkElapsed.TotalMilliseconds:F1}x");
    }
}
