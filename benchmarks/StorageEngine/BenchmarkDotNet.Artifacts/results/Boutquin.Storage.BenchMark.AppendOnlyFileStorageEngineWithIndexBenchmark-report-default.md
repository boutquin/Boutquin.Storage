
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JINISG : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean       | Error      | StdDev    |
------------------ |---------- |-----------:|-----------:|----------:|
 SearchNonExisting | 10        |   1.978 ms |  0.1616 ms | 0.0420 ms |
 SearchExisting    | 10        |   7.783 ms |  0.4536 ms | 0.1178 ms |
 Read              | 10        |   8.032 ms |  0.6409 ms | 0.1664 ms |
 Write             | 10        |   9.057 ms |  0.3159 ms | 0.0820 ms |
 SearchNonExisting | 100       |  17.683 ms |  5.4390 ms | 1.4125 ms |
 SearchExisting    | 100       |  66.186 ms |  1.9406 ms | 0.3003 ms |
 Read              | 100       |  67.476 ms |  0.9837 ms | 0.1522 ms |
 Write             | 100       |  78.134 ms |  3.4394 ms | 0.8932 ms |
 SearchNonExisting | 1000      | 152.105 ms |  1.5059 ms | 0.2330 ms |
 SearchExisting    | 1000      | 664.261 ms |  3.0767 ms | 0.7990 ms |
 Read              | 1000      | 664.740 ms |  4.2531 ms | 0.6582 ms |
 Write             | 1000      | 788.722 ms | 21.4107 ms | 5.5603 ms |
