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
---

# Final targeted performance and correctness pass (2026-10-04)

Scope: no broad refactor. Each item below was gated on correctness,
measurable gain, allocation/retained-memory reduction, or less DB/network work.
Unit suite after this pass: **130 passed, 0 failed** (`Category=Unit`;
was 119 before; +11 new targeted tests). Live integration benches still
require `NZ_DEV_HOST` and were not run; replay/loopback measurements below
isolate client-side cost (no DB latency).

Method for the drain numbers: replay fixtures over loopback
(`NzReplayServer`, `dimdate.nzreplay.gz` 3652x19, `fact200k.nzreplay.gz`
200000x7), best of 5, `GC.GetAllocatedBytesForCurrentThread` + wall time.
"Before" for early-dispose = old `Close()` behavior (full decode of every
remaining row, equivalent to a full read); "After" = new discard drain.
Network bytes are still transferred (protocol requires drain to RFQ); the win
is decode CPU + allocations.

## P0. stackalloc bool[count] initialization (correctness)

### Problem

`NzParameterHelper.RenderNamed` used `stackalloc bool[count]` without
clearing. Stack memory is uninitialized. A stale `true` hides an unused
parameter (missing "provided but not used" error); behavior depends on prior
stack contents.

### Before

No explicit init. Flaky by nature; repeated valid renders could leave `true`
bits that mask a later unused-parameter error.

### After

`used.Clear()` immediately after the stackalloc/heap branch. One
`Span.Clear` (zero cost for count<=128, single vectorized zero).

### Change

Correctness only; no perf claim.

### Decision

**ACCEPTED.** Regression tests: 50x valid+invalid interleaved renders
(stackalloc branch) + 130-param heap-branch validation.

## P1. Pool validation changed CommandTimeout (correctness)

### Problem

`NzConnectionPool.IsConnectionValidAsync` set `cmd.CommandTimeout = 5`, but
`NzCommand.CommandTimeout` forwards to shared `NzConnection.CommandTimeout`.
After an idle validation, the app's connection (e.g. 60 s) silently became
5 s.

### Before

Probe permanently mutated the physical connection timeout.

### After

Save `connection.CommandTimeout`, `try` probe, `finally` restore. Covers
success, failure (`return false`), and cancellation (rethrow) paths. No API
change; per-command timeout redesign explicitly deferred as unsafe.

### Change

Correctness; one extra TimeSpan copy per validation (negligible vs a
`SELECT 1` round trip).

### Decision

**ACCEPTED.** Tests: failure preserves 60 s, cancellation preserves 60 s,
closed-connection short-circuit preserves timeout. Success path shares the
same `finally`.

## P2/P3. Fast discard drain + SingleRow (largest real win)

### Problem

`NzDataReader.Close()` drained via full `DoNextStep`/`ParseDbosTupleData`:
`SELECT 200k; Read(); Dispose();` decoded 199999 unneeded rows (strings,
numerics, dates, RowValue churn). `ExecuteDbDataReader(SingleRow)` ignored
`behavior`, so `ExecuteScalar` paid the same.

### Before (measured, new-code harness; old Close == full read)

- dimdate full read all cols: ~7 ms, ~1130 KB.
- fact200k full read all cols: ~36-37 ms, ~20320 KB (~20 MB).

### After (new discard drain: framing+length validation, skip bytes, no decode)

- dimdate read-1 + Dispose: ~0 ms, ~15.1 KB. Read-10 + Dispose: ~0 ms, ~16 KB.
- dimdate ExecuteScalar: ~0 ms, ~15.1 KB.
- fact200k read-1 + Dispose: ~9-11 ms, ~8.0 KB. Read-10: ~8-12 ms, ~8.4 KB.
- fact200k ExecuteScalar: ~9-10 ms, ~8.2 KB.
- `connection.DiscardedRows` counter confirms skipped rows; replay tests
  assert reuse after early dispose (sync+async) and SingleRow/ExecuteScalar
  correctness.

### Change

