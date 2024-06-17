
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4529/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JLVPNM : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=3  
WarmupCount=1  

 Method            | ItemCount | Mean       | Error       | StdDev     |
------------------ |---------- |-----------:|------------:|-----------:|
 SearchExisting    | 10        |   7.745 ms |   1.7467 ms |  0.0957 ms |
 Read              | 10        |   7.819 ms |   0.9870 ms |  0.0541 ms |
 SearchNonExisting | 10        |   7.971 ms |   1.5952 ms |  0.0874 ms |
 Write             | 10        |   8.312 ms |   2.5112 ms |  0.1376 ms |
 Write             | 100       |  66.676 ms |   8.5004 ms |  0.4659 ms |
 SearchExisting    | 100       | 157.083 ms |  23.1208 ms |  1.2673 ms |
 Read              | 100       | 157.638 ms |  31.0578 ms |  1.7024 ms |
 SearchNonExisting | 100       | 322.198 ms | 443.1053 ms | 24.2881 ms |
