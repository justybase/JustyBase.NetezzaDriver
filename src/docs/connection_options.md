# Connection options

`NzConnection` accepts a standard ADO.NET connection string. Keyword matching
is case-insensitive, and common cross-driver aliases are accepted. Check the
other driver for its complete vocabulary; for example, Java also accepts
`sslmode=allow`, which this driver does not implement.

## Required options

| Canonical key | Aliases | Description |
|---|---|---|
| `Host` | `Server` | Appliance host name or IP address. |
| `User` | `Username` | User name. |
| `Password` | `Pwd` | Password. Standard double-quote quoting and the legacy `{...}` form are both accepted. |

## Optional options

| Canonical key | Default | Description |
|---|---:|---|
| `Database` | none | Initial database. It may also be supplied to the constructor overload; most servers require one for normal use. |
| `Port` | `5480` | SQL port. |
| `Timeout` | `15` s | **Connect** timeout. Invalid or non-positive values fall back to 15 seconds. This is not the command timeout. |
| `BulkInsert` | `true` | Enables the external-table path used by `NzBulkCopy`. `false` forces row-by-row inserts. |
| `SslMode` | `prefer` | TLS policy: `disable`, `prefer`, `require`, `verify-ca`, `verify-full`. |
| `SslCertFilePath` | none | PEM bundle of trusted CA certificates. This is **not** a client certificate. Aliases: `SslCerFilePath`, `Ssl_Cert`, `SslCert`. |
| `RejectUnauthorized` | `true`, except `require` where it defaults to `false` | Overrides certificate validation for `disable`, `prefer` and `require`. It cannot weaken `verify-ca` or `verify-full`. |
| `CharVarcharEncoding` | `latin1` | Encoding of DBOS `CHAR`/`VARCHAR` values: `latin1` or `utf8`. |
| `SecurityLevel` | none | Legacy TLS spelling. It is mutually exclusive with `SslMode`. |

`SslMode` and `SecurityLevel` cannot appear in the same connection string. That
is rejected instead of silently picking one policy.

Accepted aliases: `SslMode` / `Ssl_Mode`, `SecurityLevel` / `Security_Level`,
`RejectUnauthorized` / `Reject_Unauthorized`, and `CharVarcharEncoding` /
`CharVarCharEncoding` / `Char_Varchar_Encoding` / `CharVarEncoding`.

## Pool sizing

These keys are consumed by `NzConnectionPool(NzConnectionStringBuilder)`.
`NzConnection` accepts them for ADO.NET compatibility, but it does **not**
create a pool and does not retain custom pool sizing in its `ConnectionString`;
configure the builder or pool directly. The `Pooling` key is accepted but is
not used to enable or disable the explicit pool.

| Canonical key | Default | Description |
|---|---:|---|
| `Pooling` | `true` | Accepted for compatibility; `NzConnection` does not pool. |
| `MinPoolSize` | `0` | Minimum number of connections maintained by `NzConnectionPool`. |
| `MaxPoolSize` | `10` | Maximum number of physical connections. |
| `ConnectionIdleTimeout` | `30` s | Idle time after which the pool closes a connection. |
| `ConnectionLifetime` | `0` (unlimited) | Maximum age of a pooled connection. |
| `ConnectionValidationInterval` | `30` s | Validate a connection with `SELECT 1` when it has been idle for at least this long. `0` validates on every checkout. |

## Examples

```csharp
// Minimal
new NzConnection("Host=nps.example.com;Database=ANALYTICS;User=etl;Password=secret");

// Cross-driver TLS and DBOS encoding
new NzConnection(
    "Host=nps.example.com;Database=ANALYTICS;User=etl;Password=secret;" +
    "SslMode=verify-full;SslCertFilePath=/etc/pki/netezza-ca.pem;" +
    "CharVarcharEncoding=utf8");

// Builder
var builder = new NzConnectionStringBuilder
{
    Host = "nps.example.com",
    Database = "ANALYTICS",
    UserName = "etl",
    Password = "secret",
    SslMode = SslMode.VerifyFull,
    SslCertFilePath = "/etc/pki/netezza-ca.pem",
    CharVarcharEncoding = CharEncoding.Utf8,
};

using var connection = new NzConnection(builder.ConnectionString);
```

## Runtime precedence

Connection-string values override constructor defaults. For example, a
constructor `sslCerFilePath` remains in effect only when the connection string
does not specify `SslCertFilePath`. The explicit `database` argument of the
`(connectionString, database, commandTimeoutSec, ...)` overload takes
precedence over the connection-string `Database`.

`ConnectionString` can be assigned while the connection is closed and throws
`InvalidOperationException` while it is open. `NzConnectionStringBuilder`
round-trips every supported option; `SafeConnectionString` replaces the
password with `********` for logs and diagnostics.

## Command timeout is separate

`Timeout` in a connection string controls **opening a connection**. Command
timeouts are configured on the connection or on each command:

```csharp
connection.DefaultCommandTimeout = TimeSpan.FromSeconds(30);
using var command = connection.CreateCommand("SELECT ...");
command.CommandTimeout = 10; // seconds; 0 means no command timeout
```

See [timeout_and_cancel.md](timeout_and_cancel.md) for details.