- dimdate early-dispose: time ~7 ms -> ~0 ms, alloc ~1130 KB -> ~15 KB
  (**~99% alloc reduction**).
- fact200k early-dispose: time ~37 ms -> ~9 ms (**~75% faster**; remainder is
  mandatory socket transfer), alloc ~20320 KB -> ~8 KB (**~99.96%**).
- Lazy first-col over dimdate (related): eager-first-col ~6-7 ms ->
  lazy-first-col ~1-2 ms.

Design: `NzReadBuffer.Discard/DiscardAsync` (chunked, no large alloc) +
`SkipRowStandardPayload[/Async]`, `SkipDataRowPayload[/Async]` +
`DoNextStepDiscardingRows[/Async]` (`discardRows` flag threads through the
existing parser; non-row messages unchanged). `NzDataReader.Close`,
`CloseAsyncCore`, and post-first-row `SingleRow` reads use it.
`NzCommand.RequestedBehavior` carries `SingleRow` without API break.

### Decision

**ACCEPTED.** Benchmarks added: `Sync_Read1_Dispose`, `Sync_Read10_Dispose`,
`Sync_ExecuteScalar` (both fixtures). Existing full-read benches are the
"before".

## P4. Deeper lazy decoding (generation counters / progressive offsets)

### Problem

`ParseLazyRow` still does `PrepareVariableFieldOffsets` (all varying fields)
+ `ResetForLazyDecode` for every column: O(columns) per row even when 1 of
100 is read.

### Before

Lazy first-col already wins (dimdate eager-first ~6-7 ms -> lazy ~1-2 ms,
alloc ~102 KB -> ~100 KB). Remaining O(columns) is mem writes + offset scan,
small vs actual decode.

### After

No code change in this pass.

### Change

Not implemented; estimated gain small (<10% on 19-col fixture) vs state
machine risk (partial-scan resume, generation wrap, per-column metadata
growth).

### Decision

**REJECTED (keep benchmark, document).** Existing
`Sync_FirstColumn[_Lazy]`/`Sync_TwoColumns[_Lazy]` benches remain the harness
for 20/50/100-col follow-ups.

## P5. SequentialAccess

### Problem

`SequentialAccess` is accepted but ignored; could enable progressive offsets,
less caching, streaming GetBytes.

### After

No behavior change. `SingleRow` fast path covers the highest-value
sequential case. Arbitrary backwards access still works in all modes.

### Decision

**REJECTED (no complexity without measured need).** Npgsql-style
`SeekToColumn` deferred until a replay benchmark shows material gain.

## P6. Release lazy oversize buffer on pool return

### Problem

`_lazyOversizeBuffer` (ArrayPool, potentially multi-MB) + `_lazyRowMemory` /
`_lazyRowActive` survived `ReturnAsync`, which only called
`ReleaseScratchBuffers` (`_largeReadBuffer`). Idle pooled connections pinned
transient row memory.

### Before

Oversize lazy row retained across pool idle.

### After

`ReleaseScratchBuffers()` now forwards to unified `ReleaseTransientBuffers()`:
returns `_largeReadBuffer`, returns `_lazyOversizeBuffer`, clears
`_lazyRowMemory`/`_lazyRowActive`. Persistent `NzReadBuffer` untouched.
`Close/CloseAsync` behavior unchanged (already cleared).

### Change

Retained-memory fix; steady-state time/alloc unchanged by design.

### Decision

**ACCEPTED.** Test simulates oversize transient state, asserts
`ReleaseScratchBuffers` clears it and is idempotent.

## P7. ConnectionValidationInterval=0 round trip

### Problem

`ToString()` omitted the property when `<= 0`, so explicit `0` (validate
every checkout) serialized to nothing and re-parsed as default 30.

### Before

`builder.ConnectionValidationInterval = 0; builder.ToString()` lost the 0.

### After

Always emits `ConnectionValidationInterval=...` (0, 30, 45 all survive
`ParseConnectionString`). `ParseConnectionString` made `internal` for the
round-trip test.

### Decision

**ACCEPTED.** Round-trip test for 0/45/default.

