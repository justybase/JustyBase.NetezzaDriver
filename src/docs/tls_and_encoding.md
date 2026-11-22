# TLS and text encoding

The driver follows the `sslmode` contract used by the Rust, Node, Java and
Python drivers, so the same operational policy can be shared across runtimes.

## TLS modes

| `SslMode` | Start plaintext | Require TLS | Validate chain | Validate host name |
|---|:---:|:---:|:---:|:---:|
| `disable` | yes | no | no | no |
| `prefer` (default) | yes | if the server asks | yes | yes |
| `require` | no | yes | no | no |
| `verify-ca` | no | yes | yes | no |
| `verify-full` | no | yes | yes | yes |

`prefer` starts unencrypted and upgrades when the backend requests a secured
session. `require` is encrypted but, like the other drivers, does not validate
the certificate by default. Production deployments should use `verify-full`
with a CA bundle.

```csharp
using var connection = new NzConnection(
    "Host=nps.example.com;Database=ANALYTICS;User=etl;Password=secret;" +
    "SslMode=verify-full;SslCertFilePath=/etc/pki/neteza-ca.pem");
```

## CA bundle, not a client certificate

`SslCertFilePath` (legacy spelling `SslCerFilePath`) points to a **PEM bundle of
trusted CA certificates**. It is not a client certificate and no client
certificate is sent during the handshake. This matches the
`sslCerFilePath` option in the Rust and Node drivers.

When a bundle is supplied, the driver builds an `X509Chain` with
`CustomRootTrust` and only the configured roots are trusted. System trust
stores are not silently mixed in.

## `RejectUnauthorized`

`RejectUnauthorized` can override validation for the permissive modes:

| Mode | `RejectUnauthorized=false` | `RejectUnauthorized=true` |
|---|---|---|
| `disable` | no TLS | no TLS |
| `prefer` | accept any certificate | validate chain and host |
| `require` | accept any certificate (default) | validate chain and host |
| `verify-ca` | rejected | chain only |
| `verify-full` | rejected | chain and host |

Rejecting the override for `verify-*` is intentional: a connection string must
not silently downgrade an explicitly strict policy.

## Protocol details

- TLS 1.2 and TLS 1.3 are enabled.
- The connection is upgraded in place after the Netezza version negotiation and
  the SSL request packet.
- Host-name validation uses the host from the connection string. For
  `verify-full`, connect with the certificate's DNS name rather than an IP
  alias.
- Legacy `SecurityLevel` values remain supported; `SecurityLevel` and
  `SslMode` are mutually exclusive in one connection string.

## DBOS `CHAR` / `VARCHAR` encoding

DBOS result rows carry text as bytes without an in-band encoding marker. The
driver exposes an explicit per-connection setting:

| `CharVarcharEncoding` | Encoding |
|---|---|
| `latin1` (default) | ISO-8859-1 |
| `utf8` | UTF-8 |

Aliases: `latin`, `latin-1`, `iso-8859-1`, `utf8`, `utf-8`.

```csharp
using var connection = new NzConnection(
    "Host=...;Database=...;User=...;Password=...;CharVarcharEncoding=utf8");
```

The setting applies only to DBOS `CHAR`, `VARCHAR` and the related
`VARFIXEDCHAR`, JSON/geometry text columns. `NCHAR`/`NVARCHAR` and the text
protocol path remain UTF-8. Choose the value that matches the database
encoding; Latin-1 is the historical default for parity with the other drivers.

## Offline verification

The unit suite contains an in-process TLS server with a generated private CA.
It verifies that `require` accepts a self-signed server, `verify-ca` trusts only
the configured CA and ignores the host name, `verify-full` accepts a matching
host and rejects a mismatch, and an untrusted certificate is refused.

These tests exercise the real `SslStream` handshake without an appliance. A live
appliance is still required to validate production certificates, intermediate
chains and DBOS fixtures for non-ASCII data.
