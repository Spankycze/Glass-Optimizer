using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record SelfDiagnosticItem(string Name, bool Ok, string Detail);
public sealed record SelfDiagnosticReport(DateTime TimeUtc, IReadOnlyList<SelfDiagnosticItem> Items)
{
    public bool Healthy => Items.All(x => x.Ok);
}

public static class SelfDiagnosticsService
{
    public static SelfDiagnosticReport? LastReport { get; private set; }

    public static async Task<SelfDiagnosticReport> RunAsync(CancellationToken token = default)
    {
        var report = WorkerExecutionContext.IsWorker
            ? await RunLocalAsync(token)
            : await WorkerClientService.RunAsync<SelfDiagnosticReport>("selfdiag", null, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        LastReport = report;
        return report;
    }

    internal static async Task<SelfDiagnosticReport> RunLocalAsync(CancellationToken token)
    {
        var items = new List<SelfDiagnosticItem>();
        items.Add(new("Windows", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), Environment.OSVersion.VersionString));
        items.Add(new(".NET", Environment.Version.Major >= 10, Environment.Version.ToString()));
        string winAppRuntime = Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.dll");
        items.Add(new("Windows App Runtime", File.Exists(winAppRuntime), File.Exists(winAppRuntime) ? "Runtime inicializován" : "Microsoft.WindowsAppRuntime.dll chybí"));

        var scheduleTask = CheckServiceAsync("Schedule", "Task Scheduler", token);
        var wmiTask = CheckServiceAsync("Winmgmt", "WMI", token);
        var rpcTask = CheckServiceAsync("RpcSs", "RPC", token);
        await Task.WhenAll(scheduleTask, wmiTask, rpcTask).ConfigureAwait(false);
        items.Add(await scheduleTask.ConfigureAwait(false));
        items.Add(await wmiTask.ConfigureAwait(false));
        items.Add(await rpcTask.ConfigureAwait(false));
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        items.Add(new("PowerShell", File.Exists(ps), File.Exists(ps) ? ps : "Nenalezen"));
        bool nvidia = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll"));
        items.Add(new("NVAPI", nvidia, nvidia ? "nvapi64.dll dostupná" : "NVIDIA/NVAPI není dostupná"));
        return new SelfDiagnosticReport(DateTime.UtcNow, items);
    }

    private static Task<SelfDiagnosticItem> CheckServiceAsync(string name, string label, CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var service = new ServiceController(name);
                var status = service.Status;
                bool ok = status is ServiceControllerStatus.Running or ServiceControllerStatus.Paused;
                return new SelfDiagnosticItem(label, ok, status.ToString());
            }
            catch (Exception ex) { return new SelfDiagnosticItem(label, false, ex.Message); }
        }, token);
    }
}
