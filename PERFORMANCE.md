# Performance report

This report covers the post-`39d91f1` optimization pass. The goal was to find
the highest-value remaining optimizations without rewriting already-optimized
hot paths and without changing protocol behavior or ADO.NET compatibility.

Test status in this environment:

- `Category=Unit`: **119 passed, 0 failed**.
- Live `Integration`/`Benchmark` tests require a real Netezza server
  (`NZ_DEV_HOST`, etc.) and were **not executed here**.
- Replay/unit coverage was used instead where possible
  (`NzReplayTests`, `NzReadBufferTests`, `TypedReaderTests`,
  `ParameterPlanTests`, `ParameterTests`, `PoolTests` unit classes).

Benchmark methodology:

- BenchmarkDotNet `ShortRunJob` (`WarmupCount=3`, `IterationCount=3`,
  `LaunchCount=1`) with `MemoryDiagnoser`.
- Host/runtime in the captured runs: BenchmarkDotNet `0.15.8`, Linux,
  AMD Ryzen 7 7840HS, `.NET 10.0.12`, `X64 NativeAOT`.
- Reader benchmarks replay byte-exact fixtures over loopback
  (`NzReplayServer`): `dimdate.nzreplay.gz` (`3652x19`) and
  `fact200k.nzreplay.gz` (`200000x7`). There is no database latency in these
  runs; they isolate client-side parsing/decoding cost.
- `ShortRunJob` with `N=3` is noisy. Absolute times are therefore treated as
  approximate; allocation counters and large-ratio changes are the most robust
  signals. One benchmark batch was rerun after noticing heavy machine load;
  the rerun confirmed the same allocation behavior.
- In the tables below, `Allocated` is managed bytes per BenchmarkDotNet
  operation (`1 KB = 1024 B`), `Gen0` is collections per 1000 operations, and
  `rows/s` / `B/row` are derived as `rows / Mean` and `Allocated / rows`
  where the operation reads a full replayed result set.

Commits in this pass:

- `b16540f` pool validation default
- `cf0bafb` opt-in lazy column decoding
- `1670431` parameter value formatting + pooled `ValueStringBuilder`
- `d5d9c16` hash-based named parameter binding above 8 parameters
- `75c7588` capped retained scratch buffer
- `d6a58a1` / `5fd567b` read-buffer size measurement, then keep 64 KB default
- `eae634b` descriptor parsed directly into final arrays
- `76ce9e9` `GetBytes` chunked-read cache
- `8b126e0` row hot-path null-check ordering

---

## 1. Default pooled-connection validation

### Name

Pool checkout validation — skip `SELECT 1` for recently returned connections.

### Before

- `NzConnectionStringBuilder.ConnectionValidationInterval` default: `0`.
- `NzConnectionPool.ShouldValidateIdleConnection(now, now)` for the default
  pool: `true`.
- Cost per pooled checkout when an idle connection was available: one extra
  backend command round trip (`SELECT 1` + `ExecuteReaderAsync` + `ReadAsync`).

### After

- Default `ConnectionValidationInterval` is `30` seconds.
- A healthy connection returned moments ago is rented again without a probe.
- Validation still happens when the connection has been idle past the
  interval, is closed, or has exceeded its lifetime.
- Setting the interval to `0` explicitly preserves validate-on-every-checkout.
- `ConnectionValidationInterval` is also parsed from connection strings, so the
  builder round trip does not lose it.
- Updated/added unit tests cover: default skips recent probes, idle-beyond-
  interval is validated, `0` means every checkout, closed connections are never
  skipped, expired connections require validation, and cancelled `RentAsync`
  does not create a connection.
- The existing reuse integration test now expects
  `ConnectionValidationCount == 0` after rent/return/rent with defaults
  (requires a live server; not executed in this environment).

### Change

- Database round trips eliminated for the normal hot-pool path:
  **1 `SELECT 1` round trip per checkout → 0** for healthy connections idle
  less than the validation interval.
