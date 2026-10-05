using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using SpankyhoOptimizer.Services;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
[InProcess]
public class NetworkHotPathBenchmarks
{
    private readonly string[] _expected = ["1.1.1.1", "9.9.9.9"];
    private readonly string[] _clean = ["1.1.1.1", "9.9.9.9"];
    private readonly string[] _leaked = ["1.1.1.1", "9.9.9.9", "192.168.1.1"];

    [Benchmark(Baseline = true)]
    public string[] DnsReadbackClean() => DnsProfileService.FindUnexpectedResolvers(_expected, _clean);

    [Benchmark]
    public string[] DnsReadbackLeak() => DnsProfileService.FindUnexpectedResolvers(_expected, _leaked);

    [Benchmark]
    public Task<MtuAdapterInfo> ActiveAdapterEnumeration() => MtuOptimizerService.GetActiveAdapterAsync();
}
