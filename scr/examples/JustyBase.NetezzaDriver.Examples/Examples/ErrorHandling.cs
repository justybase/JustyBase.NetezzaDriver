using System.Data;

namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class ErrorHandling
{
    public static async Task RunAsync()
    {
        await using var conn = await ConnectionHelper.OpenAsync();

        // ── Invalid SQL syntax ──
        Console.WriteLine("Invalid SQL syntax:");
        try
        {
            await using var cmd = conn.CreateCommand("SELEC 1");
            await cmd.ExecuteNonQueryAsync();
        }
        catch (NetezzaException ex)
        {
            Console.WriteLine($"  NetezzaException: {ex.Message}");
        }

        // ── Missing table ──
        Console.WriteLine("Missing table:");
        try
        {
            await using var cmd = conn.CreateCommand("SELECT * FROM nonexistent_table_xyz");
            await cmd.ExecuteReaderAsync();
        }
        catch (NetezzaException ex)
        {
            Console.WriteLine($"  NetezzaException: {ex.Message}");
        }

        // ── DBNull handling ──
        Console.WriteLine("DBNull handling:");
        await using (var setup = conn.CreateCommand("CREATE TEMP TABLE null_demo (id INTEGER, val VARCHAR(20))"))
            await setup.ExecuteNonQueryAsync();

        await using (var insert = conn.CreateCommand("INSERT INTO null_demo VALUES (1, NULL)"))
            await insert.ExecuteNonQueryAsync();

        await using (var query = conn.CreateCommand("SELECT id, val FROM null_demo"))
        await using (var reader = await query.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                // Always check IsDBNull before reading as a specific type
                var val = reader.IsDBNull(1) ? "(NULL)" : reader.GetString(1);
                Console.WriteLine($"  id={reader.GetInt32(0)}, val={val}");
            }
        }

        // ── Timeout handling ──
        Console.WriteLine("\nTimeout handling (using HEAVY_SQL):");
        try
        {
            await using var cmd = conn.CreateCommand(ConnectionHelper.HEAVY_SQL);
            cmd.CommandTimeout = 1;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (NetezzaException ex) when (ex.Message.Contains("Command timeout", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  NetezzaException (Command timeout): query timed out as expected");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  OperationCanceledException: query timed out");
        }

        Console.WriteLine("ErrorHandling completed.");
    }
}
