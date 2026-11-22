# JustyBase.NetezzaDriver

[![NuGet](https://img.shields.io/nuget/v/JustyBase.NetezzaDriver)](https://www.nuget.org/packages/JustyBase.NetezzaDriver)
[![NuGet downloads](https://img.shields.io/nuget/dt/JustyBase.NetezzaDriver)](https://www.nuget.org/packages/JustyBase.NetezzaDriver)
[![CI](https://github.com/justybase/JustyBase.NetezzaDriver/actions/workflows/ci.yml/badge.svg)](https://github.com/justybase/JustyBase.NetezzaDriver/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0%20%7C%2011.0-512BD4)](https://dotnet.microsoft.com/)

<p align="center">
  <a href="https://www.nuget.org/packages/JustyBase.NetezzaDriver"><img src="images/netezza-driver-badge.png" alt="JustyBase Netezza ADO.NET driver" width="420"></a>
</p>

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

// TLS with the cross-driver sslmode spelling
new NzConnection(
    "Host=host;Database=db;User=user;Password=pass;" +
    "SslMode=verify-full;SslCertFilePath=C:\\certs\\netezza-ca.pem");

// Connection string builder
var builder = new NzConnectionStringBuilder
{
    Host = "host",
    Database = "db",
    UserName = "user",
    Password = "pass",
    Port = 5480,
    BulkInsert = true,   // NzBulkCopy external-table transfer (default)
    SslMode = SslMode.VerifyFull,
    SslCertFilePath = @"C:\certs\netezza-ca.pem",
    CharVarcharEncoding = CharEncoding.Latin1,
};

using var connection = new NzConnection(builder.ConnectionString);
connection.Open();
```

Pooling is explicit: `NzConnection` does not create a pool. Pass the same
builder to `NzConnectionPool` when you want pooled physical connections.

## Feature overview

| Capability | Supported | Notes |
|---|:---:|---|
| Synchronous ADO.NET | ✅ | `Open`, `ExecuteReader`, `ExecuteScalar`, `ExecuteNonQuery` |
| Asynchronous ADO.NET | ✅ | `OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`, cancellation tokens |
| Named parameters | ✅ | `:name` and `@name` |
| Positional parameters | ✅ | `?` |
| Connection pooling | ✅ | Min/max size, idle timeout, lifetime, validation |
| Transactions | ✅ | `ReadCommitted`, explicit commit/rollback, no nesting |
| Column metadata | ✅ | Standard schema plus `ProviderType`, `TypeModifier`, declared type |
| Catalog introspection | ✅ | Schemas, tables, columns, views, procedures, keys, sizes, sessions |
| Command timeout | ✅ | Per connection and per command |
| Query cancellation | ✅ | `CancelQuery` and `CancellationToken` |
| TLS / certificate validation | ✅ | `sslmode=disable/prefer/require/verify-ca/verify-full` |
| Binary text encoding | ✅ | `CharVarcharEncoding=latin1/utf8` for DBOS `CHAR`/`VARCHAR` |
| Streaming rows | ✅ | Reader and mapped async enumeration |
| Bulk copy | ✅ | `NzBulkCopy` streams text rows through the external-table protocol |
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

### Result type mapping

`NUMERIC` columns whose declared precision is at most 28 are returned as
`decimal` and `GetFieldType` reports `typeof(decimal)`. Precision 29–38 is
returned as the server's exact decimal text (`string`), because `System.Decimal`
has only 29 significant digits and silently rounds wider values. The CLR type is
chosen from the declared precision, never from an individual row value.

`CHAR`/`VARCHAR` values on the binary (DBOS) path are decoded as Latin-1 by
default, matching the Rust, Node and Python drivers. Databases that store those
columns as UTF-8 can set `CharVarcharEncoding=utf8` in the connection string.
`NCHAR`/`NVARCHAR` and the text result path always use UTF-8.

## Bulk inserts

Use `NzBulkCopy` to insert positional rows using Netezza's external-table
transfer protocol. For two or more supported rows it sends one
`INSERT … SELECT … FROM EXTERNAL` statement and streams the payload over the
existing connection; it does not create a local file or a second connection.

```csharp
var bulk = connection.CreateBulkCopy();
bulk.DestinationTableName = "ADMIN.TARGET";
bulk.ColumnNames.Add("ID");
bulk.ColumnNames.Add("NAME");
bulk.ColumnNames.Add("CREATED");

int inserted = await bulk.WriteToServerAsync(new object?[][]
{
    new object?[] { 1, "one", new DateOnly(2026, 11, 22) },
    new object?[] { 2, "zażółć 😀", new DateOnly(2026, 11, 23) }
});
```

`WriteToServer` also accepts a `DataTable` or `IDataReader`; the async API
accepts a `DataTable`, positional rows or a `DbDataReader`. If `ColumnNames` is
empty, source values are assigned in the table's column order. Supported
values include strings, numbers, booleans, `DateOnly`, `TimeOnly`, `DateTime`,
`TimeSpan` and `Guid`; `null`/`DBNull` are preserved. Values that cannot be
represented safely by the text import format (for example, `byte[]`, non-finite
floating-point values or unsupported control characters) use row-by-row
`INSERT` fallback. One-row writes also use the fallback.

Source rows are buffered in memory before transfer so the driver can infer
external-column widths and determine whether fallback is required before
starting the statement. External-table bulk copy is enabled by default. To
force row-by-row inserts, set `BulkInsert = false` in
`NzConnectionStringBuilder` (or `BulkInsert=False` in the connection string).
The bulk statement is atomic at the statement level; fallback inserts follow
the connection's normal transaction/autocommit behavior.

Bulk copy is **explicit**: the driver does not detect multiple `ExecuteNonQuery`
calls or rewrite multi-statement `CommandText`. Use `NzBulkCopy` to opt in.

See [`src/docs/bulk_copy.md`](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/bulk_copy.md) for the full API and behavior reference.

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
is returned, and runs maintenance in the background. The `Pooling` key alone
does not make `NzConnection` pooled; create a pool explicitly. See
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
`DeclaredTypeName`. The returned collection and column objects are immutable
and cached for the current result set, so repeated schema queries do not
rebuild the metadata graph. `NzDataReader` implements
`IDbColumnSchemaGenerator`, so the standard `DbDataReaderExtensions` path
returns the same native objects instead of `DataRowDbColumn` wrappers.

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

The driver implements the shared `sslmode` vocabulary:
`disable`, `prefer` (default), `require`, `verify-ca` and `verify-full`.
Certificate validation is **strict by default**. The Java driver also accepts
`allow`; this driver does not implement it.

| `SslMode` | Handshake | Server certificate |
|---|---|---|
| `disable` | plaintext only; fails if the server requires TLS | not checked |
| `prefer` (default) | plaintext, upgraded if the server requires TLS | chain and host |
| `require` | TLS required | not checked |
| `verify-ca` | TLS required | chain only (host name may differ) |
| `verify-full` | TLS required | chain and host |

```csharp
var connection = new NzConnection(
    "Host=host;Database=db;User=user;Password=pass;" +
    "SslMode=verify-full;SslCertFilePath=C:\\certs\\netezza-ca.pem");

connection.Open();
```

`SslCertFilePath` is a PEM bundle of trusted CA certificates, matching the
`sslCerFilePath` option in the other drivers. `RejectUnauthorized=false` is
available for private labs, but combining it with `verify-ca` or `verify-full`
is rejected instead of silently weakening the requested policy. The legacy
`SecurityLevel=PreferredUnsecured|OnlyUnsecuredSession|PreferredSecuredSession|OnlySecuredSession`
spelling and constructor enum remain available; `SslMode` and `SecurityLevel`
cannot be combined in one connection string.

## ADO.NET coverage

| Type | Implementation |
|---|---|
| `DbConnection` | `NzConnection` |
| `DbCommand` | `NzCommand` |
| `DbDataReader` | `NzDataReader` |
| `DbParameter` / `DbParameterCollection` | Named and positional parameters |
| `DbTransaction` | `NzTransaction` |
| `DbProviderFactory` | `NzFactory.Instance` |
| `DbConnectionStringBuilder` | `NzConnectionStringBuilder` |
| `IsolationLevel` | `ReadCommitted` and `Unspecified`; other levels and nested transactions throw |
| `ChangeDatabase` | `SET CATALOG`; rejected during an active transaction |

### Provider factory and connection strings

`NzFactory` implements `DbProviderFactory`, so the driver can be resolved
through the standard ADO.NET factory model:

```csharp
DbProviderFactories.RegisterFactory(
    "JustyBase.NetezzaDriver", NzFactory.Instance);

DbConnection connection = NzFactory.Instance.CreateConnection()!;
connection.ConnectionString =
    "Host=host;Database=db;User=user;Password=pass";
connection.Open();
```

`NzConnectionStringBuilder` derives from `DbConnectionStringBuilder`.
Passwords containing semicolons round-trip through standard quoting
(`Password="p;ass"`) and the legacy `Password={p;ass}` form is still accepted.
The standard indexer, `TryGetValue`, `Remove` and `ConnectionString` members
are available.

`CommandTimeout` is per command: setting it on one command does not change the
connection default or another command. `ConnectionString` may be assigned while
the connection is closed; assigning it while open throws
`InvalidOperationException`.

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

Earlier optimization passes, including rejected experiments and allocation
details, are recorded in
[PERFORMANCE.md](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/PERFORMANCE.md).

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
- `NZ_DEV_DB` (or `NZ_DEV_DATABASE`)
- `NZ_DEV_USER`
- `NZ_DEV_PASSWORD`

No credentials or lab addresses are embedded in the repository. Live tests fail
fast when the variables are missing. `NZ_DEV_ALLOW_LAB_DEFAULTS=1` opts into the
disposable-lab defaults and must not be used in CI. See
[CONTRIBUTING.md](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/CONTRIBUTING.md)
for the test categories and benchmark commands.

The unit suite includes byte-exact replays of the shared golden wire corpus
(`src/JustyBase.NetezzaDriver.TestSupport/Fixtures/Wire`), fault injection for
errors, notices, command timeouts and truncated responses, and seeded
property/fuzz tests. Set `NZ_FUZZ_ITERATIONS` to run longer fuzz campaigns
locally; CI uses the default 250 iterations.

Quality gates mirror the Rust driver's CI:

```bash
dotnet format ./src/JustyBase.NetezzaDriver.slnx --verify-no-changes
dotnet test ./src/JustyBase.NetezzaDriver.Tests/JustyBase.NetezzaDriver.Tests.csproj \
  -c Release -f net10.0 --filter "Category=Unit" \
  --collect:"XPlat Code Coverage" --results-directory TestResults/coverage
pwsh ./eng/check-coverage.ps1 -ResultsDirectory TestResults/coverage
```

The coverage script enforces two gates: 70% for the offline protocol primitives
(decoders, reader and parameter formatting) and a ratcheting 55% floor for the
whole driver. Bulk copy, external tables and catalog DDL need a live appliance
and are covered by the conformance profile rather than by offline line
coverage.

## Cross-driver conformance

A shared conformance corpus runs the same cases against the C#, Rust, Node and
Python drivers. It provisions its own `CONFORMANCE` schema on a live appliance.

```powershell
../conformance/run-all.ps1 -Profile smoke -Bootstrap
```

The C# runner is available at
`tools/JustyBase.NetezzaDriver.Conformance` and produces a case matrix, known
divergences and JUnit XML. Handshake failures can be exercised without an
appliance; the vendored case file lives in `eng/conformance/cases`:

```powershell
dotnet run --project tools\JustyBase.NetezzaDriver.Conformance -- \
  --profile handshake --offline --cases-dir eng\conformance\cases
```

## Documentation

- [Connection options](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/connection_options.md)
- [TLS and text encoding](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/tls_and_encoding.md)
- [Data types and CLR mapping](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/data_types.md)
- [Parameters](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/parameters.md)
- [Connection pooling](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/pooling.md)
- [Bulk inserts](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/bulk_copy.md)
- [Metadata API](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/metadata_api.md)
- [Timeouts and cancellation](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/src/docs/timeout_and_cancel.md)
- [Runnable examples](https://github.com/justybase/JustyBase.NetezzaDriver/tree/master/src/examples/JustyBase.NetezzaDriver.Examples)
- [Contributing](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/CONTRIBUTING.md)
- [Security policy](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/SECURITY.md)
- [Changelog](https://github.com/justybase/JustyBase.NetezzaDriver/blob/master/CHANGELOG.md)

## Contributing

Issues and pull requests are welcome. Please include the driver version, .NET
version, server version and a minimal reproducer when reporting a problem.

Before opening a pull request:

```bash
dotnet build ./src/JustyBase.NetezzaDriver.slnx -c Release
dotnet format ./src/JustyBase.NetezzaDriver.slnx --verify-no-changes
dotnet test ./src/JustyBase.NetezzaDriver.Tests/JustyBase.NetezzaDriver.Tests.csproj \
  -c Release --filter "Category=Unit"
dotnet run --project ./tools/JustyBase.NetezzaDriver.Conformance \
  -c Release -- --profile handshake --offline --cases-dir eng/conformance/cases
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
