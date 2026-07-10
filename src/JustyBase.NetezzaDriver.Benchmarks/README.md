
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