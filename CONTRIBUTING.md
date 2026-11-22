# Contributing

Thanks for helping improve `JustyBase.NetezzaDriver`. The project favours small,
reviewable changes with offline tests; most behaviour can be verified without a
Netezza appliance.

## Prerequisites

- .NET SDK 11.0.100 or a newer 11.0.x feature band (`global.json` pins 11)
  and the .NET 10 runtime for the `net10.0` test target.
- PowerShell 7 for the coverage gate.
- Optional: a Netezza appliance for integration and benchmark tests.

## Repository layout

| Path | Purpose |
|---|---|
| `src/JustyBase.NetezzaDriver` | Driver assembly and public API. |
| `src/JustyBase.NetezzaDriver.Tests` | Unit, replay, integration and benchmark tests. |
| `src/JustyBase.NetezzaDriver.TestSupport` | Replay server and wire fixtures. |
| `src/JustyBase.NetezzaDriver.Benchmarks` | BenchmarkDotNet suites. |
| `src/docs` | Focused user documentation. |
| `tools/JustyBase.NetezzaDriver.Conformance` | Cross-driver conformance runner. |
| `eng` | Coverage and offline conformance support. |

## Local gates

Run these before opening a pull request:

```powershell
dotnet restore .\src\JustyBase.NetezzaDriver.slnx
dotnet build .\src\JustyBase.NetezzaDriver.slnx -c Release --no-restore
dotnet format .\src\JustyBase.NetezzaDriver.slnx --verify-no-changes --no-restore
dotnet test .\src\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj `
  -c Release --filter "Category=Unit"
```

Coverage gate:

```powershell
Remove-Item -Recurse -Force TestResults\coverage -ErrorAction SilentlyContinue
dotnet test .\src\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj `
  -c Release -f net10.0 --filter "Category=Unit" `
  --collect:"XPlat Code Coverage" --results-directory TestResults\coverage
pwsh .\eng\check-coverage.ps1 -ResultsDirectory TestResults\coverage
```

Offline conformance (handshake failures, no appliance):

```powershell
dotnet run --project .\tools\JustyBase.NetezzaDriver.Conformance `
  -c Release -- --profile handshake --offline --cases-dir eng\conformance\cases
```

## Test categories

| Category | Requirements | Purpose |
|---|---|---|
| `Unit` | None | Deterministic, offline. Replay fixtures, protocol fuzzing, option parsing, TLS with a private CA. |
| `Integration` | `NZ_DEV_*` | Real appliance: SQL, metadata, pooling, bulk copy, TLS. |
| `Stress` | Appliance | Large payload boundaries and long-running checks. |
| `Benchmark` | Appliance | Performance checks inside the test project. |

Live tests read `NZ_DEV_HOST`, `NZ_DEV_PORT`, `NZ_DEV_DB` (or
`NZ_DEV_DATABASE`), `NZ_DEV_USER` and `NZ_DEV_PASSWORD`. They fail fast rather
than falling back to a hard-coded lab password. `NZ_DEV_ALLOW_LAB_DEFAULTS=1`
opts into the local defaults (`127.0.0.1`, `JUST_DATA`, `admin`/`password`) for
a disposable lab.

## Benchmarks

Replay benchmarks run offline against byte-exact recorded responses:

```powershell
dotnet run -c Release -f net10.0 --project .\src\JustyBase.NetezzaDriver.Benchmarks `
  -- --filter "*ReplayReaderBench*"
```

Live benchmarks require the same `NZ_DEV_*` variables:

```powershell
dotnet run -c Release -f net10.0 --project .\src\JustyBase.NetezzaDriver.Benchmarks `
  -- --filter "*AsyncReaderBench*"
```

For a performance change, include:

- the benchmark name and exact command;
- before/after mean, standard deviation and allocations;
- whether the change affects allocations, throughput or both;
- confirmation that protocol behaviour, ADO.NET semantics and tests are
  unchanged.

Do not tune a hot path without a measurement. `PERFORMANCE.md` records earlier
experiments and the reasons some apparently obvious optimizations were rejected.

## Change expectations

- Add or update a unit test for every behaviour change. Prefer a replay fixture
  when the backend protocol is involved.
- Update the relevant document in `src/docs` and the README when a public option
  or contract changes.
- Keep changes scoped. A refactor and a behaviour change belong in separate
  commits.
- Follow the existing C# style; `dotnet format` is authoritative.
- Public API additions should be AOT-friendly and must not require native
  dependencies.

## Commit messages

Use a short imperative subject, optionally with a scope:

```text
feat: add sslmode parity
fix(reader): keep FieldCount at zero for DDL
docs: document connection options
perf: reduce DBOS string allocations
```

Explain user-visible behaviour changes and the validation performed in the body.

## Reporting issues

Include the driver version, .NET version, server version, a minimal reproducer
and whether the connection used `SslMode`/`CharVarcharEncoding`. Never paste a
real password; use `SafeConnectionString` or redact it manually.
