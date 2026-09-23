
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
