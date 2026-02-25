# JustyBase.NetezzaDriver.Tests

## Test categories
- `Category=Unit` - pure unit tests without database dependency.
- `Category=Integration` - tests requiring a running Netezza instance.

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
dotnet test .\scr\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Unit"
```

Run integration tests only:
```bash
dotnet test .\scr\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Integration"
```
