
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4529/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JLVPNM : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=3  
WarmupCount=1  

 Method            | ItemCount | Mean      | Error      | StdDev    |
------------------ |---------- |----------:|-----------:|----------:|
 SearchNonExisting | 10        |  1.990 ms |  0.6724 ms | 0.0369 ms |
 SearchExisting    | 10        |  8.050 ms |  0.8840 ms | 0.0485 ms |
 Read              | 10        |  8.397 ms |  7.1161 ms | 0.3901 ms |
 Write             | 10        |  9.939 ms |  4.3518 ms | 0.2385 ms |
 SearchNonExisting | 100       | 19.278 ms | 21.7813 ms | 1.1939 ms |
 SearchExisting    | 100       | 68.446 ms | 20.2687 ms | 1.1110 ms |
 Read              | 100       | 68.945 ms | 10.6698 ms | 0.5848 ms |
 Write             | 100       | 84.378 ms |  3.4058 ms | 0.1867 ms |
