namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class AsyncOperations
{
    public static async Task RunAsync()
    {
        // ── CancellationToken from a timeout ──
        Console.WriteLine("Async with CancellationToken (30s timeout):");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var conn = await ConnectionHelper.OpenAsync();
        await using var cmd = conn.CreateCommand("SELECT 1 AS async_test");

        await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
        while (await reader.ReadAsync(cts.Token))
        {
            Console.WriteLine($"  Result: {reader.GetInt32(0)}");
        }

        // ── Concurrent async queries ──
        Console.WriteLine("\nConcurrent async queries:");
        var queries = new[] {
            "SELECT 100 AS n",
            "SELECT 200 AS n",
            "SELECT 300 AS n",
        };

        var results = await Task.WhenAll(queries.Select(async sql =>
        {
            await using var c = await ConnectionHelper.OpenAsync();
            await using var q = c.CreateCommand(sql);
            var r = await q.ExecuteScalarAsync();
            await c.CloseAsync();
            return r?.ToString() ?? "NULL";
        }));

        foreach (var r in results)
            Console.WriteLine($"  Result: {r}");

        // ── Mixed sync/async pattern ──
        // (not recommended — demonstrates the option)
        Console.WriteLine("\nSync call on async connection (use only when needed):");
        await using var sc = await ConnectionHelper.OpenAsync();
        using var syncCmd = sc.CreateCommand("SELECT 999");
        var syncResult = syncCmd.ExecuteScalar();
        Console.WriteLine($"  Result: {syncResult}");

        Console.WriteLine("AsyncOperations completed.");
    }
}