- No client-side allocation claim is made here; one backend round trip is
  normally worth far more than formatting/parser micro-optimizations.

### Decision

**ACCEPTED.**

---

## 2. Lazy column decoding

### Name

Opt-in lazy `RowStandard` column decoding.

### Before

`ParseDbosTupleData` eagerly decoded every column of every row into
`RowValue`, even when the application read only one or two columns.

Pure-eager baseline, full read (`ReplayReaderBench.Sync_GetValue`):

- DimDate `3652x19`: Mean `2.305 ms`, Allocated `1.1 MB`, Gen0 `136.7188`,
  rows/s `~1,584,000`, `~317 B/row`.
- Fact `200000x7`: Mean `42.692 ms`, Allocated `19.84 MB`, Gen0 `2416.6667`,
  rows/s `~4,685,000`, `~104 B/row`.

Eager subset baseline from the same lazy-comparison batch
(`UseLazyColumnDecoding=false`, `GetValue` only):

- DimDate first column: Mean `2,052.0 us`, Allocated `103.38 KB`,
  Gen0 `11.7188`, rows/s `~1,780,000`, `~29.0 B/row`.
- DimDate two columns: Mean `2,026.6 us`, Allocated `188.97 KB`,
  Gen0 `21.4844`, rows/s `~1,802,000`, `~53.0 B/row`.
- Fact first column: Mean `41,402.3 us`, Allocated `4696.26 KB`,
  Gen0 `500.0000`, rows/s `~4,831,000`, `~24.0 B/row`.
- Fact two columns: Mean `43,579.7 us`, Allocated `9383.78 KB`,
  Gen0 `1100.0000`, rows/s `~4,589,000`, `~48.0 B/row`.

### After

`NzConnection.UseLazyColumnDecoding` parses row structure/nullability and
retains the payload until the next protocol read; columns decode on first
access and are cached in `RowValue`. `IsDBNull` uses the null bitmap without
materializing values. Default remains eager.

Same batch, lazy enabled:

- DimDate first column: Mean `667.2 us`, Allocated `101.26 KB`,
  Gen0 `11.7188`, rows/s `~5,474,000`, `~28.4 B/row`.
- DimDate two columns: Mean `809.2 us`, Allocated `186.85 KB`,
  Gen0 `22.4609`, rows/s `~4,513,000`, `~52.4 B/row`.
- Fact first column: Mean `21,291.9 us`, Allocated `4696.06 KB`,
  Gen0 `562.5000`, rows/s `~9,393,000`, `~24.0 B/row`.
- Fact two columns: Mean `21,480.5 us`, Allocated `9383.56 KB`,
  Gen0 `1125.0000`, rows/s `~9,311,000`, `~48.0 B/row`.
- A replay unit test asserts lazy-decoded DimDate values match eager decoding.

### Change

- Time for narrow access over wide rows: roughly **2–3x faster**
  (DimDate 1 column `2052 us -> 667 us`; Fact 1 column `41.4 ms -> 21.3 ms`).
- Allocation is essentially unchanged for subset access because unaccessed
  value types were already stored in reused structs and strings go through
  pooling.
- Read-all under lazy mode is slightly slower due per-access checks, so lazy
  stays opt-in.

### Decision

**ACCEPTED as opt-in.** Eager remains the default.

---

## 3. Parameter value formatting and rendering

### Name

Span-based SQL literal formatting plus pooled `ValueStringBuilder`.

### Before

`NzParameter.AppendSqlLiteral` created temporary strings via `ToString()`,
including one small hex string per `byte[]` byte. Rendering allocated one
`StringBuilder` plus its private buffer per execution.

Cached-plan render baseline:

- 10 mixed named params: Mean `1,431.5 ns`, Allocated `1424 B`, Gen0 `0.1698`.
- 2 `byte[64]` params: Mean `1,725.3 ns`, Allocated `5280 B`, Gen0 `0.6294`.
- Repeated cached plan + render, 20x10 named ints: Mean `14,051
...[truncated 9113 chars]