using System;
using System.Collections.Generic;
using System.Management;

namespace SpankyhoOptimizer.Services;

public sealed record WindowsCapabilitySnapshot(int Build, string ReleaseBand,
    bool StandardCimv2, bool PsMMAgent, bool TaskSchedulerCom, bool ModernPowerApi,
    bool FutureBuild, IReadOnlyList<string> Missing);

public static class WindowsCapabilityService
{
    private static readonly Lazy<WindowsCapabilitySnapshot> Cache = new(Probe);
    public static WindowsCapabilitySnapshot Current => Cache.Value;

    public static bool Supports(string capability) => capability.ToLowerInvariant() switch
    {
        "standardcimv2" => Current.StandardCimv2,
        "ps_mmagent" => Current.PsMMAgent,
        "taskscheduler" => Current.TaskSchedulerCom,
        "power" => Current.ModernPowerApi,
        _ => false
    };

    private static WindowsCapabilitySnapshot Probe()
    {
        int build = Environment.OSVersion.Version.Build;
        var missing = new List<string>();
        bool standard = NamespaceExists(@"root\StandardCimv2");
        bool mma = NamespaceExists(@"root\Microsoft\Windows\PS_MMAgent");
        bool task = Type.GetTypeFromProgID("Schedule.Service") != null;
        bool power = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        if (!standard) missing.Add("root\\StandardCimv2");
        if (!mma) missing.Add("PS_MMAgent");
        if (!task) missing.Add("Task Scheduler COM");
        if (!power) missing.Add("Modern power API");
        string band = build >= 26300 ? "26H2+" : build >= 26100 ? "24H2/25H2" : "Legacy Windows 11";
        return new WindowsCapabilitySnapshot(build, band, standard, mma, task, power, build >= 26300, missing);
    }

    private static bool NamespaceExists(string path)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\" + path);
            scope.Options.Timeout = TimeSpan.FromSeconds(2);
            scope.Connect();
            return scope.IsConnected;
        }
        catch { return false; }
    }
}
