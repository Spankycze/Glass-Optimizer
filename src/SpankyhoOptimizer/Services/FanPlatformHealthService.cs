using System;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

public sealed record FanPlatformHealth(
    bool PawnIoInstalled,
    bool PawnIoRunning,
    bool LConnectRunning,
    string LConnectDetails);

public static class FanPlatformHealthService
{
    public static FanPlatformHealth Read()
    {
        bool pawnInstalled = false;
        bool pawnRunning = false;
        try
        {
            using var pawn = new ServiceController("PawnIO");
            _ = pawn.Status;
            pawnInstalled = true;
            pawnRunning = pawn.Status == ServiceControllerStatus.Running;
        }
        catch { }

        var matches = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ProcessSnapshotCleanup.Inspect(Process.GetProcesses, processes =>
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (name.Contains("L-Connect", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("LConnect", StringComparison.OrdinalIgnoreCase))
                            matches.Add(name);
                    }
                    catch { }
                }
                return true;
            });
        }
        catch { }
        string[] names = matches.ToArray();

        bool serviceRunning = IsServiceRunning("LConnectService") || IsServiceRunning("LConnectServiceWatcher");
        bool lconnect = names.Length > 0 || serviceRunning;
        string details = names.Length > 0 ? string.Join(", ", names) : serviceRunning ? "L-Connect service" : string.Empty;

        return new FanPlatformHealth(pawnInstalled, pawnRunning, lconnect, details);
    }

    public static string StopLConnectBestEffort()
    {
        int killed = 0;
        ProcessSnapshotCleanup.Inspect(Process.GetProcesses, processes =>
        {
            foreach (Process process in processes)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!name.Contains("L-Connect", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("LConnect", StringComparison.OrdinalIgnoreCase))
                        continue;
                    process.Kill(true);
                    killed++;
                }
                catch { }
            }
            return true;
        });

        int stopped = 0;
        foreach (string serviceName in new[] { "LConnectServiceWatcher", "LConnectService" })
        {
            try
            {
                using var service = new ServiceController(serviceName);
                if (service.Status == ServiceControllerStatus.Stopped) continue;
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(3));
                stopped++;
            }
            catch { }
        }

        return $"L-Connect: ukončené procesy {killed}, zastavené služby {stopped}";
    }

    private static bool IsServiceRunning(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status == ServiceControllerStatus.Running ||
                   service.Status == ServiceControllerStatus.StartPending;
        }
        catch { return false; }
    }
}
