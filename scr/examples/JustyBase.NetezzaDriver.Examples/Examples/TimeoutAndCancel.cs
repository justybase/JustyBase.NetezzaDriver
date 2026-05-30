namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class TimeoutAndCancel
{
    public static async Task RunAsync()
    {
        // Each sub-section uses a fresh connection because a timeout
        // or cancel leaves the underlying stream in an unusable state.

        // ── CommandTimeout ──
        await TestCommandTimeoutAsync();

        // ── DefaultCommandTimeout ──
        await TestDefaultCommandTimeoutAsync();

        // ── CancellationToken ──
        await TestCancellationTokenAsync();

        // ── Connection-level cancel ──
        await TestConnectionCancelAsync();
    }

    private static async Task TestCommandTimeoutAsync()
    {
        Console.WriteLine("CommandTimeout (3 seconds on slow query):");
        await using var conn = await ConnectionHelper.OpenAsync();
        await using var cmd = conn.CreateCommand(ConnectionHelper.HEAVY_SQL);
        cmd.CommandTimeout = 3;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"  Completed in {sw.Elapsed.TotalSeconds:F2}s");
        }
        catch (NetezzaException ex) when (ex.Message.Contains("Command timeout", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Timed out after {sw.Elapsed.TotalSeconds:F2}s (NetezzaException)");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"  Timed out after {sw.Elapsed.TotalSeconds:F2}s");
        }
    }

    private static async Task TestDefaultCommandTimeoutAsync()
    {
        Console.WriteLine("CommandTimeout (2 seconds via connection.CommandTimeout):");
        await using var conn = await ConnectionHelper.OpenAsync();
        conn.CommandTimeout = TimeSpan.FromSeconds(2);
        await using var cmd = conn.CreateCommand(ConnectionHelper.HEAVY_SQL);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"  Completed in {sw.Elapsed.TotalSeconds:F2}s");
        }
        catch (NetezzaException ex) when (ex.Message.Contains("Command timeout", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Timed out after {sw.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"  Timed out after {sw.Elapsed.TotalSeconds:F2}s");
        }
    }

    private static async Task TestCancellationTokenAsync()
    {
        Console.WriteLine("CancellationToken (3 seconds on slow query):");
        await using var conn = await ConnectionHelper.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var cmd = conn.CreateCommand(ConnectionHelper.HEAVY_SQL);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await cmd.ExecuteNonQueryAsync(cts.Token);
            Console.WriteLine($"  Completed in {sw.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"  Cancelled after {sw.Elapsed.TotalSeconds:F2}s");
        }
        catch (NetezzaException ex) when (ex.Message.Contains("Command timeout", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Timed out after {sw.Elapsed.TotalSeconds:F2}s");
        }
    }

    private static async Task TestConnectionCancelAsync()
    {
        Console.WriteLine("\nConnection-level cancel:");
        await using var conn = await ConnectionHelper.OpenAsync();
        var cancelTask = Task.Run(async () =>
        {
            await Task.Delay(2000);
            Console.WriteLine("  Sending cancel...");
            conn.CancelQuery();
        });

        await using var cmd = conn.CreateCommand(ConnectionHelper.HEAVY_SQL);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"  Completed in {sw.Elapsed.TotalSeconds:F2}s");
        }
        catch (NetezzaException ex) when (
            ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Cancelled in {sw.Elapsed.TotalSeconds:F2}s: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Exception in {sw.Elapsed.TotalSeconds:F2}s: {ex.GetType().Name}: {ex.Message}");
        }

        await cancelTask;
    }
}
