# Security policy

## Supported versions

| Version | Supported |
|---|---|
| 1.12.x | Yes |
| < 1.12 | No |

## Reporting a vulnerability

Please do **not** open a public issue for a suspected vulnerability. Use
GitHub's private vulnerability reporting on the repository:

<https://github.com/justybase/JustyBase.NetezzaDriver/security/advisories/new>

Include the driver version, .NET version, a minimal reproducer, and the
connection options involved (`SslMode`, `SslCertFilePath`,
`RejectUnauthorized`). We will acknowledge the report and coordinate a fix and
disclosure.

## Security model

### TLS

- `SslMode=prefer` is the default and validates the chain and host name when
  the server upgrades the session.
- `SslMode=require` is encrypted but does not validate the certificate by
  default; use `verify-ca` or `verify-full` for production.
- `SslCertFilePath` is a PEM **CA bundle**, not a client certificate. No client
  certificate is sent.
- `RejectUnauthorized=false` is accepted only for `disable`, `prefer` and
  `require`. It cannot weaken `verify-ca` or `verify-full`.
- TLS 1.2 and TLS 1.3 are enabled. Older protocol versions are not offered.

See [src/docs/tls_and_encoding.md](src/docs/tls_and_encoding.md) for the full
matrix.

### Credentials

- Connection strings contain a password in clear text by design. Do not log
  them. Use `NzConnectionStringBuilder.SafeConnectionString` or
  `ToSafeString()` to redact the password before writing diagnostics.
- The driver does not write passwords or result values to logs at information
  level. Debug logging can include protocol payloads and must be treated as
  sensitive.
- Prefer a secret store or environment variables over checked-in connection
  strings.

### SQL construction

Parameters are rendered inline as escaped SQL literals, matching the Python
`nzpy_extended` driver. The parser skips string literals, quoted identifiers,
line comments and block comments. This prevents placeholder substitution inside
those regions, but it is not a substitute for validating dynamic SQL:

- Never concatenate untrusted identifiers into SQL. Identifiers cannot be
  parameterized; validate them against an allow-list.
- Use parameters for values, not string interpolation.
- Treat `CommandText` and server error text as sensitive.

### Bulk copy and external tables

`NzBulkCopy` uses the external-table protocol with `REMOTESOURCE 'jdbc'` and a
UTF-8 text format. Values containing unsupported control characters fall back
to row-by-row inserts. Review the target table and column list before loading
untrusted data.

## Dependency and release hygiene

- The package has no native dependencies.
- CI runs formatting, unit tests, coverage gates, offline conformance and pack
  on Linux and Windows.
- Publish workflows use NuGet trusted publishing (`nuget/login`) rather than a
  long-lived API key in the repository.
