
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-NZHMVY : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean           | Error          | StdDev        |
------------------ |---------- |---------------:|---------------:|--------------:|
 SearchExisting    | 10        |       7.581 ms |      0.2927 ms |     0.0453 ms |
 Read              | 10        |       7.717 ms |      0.4394 ms |     0.1141 ms |
 Write             | 10        |       7.827 ms |      1.3598 ms |     0.3531 ms |
 SearchNonExisting | 10        |       8.250 ms |      2.3125 ms |     0.6006 ms |
 Write             | 100       |      66.849 ms |      3.4447 ms |     0.5331 ms |
 SearchExisting    | 100       |     170.365 ms |     14.8993 ms |     3.8693 ms |
 Read              | 100       |     181.389 ms |     37.9296 ms |     5.8696 ms |
 SearchNonExisting | 100       |     388.223 ms |     62.3706 ms |    16.1974 ms |
 Write             | 1000      |     668.652 ms |      8.9828 ms |     2.3328 ms |
 SearchExisting    | 1000      |  51,379.167 ms |  3,896.1090 ms |   602.9272 ms |
 Read              | 1000      |  58,750.617 ms |  6,945.2287 ms | 1,803.6538 ms |
 SearchNonExisting | 1000      | 153,963.529 ms | 12,426.9615 ms | 3,227.2424 ms |
