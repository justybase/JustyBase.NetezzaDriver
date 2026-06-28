# JustyBase.NetezzaDriver

[![NuGet](https://img.shields.io/nuget/v/JustyBase.NetezzaDriver)](https://www.nuget.org/packages/JustyBase.NetezzaDriver)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)

A **pure C# ADO.NET driver** for IBM Netezza Performance Server (NPS) — no native dependencies, no ODBC bridge. The code is based on [nzpy](https://github.com/IBM/nzpy) (Python) and [npgsql](https://github.com/npgsql/npgsql) (PostgreSQL).

## Features at a glance

| Feature | Status | Notes |
|---------|--------|-------|
| **Sync ADO.NET** (`Open`, `ExecuteReader`, `ExecuteScalar`) | ✅ | Full `DbConnection`, `DbCommand`, `DbDataReader` |
| **Async ADO.NET** (`OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`) | ✅ | CancellationToken support on all async methods |
| **Parameterized queries** (`:name`, `@name`, `?`) | ✅ | C# to SQL literal rendering |
| **Connection pooling** (`NzConnectionPool`) | ✅ | Configurable min/max, idle timeout, lifetime, maintenance |
| **Transactions** (`BeginTransaction`, `Commit`, `Rollback`) | ✅ | `ReadCommitted` isolation, `AutoCommit` toggle |
| **Column metadata** (`GetColumnSchema`, `GetDeclaredTypeName`) | ✅ | Extended via `NzDbColumn` with `ProviderType`, `TypeModifier` |
| **Catalog introspection** (`NzMetadata`) | ✅ | Schemas, tables, columns, views, procs, sizes, sessions, search |
| **Command timeout** (`CommandTimeout`, `DefaultCommandTimeout`) | ✅ | Per-command and per-connection |
| **Query cancel** (`CancelQuery`, `CancellationToken`) | ✅ | Connection-level and token-based |
| **SSL/TLS** | ✅ | Certificate validation, `SecurityLevelCode` flags |
| **Netezza data types** | ✅ | Numeric, varchar, nchar, timestamp, date, time, interval, bytea, etc. |
| **AOT-compatible** | ✅ | `IsAotCompatible=true` |

## Installation

```bash
dotnet add package JustyBase.NetezzaDriver
```

The package targets **.NET 8**, **.NET 9**, and **.NET 10**.

## Quick start

### Synchronous

```csharp
using JustyBase.NetezzaDriver;

using var conn = new NzConnection("username", "password", "host", "database");
conn.Open();

using var cmd = conn.CreateCommand("SELECT 'Hello from Netezza' AS msg");
using var reader = cmd.ExecuteReader();
while (reader.Read())
    Console.WriteLine(reader.GetString(0));
```

### Asynchronous

```csharp
using JustyBase.NetezzaDriver;

await using var conn = new NzConnection("username", "password", "host", "database");
await conn.OpenAsync();

await using var cmd = conn.CreateCommand("SELECT 'Hello async' AS msg");
await using var reader = await cmd.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine(reader.GetString(0));
```

### Connection string

```csharp
// Constructors
new NzConnection("user", "password", "host", "database");
new NzConnection("user", "password", "host", "database", port: 5480);
new NzConnection("user", "password", "host", "database", port: 5480,
    securityLevel: SecurityLevelCode.OnlySecuredSession,
    sslCerFilePath: @"C:\certs\netezza.pem");

// Or via NzConnectionStringBuilder
var builder = new NzConnectionStringBuilder
{
    Host = "host", Database = "db", UserName = "user", Password = "pass",
    Port = 5480, Pooling = true, MinPoolSize = 1, MaxPoolSize = 10
};
using var conn = new NzConnection(builder.ConnectionString);
conn.Open();
```

## Parameterized queries

Both **named** (`:name`, `@name`) and **positional** (`?`) parameters are supported. Parameters are rendered inline as SQL literals.

```csharp
// Named parameters
await using var cmd = conn.CreateCommand(
    "SELECT id, name FROM users WHERE age > :minAge AND city = :city");
cmd.Parameters.AddWithValue(":minAge", 18);
cmd.Parameters.AddWithValue(":city", "New York");
await using var reader = await cmd.ExecuteReaderAsync();

// Positional parameters
await using var cmd2 = conn.CreateCommand(
    "SELECT count(*) FROM users WHERE status = ?");
cmd2.Parameters.Add(new NzParameter { Value = "active", IsPositional = true });
var count = await cmd2.ExecuteScalarAsync();
```

**C# to SQL type mapping:**

| C# type | SQL literal | Example |
|---------|-------------|---------|
| `null` / `DBNull` | `NULL` | `NULL` |
| `bool` | `TRUE` / `FALSE` | `TRUE` |
| `int`, `long`, `short` | number | `42` |
| `float`, `double`, `decimal` | number | `3.14` |
| `string` | `'escaped'` | `'O''Brien'` |
| `DateTime` | `'yyyy-MM-dd HH:mm:ss.ffffff'` | `'2024-01-15 10:30:00.000000'` |
| `DateOnly` | `'yyyy-MM-dd'` | `'2024-01-15'` |
| `TimeOnly` | `'HH:mm:ss'` | `'14:30:00'` |
| `TimeSpan` | `'HH:mm:ss'` | `'14:30:00'` |
| `byte[]` | `x'hex'` | `x'deadbeef'` |
| `Guid` | `'guid'` | `'...'` |

See [docs/parameters.md](scr/docs/parameters.md) for details.

## Connection pooling

`NzConnectionPool` provides a thread-safe, semaphore-based pool.

```csharp
var pool = new NzConnectionPool(
    host: "host", database: "db", user: "user", password: "pass",
    port: 5480, minPoolSize: 1, maxPoolSize: 10,
    connectionIdleTimeoutSeconds: 30);

// Rent a connection (auto-return via await using)
await using var pooled = await pool.RentAsync();
await using var cmd = pooled.Connection.CreateCommand("SELECT 1");
var result = await cmd.ExecuteScalarAsync();
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `minPoolSize` | `0` | Maintain at least this many idle connections |
| `maxPoolSize` | `10` | Maximum concurrent connections |
| `connectionIdleTimeoutSeconds` | `30` | Close idle connections after this |
| `connectionLifetimeSeconds` | `0` | Max connection age (0 = unlimited) |

The pool validates connections with `SELECT 1`, automatically rolls back open transactions on return, and runs a background maintenance timer every 30 seconds.

See [docs/pooling.md](scr/docs/pooling.md) for details.

## Column metadata

`NzDataReader` exposes extended column metadata beyond the standard ADO.NET schema:

```csharp
// Standard ADO.NET
var schema = reader.GetColumnSchema(); // ReadOnlyCollection<DbColumn>

// Extended methods
int oid = reader.GetProviderType(0);         // OID (e.g., 23 for INTEGER)
string declared = reader.GetDeclaredTypeName(0); // "VARCHAR(32)", "NUMERIC(10,2)"
```

Each `NzDbColumn` in the schema includes `ProviderType`, `TypeModifier`, and `DeclaredTypeName`.

## Catalog introspection

`NzConnection.Meta` provides async access to Netezza system catalog views:

```csharp
var meta = conn.Meta;

var schemas   = await meta.GetSchemasAsync();
var tables    = await meta.GetTablesAsync("ADMIN");
var columns   = await meta.GetColumnsAsync("DIMDATE", "ADMIN");
var views     = await meta.GetViewsAsync();
var procs     = await meta.GetProceduresAsync();
var distKey   = await meta.GetDistributionKeyAsync("DIMDATE", "ADMIN");
var sizes     = await meta.GetTableSizesAsync();
var sessions  = await meta.GetSessionsAsync();
var search    = await meta.SearchObjectsAsync("DIM%");
```

See [docs/metadata_api.md](scr/docs/metadata_api.md) for full API reference.

## Timeout and cancel

Per-command and per-connection timeout:

```csharp
// Per-connection default
connection.DefaultCommandTimeout = TimeSpan.FromSeconds(30);

// Per-command override
cmd.CommandTimeout = 10; // seconds

// CancellationToken
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await using var reader = await cmd.ExecuteReaderAsync(cts.Token);

// Connection-level cancel
connection.CancelQuery();
```

See [docs/timeout_and_cancel.md](scr/docs/timeout_and_cancel.md) for details.

## ADO.NET support

| Interface / Class | Status | Notes |
|------------------|--------|-------|
| `DbConnection` (`NzConnection`) | ✅ | `Open`/`Close`, `OpenAsync`, `BeginTransaction`, `DataSource` |
| `DbCommand` (`NzCommand`) | ✅ | `CommandType.Text`, parameterized queries |
| `DbDataReader` (`NzDataReader`) | ✅ | Typed getters, `GetColumnSchema`, `GetBytes`, `GetChars` |
| `DbParameter` / `DbParameterCollection` | ✅ | Named + positional parameters |
| `DbTransaction` (`NzTransaction`) | ✅ | `Commit`, `Rollback` |
| `IsolationLevel.ReadCommitted` | ✅ | Also accepts `Unspecified` as ReadCommitted |
| `ChangeDatabase` | ✅ | Uses Netezza `SET CATALOG`; not allowed during an active transaction |

## Behavioral Changes (since v1.4.0)

Starting from version 1.4.0, retrieving a `NULL` column value as a non-nullable type (e.g., `reader.GetString(0)` when the value is `DBNull`) throws `InvalidCastException` instead of returning a default value. Always check `reader.IsDBNull(i)` before calling typed getters, or use nullable types.

## Security (SSL/TLS)

TLS certificate validation is **strict by default** — certificates with TLS policy errors are rejected.

```csharp
var conn = new NzConnection("user", "password", "host", "database",
    securityLevel: SecurityLevelCode.OnlySecuredSession,
    sslCerFilePath: @"C:\path\to\certificate.pem");
conn.Open();
```

## Benchmark (sync vs async DataReader)

The benchmark project at `scr/JustyBase.NetezzaDriver.Benchmarks` compares sync (`ExecuteReader`) vs async (`ExecuteReaderAsync`) for large data reads.

```bash
dotnet run -c Release -f net10.0 --project .\scr\JustyBase.NetezzaDriver.Benchmarks -- --filter *AsyncReaderBench*
```

Sample results (net10.0, BenchmarkDotNet, Windows 11):

| Scenario            | Sync Mean | Sync Allocated | Async Mean | Async Allocated | Time Ratio | Alloc Ratio |
|---------------------|----------:|---------------:|-----------:|----------------:|-----------:|------------:|
| LargeMixed_500k     | 1.011 s   | 49.66 MB       | 1.006 s    | 52.27 MB        | 0.99x      | 1.05x       |
| NumericScalars_300k | 2.158 s   | 29.76 MB       | 2.164 s    | 31.13 MB        | 1.00x      | 1.05x       |
| TemporalNulls_300k  | 1.831 s   | 28.84 MB       | 1.867 s    | 30.12 MB        | 1.02x      | 1.04x       |
| Textual_250k        | 1.465 s   | 29.33 MB       | 1.514 s    | 29.45 MB        | 1.03x      | 1.00x       |

Async overhead is negligible (~1–3% time, 0–5% allocations).

## Testing

```bash
# Unit tests only
dotnet test .\scr\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Unit"

# Integration tests (requires live Netezza)
dotnet test .\scr\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Integration"
```

Integration tests read connection settings from environment variables:
- `NZ_DEV_HOST`, `NZ_DEV_PORT` (default 5480), `NZ_DEV_DB`, `NZ_DEV_USER`, `NZ_DEV_PASSWORD`

## License

Copyright 2025–2026 Krzysztof Duśko  
Copyright 2019–2020 IBM, Inc.

Licensed under the [Apache License, Version 2.0](LICENSE).

## Contact

For questions, bug reports, or feature requests, [open an issue on GitHub](https://github.com/KrzysztofDusko/JustyBase.NetezzaDriver/issues).

## Documentation

- [Parameters](scr/docs/parameters.md) — Named and positional parameter reference
- [Pooling](scr/docs/pooling.md) — Connection pool configuration and lifecycle
- [Metadata API](scr/docs/metadata_api.md) — Catalog introspection methods
- [Timeout & Cancel](scr/docs/timeout_and_cancel.md) — Command timeout, CancellationToken, CancelQuery
- [Examples project](scr/examples/JustyBase.NetezzaDriver.Examples/) — Runnable C# examples
