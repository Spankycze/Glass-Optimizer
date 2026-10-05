using System.Diagnostics;
using System.Text.Json;
using SpankyhoOptimizer.Services;

if (args.Length != 1) throw new ArgumentException("One output JSON path required");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
var probe = new AiSystemPerformanceProbe();
var measurements = new List<object>();
for (int i = 0; i < 7; i++)
{
    var elapsed = Stopwatch.StartNew();
    var sample = await probe.SampleAsync(timeout.Token);
    measurements.Add(new { Sample = sample, ProbeElapsedMs = elapsed.Elapsed.TotalMilliseconds,
        ExperimentBlocker = AiSystemPerformancePolicy.SampleBlocker(sample) });
    if (i < 6) await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
}
string output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Read-only native probe: {measurements.Count} samples saved. No tweak transactions invoked.");