## P8. Cap large GetBytes cache retention

### Problem

`_getBytesCache` rents up to the full encoded value and holds it to reader
dispose. For 10-100 MB values this pins megabytes.

### After

No threshold implemented in this pass. Retention is bounded by reader
lifetime, and the row's own string is retained anyway (same order). Chunk
params extended (10 MB, 64 KB) so the trade-off can be measured properly
later. Incremental-`Encoder` alternative noted but not implemented (must
preserve byte-offset split semantics).

### Decision

**REJECTED (keep benchmark, no threshold picked arbitrarily).**

## P9. Cache named parameter bindings

### Problem

Hash table for >8 params is rebuilt per render. Caching
placeholder->index across renders could help 16-64 param repeated executes,
but `ParameterName` mutation/collection edits require invalidation (stale
binding = wrong SQL).

### After

Not implemented. Template plan is already cached; per-render hash build is
one `Rent` + O(n) FNV + O(placeholders) probes. Added sizing probe benchmark
(`Render_SizedNamed_Repeated`, 8/16/32/64 x20 renders) for future decision.

### Decision

**REJECTED (benchmark added, not worth invalidation risk on current data).**

## P10. Dead code

### Problem

`CreateCommandTimeoutTokenSource` coexisted unused alongside
`TryGetCommandTimeoutToken` (compiler-confirmed zero callers; public surface
unaffected).

### After

Removed. No other dead infra removed (buffer/parse/metadata helpers all have
callers).

### Decision

**ACCEPTED (removal only).**

## P11. Explicitly not touched

Numeric conversion, primitive getters, length-validation fast paths, ArrayPool
ownership, ValueStringBuilder, NzReadBuffer primitives, GetFieldValue<T>,
UTF-8 query encoding, descriptor arrays, string pooling: no changes (no
regression signal). No unsafe/SIMD/custom allocators added.

---

## Remaining bottlenecks / do-not-touch-without-profiling

1. Socket transfer during drain: early-dispose still reads all bytes to RFQ
   (fact200k ~9 ms floor). Further wins need server-side cancellation
   (`CancelQuery`) or `CommandBehavior.CloseConnection` semantics, not client
   decode tweaks.
2. Wide-row lazy residual O(columns) (P4): needs 50/100-col fixtures before
   any generation/progressive-offset work.
3. `GetBytes` >4 MB retention (P8): needs incremental-encoder benchmark.
4. Named binding cache (P9): needs BDN numbers from the new probe.
5. Per-command timeout architecture (P1 follow-up): `NzCommand` timeout is
   still shared via `NzConnection`; making it per-command is an API/behavior
   change requiring compat review.

---

# Pool lifecycle + result-state pass (2026-10-04, cz. 2)

Zakres: tylko pozostałości z listy. Bez zmian w `NzReadBuffer`, `Numeric`,
`GetFieldValue<T>`, parameter rendering, lazy architekturze,
`SequentialAccess`, `ValueStringBuilder`. Unit: **135 passed, 0 failed**
(`Category=Unit`; było 130; +5 nowych testów).

## 1. Pool Dispose / Return race (poprawność)

### Problem

`RentAsync() → pool.DisposeAsync() → PooledNzConnection.DisposeAsync()`
kończył się `ObjectDisposedException`: `ReturnAsync()` wołał
`_semaphore.Release()` na zdisposowanym `SemaphoreSlim`. Dodatkowo
bezwarunkowy `Interlocked.Decrement` po `TryRemove` psuł licznik przy
podwójnym zwrocie.

### Before

Zwrot po dispose poola rzucał; licznik mógł zejść poniżej zera.

### After

- `ReturnAsync`: wpisowy fast-path `_disposed` (domknięcie connection,
  decrement tylko przy faktycznym `TryRemove`, zero dotykania semafora;
  idempotentny podwójny zwrot), sekcja enqueue+release pod `_idleLock`
  z re-checkiem `_disposed` w środku (serializacja z teardownem),
  `ReleaseSemaphoreSafe`/`ReleaseIdleLockSafe` połykające ODE.
