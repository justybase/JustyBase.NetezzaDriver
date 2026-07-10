namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class ConnectionPooling
{
    public static async Task RunAsync()
    {
        var pool = new NzConnectionPool(
            host: ConnectionHelper.Host,
            database: ConnectionHelper.Database,
            user: ConnectionHelper.User,
            password: ConnectionHelper.Password,
            port: ConnectionHelper.Port,
            minPoolSize: 1,
            maxPoolSize: 5,
            connectionIdleTimeoutSeconds: 30
        );

        Console.WriteLine($"Pool created. Max: {pool.MaxPoolSize}");

        // ── Rent a connection ──
        Console.WriteLine("Renting connection...");
        await using (var pooled = await pool.RentAsync())
        {
            await using var cmd = pooled.Connection.CreateCommand("SELECT 1 AS pool_test");
            var result = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"  Query result: {result}");
            Console.WriteLine($"  Active: {pool.ActiveCount}, Idle: {pool.IdleCount}");
        }
        Console.WriteLine($"  After return — Active: {pool.ActiveCount}, Idle: {pool.IdleCount}");

        // ── Rent multiple connections concurrently ──
        Console.WriteLine("Renting 3 connections concurrently...");
        var tasks = new Task<string>[3];
        for (int i = 0; i < 3; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(async () =>
            {
                await using var pooled = await pool.RentAsync();
                await using var cmd = pooled.Connection.CreateCommand("SELECT 1 AS concurrent_test");
                var _ = await cmd.ExecuteScalarAsync();
                return $"  Worker {idx} done";
            });
        }
        foreach (var result in await Task.WhenAll(tasks))
            Console.WriteLine(result);

        Console.WriteLine($"After concurrent — Active: {pool.ActiveCount}, Idle: {pool.IdleCount}");

        // ── Pool info ──
        Console.WriteLine($"Pool stats — Active: {pool.ActiveCount}, Idle: {pool.IdleCount}");

        await pool.DisposeAsync();
        Console.WriteLine("ConnectionPooling completed.");
    }
}
