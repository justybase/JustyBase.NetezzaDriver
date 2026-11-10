# JustyBase.NetezzaDriver

[![NuGet](https://img.shields.io/nuget/v/JustyBase.NetezzaDriver)](https://www.nuget.org/packages/JustyBase.NetezzaDriver)
[![NuGet downloads](https://img.shields.io/nuget/dt/JustyBase.NetezzaDriver)](https://www.nuget.org/packages/JustyBase.NetezzaDriver)
[![CI](https://github.com/justybase/JustyBase.NetezzaDriver/actions/workflows/ci.yml/badge.svg)](https://github.com/justybase/JustyBase.NetezzaDriver/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0%20%7C%2011.0-512BD4)](https://dotnet.microsoft.com/)

A **pure managed ADO.NET driver** for IBM Netezza Performance Server (NPS).
No ODBC bridge, no native client library, and no platform-specific runtime
dependency — just a .NET assembly that speaks the Netezza wire protocol.

The implementation follows the behaviour of [nzpy](https://github.com/IBM/nzpy)
and the wire-protocol conventions established by
[Npgsql](https://github.com/npgsql/npgsql), with additional Netezza-specific
metadata, diagnostics and performance work.

---

## Why this driver

- **100% managed code** — works on Windows, Linux and macOS without installing
  an IBM client.
- **Familiar ADO.NET surface** — `DbConnection`, `DbCommand`, `DbDataReader`,
  `DbTransaction` and `DbParameter`.
- **Sync and async** — every async method accepts a `CancellationToken`.
- **Built for large result sets** — streaming readers, mapped row enumeration,
  bounded allocation and optional lazy column decoding.
- **Production features included** — connection pooling, TLS, command timeouts,
  query cancellation, catalog introspection and structured server diagnostics.
- **AOT-friendly** — the package is marked `IsAotCompatible`.

## Supported frameworks

| Package target | Status |
|---|---|
| .NET 8 | Supported |
| .NET 9 | Supported |
| .NET 10 | Supported |
| .NET 11 | Supported |

## Installation

```bash
dotnet add package JustyBase.NetezzaDriver
```

Or with the Package Manager:

```powershell
Install-Package JustyBase.NetezzaDriver
```

## Quick start

### Synchronous

```csharp
using JustyBase.NetezzaDriver;

using var connection = new NzConnection("user", "password", "host", "database");
connection.Open();

using var command = connection.CreateCommand("SELECT 'Hello from Netezza' AS msg");
using var reader = command.ExecuteReader();

while (reader.Read())
{
    Console.WriteLine(reader.GetString(0));
}
```

### Asynchronous

```csharp
using JustyBase.NetezzaDriver;

await using var connection = new NzConnection("user", "password", "host", "database");
await connection.OpenAsync();

await using var command = connection.CreateCommand("SELECT 'Hello async' AS msg");
await using var reader = await command.ExecuteReaderAsync();

while (await reader.ReadAsync())
{
    Console.WriteLine(reader.GetString(0));
}
```

## Connection configuration

```csharp
// Direct constructor
new NzConnection("user", "password", "host", "database");
new NzConnection("user", "password", "host", "database", port: 5480);

// TLS
new NzConnection(
    "user", "password", "host", "database",
    port: 5480,
    securityLevel: SecurityLevelCode.OnlySecuredSession,
    sslCerFilePath: @"C:\certs\netezza.pem");

// Connection string builder
var builder = new NzConnectionStringBuilder
{
    Host = "host",
    Database = "db",
    UserName = "user",
    Password = "pass",
    Port = 5480,
    Pooling = true,
    MinPoolSize = 1,
    MaxPoolSize = 10
};

using var connection = new NzConnection(builder.ConnectionString);
connection.Open();
```

## Feature overview

| Capability | Supported | Notes |
|---|:---:|---|
| Synchronous ADO.NET | ✅ | `Open`, `ExecuteReader`, `ExecuteScalar`, `ExecuteNonQuery` |
| Asynchronous ADO.NET | ✅ | `OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`, cancellation tokens |
| Named parameters | ✅ | `:name` and `@name` |
| Positional parameters | ✅ | `?` |
| Connection pooling | ✅ | Min/max size, idle timeout, lifetime, validation |
| Transactions | ✅ | `ReadCommitted`, explicit commit/rollback |
| Column metadata | ✅ | Standard schema plus `ProviderType`, `TypeModifier`, declared type |
| Catalog introspection | ✅ | Schemas, tables, columns, views, procedures, keys, sizes, sessions |
| Command timeout | ✅ | Per connection and per command |
| Query cancellation | ✅ | `CancelQuery` and `CancellationToken` |
| TLS / certificate validation | ✅ | Strict validation by default |
| Streaming rows | ✅ | Reader and mapped async enumeration |
| Structured server errors | ✅ | SQLSTATE, severity, detail, hint, raw response |
| AOT compatibility | ✅ | `IsAotCompatible=true` |
| Netezza data types | ✅ | Numeric, text, temporal, interval, bytea and more |

## Parameterized queries

Both named and positional parameters are supported. Parameters are rendered as
escaped SQL literals before execution.

```csharp
// Named parameters
await using var command = connection.CreateCommand(
    "SELECT id, name FROM users WHERE age > :minAge AND city = :city");

command.Parameters.AddWithValue(":minAge", 18);
command.Parameters.AddWithValue(":city", "New York");

await using var reader = await command.ExecuteReaderAsync();

// Positional parameters
await using var command2 = connection.CreateCommand(
    "SELECT count(*) FROM users WHERE status = ?");

command2.Parameters.Add(new NzParameter { Value = "active", IsPositional = true });
var count = await command2.ExecuteScalarAsync();
```

### Parameter type mapping

| .NET value | SQL literal |
|---|---|
| `null` / `DBNull` | `NULL` |
| `bool` | `TRUE` / `FALSE` |
| `int`, `long`, `short` | numeric literal |
| `float`, `double`, `decimal` | numeric literal |
| `string` | escaped string literal |
| `DateTime` | `'yyyy-MM-dd HH:mm:ss.ffffff'` |
| `DateOnly` | `'yyyy-MM-dd'` |
| `TimeOnly`, `TimeSpan` | `'HH:mm:ss'` |
| `byte[]` | `x'hex'` |
| `Guid` | `'guid'` |

See [`src/docs/parameters.md`](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/parameters.md) for the full reference.

## Streaming large result sets

`ExecuteRowsAsync` maps rows while the reader is active, so large results can be
processed without materialising them first.

```csharp
await using var command = connection.CreateCommand(
    "SELECT ID, NAME FROM MY_TABLE");

await foreach (var item in command.ExecuteRowsAsync(
    reader => new Item(reader.GetInt64(0), reader.GetString(1)),
    cancellationToken))
{
    await ProcessAsync(item, cancellationToken);
}
```

The mapper is synchronous; the returned `IAsyncEnumerable<T>` streams rows and
passes cancellation through to database reads. Disposing the enumeration closes
the reader.

## Connection pooling

`NzConnectionPool` provides a thread-safe pool with validation and background
maintenance.

```csharp
var pool = new NzConnectionPool(
    host: "host",
    database: "db",
    user: "user",
    password: "pass",
    port: 5480,
    minPoolSize: 1,
    maxPoolSize: 10,
    connectionIdleTimeoutSeconds: 30);

await using var pooled = await pool.RentAsync();
await using var command = pooled.Connection.CreateCommand("SELECT 1");
var result = await command.ExecuteScalarAsync();
```

| Option | Default | Description |
|---|---:|---|
| `minPoolSize` | `0` | Minimum number of idle connections |
| `maxPoolSize` | `10` | Maximum concurrent connections |
| `connectionIdleTimeoutSeconds` | `30` | Close idle connections after this period |
| `connectionLifetimeSeconds` | `0` | Maximum connection age (`0` = unlimited) |

The pool validates connections, rolls back open transactions when a connection
is returned, and runs maintenance in the background. See
[`src/docs/pooling.md`](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/pooling.md) for details.

## Catalog introspection

`NzConnection.Meta` exposes the Netezza system catalog.

```csharp
var meta = connection.Meta;

var schemas   = await meta.GetSchemasAsync();
var tables    = await meta.GetTablesAsync("ADMIN");
var columns   = await meta.GetColumnsAsync("DIMDATE", "ADMIN");
var views     = await meta.GetViewsAsync();
var procedures = await meta.GetProceduresAsync();
var distKey   = await meta.GetDistributionKeyAsync("DIMDATE", "ADMIN");
var sizes     = await meta.GetTableSizesAsync();
var sessions  = await meta.GetSessionsAsync();
var objects   = await meta.SearchObjectsAsync("DIM%");
var tableDdl  = await meta.GetTableDdlAsync("DIMDATE", "ADMIN");
```

Additional methods cover the current database and schema, sequences, users,
groups, query history, organize keys, table keys, comments and ownership. DDL
helpers reconstruct SQL for tables, views, procedures, external tables and
synonyms. See [`src/docs/metadata_api.md`](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/metadata_api.md) for the
full API.

## Column metadata

`NzDataReader` exposes standard ADO.NET schema information and extended
Netezza metadata.

```csharp
var schema = reader.GetColumnSchema();          // ReadOnlyCollection<DbColumn>
int oid = reader.GetProviderType(0);            // e.g. 23 = INTEGER
string declared = reader.GetDeclaredTypeName(0); // e.g. "VARCHAR(32)"
```

Each `NzDbColumn` includes `ProviderType`, `TypeModifier` and
`DeclaredTypeName`.

## Timeouts and cancellation

```csharp
// Connection-wide default
connection.DefaultCommandTimeout = TimeSpan.FromSeconds(30);

// Per-command override
command.CommandTimeout = 10;

// Token-based cancellation
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await using var reader = await command.ExecuteReaderAsync(cts.Token);

// Connection-level cancellation
connection.CancelQuery();
```

Timeouts are absolute and leave the session reusable when the protocol can be
resynchronised. See [`src/docs/timeout_and_cancel.md`](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/timeout_and_cancel.md)
for details.

## TLS and security

TLS certificate validation is **strict by default**. Certificates with policy or
chain errors are rejected.

```csharp
var connection = new NzConnection(
    "user", "password", "host", "database",
    securityLevel: SecurityLevelCode.OnlySecuredSession,
    sslCerFilePath: @"C:\path\to\certificate.pem");

connection.Open();
```

## ADO.NET coverage

| Type | Implementation |
|---|---|
| `DbConnection` | `NzConnection` |
| `DbCommand` | `NzCommand` |
| `DbDataReader` | `NzDataReader` |
| `DbParameter` / `DbParameterCollection` | Named and positional parameters |
| `DbTransaction` | `NzTransaction` |
| `IsolationLevel` | `ReadCommitted` and `Unspecified` |
| `ChangeDatabase` | `SET CATALOG`; rejected during an active transaction |

### NULL handling

Since v1.4.0, reading a `NULL` value through a non-nullable typed getter throws
`InvalidCastException`. Check `reader.IsDBNull(i)` or request a nullable type.

## Performance

The benchmark project compares synchronous and asynchronous readers on large
result sets.

```bash
dotnet run -c Release -f net10.0 \
  --project ./src/JustyBase.NetezzaDriver.Benchmarks \
  -- --filter *AsyncReaderBench*
```

Sample results (Windows 11, .NET 10):

| Scenario | Sync mean | Sync allocated | Async mean | Async allocated | Time ratio |
|---|---:|---:|---:|---:|---:|
| Large mixed, 500k rows | 1.011 s | 49.66 MB | 1.006 s | 52.27 MB | 0.99x |
| Numeric scalars, 300k rows | 2.158 s | 29.76 MB | 2.164 s | 31.13 MB | 1.00x |
| Temporal/null values, 300k rows | 1.831 s | 28.84 MB | 1.867 s | 30.12 MB | 1.02x |
| Textual, 250k rows | 1.465 s | 29.33 MB | 1.514 s | 29.45 MB | 1.03x |

Async adds roughly 1–3% CPU time and 0–5% allocations in these scenarios.

## Server diagnostics

`NetezzaException` exposes structured backend information:

- `Message` — primary server message
- `SqlState`, `Severity`, `Detail`, `Hint` — structured fields when available
- `RawResponse` — complete decoded backend payload
- `Diagnostics` — all protocol fields, including fields unknown to this driver

`command.Notices` contains notices from the most recent execution; the
connection-wide `NoticeReceived` event remains available.

## Testing

```bash
# Offline unit tests
dotnet test ./src/JustyBase.NetezzaDriver.Tests/JustyBase.NetezzaDriver.Tests.csproj \
  --filter "Category=Unit"

# Integration tests (live Netezza required)
dotnet test ./src/JustyBase.NetezzaDriver.Tests/JustyBase.NetezzaDriver.Tests.csproj \
  --filter "Category=Integration"
```

Live tests read their configuration from:

- `NZ_DEV_HOST`
- `NZ_DEV_PORT` (default `5480`)
- `NZ_DEV_DB`
- `NZ_DEV_USER`
- `NZ_DEV_PASSWORD`

No credentials or lab addresses are embedded in the repository.

## Cross-driver conformance

A shared conformance corpus runs the same cases against the C#, Rust, Node and
Python drivers. It provisions its own `CONFORMANCE` schema on a live appliance.

```powershell
../conformance/run-all.ps1 -Profile smoke -Bootstrap
```

The C# runner is available at
`tools/JustyBase.NetezzaDriver.Conformance` and produces a case matrix, known
divergences and JUnit XML.

## Documentation

- [Parameters](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/parameters.md)
- [Connection pooling](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/pooling.md)
- [Metadata API](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/metadata_api.md)
- [Timeouts and cancellation](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/timeout_and_cancel.md)
- [Runnable examples](https://github.com/justybase/JustyBase.NetezzaDriver/tree/master/src/examples/JustyBase.NetezzaDriver.Examples)

## Contributing

Issues and pull requests are welcome. Please include the driver version, .NET
version, server version and a minimal reproducer when reporting a problem.

Before opening a pull request:

```bash
dotnet build ./src/JustyBase.NetezzaDriver.slnx -c Release
dotnet test ./src/JustyBase.NetezzaDriver.Tests/JustyBase.NetezzaDriver.Tests.csproj \
  -c Release --filter "Category=Unit"
```

## License

Copyright 2025–2026 Krzysztof Duśko  
Copyright 2019–2020 IBM, Inc.

Licensed under the [Apache License, Version 2.0](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/LICENSE).

## Contact

- Bug reports and feature requests:
  [GitHub Issues](https://github.com/justybase/JustyBase.NetezzaDriver/issues)
- NuGet package:
  [JustyBase.NetezzaDriver](https://www.nuget.org/packages/JustyBase.NetezzaDriver)