- `DisposeAsync`: idempotentny przez `_disposeLock`; zbieranie `_active`
  z `TryRemove` + decrement (kto usunął wpis, ten jest właścicielem
  제 licznika — brak double-decrement z concurrent `ReturnAsync`).
- `RentAsync`: flaga `permitAcquired` + safe release; re-check `_disposed`
  po `WaitAsync`.

### Change

Poprawność; jeden lock na zwrot (poza ścieżką wierszy — pomijalny).

### Decision

**ACCEPTED.** Test: `ReturnAfterPoolDispose_DoesNotThrow_DisposesConnection`
(zwrot + podwójny zwrot po dispose: brak wyjątku, licznik wraca do 0).

## 2. MaxPoolSize race podczas maintenance (poprawność)

### Problem

`CleanupIdleAsync()` zdejmował wszystkie idle z `ConcurrentQueue` do
tymczasowej listy. Równoległy `RentAsync()` widział pustą kolejkę i tworzył
nowe physical connections mimo `_totalConnections == MaxPoolSize`.

### Before

Chwilowe przekroczenia limitu puli przy zbiegnięciu maintenance + rent.

### After

Atomowa rezerwacja slotu (`Interlocked.CompareExchange` w
`TryReserveConnectionSlot`; CAS udaje się tylko gdy wartość < max, więc
`_totalConnections` nigdy nie przekracza `MaxPoolSize`). Tworzą tylko
posiadacze rezerwacji (`RentAsync`, refill `minPoolSize` w maintenance);
wyjątek z `Open` oddaje rezerwację. Brak slotu + pusta kolejka = krótki
`Task.Delay(10)` + retry (maintenance odkłada wpisy bezzwłocznie; brak
deadlocka — maintenance nie potrzebuje permitów; analiza w kodzie).

### Change

Poprawność; pętla retry tylko w transjentnym oknie maintenance.

### Decision

**ACCEPTED.** Testy: deterministyczny cap (`CapsAtMaxPoolSize`),
konkurencyjny stress 32×2000 rezerwacji (nigdy > max, finał 0) oraz
`FailedOpen_ReleasesReservation` (8 równoległych rentów na zamknięty port —
licznik wraca do 0).

## 3. ReleaseResultStateForPooling (retained memory)

### Problem

Zwracane do poola connection trzymało ostatni result set: `RowValue[]`
(duże `string`/`object`), lazy row state, `NewPreparedStatement`
(metadata + string pools).

### Before

Idle connection pinowało pamięć ostatniego wyniku.

### After

`ReleaseResultStateForPooling()` (`NzConnection` + `NzCommand`): `Array.Clear`
na obu `RowValue[]` (instancje zachowane do reuse), wyzerowanie lazy state,
`NewPreparedStatement = null`. Wołane w `ReturnAsync` obok
`ReleaseTransientBuffers()`. Celowo bez zmian architektury lazy.

### Change

Retained-memory; koszt raz na zwrot (Clear tablicy, nie na wiersz).

### Decision

**ACCEPTED.** Test z 2× 1 MB stringów + `WeakReference`: po zwrocie oba
martwe (GC), statement null, bufory reusable zachowane
(`IsDBNullFast`/`GetValue` → `DBNull`, brak NRE).

## 4. Benchmark fast drain — uczciwy baseline

### Problem

Porównanie fast drain vs pełny odczyt z `GetValue()` zawierało koszt
boxingu — niesprawiedliwe wobec starego `Close()`, który dekodował, ale
nigdy nie wołał `GetValue()`.

### Before (pełny odczyt z GetValue)

- dimdate: ~7 ms / ~1130 KB. fact200k: ~37 ms / ~20320 KB.

### After — nowy baseline `Sync_FullDecode_NoGetValue` (dekodowanie jak legacy drain, bez GetValue) vs discard

- dimdate: legacy drain ~6 ms / ~17 KB → discard ~0 ms / ~15 KB
  (koszt samego dekodowania wyeliminowany w całości).
