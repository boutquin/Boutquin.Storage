
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4529/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JLVPNM : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=3  
WarmupCount=1  

 Method            | ItemCount | Mean      | Error      | StdDev    |
------------------ |---------- |----------:|-----------:|----------:|
 SearchNonExisting | 10        |  1.001 ms |  0.1656 ms | 0.0091 ms |
 SearchExisting    | 10        |  1.013 ms |  0.0967 ms | 0.0053 ms |
 Read              | 10        |  1.013 ms |  0.0856 ms | 0.0047 ms |
 Write             | 10        |  1.966 ms |  0.7775 ms | 0.0426 ms |
 SearchExisting    | 100       |  9.953 ms |  2.2418 ms | 0.1229 ms |
 SearchNonExisting | 100       |  9.982 ms |  2.3115 ms | 0.1267 ms |
 Read              | 100       | 10.064 ms |  3.5807 ms | 0.1963 ms |
 Write             | 100       | 19.333 ms | 16.6551 ms | 0.9129 ms |
