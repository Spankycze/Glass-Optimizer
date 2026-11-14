using System.Diagnostics;
using System.Text.Json;
using SpankyhoOptimizer.Services;

static double? Finite(double value) => double.IsFinite(value) ? value : null;
static object Report(AiSystemPerformanceSample s, double elapsedMs) => new
{
    Sample = new { s.Utc, CpuPercent = Finite(s.CpuPercent), UsedMemoryMb = Finite(s.UsedMemoryMb),
        SchedulerDelayMs = Finite(s.SchedulerDelayMs), FixedWorkMs = Finite(s.FixedWorkMs),
        s.WorkloadKey, TemperatureC = s.TemperatureC is { } c ? Finite(c) : null,
        s.Valid, s.Reason, s.ActiveComponents, WorkloadCpuPercent = Finite(s.WorkloadCpuPercent) },
    ProbeElapsedMs = elapsedMs, ExperimentBlocker = AiSystemPerformancePolicy.SampleBlocker(s)
};
if (args.Length == 1 && args[0] == "--selftest")
{
    var invalid = new AiSystemPerformanceSample(DateTime.UtcNow, double.NaN, double.PositiveInfinity,
        0, 1, "", null, false, "First native sample establishes counters", WorkloadCpuPercent: double.NaN);
    using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(Report(invalid, 2)));
    var sample = parsed.RootElement.GetProperty("Sample");
    if (sample.GetProperty("CpuPercent").ValueKind != JsonValueKind.Null ||
        sample.GetProperty("UsedMemoryMb").ValueKind != JsonValueKind.Null ||
        sample.GetProperty("WorkloadCpuPercent").ValueKind != JsonValueKind.Null ||
        !parsed.RootElement.GetProperty("ExperimentBlocker").GetString()!.Contains("Missing/unreliable measurements"))
        throw new InvalidOperationException("Invalid AI performance samples must remain blocked with valid JSON.");
    Console.WriteLine("AI_PERF_PROBE_JSON_SELFTEST_OK nonfinite=null blocker=retained");
    return;
}
if (args.Length != 1) throw new ArgumentException("One output JSON path required");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
var probe = new AiSystemPerformanceProbe();
var measurements = new List<object>();
for (int i = 0; i < 7; i++)
{
    var elapsed = Stopwatch.StartNew();
    var sample = await probe.SampleAsync(timeout.Token);
    measurements.Add(Report(sample, elapsed.Elapsed.TotalMilliseconds));
    if (i < 6) await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
}
string output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Read-only native probe: {measurements.Count} samples saved. No tweak transactions invoked.");
