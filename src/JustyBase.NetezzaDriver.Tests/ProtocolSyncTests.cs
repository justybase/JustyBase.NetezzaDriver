using System.Data.Common;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Regression: orphaned SELECT CURRENT_SID response must not be attributed to a later CALL.
/// Mirrors Node ProtocolSync.smoke tests.
/// </summary>
[Collection("Sequential")]
[Trait("Category", "Integration")]
public class ProtocolSyncTests : IAsyncLifetime
{
    private const string ProcName = "JUST_DATA.ADMIN.CS_PROTOCOL_SYNC_TEST";
    private const string CallSql = $"CALL {ProcName}();";

    private NzConnection _conn = null!;

    public async ValueTask InitializeAsync()
    {
        _conn = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        await _conn.OpenAsync();

        var createSql = $"""
            CREATE OR REPLACE PROCEDURE {ProcName}()
            RETURNS INTEGER
            EXECUTE AS OWNER
            LANGUAGE NZPLSQL
            AS BEGIN_PROC
            BEGIN
                RETURN NULL;
            END;
            END_PROC;
            """;

        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = createSql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is null)
        {
            return;
        }

        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"DROP PROCEDURE {ProcName}";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // best-effort cleanup
        }

        await _conn.DisposeAsync();
    }

    private static async Task<object?> CaptureSidAsync(NzConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CURRENT_SID";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetValue(0);
    }

    private static async Task<(string[] Columns, bool HasRow, object? FirstValue)> ReadFirstResultAsync(DbDataReader reader)
    {
        var columns = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = reader.GetName(i);
        }

        bool hasRow = false;
        object? firstValue = null;
        if (await reader.ReadAsync())
        {
            hasRow = true;
            firstValue = reader.IsDBNull(0) ? null : reader.GetValue(0);
        }

        while (await reader.NextResultAsync())
        {
            while (await reader.ReadAsync())
            {
                // drain extra result sets
            }
        }

        return (columns, hasRow, firstValue);
    }

    private static void AssertNotCurrentSid(string[] columns)
    {
        Assert.NotEmpty(columns);
        Assert.DoesNotContain(columns, name =>
            string.Equals(name, "CURRENT_SID", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name =>
            name.Contains("CURRENT_SID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectCurrentSidThenCall_ReturnsProcedureColumnsNotCurrentSid()
    {
        var sid = await CaptureSidAsync(_conn);
        Assert.NotNull(sid);

        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = CallSql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = await ReadFirstResultAsync(reader);

        AssertNotCurrentSid(result.Columns);
        Assert.True(result.HasRow);
        Assert.Null(result.FirstValue);
    }

    [Fact]
    public async Task OrphanedCurrentSidResponse_IsDrainedBeforeCall()
    {
        _conn.InjectUnreadQuery("SELECT CURRENT_SID");
        await Task.Delay(200);

        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = CallSql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = await ReadFirstResultAsync(reader);

        AssertNotCurrentSid(result.Columns);
        Assert.True(result.HasRow);
        Assert.Null(result.FirstValue);

        // Connection remains usable after recovery
        var sid = await CaptureSidAsync(_conn);
        Assert.NotNull(sid);
    }

    [Fact]
    public async Task RepeatedSidThenCall_StaysStable()
    {
        for (int i = 0; i < 10; i++)
        {
            await CaptureSidAsync(_conn);

            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = CallSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = await ReadFirstResultAsync(reader);

            AssertNotCurrentSid(result.Columns);
            Assert.Null(result.FirstValue);
        }
    }
}
