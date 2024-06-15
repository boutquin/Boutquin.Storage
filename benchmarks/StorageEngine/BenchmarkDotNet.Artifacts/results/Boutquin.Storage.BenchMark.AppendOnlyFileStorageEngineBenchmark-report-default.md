
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4412/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JINISG : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=5  
WarmupCount=1  

 Method            | ItemCount | Mean           | Error          | StdDev        |
------------------ |---------- |---------------:|---------------:|--------------:|
 Write             | 10        |       7.053 ms |      0.4143 ms |     0.1076 ms |
 Read              | 10        |       7.562 ms |      1.4675 ms |     0.3811 ms |
 SearchExisting    | 10        |       7.595 ms |      0.1919 ms |     0.0498 ms |
 SearchNonExisting | 10        |       7.966 ms |      0.8111 ms |     0.2106 ms |
 Write             | 100       |      60.793 ms |      1.4540 ms |     0.3776 ms |
 Read              | 100       |     158.482 ms |      3.6000 ms |     0.9349 ms |
 SearchExisting    | 100       |     158.733 ms |      5.0067 ms |     1.3002 ms |
 SearchNonExisting | 100       |     301.394 ms |     25.0937 ms |     3.8833 ms |
 Write             | 1000      |     615.087 ms |     29.6627 ms |     7.7033 ms |
 SearchExisting    | 1000      |  34,940.972 ms | 16,597.0840 ms | 4,310.2100 ms |
 Read              | 1000      |  46,191.133 ms | 13,365.9111 ms | 3,471.0847 ms |
 SearchNonExisting | 1000      | 168,189.345 ms | 28,129.2126 ms | 7,305.0672 ms |
