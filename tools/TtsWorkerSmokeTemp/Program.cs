using System.Diagnostics;
using SpankyhoOptimizer.Services;

string output = Path.Combine(Path.GetTempPath(), "spanky-worker-smoke-" + Guid.NewGuid().ToString("N") + ".mp3");
try
{
    var warm = Stopwatch.StartNew();
    await NeuralTtsWorkerService.WarmAsync();
    warm.Stop();

    var gen = Stopwatch.StartNew();
    long reported = await NeuralTtsWorkerService.GenerateAsync("Test hlasu.", output, CancellationToken.None);
    gen.Stop();

    long size = File.Exists(output) ? new FileInfo(output).Length : 0;
    Console.WriteLine($"WARM_MS={warm.ElapsedMilliseconds} GENERATE_MS={gen.ElapsedMilliseconds} REPORTED_MS={reported} SIZE={size}");
    Environment.ExitCode = size > 1000 ? 0 : 2;
}
finally
{
    await NeuralTtsWorkerService.StopAsync();
    try { File.Delete(output); } catch { }
}