- fact200k: legacy drain ~32 ms / ~8 KB → discard ~10 ms / ~8 KB
  (**−69% CPU**; pozostałe ~10 ms to czysty transfer socketu do RFQ;
  alokacje równe — zysk to czyste CPU).

### Decision

**ACCEPTED (fast drain zostaje; liczby powyżej to rzetelniejszy pomiar).**

---

## DO NOT TOUCH WITHOUT PROFILING

Po powyższych poprawkach driver uznaje się za architektonicznie domknięty:
koszty sieciowe > dekodowanie > alokacje per-wiersz, hot pathy na poziomie
Npgsql. Dalsze zmiany wyłącznie z pomiarem (replay fixture + MemoryDiagnoser).
Zakazane bez profilowania: `NzReadBuffer`, konwersje numeryczne, gettery
prymitywne, `GetFieldValue<T>`, parameter rendering, lazy decoding,
`SequentialAccess`, `ValueStringBuilder`, ownership ArrayPool.

---

# Pool lifecycle/cancellation pass (2026-10-04, cz. 3)

Czysto correctness/lifecycle w `NzConnectionPool`. Zero zmian w
`NzReadBuffer`, dekodowaniu `RowStandard`, fast drain, `Numeric`,
`GetFieldValue<T>`, parameter rendering, lazy decoding, `SequentialAccess`,
`ValueStringBuilder`, ścieżkach ArrayPool. Unit: **139 passed, 0 failed**
(było 135; +4 nowe testy).

## P1a. CleanupIdleAsync gubił connections przy cancellation

### Problem

`ThrowIfCancellationRequested()` stał PO zdjęciu wpisu z `_idle`, a
re-enqueue/dispose dopiero za pętlą — cancel w środku gubił physical
connections z trackingu (bez właściciela, licznik zawyżony).

### Fix

Anulowanie może już tylko *zapobiec rozpoczęciu* draina
(`WaitAsync(token)`); po zdjęciu wpisu rozliczenie jest synchroniczne
i bez punktów anulowania (klasyfikacja + requeue/dispose deterministyczne).

### Test

`Maintenance_CancelledToken_LosesNothing`: 50× naprzemiennie anulowany/żywy
maintenance na 3 idle connections — za każdym razem
`Idle + Active == Total`, nic nie ginie.

## P1b. Leak rezerwacji po udanym Open w maintenance

### Problem

`TryReserve → Open OK → WaitAsync(_idleLock) rzuca OCE/ODE` = utworzone
połączenie bez dispose + rezerwacja bez release (`_totalConnections`
trwale zawyżone).

### Fix

Nowa metoda `TryCreateAndParkIdleAsync` z regułą ownership w try/finally:
sukces = parkowanie (rezerwacja staje się żywym połączeniem), każda inna
ścieżka = `DisposeConnectionAsync` + `ReleaseReservation`. Flaga `parked`,
pojedynczy punkt cleanupu. Do tego hook testowy `BeforeParkIdleForTests`.

### Test

`MaintenanceRefill_CancelAfterOpen_DisposesAndReleases`: rezerwacja → udany
Open (replay) → deterministyczny cancel przed enqueue (bramka TCS) →
connection `Closed`, `TotalConnections` wraca do poprzedniej wartości.

## P2. Współdzielony teardown DisposeAsync

### Problem

Drugi równoległy `DisposeAsync()` wracał natychmiast po fladze `_disposed`,
zanim pierwszy skończył maintenance shutdown / Clear / reap / dispose
prymitywów — zakończenie nie oznaczało domknięcia puli.

### Fix

Pierwszy caller tworzy jeden `DisposeCoreAsync()` Task (`_disposeTask`),
każdy kolejny awaituje ten sam Task. Teardown dokładnie raz
(`DisposeCoreRunCount == 1`), brak double-dispose/decrement/deadlocków,
równoległy `ReturnAsync` bezpieczny jak wcześniej.

### Test

`ConcurrentDisposeAsync_AllAwaitFullTeardown_ExactlyOnce`: 8 równoległych
dispose, potem `Total == Active == Idle == 0`, rent rzuca ODE.

