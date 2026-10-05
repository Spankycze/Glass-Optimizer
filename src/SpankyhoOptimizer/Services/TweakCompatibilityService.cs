using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static class TweakCompatibilityService
{
    public static async Task<(bool Supported, string Reason)> CheckAsync(TweakDefinition tweak, CancellationToken token = default)
    {
        var staticCheck = CheckStatic(tweak, WindowsCapabilityService.Current);
        if (!staticCheck.Supported) return staticCheck;
        if (IsAnyHardware(tweak)) return (true, string.Empty);
        var hw = await HardwareSnapshotService.GetAsync(false, token);
        return CheckHardwareScope(tweak, hw);
    }

    internal static (bool Supported, string Reason) CheckWithSnapshot(TweakDefinition tweak, HardwareSnapshot? hw)
    {
        var staticCheck = CheckStatic(tweak, WindowsCapabilityService.Current);
        if (!staticCheck.Supported) return staticCheck;
        if (IsAnyHardware(tweak)) return (true, string.Empty);
        if (hw == null) return (false, "Hardware snapshot není dostupný.");
        return CheckHardwareScope(tweak, hw);
    }

    private static bool IsAnyHardware(TweakDefinition tweak) =>
        string.Equals(tweak.HardwareScope, "Any", StringComparison.OrdinalIgnoreCase);
    private static (bool Supported, string Reason) CheckHardwareScope(TweakDefinition tweak, HardwareSnapshot hw)
    {
        string scope = tweak.HardwareScope.Trim().ToLowerInvariant();
        string vendor = hw.Gpu?.Vendor ?? string.Empty;
        bool hasGpu = hw.Gpu != null;
        bool isNvidia = vendor.Contains("nvidia", StringComparison.OrdinalIgnoreCase);
        bool isAmd = vendor.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
                     vendor.Contains("advanced micro devices", StringComparison.OrdinalIgnoreCase);
        bool isIntel = vendor.Contains("intel", StringComparison.OrdinalIgnoreCase);

        bool ok = scope switch
        {
            "gpu" or "gpu:any" => hasGpu,
            "nvidia" or "gpu:nvidia" => isNvidia,
            "amd" or "gpu:amd" => isAmd,
            "intel" or "gpu:intel" => isIntel,
            "bluetooth" => hw.HasClass("Bluetooth") || hw.HasName("Bluetooth"),
            "optical" => hw.HasClass("CDROM") || hw.HasName("DVD") || hw.HasName("CD-ROM"),
            "serial" => hw.HasClass("Ports") || hw.HasName("COM"),
            "floppy" => hw.HasName("Floppy"),
            "network" => hw.HasClass("Net"),
            _ => true
        };

        if (ok) return (true, string.Empty);

        string reason = scope.StartsWith("gpu:", StringComparison.Ordinal) || scope is "nvidia" or "amd" or "intel" or "gpu"
            ? $"GPU compatibility: požadováno '{tweak.HardwareScope}', detekováno '{(string.IsNullOrWhiteSpace(vendor) ? "žádné GPU" : vendor)}'."
            : $"Hardware scope '{tweak.HardwareScope}' nebyl v systému nalezen.";
        return (false, reason);
    }

    internal static (bool Supported, string Reason) CheckStatic(TweakDefinition tweak, WindowsCapabilitySnapshot capabilities)
    {
        if (capabilities.Build < tweak.MinWindowsBuild)
            return (false, $"Vyžaduje Windows build {tweak.MinWindowsBuild} nebo novější.");
        if (tweak.Id.Equals("mem.compression", StringComparison.OrdinalIgnoreCase) && !capabilities.PsMMAgent)
            return (false, "PS_MMAgent provider není na tomto Windows buildu dostupný.");
        return (true, string.Empty);
    }
}
