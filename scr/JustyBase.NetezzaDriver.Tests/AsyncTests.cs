using System.Data;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class AsyncTests
{
    private const string HeavyQuery =
    """
        SELECT F1.PRODUCTKEY, COUNT(DISTINCT (F1.PRODUCTKEY / F2.PRODUCTKEY))
        FROM
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F1,
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F2
        GROUP BY 1
        LIMIT 500
    """;

    [Fact]
    public async Task OpenAsync_ShouldConnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_ShouldExecute()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT 1");
        _ = await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task ExecuteReaderAsync_ShouldReturnReader()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT 1 AS col1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(1, reader.GetInt32(0));
    }

    [Fact]
    public async Task ExecuteReaderAsync_ShouldReadLargeResultSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT PRODUCTKEY FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 5000");
        await using var reader = await command.ExecuteReaderAsync(ct);
        int rows = 0;
        while (await reader.ReadAsync(ct))
        {
            _ = reader.GetValue(0);
            rows++;
        }
        Assert.True(rows >= 1000);
    }

    [Fact]
    public async Task Reader_GetBytesAndGetChars_ShouldWorkForTextValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT 'ABC'::VARCHAR(10)");
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));

        var chars = new char[3];
        long charsCopied = reader.GetChars(0, 0, chars, 0, chars.Length);
        Assert.Equal(3, charsCopied);
        Assert.Equal("ABC", new string(chars));

        var bytes = new byte[3];
        long bytesCopied = reader.GetBytes(0, 0, bytes, 0, bytes.Length);
        Assert.Equal(3, bytesCopied);
        Assert.Equal("ABC", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task ExecuteScalarAsync_ShouldReturnScalar()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT 123");
        var result = await command.ExecuteScalarAsync(ct);
        Assert.NotNull(result);
        Assert.Equal(123, Convert.ToInt32(result));
    }

    [Fact]
    public async Task ExecuteScalarAsync_ShouldHandleNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT NULL::INT");
        var result = await command.ExecuteScalarAsync(ct);
        Assert.True(result is null or DBNull);
    }

    [Fact]
    public async Task NextResultAsync_ShouldHandleMultipleResultSets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT 1;SELECT 2;SELECT 3");
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<int> results = [];
        do
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(Convert.ToInt32(reader.GetValue(0)));
            }
        } while (await reader.NextResultAsync(ct));

        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public async Task ReaderCloseAsync_ShouldCloseReader()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT 1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        await reader.CloseAsync();
        Assert.True(reader.IsClosed);
    }

    [Fact]
    public async Task CloseAsync_ShouldCloseConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await connection.CloseAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task CancellationToken_ShouldCancel()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection.OpenAsync(cts.Token));
    }

    [Fact]
    public async Task ExecuteReaderAsync_WithCancelledToken_ShouldCancel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand("SELECT 1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await command.ExecuteReaderAsync(cts.Token));
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_ShouldRespectCommandTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        connection.CommandTimeout = TimeSpan.FromSeconds(2);
        await using var command = connection.CreateCommand(HeavyQuery);
        var exception = await Assert.ThrowsAsync<NetezzaException>(async () => await command.ExecuteNonQueryAsync(ct));
        Assert.Contains("timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommitRollbackAsync_ShouldRespectTransactionBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await connection.OpenAsync(ct);
        connection.AutoCommit = false;
        await using var command = connection.CreateCommand();
        string tableName = $"T_ASYNC_TX_{Guid.NewGuid():N}"[..20];

        try
        {
            command.CommandText = $"DROP TABLE {tableName} IF EXISTS";
            await command.ExecuteNonQueryAsync(ct);

            command.CommandText = $"CREATE TABLE {tableName}(c1 INT)";
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = $"INSERT INTO {tableName} VALUES (1)";
            await command.ExecuteNonQueryAsync(ct);
            await connection.RollbackAsync(ct);

            command.CommandText = $"CREATE TABLE {tableName}(c1 INT)";
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = $"INSERT INTO {tableName} VALUES (1)";
            await command.ExecuteNonQueryAsync(ct);
            await connection.CommitAsync(ct);

            command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            var count = await command.ExecuteScalarAsync(ct);
            Assert.Equal(1, Convert.ToInt32(count));
        }
        finally
        {
            try
            {
                command.CommandText = $"DROP TABLE {tableName} IF EXISTS";
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (Exception)
            {
            }
            connection.AutoCommit = true;
        }
    }
}
