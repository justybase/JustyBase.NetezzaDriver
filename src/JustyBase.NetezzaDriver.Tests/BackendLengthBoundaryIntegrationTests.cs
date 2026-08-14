using System.Data.Common;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public sealed class BackendLengthBoundaryIntegrationTests
{
    private const string ExactDimDateSql = "SELECT * FROM JUST_DATA.ADMIN.DIMDATE";

    [Fact]
    public void ExactDimDate_CanReadRepeatedReadersBeyondPreviewBoundary()
    {
        using var connection = CreateConnection();
        connection.Open();

        for (int iteration = 1; iteration <= 3; iteration++)
        {
            using var command = connection.CreateCommand(ExactDimDateSql);
            using var reader = command.ExecuteReader();

            int rows = ReadBeyondPreviewBoundary(reader, ExactDimDateSql);

            Assert.True(
                rows > 500,
                $"Iteration {iteration} read only {rows} rows from the exact DIMDATE query.");
        }
    }

    [Fact]
    public async Task ExactDimDateAsync_CanReadBeyondPreviewBoundary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand(ExactDimDateSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        int rows = await ReadBeyondPreviewBoundaryAsync(reader, ExactDimDateSql, cancellationToken);

        Assert.True(rows > 500, $"The async exact DIMDATE query read only {rows} rows.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(1000)]
    [InlineData(1500)]
    public void DimDate_LimitBoundaries_ReturnExpectedRows(int limit)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand(
            $"SELECT * FROM JUST_DATA.ADMIN.DIMDATE ORDER BY ROWID LIMIT {limit}");
        using var reader = command.ExecuteReader();

        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }

        Assert.Equal(limit, rows);
    }

    [Fact]
    public void PartialReaderDispose_DoesNotCorruptTheNextCommand()
    {
        using var connection = CreateConnection();
        connection.Open();

        using (var command = connection.CreateCommand(ExactDimDateSql))
        using (var reader = command.ExecuteReader())
        {
            for (int row = 1; row <= 499; row++)
            {
                Assert.True(reader.Read(), $"The exact DIMDATE query ended before row {row}.");
            }
        }

        using (var scalarCommand = connection.CreateCommand("SELECT 1"))
        {
            Assert.Equal(1, Convert.ToInt32(scalarCommand.ExecuteScalar()));
        }

        using var finalCommand = connection.CreateCommand(ExactDimDateSql);
        using var finalReader = finalCommand.ExecuteReader();
        int rowsAfterReuse = DrainCurrentResult(finalReader);

        Assert.True(
            rowsAfterReuse > 500,
            $"The connection returned only {rowsAfterReuse} rows after partial reader disposal.");
    }

    [Fact]
    public void ExactDimDateResult_CanAdvanceToTheFollowingResult()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand($"{ExactDimDateSql}; SELECT 1");
        using var reader = command.ExecuteReader();

        int firstResultRows = DrainCurrentResult(reader);
        Assert.True(firstResultRows > 500, $"First result contained only {firstResultRows} rows.");

        Assert.True(reader.NextResult(), "The scalar result after DIMDATE was not available.");
        Assert.True(reader.Read(), "The scalar result had no row.");
        Assert.Equal(1, Convert.ToInt32(reader.GetValue(0)));
        Assert.False(reader.Read(), "The scalar result unexpectedly contained more than one row.");
        Assert.False(reader.NextResult(), "An unexpected third result was returned.");
    }

    private static NzConnection CreateConnection()
    {
        return new NzConnection(
            Config.UserName,
            Config.Password,
            Config.Host,
            Config.DbName,
            Config.Port,
            loggerFactory: ProtocolTraceTestLogger.CreateIfEnabled());
    }

    private static int ReadBeyondPreviewBoundary(DbDataReader reader, string sql)
    {
        int rows = 0;

        try
        {
            while (rows < 500)
            {
                Assert.True(reader.Read(), $"SQL ended at row {rows + 1}: {sql}");
                rows++;
            }

            Assert.True(reader.Read(), $"SQL has no row 501: {sql}");
            rows++;

            while (reader.Read())
            {
                rows++;
            }

            return rows;
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Driver failed while reading row {rows + 1}. SQL: {sql}",
                ex);
        }
    }

    private static async Task<int> ReadBeyondPreviewBoundaryAsync(
        DbDataReader reader,
        string sql,
        CancellationToken cancellationToken)
    {
        int rows = 0;

        try
        {
            while (rows < 500)
            {
                Assert.True(
                    await reader.ReadAsync(cancellationToken),
                    $"SQL ended at row {rows + 1}: {sql}");
                rows++;
            }

            Assert.True(
                await reader.ReadAsync(cancellationToken),
                $"SQL has no row 501: {sql}");
            rows++;

            while (await reader.ReadAsync(cancellationToken))
            {
                rows++;
            }

            return rows;
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Driver failed asynchronously while reading row {rows + 1}. SQL: {sql}",
                ex);
        }
    }

    private static int DrainCurrentResult(DbDataReader reader)
    {
        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }

        return rows;
    }
}
