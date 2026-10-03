
### DbDataReader benchmarks

| Method             | Query                | Mean       | Error     | StdDev    | Gen0      |  Allocated   |
|------------------- |--------------------- |-----------:|----------:|----------:|----------:|-------------:|
| JustyNzDriver      | /*1*/(...)50000 [80] | 123.789 ms | 1.9131 ms | 1.6959 ms |  500.0000 |  5 086.00 KB |
| JustyNzDriverTyped | /*1*/(...)50000 [80] | 121.499 ms | 2.3356 ms | 2.9538 ms |         - |      7.84 KB |
| NzOdbc             | /*1*/(...)50000 [80] | 176.228 ms | 3.2665 ms | 3.0555 ms | 1333.3333 | 12 895.74 KB |
| JustyNzDriver      | /*2*/(...)10000 [67] |   7.581 ms | 0.1515 ms | 0.3026 ms |  132.8125 |  1 131.78 KB |
| JustyNzDriverTyped | /*2*/(...)10000 [67] |   7.276 ms | 0.1408 ms | 0.1880 ms |         - |     19.06 KB |
| NzOdbc             | /*2*/(...)10000 [67] |  14.075 ms | 0.2108 ms | 0.1972 ms |  250.0000 |  2 064.83 KB |


### External table benchmarks

| Method                            | Mean    | Error    | StdDev   | Allocated |
|---------------------------------- |--------:|---------:|---------:|----------:|
| ExternalUnloadAndLoadNz           | 1.690 s | 0.0165 s | 0.0138 s |   28.6 KB |
| ExternalUnloadAndLoadOriginalOdbc | 1.683 s | 0.0058 s | 0.0054 s |    7.9 KB |

### Generic field access experiment

`FieldAccessBench` reads the same 100,000 `BIGINT` values from
`JUST_DATA..FACTPRODUCTINVENTORY` and checks a checksum. The following
before/after runs evaluated a direct `GetFieldValue<long>` fast path; the
candidate was removed because the improvement was not consistent across JIT
and NativeAOT and allocation savings did not meet the 10% acceptance threshold.

| Runtime | Version | Method | Mean | Allocated |
|---------|---------|--------|-----:|----------:|
| .NET 10 JIT, before | existing cast | 474.5 ms | 60.28 MB |
| .NET 10 JIT, before | `GetFieldValue<long>` | 507.6 ms | 60.28 MB |
| .NET 10 JIT, candidate | `GetFieldValue<long>` | 490.8 ms | 57.99 MB |
| .NET 10 NativeAOT, before | existing cast | 328.5 ms | 60.28 MB |
| .NET 10 NativeAOT, before | `GetFieldValue<long>` | 347.5 ms | 60.28 MB |
| .NET 10 NativeAOT, candidate | `GetFieldValue<long>` | 358.7 ms | 58.00 MB |

Each run used three measured iterations and one warmup. The candidate saved
about 24 bytes per row (3.8% of total measured allocations), while its generic
access time improved 3.3% in JIT and regressed 3.2% in NativeAOT. The timing
spread and opposite direction do not establish a throughput gain. The benchmark
can be rerun with:

```bash
dotnet run -c Release --project src/JustyBase.NetezzaDriver.Benchmarks --framework net10.0 -- \
  --filter '*FieldAccessBench*' --runtimes net10.0 --iterationCount 3 --warmupCount 1
```

### Varying-field offset decoding

`VariableFieldOffsetBench` compares the former repeated prefix walk with the
one-pass offset table on synthetic DBOS rows. This isolates the offset work;
it does not represent end-to-end database throughput. A .NET 10 NativeAOT
ShortRun on an AMD Ryzen 7 7840HS measured:

| Varying fields | Repeated scan | Precomputed offsets | Ratio |
|---------------:|--------------:|--------------------:|------:|
| 8 | 21.29 ns | 15.44 ns | 0.73x |
| 64 | 2.854 us | 154.86 ns | 0.05x |
| 256 | 58.36 us | 657.50 ns | 0.01x |

The benchmark ran three measured iterations per case with no managed
allocations in either method. The sample supports linear scaling for offset
calculation; validate end-to-end impact with the live reader benchmarks for a
representative schema.

Run it with:

```bash
dotnet run -c Release --project src/JustyBase.NetezzaDriver.Benchmarks --framework net10.0 -- \
  --filter '*VariableFieldOffsetBench*'
```

### Read-buffer and row-decode changes (2026)

