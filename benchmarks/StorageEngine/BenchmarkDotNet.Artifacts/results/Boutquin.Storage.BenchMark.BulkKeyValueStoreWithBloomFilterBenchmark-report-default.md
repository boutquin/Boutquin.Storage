
BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.4529/22H2/2022Update)
Intel Core i9-10910 CPU 3.60GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 8.0.300
  [Host]     : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2
  Job-JLVPNM : .NET 8.0.5 (8.0.524.21615), X64 RyuJIT AVX2

Runtime=.NET 8.0  InvocationCount=16  IterationCount=3  
WarmupCount=1  

 Method            | ItemCount | Mean           | Error          | StdDev        |
------------------ |---------- |---------------:|---------------:|--------------:|
 SearchNonExisting | 10        |       3.638 μs |       7.604 μs |     0.4168 μs |
 SearchNonExisting | 100       |      31.844 μs |      35.966 μs |     1.9714 μs |
 SearchExisting    | 10        |   7,787.212 μs |   2,195.947 μs |   120.3672 μs |
 Read              | 10        |   7,809.710 μs |     204.058 μs |    11.1851 μs |
 Write             | 10        |   8,090.566 μs |   5,164.911 μs |   283.1062 μs |
 Write             | 100       |  67,630.160 μs |  24,484.282 μs | 1,342.0661 μs |
 Read              | 100       | 156,162.542 μs |  25,566.361 μs | 1,401.3785 μs |
 SearchExisting    | 100       | 177,593.033 μs | 143,849.708 μs | 7,884.8879 μs |
