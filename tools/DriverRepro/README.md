# Live driver reproduction harnesses

This directory contains two console applications which run the same live-database scenarios:

- `DriverRepro.Source` uses the current driver source through a `ProjectReference`.
- `DriverRepro.Package172` uses the exact `JustyBase.NetezzaDriver` NuGet package version `1.7.2`, matching the dependency currently used by JustyBase.

The default SQL is deliberately the exact query reported by the user:

```sql
SELECT * FROM JUST_DATA.ADMIN.DIMDATE
```

The runner does not trim the result or skip a bad row. Every scenario requires the query to cross row 500, then drains the remaining rows. Failures print the phase, row, column and complete exception stack trace.

## Scenarios

`matrix` runs the normal cases one after another on fresh connections:

- `minimal-sync`: plain synchronous `Read()` and row counting only;
- `app-sync`: `CommandBehavior.SequentialAccess`, `HasRows`, schema/metadata calls, then `GetValue()` for every column, matching the JustyBase reader path;
- `app-async`: the corresponding async reader path;
- `reuse`: fully drain, execute `SELECT 1`, then drain the exact query again on one connection;
- `close-reuse`: dispose the reader exactly after row 500, reuse the connection, then read the exact query again;
- `repeated`: drain the exact query twice on one connection;
- `multi-result`: drain the exact query, call `NextResult()`, and read `SELECT 1`.

Additional targeted modes are `cancel-reuse`, `slow-async`, and `stress`.

`cancel-reuse` intentionally calls `command.Cancel()` at row 500, so run it separately when investigating cancellation behavior. `stress` opens several independent connections and repeatedly executes the app-like scenario for a bounded time; it defaults to four workers and 30 seconds.

## Running

From the repository root:

```powershell
dotnet run --project .\tools\DriverRepro.Source\DriverRepro.Source.csproj -c Release -- matrix
dotnet run --project .\tools\DriverRepro.Package172\DriverRepro.Package172.csproj -c Release -- matrix
```

For the exact app-like path only:

```powershell
dotnet run --project .\tools\DriverRepro.Package172\DriverRepro.Package172.csproj -c Release -- app-sync
```

For background stress:

```powershell
$env:NZ_REPRO_STRESS_SECONDS = "120"
$env:NZ_REPRO_STRESS_WORKERS = "8"
dotnet run --project .\tools\DriverRepro.Package172\DriverRepro.Package172.csproj -c Release -- stress
```

Connection variables are `NZ_DEV_HOST`, `NZ_DEV_PORT`, `NZ_DEV_DB` (or the application's `NZ_DEV_DATABASE`), `NZ_DEV_USER`, and `NZ_DEV_PASSWORD`. Set `NZ_PROTOCOL_TRACE=1` to print the driver's protocol response and length diagnostics. The SQL can be overridden with `NZ_REPRO_SQL`, but leaving it unset is recommended for this investigation so that the exact `DIMDATE` query remains under test.
