# Changelog

All notable changes to `JustyBase.NetezzaDriver` are documented here.

## Unreleased

### Changed

- `GetColumnSchema()` caches the immutable `DbColumn` collection per current
  result set; repeated calls no longer rebuild the metadata graph.
- `NzDataReader` implements `IDbColumnSchemaGenerator`, so
  `DbDataReaderExtensions.GetColumnSchema` returns provider-native
  `NzDbColumn` objects instead of rebuilding `DataRowDbColumn` wrappers.

### Fixed

- `GetSchemaTable()` and `GetColumnSchema()` invalidate their caches when
  `NextResult()` advances to another result set.
- Custom CA validation now requires the server-authentication EKU on the leaf
  certificate instead of accepting any certificate signed by a trusted root.

## 1.12.2

### Added

- Cross-driver `SslMode` values: `disable`, `prefer` (default), `require`,
  `verify-ca` and `verify-full`, with a PEM CA bundle and explicit host-name
  validation.
- `CharVarcharEncoding=latin1|utf8` for DBOS `CHAR`/`VARCHAR` decoding.
  National character types and the text protocol path remain UTF-8.
- Deterministic `NUMERIC` typing: declared precision up to 28 returns
  `decimal`; 29–38 returns the exact server text as `string`.
- Offline TLS handshake tests with a generated private CA, and connection
  option/round-trip tests.

### Changed

- `SslCertFilePath` / `SslCerFilePath` is now a **CA bundle**, not a client
  certificate, matching the Rust, Node and Java drivers.
- `SslMode=require` encrypts without validating the certificate by default;
  `verify-ca` validates the chain and `verify-full` validates chain and host.
- `RejectUnauthorized=false` is rejected for `verify-ca` and `verify-full`.
- `SslMode` and the legacy `SecurityLevel` are mutually exclusive in one
  connection string.
- Transactions support `ReadCommitted` (and `Unspecified` mapped to it).
  Other isolation levels and nested transactions throw.
- `FieldCount` is `0` for non-query results such as DDL; it is no longer `-1`.
- `Timeout=0` in a connection string falls back to the 15-second connect
  default instead of meaning an invalid/zero timeout.
- `NzConnection` is split into protocol, execution, external-table, result and
  buffer partial classes. No behaviour change in this refactor.

### Fixed

- `NUMERIC` values wider than `System.Decimal` are no longer silently rounded.
- TLS no longer requires or misuses a client certificate.
- Pooled connections receive the configured TLS and DBOS encoding options.

## 1.12.0

### Added

- `NzBulkCopy` external-table bulk inserts through the existing connection,
  with a row-by-row fallback for unsupported values.
- `BulkInsert` connection option.

## Earlier releases

See the commit history and release notes on GitHub:
<https://github.com/justybase/JustyBase.NetezzaDriver/commits/master>
