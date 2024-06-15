
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JINISG : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean              | Error             | StdDev            |
------------------ |---------- |------------------:|------------------:|------------------:|
 SearchNonExisting | 10        |          3.339 μs |          1.062 μs |         0.1643 μs |
 SearchNonExisting | 100       |         33.940 μs |          9.007 μs |         2.3390 μs |
 Write             | 10        |      7,107.038 μs |        472.123 μs |       122.6088 μs |
 SearchExisting    | 10        |      7,591.380 μs |        222.921 μs |        57.8919 μs |
 Read              | 10        |      7,629.140 μs |        302.622 μs |        78.5901 μs |
 Write             | 100       |     61,261.050 μs |      5,697.209 μs |       881.6494 μs |
 Read              | 100       |    157,203.731 μs |      9,981.153 μs |     1,544.5945 μs |
 SearchExisting    | 100       |    159,193.431 μs |      3,050.399 μs |       792.1788 μs |
 SearchNonExisting | 1000      |    351,691.071 μs |      9,081.569 μs |     2,358.4546 μs |
 Write             | 1000      |    613,605.469 μs |      8,833.342 μs |     2,293.9908 μs |
 Read              | 1000      | 46,460,881.966 μs | 29,030,501.058 μs | 7,539,128.9343 μs |
 SearchExisting    | 1000      | 47,372,510.256 μs |  8,893,294.121 μs | 2,309,560.2414 μs |
