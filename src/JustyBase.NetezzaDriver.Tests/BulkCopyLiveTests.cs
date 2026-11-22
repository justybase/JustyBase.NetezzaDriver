using JustyBase.NetezzaDriver;
using System.Data;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public sealed class BulkCopyLiveTests
{
    [Fact(Timeout = 60000)]
    public async Task BulkCopyTransfersTypedUnicodeAndNullValues()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(
            "CREATE TEMP TABLE CS_BULK_COPY_TYPES (ID INT, NAME NVARCHAR(100), AMOUNT NUMERIC(18,2), CREATED DATE, ACTIVE BOOLEAN)");
        await command.ExecuteNonQueryAsync(cancellationToken);

        var bulk = connection.CreateBulkCopy();
        bulk.DestinationTableName = "CS_BULK_COPY_TYPES";
        bulk.ColumnNames.Add("ID");
        bulk.ColumnNames.Add("NAME");
        bulk.ColumnNames.Add("AMOUNT");
        bulk.ColumnNames.Add("CREATED");
        bulk.ColumnNames.Add("ACTIVE");

        object?[][] rows =
        [
            [1, "one", 12.50m, new DateOnly(2026, 11, 22), true],
            [2, "two\tlines\nand\\slash", null, null, false],
            [3, "zażółć 😀", -0.01m, new DateOnly(2024, 2, 29), null]
        ];

        int inserted = await bulk.WriteToServerAsync(rows, cancellationToken);

        Assert.Equal(rows.Length, inserted);
        await using var select = connection.CreateCommand(
            "SELECT ID, NAME, AMOUNT, CREATED, ACTIVE FROM CS_BULK_COPY_TYPES ORDER BY ID");
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("one", reader.GetString(1));
        Assert.Equal(12.50m, reader.GetDecimal(2));
        Assert.Equal(new DateTime(2026, 11, 22), reader.GetDateTime(3));
        Assert.True(reader.GetBoolean(4));

        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("two\tlines\nand\\slash", reader.GetString(1));
        Assert.True(await reader.IsDBNullAsync(2, cancellationToken));
        Assert.True(await reader.IsDBNullAsync(3, cancellationToken));
        Assert.False(reader.GetBoolean(4));

        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("zażółć 😀", reader.GetString(1));
        Assert.Equal(-0.01m, reader.GetDecimal(2));
        Assert.True(await reader.IsDBNullAsync(4, cancellationToken));
        Assert.False(await reader.ReadAsync(cancellationToken));
    }

    [Fact(Timeout = 60000)]
    public async Task BulkCopyHandlesLargeRowsAndFallsBackForUnsupportedValues()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(
            "CREATE TEMP TABLE CS_BULK_COPY_LARGE (ID INT, TXT VARCHAR(100))");
        await command.ExecuteNonQueryAsync(cancellationToken);

        var rows = Enumerable.Range(0, 12_000)
            .Select(i => new object?[] { i, $"row-{i}" })
            .ToArray();
        var bulk = connection.CreateBulkCopy();
        bulk.DestinationTableName = "CS_BULK_COPY_LARGE";
        int inserted = await bulk.WriteToServerAsync(rows, cancellationToken);
        Assert.Equal(rows.Length, inserted);

        await using (var count = connection.CreateCommand("SELECT COUNT(*) FROM CS_BULK_COPY_LARGE"))
        {
            Assert.Equal(rows.Length, Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)));
        }

        await using (var createFallback = connection.CreateCommand(
            "CREATE TEMP TABLE CS_BULK_COPY_FALLBACK (ID INT, TXT VARCHAR(50))"))
        {
            await createFallback.ExecuteNonQueryAsync(cancellationToken);
        }

        var fallback = connection.CreateBulkCopy();
        fallback.DestinationTableName = "CS_BULK_COPY_FALLBACK";
        int fallbackCount = fallback.WriteToServer(
        [
            [1, new FallbackText("first")],
            [2, new FallbackText("second")]
        ]);
        Assert.Equal(2, fallbackCount);

        await using var select = connection.CreateCommand(
            "SELECT TXT FROM CS_BULK_COPY_FALLBACK ORDER BY ID");
        await using var values = await select.ExecuteReaderAsync(cancellationToken);
        Assert.True(await values.ReadAsync(cancellationToken));
        Assert.Equal("first", values.GetString(0));
        Assert.True(await values.ReadAsync(cancellationToken));
        Assert.Equal("second", values.GetString(0));
        Assert.False(await values.ReadAsync(cancellationToken));
    }

    [Fact(Timeout = 60000)]
    public async Task BulkInsertFalseUsesTheRowByRowFallback()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var builder = new NzConnectionStringBuilder
        {
            Host = Config.Host,
            Port = Config.Port,
            Database = Config.DbName,
            UserName = Config.UserName,
            Password = Config.Password,
            BulkInsert = false
        };
        await using var connection = new NzConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var create = connection.CreateCommand("CREATE TEMP TABLE CS_BULK_COPY_DISABLED (ID INT, TXT VARCHAR(30))");
        await create.ExecuteNonQueryAsync(cancellationToken);

        var bulk = connection.CreateBulkCopy();
        bulk.DestinationTableName = "CS_BULK_COPY_DISABLED";
        var sourceTable = new DataTable();
        sourceTable.Columns.Add("ID", typeof(int));
        sourceTable.Columns.Add("TXT", typeof(string));
        sourceTable.Rows.Add(1, "first");
        sourceTable.Rows.Add(2, "second");
        using var sourceReader = sourceTable.CreateDataReader();
        int inserted = bulk.WriteToServer(sourceReader);
        int insertedFromTable = bulk.WriteToServer(sourceTable);

        Assert.Equal(2, inserted);
        Assert.Equal(2, insertedFromTable);
        await using var countCommand = connection.CreateCommand("SELECT COUNT(*) FROM CS_BULK_COPY_DISABLED");
        await using var count = await countCommand.ExecuteReaderAsync(cancellationToken);
        Assert.True(await count.ReadAsync(cancellationToken));
        Assert.Equal(4L, count.GetInt64(0));
    }

    private static NzConnection NewConnection()
        => new(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);

    private sealed record FallbackText(string Value)
    {
        public override string ToString() => Value;
    }
}
