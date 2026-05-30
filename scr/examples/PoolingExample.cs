// Example: Using NzConnectionPool for connection pooling

using JustyBase.NetezzaDriver;

// Create a pool with max 5 connections, idle timeout 30s
var pool = new NzConnectionPool(
    host: "host",
    database: "db",
    user: "user",
    password: "password",
    port: 5480,
    minPoolSize: 1,
    maxPoolSize: 5,
    connectionIdleTimeoutSeconds: 30
);

// Rent a connection from the pool
var pooled = await pool.RentAsync();
try
{
    await using var cmd = pooled.Connection.CreateCommand("SELECT count(*) FROM my_table");
    var result = await cmd.ExecuteScalarAsync();
    Console.WriteLine($"Row count: {result}");
}
finally
{
    // Return connection to pool (not disposed!)
    await pooled.DisposeAsync();
}

// Or use await using for auto-return:
await using (var c = await pool.RentAsync())
{
    await using var cmd = c.Connection.CreateCommand("SELECT 1");
    Console.WriteLine(await cmd.ExecuteScalarAsync());
}

await pool.DisposeAsync();
