using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record PerformanceTraceSession(
    string OutputDirectory,
    bool WprStarted,
    Task<ProcessResult>? DotNetTraceTask,
    Task<ProcessResult>? DotNetCountersTask,
    string NetTracePath,
    string CountersPath,
    string EtlPath);

public static class PerformanceTraceService
{
    public static async Task<PerformanceTraceSession> StartAsync(string outputDirectory, CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        int pid = Environment.ProcessId;
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string traceExe = Path.Combine(user, ".dotnet", "tools", "dotnet-trace.exe");
        string countersExe = Path.Combine(user, ".dotnet", "tools", "dotnet-counters.exe");
        string wprExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");
        string netTrace = Path.Combine(outputDirectory, "performance.nettrace");
        string counters = Path.Combine(outputDirectory, "runtime-counters.json");
        string etl = Path.Combine(outputDirectory, "startup.etl");

        bool wprStarted = false;
        if (File.Exists(wprExe))
        {
            var result = await ProcessRunner.RunAsync(wprExe, "-start CPU.light -start DiskIO.light",
                TimeSpan.FromSeconds(10), token, logNonZeroExitAsError: false).ConfigureAwait(false);
            wprStarted = result.Success;
        }

        Task<ProcessResult>? traceTask = File.Exists(traceExe)
            ? ProcessRunner.RunAsync(traceExe,
                $"collect -p {pid} --profile dotnet-sampled-thread-time,gc-collect --duration 00:00:00:06 -o \"{netTrace}\"",
                TimeSpan.FromSeconds(15), token, logNonZeroExitAsError: false)
            : null;
        Task<ProcessResult>? countersTask = File.Exists(countersExe)
            ? ProcessRunner.RunAsync(countersExe,
                $"collect -p {pid} --counters System.Runtime --duration 00:00:00:06 --format json -o \"{counters}\"",
                TimeSpan.FromSeconds(15), token, logNonZeroExitAsError: false)
            : null;

        return new(outputDirectory, wprStarted, traceTask, countersTask, netTrace, counters, etl);
    }

    public static async Task<string> StopAsync(PerformanceTraceSession session, CancellationToken token)
    {
        if (session.DotNetTraceTask != null) await session.DotNetTraceTask.ConfigureAwait(false);
        if (session.DotNetCountersTask != null) await session.DotNetCountersTask.ConfigureAwait(false);

        if (session.WprStarted)
        {
            string wprExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");
            _ = await ProcessRunner.RunAsync(wprExe, $"-stop \"{session.EtlPath}\"",
                TimeSpan.FromSeconds(20), token, logNonZeroExitAsError: false).ConfigureAwait(false);
        }

        bool traceOk = File.Exists(session.NetTracePath) && new FileInfo(session.NetTracePath).Length > 4096;
        bool countersOk = File.Exists(session.CountersPath) && new FileInfo(session.CountersPath).Length > 256;
        bool wprOk = !session.WprStarted || (File.Exists(session.EtlPath) && new FileInfo(session.EtlPath).Length > 4096);
        return $"PERF_TRACE_SMOKE_OK dotnetTrace={traceOk} counters={countersOk} wpr={wprOk} dir={session.OutputDirectory}";
    }
}
