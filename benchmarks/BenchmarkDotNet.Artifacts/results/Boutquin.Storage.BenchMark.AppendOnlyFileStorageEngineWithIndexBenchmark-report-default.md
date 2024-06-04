
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-NZHMVY : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean       | Error      | StdDev    |
------------------ |---------- |-----------:|-----------:|----------:|
 Read              | 1000      |         NA |         NA |        NA |
 SearchExisting    | 1000      |         NA |         NA |        NA |
 SearchNonExisting | 10        |   2.008 ms |  0.2685 ms | 0.0697 ms |
 SearchExisting    | 10        |   4.263 ms |  0.3099 ms | 0.0805 ms |
 Read              | 10        |   4.267 ms |  0.4947 ms | 0.0766 ms |
 Write             | 10        |   9.694 ms |  0.4541 ms | 0.1179 ms |
 SearchNonExisting | 100       |  18.193 ms |  5.9737 ms | 1.5514 ms |
 SearchExisting    | 100       |  38.402 ms |  6.8196 ms | 1.0553 ms |
 Read              | 100       |  41.448 ms | 12.1851 ms | 3.1644 ms |
 Write             | 100       |  82.967 ms |  0.2534 ms | 0.0658 ms |
 SearchNonExisting | 1000      | 154.118 ms |  3.7673 ms | 0.9783 ms |
 Write             | 1000      | 851.168 ms | 23.3503 ms | 6.0640 ms |

Benchmarks with issues:
  AppendOnlyFileStorageEngineWithIndexBenchmark.Read: Job-NZHMVY(Runtime=.NET 8.0, InvocationCount=16, IterationCount=5, WarmupCount=1) [ItemCount=1000]
  AppendOnlyFileStorageEngineWithIndexBenchmark.SearchExisting: Job-NZHMVY(Runtime=.NET 8.0, InvocationCount=16, IterationCount=5, WarmupCount=1) [ItemCount=1000]
