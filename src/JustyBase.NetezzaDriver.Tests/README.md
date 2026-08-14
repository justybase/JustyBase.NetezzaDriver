# JustyBase.NetezzaDriver.Tests

## Test categories
- `Category=Unit` - pure unit tests without database dependency.
- `Category=Integration` - tests requiring a running Netezza instance.
- `Category=Stress` - opt-in, bounded, read-only load against a running Netezza instance.

## Configuration (integration tests)
Integration tests read connection settings from environment variables:
- `NZ_DEV_HOST`
- `NZ_DEV_PORT`
- `NZ_DEV_DB`
- `NZ_DEV_USER`
- `NZ_DEV_PASSWORD`

If variables are not provided, local defaults are used.

## Running tests
Run unit tests only:
```bash
dotnet test .\src\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Unit"
```

Run integration tests only:
```bash
dotnet test .\src\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Integration"
```

Run the boundary integration tests, including synchronous/asynchronous reads,
reader reuse, and multi-result handling:
```powershell
dotnet test .\src\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~BackendLengthBoundaryIntegrationTests"
```

Run the exact DIMDATE overflow regression test:
```powershell
dotnet test .\src\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~BackendLengthOverflowReproTests"
```

Set `NZ_PROTOCOL_TRACE=1` to print backend response types and raw protocol
length fields captured by that test. The regression query is intentionally
fixed to `SELECT * FROM JUST_DATA.ADMIN.DIMDATE`.

## Read-only stress test

The stress test is skipped unless `NZ_STRESS=1` is set. It starts four
background connections, executes only read-only queries, and keeps the exact
DIMDATE query running in the foreground across the 500-row boundary.

```powershell
$env:NZ_STRESS = "1"
$env:NZ_STRESS_DURATION_SECONDS = "30"   # 1..300, default 30
$env:NZ_STRESS_WORKERS = "4"              # 1..16, default 4
$env:NZ_STRESS_FOREGROUND_ROUNDS = "10"   # 1..100, default 10

dotnet test .\src\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj `
  -c Release `
  --filter "Category=Stress"
```

The .NET tests use `NZ_DEV_DB` for the database name. The Node and Python
drivers are not test dependencies and are not used as a protocol oracle;
their existing scenarios only informed the read-only workload and boundary
matrix.
