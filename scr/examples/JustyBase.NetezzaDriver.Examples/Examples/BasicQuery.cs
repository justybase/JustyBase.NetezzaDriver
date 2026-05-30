using System.Data;

namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class BasicQuery
{
    public static async Task RunAsync()
    {
        // ── Sync query ──
        Console.WriteLine("Sync query:");
        using (var conn = ConnectionHelper.Open())
        {
            using var cmd = conn.CreateCommand("SELECT 1 AS num, 'Hello' AS greeting");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Console.WriteLine($"  num={reader.GetInt32(0)}, greeting={reader.GetString(1)}");
            }
        }

        // ── Async query ──
        Console.WriteLine("Async query:");
        await using (var conn = await ConnectionHelper.OpenAsync())
        {
            await using var cmd = conn.CreateCommand("SELECT 2 AS num, 'Async world' AS greeting");
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Console.WriteLine($"  num={reader.GetInt32(0)}, greeting={reader.GetString(1)}");
            }
        }

        // ── ExecuteScalar ──
        Console.WriteLine("Scalar:");
        await using (var conn = await ConnectionHelper.OpenAsync())
        {
            await using var cmd = conn.CreateCommand("SELECT count(*) FROM (SELECT 1) t");
            var result = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"  count = {result}");
        }

        Console.WriteLine("BasicQuery completed.");
    }
}
