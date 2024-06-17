
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4529/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JLVPNM : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=3  
WarmupCount=1  

 Method            | ItemCount | Mean       | Error      | StdDev     |
------------------ |---------- |-----------:|-----------:|-----------:|
 Read              | 10        |   8.168 ms |   6.197 ms |  0.3397 ms |
 SearchExisting    | 10        |   8.666 ms |   2.631 ms |  0.1442 ms |
 SearchNonExisting | 10        |   8.855 ms |   2.587 ms |  0.1418 ms |
 Write             | 10        |  11.128 ms |   2.138 ms |  0.1172 ms |
 Write             | 100       |  94.059 ms |  34.718 ms |  1.9030 ms |
 Read              | 100       | 164.497 ms |  13.684 ms |  0.7501 ms |
 SearchExisting    | 100       | 169.550 ms |  35.821 ms |  1.9635 ms |
 SearchNonExisting | 100       | 393.708 ms | 578.550 ms | 31.7123 ms |