`NzConnection` now owns a single application-level read buffer
(`NzReadBuffer`) layered directly over the raw `NetworkStream`/`SslStream`.
The public `Open`/`OpenAsync` overloads default to `useBufferedStream: false`;
passing `true` still opts into the legacy `BufferedStream` (which would double
buffer and reintroduced an intermittent socket-read timeout on long result
sets). Protocol primitives are served from the buffer, `RowStandard` payloads
are decoded in place (no per-row copy into `_tmp_buffer`), per-field descriptor
lists are frozen into arrays, the type dispatch is a single `switch`, and the
per-read byte-offset for diagnostics is computed only when debug logging is
enabled.

Synthetic results on .NET 10 NativeAOT, AMD Ryzen 7 7840HS, `ShortRun`, no
server:

| Method | Old | New | Ratio |
|--------|----:|----:|------:|
| Stream.ReadExactly per int32 vs NzReadBuffer in-buffer | 3,731 ns | 1,230 ns | 0.33x |
| Interpolated validation context vs numeric fast check | 28,462 ns | 240 ns | 0.06x |
| Linked CTS per command vs reused `TryReset` | 57.9 ns / 144 B | 37.6 ns / 0 B | 0.65x |
| `GetValue` boxing vs `GetFieldValue<int>` | 3.16 ns / 24 B | 0.97 ns / 0 B | 0.31x |
| Reparse parameters per execute vs cached plan | 18,233 ns | 10,850 ns | 0.60x |

`LiveReaderBench` measures the same reader path end-to-end against a live
Netezza instance (50k rows per scenario, `ShortRun`). Before and after the
unified-buffer/decode work the means were within noise:

| Scenario | Before | After |
|----------|-------:|------:|
| MixedWide_50k | 562.6 ms | 552.3 ms |
| Numeric_50k | 2,200.5 ms | 2,226.0 ms |
| Text_50k | 1,332.0 ms | 1,332.5 ms |

The end-to-end result is dominated by backend execution and network round
trips, so the client-side read/decode reductions do not move these totals; they
show up in the isolated microbenchmarks above and removed the intermittent
15 s socket timeouts previously seen while draining large result sets.

```bash
dotnet run -c Release --project src/JustyBase.NetezzaDriver.Benchmarks --framework net10.0 -- \
  --filter '*LiveReaderBench*'
```

### Database-free replay benchmark

`ReplayReaderBench` removes the live database from the equation entirely. A real
query response is recorded byte-for-byte through `RecordingProxy`, split into one
server segment per frontend query, and stored as a `.nzreplay.gz` fixture.
`NzReplayServer` replays those segments over a loopback TCP socket, so the driver
follows its normal socket/handshake code paths with no database or real network
latency. One connection is opened once and reused; the final segment is repeated
for subsequent identical queries. `ReplayReaderBench` and `NzReplayTests`
(`Category=Unit`) use the same fixtures, so the client read path can be measured
and regression-tested without `NZ_DEV_HOST`.

Recording fixtures (requires a live server; writes to
`src/JustyBase.NetezzaDriver.TestSupport/Fixtures`):

```bash
dotnet run -c Release --project tools/NzReplayCapture
```

Running the replay benchmark and the database-free replay tests:

```bash
dotnet run -c Release --project src/JustyBase.NetezzaDriver.Benchmarks --framework net10.0 -- \
  --filter '*ReplayReaderBench*'
dotnet test src/JustyBase.NetezzaDriver.Tests/JustyBase.NetezzaDriver.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~NzReplayTests'
```

Measured on .NET 10 NativeAOT, AMD Ryzen 7 7840HS, `ShortRun`, loopback replay,
`GetValue` over every column:

| Scenario | Old (HEAD 1.9.3) | New | Speedup | Allocated old | Allocated new |
|----------|-----------------:|----:|--------:|--------------:|--------------:|
| DimDate 3,652 x 19 | 21.98 ms | 2.384 ms | ~9.2x | 3.23 MB | 1.10 MB |
| Fact 200,000 x 7 | 1,098.82 ms | 47.134 ms | ~23x | 138.87 MB | 19.84 MB |

The baseline is the legacy read path with `useBufferedStream: false`. The legacy
`BufferedStream` default cannot run the request-paced replay because it throws
when a write follows a read that left its internal buffer non-empty; that
limitation is one of the reasons the read path now owns a single application
buffer. These numbers isolate client read/decode cost; they do not represent
end-to-end query time, which stays dominated by the backend (see
`LiveReaderBench` above).


