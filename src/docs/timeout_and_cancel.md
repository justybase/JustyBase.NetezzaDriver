# Timeout and Cancel

## Per-command timeout

Each `NzCommand` supports a `CommandTimeout` property (in seconds). If the query does not complete within the timeout, the driver cancels the query and throws `NetezzaException` with a timeout message.

```csharp
using var cmd = conn.CreateCommand("SELECT * FROM heavy_query");
cmd.CommandTimeout = 10; // seconds
try
{
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        Process(reader);
}
catch (NetezzaException ex) when (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Query timed out after 10 seconds");
}
```

## Per-connection default timeout

Set a default timeout for all commands created from a connection:

```csharp
connection.DefaultCommandTimeout = TimeSpan.FromSeconds(30);

// Commands inherit the connection's default
using var cmd1 = conn.CreateCommand("SELECT 1");
Console.WriteLine(cmd1.CommandTimeout); // 30

// Per-command override still works
cmd1.CommandTimeout = 5;
```

If `DefaultCommandTimeout` is not set explicitly, the default is 60 seconds. A timeout of `0` means "no timeout".

## CancellationToken

All async methods accept `CancellationToken` for cooperative cancellation:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
while (await reader.ReadAsync(cts.Token))
{
    // process
}
```

## Connection-level cancel

To cancel a running query without disposing the connection:

```csharp
connection.CancelQuery();
```

This sends a cancel request to the Netezza server. The connection remains open and can be used for subsequent queries.

## How timeout works

1. A `CancellationTokenSource` is created with the `CommandTimeout` value
2. On timeout, the cancellation token is triggered
3. The driver sends a cancel request to the server
4. `NetezzaException` is thrown to the caller
5. The connection remains usable (not closed)

## Best practices

- For interactive applications, use `CancellationToken` from the UI layer
- For batch processing, set `CommandTimeout` to a reasonable value for your workload
- A timeout of `0` means "no timeout" (wait indefinitely)
- Timeouts that are too short can cause false positives on slow queries
- Timeouts that are too long can leave connections blocked
- Handle timeout failures as `NetezzaException`