## Dodatkowe znaleziska stresu (naprawione w tym passie)

- **Wiszący waiter (root cause hanga):** udowodnione empirycznie, że
  `SemaphoreSlim.Dispose()` nigdy nie kończy zaparkowanego `WaitAsync`.
  `RentAsync` (semaphore) i sekcja enqueue w `ReturnAsync` (`_idleLock`)
  mogły wisieć w nieskończoność przy teardown. Fix: `RentAsync` czeka na
  linked token (caller + `_disposeCts`), sekcja enqueue na
  `_idleLock.WaitAsync(_disposeCts.Token)` z obsługą OCE jak teardown.
- **Kluczowanie `_active` po Pid:** wszystkie replay connections mają
  `Pid == -1` (brak BackendKeyData) — wpisy nadpisywały się, licznik
  rozjeżdżał się w stresie. Zmiana na klucz po referencji (`NzConnection`,
  brak nadpisanego `Equals`), każdy physical connection ma własny slot.

## Stress test

`PoolLifecycle_Stress_RentReturnMaintenanceCancelDispose` (replay):
6 workerów rent→częściowy odczyt→return, 2 maintenance z losową
kancelacją, 4 równoległe `DisposeAsync` w trakcie pracy. Finał:
`Total == Active == Idle == 0`, `DisposeCoreRunCount == 1`, zero wyjątków
(`ReturnAsync` nie rzuca nawet ODE), zero deadlocków (~2 s + teardown).

---

## DO NOT TOUCH WITHOUT PROFILING.

---

# Pool atomicity/lifecycle pass (2026-10-04, cz. 4)

Mały correctness pass w `NzConnectionPool`. Zero zmian w readerze,
dekodowaniu, protokole, `NzReadBuffer`, `Numeric`, `GetFieldValue<T>`,
parameter rendering, lazy, `SequentialAccess`, `ValueStringBuilder`,
ArrayPool. Unit: **141 passed, 0 failed** (było 139; +2 nowe testy).

## 1. RentAsync: rejestracja active zsynchronizowana z teardownem

### Problem

Po udanym `CreateConnectionAsync` (lub pobraniu z idle) rejestracja
`_active.TryAdd` była bez synchronizacji. Raczej: Open kończy się, a
`DisposeAsync` zdąży zrobić reap + zamknąć primitives, po czym rent dodawał
świeży wpis i zwracał connection z martwej puli — `TotalConnections == 1`
po zakończeniu dispose.

### Fix

`TryRegisterActive(NzConnection)` pod `_disposeLock`: jeśli `_disposed` —
`false`. Używane dla nowego connection i dla kandydata z idle. Przy
porażce: `DisposeConnectionAsync` + `ReleaseReservation`/decrement +
`ObjectDisposedException`.

### Test

`Rent_OpenCompletesAfterDispose_DoesNotRegister_NoLeak` (replay + hook
`BeforeRegisterActiveForTests`): Open sukces, wstrzymanie przed rejestracją,
teardown do końca, zwolnienie → rent rzuca ODE, connection `Closed`,
`TotalConnections == 0`.

## 2. DisposeAsync czeka na rozpoczęte ReturnAsync

### Problem

`ReturnAsync` od razu `_active.TryRemove`, potem rollback i oczekiwanie na
`_idleLock`. `DisposeAsync` (widzące puste `_active`) mogło zakończyć się,
gdy return wciąż trzymał physical connection i licznik.

### Fix

`_inFlightReturns` + `_returnsDrained` TCS; `ReturnAsync` inkrementuje na
wejściu, dekrementuje w `finally` (przy 0 → `TrySetResult`).
`DisposeCoreAsync` po `Cancel()` i czeka: `cancel → maintenance → wait
in-flight returns → Clear idle → reap active → dispose primitives`.
Rollback w returnie dostał `_disposeCts.Token`, żeby teardown nie czekał na
długi rollback.

### Test

