```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9550)
AMD Ryzen 7 3700X 3.60GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  

```
| Method           | Mean     | Error   | StdDev   | Median   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |---------:|--------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| DnsReadbackClean | 174.7 ns | 6.60 ns | 19.45 ns | 168.9 ns |  1.01 |    0.15 | 0.0610 |     512 B |        1.00 |
| DnsReadbackLeak  | 238.6 ns | 8.04 ns | 23.59 ns | 232.3 ns |  1.38 |    0.20 | 0.0648 |     544 B |        1.06 |
