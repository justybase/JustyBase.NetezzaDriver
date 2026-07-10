# Connection Pooling

`NzConnectionPool` provides a thread-safe, semaphore-based connection pool for reusing Netezza connections.

## Features

- Configurable minimum and maximum pool size
- Connection idle timeout (connections idle longer than this are closed)
- Connection max lifetime (connections older than this are replaced)
- Background maintenance timer that cleans idle connections every 30 seconds
- Connection validation via `SELECT 1` before returning to consumer
- Automatic rollback of open transactions when connections are returned

## Basic usage

```csharp
var pool = new NzConnectionPool(
    host: "netezza-host",
    database: "mydb",
    user: "admin",
    password: "password",
    port: 5480,
    minPoolSize: 1,
    maxPoolSize: 10,
    connectionIdleTimeoutSeconds: 30
);

// Rent a connection
await using var pooled = await pool.RentAsync();
await using var cmd = pooled.Connection.CreateCommand("SELECT 1");
var result = await cmd.ExecuteScalarAsync();
Console.WriteLine(result);

// Connection is automatically returned to pool when DisposeAsync completes
```

## Using await using for auto-return

```csharp
await using (var c = await pool.RentAsync())
{
    await using var cmd = c.Connection.CreateCommand("SELECT count(*) FROM users");
    Console.WriteLine(await cmd.ExecuteScalarAsync());
}
// c.DisposeAsync() returns the connection to the pool
```

## Configuration via NzConnectionStringBuilder

```csharp
var builder = new NzConnectionStringBuilder
{
    Host = "netezza-host",
    Database = "mydb",
    UserName = "admin",
    Password = "password",
    Port = 5480,
    Pooling = true,
    MinPoolSize = 2,
    MaxPoolSize = 20,
    ConnectionIdleTimeout = 60,
    ConnectionLifetime = 300
};

var pool = new NzConnectionPool(builder);
```

## Pool parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `minPoolSize` | `0` | Minimum number of connections to maintain in the pool |
| `maxPoolSize` | `10` | Maximum number of connections allowed in the pool |
| `connectionIdleTimeoutSeconds` | `30` | Seconds before an idle connection is closed |
| `connectionLifetimeSeconds` | `0` | Max lifetime in seconds (0 = unlimited) |

Properties available at runtime:

| Property | Description |
|----------|-------------|
| `ActiveCount` | Number of connections currently rented out |
| `IdleCount` | Number of connections available in the pool |
| `MaxPoolSize` | Maximum pool capacity |

## Pool lifecycle

```csharp
// Create
var pool = new NzConnectionPool(...);

// Use
await using var c = await pool.RentAsync();
// ... use connection ...
await c.DisposeAsync();  // returns to pool

// Clear all idle connections
await pool.ClearAsync();

// Dispose entire pool (closes all idle + active connections)
await pool.DisposeAsync();
```

## Rollback on return

When a connection is returned to the pool, if it has an open transaction (`InTransaction == true`), the pool automatically executes `ROLLBACK` before returning the connection to the idle queue. If the rollback fails, the connection is closed and a new one will be created on the next `RentAsync`.

## Thread safety

`NzConnectionPool` is fully thread-safe:
- `RentAsync` uses a `SemaphoreSlim` to limit concurrent connections
- `_idle` uses `ConcurrentQueue<NzConnection>`
- `_active` uses `ConcurrentDictionary<int, NzConnection>`
- All counters use `Interlocked` operations

## Performance notes

- Pool validation (`SELECT 1`) runs synchronously during `RentAsync`. For low-latency requirements, consider the trade-off between validation overhead and catching dead connections.
- The maintenance timer runs every 30 seconds on a thread-pool thread.
