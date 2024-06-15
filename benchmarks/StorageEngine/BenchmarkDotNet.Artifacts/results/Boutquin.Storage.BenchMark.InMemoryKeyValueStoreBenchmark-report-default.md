
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JINISG : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean         | Error       | StdDev      |
------------------ |---------- |-------------:|------------:|------------:|
 SearchExisting    | 10        |     988.6 μs |     8.46 μs |     2.20 μs |
 SearchNonExisting | 10        |     988.6 μs |    20.92 μs |     5.43 μs |
 Read              | 10        |   1,001.8 μs |    10.68 μs |     2.77 μs |
 Write             | 10        |   1,992.9 μs |   184.04 μs |    47.79 μs |
 SearchNonExisting | 100       |   9,648.4 μs |   830.46 μs |   215.67 μs |
 SearchExisting    | 100       |   9,706.5 μs |   495.53 μs |   128.69 μs |
 Read              | 100       |   9,765.9 μs |   750.18 μs |   194.82 μs |
 Write             | 100       |  18,329.4 μs | 4,730.61 μs | 1,228.52 μs |
 SearchExisting    | 1000      |  77,044.4 μs | 1,826.91 μs |   474.44 μs |
 Read              | 1000      |  77,884.5 μs | 1,960.46 μs |   509.12 μs |
 SearchNonExisting | 1000      |  78,358.2 μs | 4,834.08 μs | 1,255.40 μs |
 Write             | 1000      | 155,338.8 μs | 3,179.99 μs |   825.83 μs |
