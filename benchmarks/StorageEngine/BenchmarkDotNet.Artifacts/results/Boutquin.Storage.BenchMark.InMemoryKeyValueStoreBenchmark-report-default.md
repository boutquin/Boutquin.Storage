
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-XHPHAG : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean         | Error        | StdDev      |
------------------ |---------- |-------------:|-------------:|------------:|
 SearchNonExisting | 10        |     961.6 μs |     26.65 μs |     4.12 μs |
 SearchExisting    | 10        |     962.2 μs |     31.38 μs |     8.15 μs |
 Read              | 10        |     979.0 μs |     38.69 μs |    10.05 μs |
 Write             | 10        |   1,901.9 μs |    120.46 μs |    18.64 μs |
 SearchExisting    | 100       |   9,443.9 μs |    637.41 μs |   165.53 μs |
 SearchNonExisting | 100       |   9,450.6 μs |    699.11 μs |   181.56 μs |
 Read              | 100       |   9,553.2 μs |    523.11 μs |   135.85 μs |
 Write             | 100       |  18,038.3 μs |  6,274.20 μs | 1,629.39 μs |
 SearchNonExisting | 1000      |  74,915.1 μs |  2,206.33 μs |   341.43 μs |
 SearchExisting    | 1000      |  75,110.4 μs |  1,946.70 μs |   505.55 μs |
 Read              | 1000      |  76,090.2 μs |  5,453.94 μs |   844.00 μs |
 Write             | 1000      | 152,111.0 μs | 13,249.76 μs | 2,050.41 μs |
