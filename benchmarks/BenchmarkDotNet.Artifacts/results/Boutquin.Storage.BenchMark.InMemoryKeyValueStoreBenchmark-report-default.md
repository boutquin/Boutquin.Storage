
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-NZHMVY : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean         | Error       | StdDev      |
------------------ |---------- |-------------:|------------:|------------:|
 SearchExisting    | 10        |     985.9 μs |    28.70 μs |     7.45 μs |
 SearchNonExisting | 10        |     994.3 μs |    28.84 μs |     7.49 μs |
 Read              | 10        |   1,004.1 μs |    21.64 μs |     5.62 μs |
 Write             | 10        |   2,167.1 μs |   823.47 μs |   213.85 μs |
 SearchNonExisting | 100       |   9,656.4 μs |   801.78 μs |   124.08 μs |
 Read              | 100       |   9,664.9 μs | 1,466.69 μs |   226.97 μs |
 SearchExisting    | 100       |   9,865.5 μs |   403.20 μs |   104.71 μs |
 Write             | 100       |  18,159.6 μs | 5,099.99 μs | 1,324.45 μs |
 SearchNonExisting | 1000      |  77,601.5 μs | 3,137.02 μs |   814.68 μs |
 SearchExisting    | 1000      |  78,031.0 μs | 1,956.34 μs |   508.05 μs |
 Read              | 1000      |  78,083.0 μs | 1,915.89 μs |   497.55 μs |
 Write             | 1000      | 156,462.5 μs | 3,280.98 μs |   852.06 μs |