`DisposeAsync_WaitsForInFlightReturn` (hook `BeforeReturnCleanupForTests`):
return wisi w połowie cleanupu, dispose nie kończy się przed zwolnieniem;
po zwolnieniu `Total == Active == Idle == 0`, `DisposeCoreRunCount == 1`.

## 3. (P2) Brak linked CTS przy `CancellationToken.None`

### Problem

`RentAsync` zawsze robił `CreateLinkedTokenSource(cancellationToken,
_disposeCts.Token)` → CTS + registrations na każdy checkout.

### Fix

Gdy `!cancellationToken.CanBeCanceled`, `rentToken = _disposeCts.Token`
bez alokacji; linked CTS tworzony tylko dla tokenu callera (i zwalniany w
`finally`).

### Benchmark (`PoolRentBench`, replay, ShortRun, `MemoryDiagnoser`)

| Wariant | Mean | Allocated |
|---|---:|---:|
| `Rent+Return (CancellationToken.None)` (new) | 233.2 ns | **152 B** |
| `Rent+Return (cancelable → linked CTS)` (old path) | 297.1 ns | 232 B |

### Change

Common path (None): **−80 B i ~−64 ns na Rent+Return** (~35% alokacji
checkout). Wartość zależna od maszyny; liczy się kierunek i brak CTS.

### Decision

**ACCEPTED.**

---

## DO NOT TOUCH WITHOUT PROFILING.

---

# Pool return-barrier fix (2026-10-04, cz. 5)

Jeden realny P1 wskazany w review. Unit: **142 passed, 0 failed** (było 141;
+1 regresyjny test). CI (`.github/workflows/ci.yml`) uruchamia
`--filter "Category=Unit"` na push do `master`.

## Problem

`_returnsDrained` był **jednorazowym** `TaskCompletionSource`. Po pierwszym
cyklu Return `TrySetResult()` zostawiał go completed na zawsze, więc kolejny
`DisposeAsync` widzący `_inFlightReturns > 0` awaituje **już ukończony** Task
i idzie dalej, mimo że drugi `ReturnAsync` wciąż trwa. Dodatkowo istniał
race `0 → Return`: `DisposeCoreAsync` mógł odczytać `_inFlightReturns == 0`
tuż przed inkrementacją returnu.

## Fix

Bariera przeniesiona pod ten sam `_disposeLock`, który ustawia `_disposed`:

- `TryBeginReturn()`: pod lockiem — jeśli `_disposed` → `false` (return idzie
  ścieżką bezpośredniego zamknięcia, nie jest liczony); przy `0 → 1` tworzy
  **świeży** TCS.
- `EndReturn()`: pod lockiem dekrement; przy `0` przechwytuje TCS i zeruje
  pole; `TrySetResult` poza lockiem.
- `DisposeAsync()`: pod lockiem ustawia `_disposed` i **przechwytuje**
  `_returnsDrained?.Task` (dokładny zbiór rozpoczętych returnów), przekazuje do
  `DisposeCoreAsync`, który go awaituje.

Ustawienie `_disposed` i `TryBeginReturn` pod tym samym lockiem zamyka race
`0 → Return`; świeży TCS na każdy cykl zamyka bug jednorazowości.

## Test

`DisposeAsync_WaitsForInFlightReturn_AfterEarlierReturnCycle` (replay):
1. pełny cykl rent/return (stara bariera byłaby już zużyta) →
2. rent ponownie → 3. drugi return zatrzymany w `BeforeReturnCleanupForTests`
→ 4. `DisposeAsync` → 5. **nie kończy się** → 6. zwolnienie →
7. dispose kończy się → 8. `Total == Active == Idle == 0`,
`DisposeCoreRunCount == 1`. Test zawsze zwalnia hook w `finally`, więc
regresja kończy się szybkim `Assert`, nie zawieszeniem.

Weryfikacja mutacyjna: tymczasowe pominięcie awajtu bariery w
`DisposeCoreAsync` powoduje natychmiastowy fail testu; przywrócenie → zielono.

## Decision

**ACCEPTED.**

---

## STOP — DO NOT TOUCH WITHOUT PROFILING.
