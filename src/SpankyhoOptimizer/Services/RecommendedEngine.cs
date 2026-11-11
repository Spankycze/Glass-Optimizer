using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record RecommendationDecision(
    string TweakId, bool Enable, bool Eligible, string Reason, string Tier);

public sealed record RecommendationPlan(
    DateTime CreatedUtc, string HardwareSummary,
    IReadOnlyList<RecommendationDecision> Decisions)
{
    public int EligibleCount => Decisions.Count(x => x.Eligible);
}

public static class RecommendedEngine
{
    private static readonly (string Key, string Label, string[] Needles)[] UsedFeatureRules =
    {
        ("printer", "tisk", new[] { "spooler", "print" }),
        ("bluetooth", "Bluetooth", new[] { "bth", "bluetooth", "rfcomm" }),
        ("xbox", "Xbox / Game Pass", new[] { "xbox", "xbl" }),
        ("bio", "Windows Hello", new[] { "bio", "wbio" }),
        ("phone", "Phone Link", new[] { "phone" }),
        ("camera", "kamera/skener", new[] { "camera", "frame", "stisvc" }),
        ("rdp", "Remote Desktop", new[] { "rdp", "termservice", "umrdp" }),
        ("vpn", "VPN", new[] { "ras", "vpn", "wan" }),
        ("touch", "dotyk/pero", new[] { "tablet", "touch" }),
        ("updates", "aktualizace", new[] { "update", "wuauserv", "bits" }),
        ("store", "Microsoft Store", new[] { "store", "appx", "clipsvc" }),
        ("notifications", "oznámení", new[] { "wpn", "notification" }),
        ("hyperv", "Hyper-V/WSL2", new[] { "hyperv", "hypervisor", "vmp", "whp", "vbs", "hvci" })
    };

    private static readonly string[] PrinterNeedles = { "spooler", "print" };
    private static readonly string[] BluetoothNeedles = { "bth", "bluetooth", "rfcomm" };
    private static readonly string[] CameraNeedles = { "camera", "frame", "stisvc", "scanner" };
    private static readonly string[] TouchNeedles = { "tablet", "touch", "pen", "hid" };
    private static readonly string[] BatteryNeedles = { "battery", "power", "sensor" };

    private readonly record struct HardwareFlags(
        bool HasGpu,
        bool HasNetwork,
        bool HasStorage,
        bool IsLaptop,
        bool HasPrinter,
        bool HasBluetooth,
        bool HasCamera,
        bool HasTouch);

    internal static bool MustBlockRecommendations(SafePresetProfile preset) =>
        preset.IsUntrusted || preset.ExcludedTweakIds == null ||
        preset.ExcludedTweakIds.Any(string.IsNullOrWhiteSpace);

    internal static bool MustBlockFeatureProfile(UserFeatureProfile profile) =>
        profile.IsUntrusted || profile.KeepFeatures == null ||
        profile.KeepFeatures.Any(string.IsNullOrWhiteSpace);

    internal static RecommendationPlan BuildBlockedFeatureProfilePlan(IEnumerable<TweakDefinition> tweaks)
    {
        const string reason = "Uložené používané funkce nejsou bezpečně čitelné. " +
                              "Recommended nemůže určit, co má chránit; změny jsou zablokované.";
        return new RecommendationPlan(DateTime.UtcNow,
            "Recommended blocked — used feature preferences unavailable",
            tweaks.Select(t => new RecommendationDecision(t.Id, t.Current, false, reason, "Blocked")).ToArray());
    }

    internal static RecommendationPlan BuildBlockedPresetPlan(IEnumerable<TweakDefinition> tweaks)
    {
        // Never infer permission to alter a tweak from missing/garbled user
        // exclusions. Preserve every current state without a hardware probe.
        const string reason = "Nelze bezpečně načíst uložené výjimky Recommended. " +
                              "Žádná změna nebude navržena, dokud se profil neopraví.";
        return new RecommendationPlan(DateTime.UtcNow,
            "Recommended blocked — saved exclusions unavailable",
            tweaks.Select(t => new RecommendationDecision(t.Id, t.Current, false, reason, "Blocked")).ToArray());
    }

    public static async Task<RecommendationPlan> BuildAsync(
        IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        var preset = SafePresetProfileService.Load();
        if (MustBlockRecommendations(preset))
            return BuildBlockedPresetPlan(tweaks);
        var features = UserFeatureProfileService.Load();
        if (MustBlockFeatureProfile(features))
            return BuildBlockedFeatureProfilePlan(tweaks);
        var hw = await HardwareSnapshotService.GetAsync(false, token);
        var keepFeatures = features.KeepFeatures.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedTweaks = preset.ExcludedTweakIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flags = new HardwareFlags(
            HasGpu: hw.Gpu != null && !string.IsNullOrWhiteSpace(hw.Overview.Gpu),
            HasNetwork: hw.HasClass("Net"),
            HasStorage: hw.HasClass("DiskDrive") || hw.HasClass("Disk") || hw.HasName("NVMe") || hw.HasName("SSD"),
            IsLaptop: hw.HasClass("Battery") || hw.HasName("Battery"),
            HasPrinter: hw.HasClass("Printer") || hw.HasClass("PrintQueue") || hw.HasName("Printer"),
            HasBluetooth: hw.HasClass("Bluetooth") || hw.HasName("Bluetooth"),
            HasCamera: hw.HasClass("Camera") || hw.HasClass("Image") || hw.HasName("Camera") || hw.HasName("Webcam") || hw.HasName("Scanner"),
            HasTouch: hw.HasName("Touch") || hw.HasName("Pen") || hw.HasName("Digitizer"));

        var list = new List<RecommendationDecision>();
        foreach (var t in tweaks)
        {
            token.ThrowIfCancellationRequested();
            list.Add(Evaluate(t, hw, flags, keepFeatures, excludedTweaks));
        }
        string summary = $"{hw.Overview.Cpu} • {hw.Overview.Gpu} • build {hw.WindowsBuild}";
        return new(DateTime.UtcNow, summary, list);
    }
    private static RecommendationDecision Evaluate(
        TweakDefinition t, HardwareSnapshot hw, HardwareFlags flags,
        HashSet<string> keepFeatures, HashSet<string> excludedTweaks)
    {
        if (!t.IsLoaded)
            return new(t.Id, t.Current, false,
                "Tweak není na tomto systému načtený nebo podporovaný.", "Blocked");
        var version = TweakVersionMatrixService.Check(t);
        if (!version.Supported)
            return new(t.Id, t.Current, false, version.Message, "Blocked");
        if (!t.Recommended || t.Risk == TweakRisk.Advanced)
            return new(t.Id, t.Current, false,
                "Není součástí bezpečného Recommended profilu.", "Manual");
        if (excludedTweaks.Contains(t.Id))
            return new(t.Id, t.Current, false,
                "Vyloučeno uživatelem v bezpečném preset editoru.", "Custom");

        string id = t.Id.ToLowerInvariant();
        if ((id == "gpu.hags" || id.EndsWith(".hwaccel", StringComparison.Ordinal)) && !flags.HasGpu)
            return new(t.Id, t.Current, false, "Nebyla nalezena podporovaná GPU.", "Protected");
        if (id == "gpu.hags" && hw.WindowsBuild < 19041)
            return new(t.Id, t.Current, false, "HAGS vyžaduje modernější Windows build.", "Blocked");
        if (id == "storage.trim" && !flags.HasStorage)
            return new(t.Id, t.Current, false, "Nebyl nalezen SSD/NVMe/diskový device pro bezpečný TRIM profil.", "Protected");
        if (t.Section == "network" && !flags.HasNetwork)
            return new(t.Id, t.Current, false, "Nebyl nalezen aktivní síťový adaptér.", "Protected");
        if (flags.IsLaptop && id is "sys.powerthrottle" or "sys.hibernation" or "sys.faststartup")
            return new(t.Id, t.Current, false, "Na notebooku se změna napájení neaplikuje automaticky.", "Protected");
        if (ProtectsUsedFeature(id, t.Group, keepFeatures, out string why))
            return new(t.Id, t.Current, false, why, "Protected");
        if (ProtectsDetectedFeature(id, t.Group, flags, out string detectedWhy))
            return new(t.Id, t.Current, false, detectedWhy, "AutoProtected");
        if (id == "sys.powerthrottle" && flags.IsLaptop)
            return new(t.Id, t.Current, false,
                "Na notebooku může zvýšit spotřebu a teploty.", "Protected");
        if ((id.Contains("bluetooth") || id.Contains("bth")) && flags.HasBluetooth)
            return new(t.Id, t.Current, false,
                "Bluetooth hardware je přítomný.", "Protected");
        if (id == "mem.compression")
            return new(t.Id, true, true, $"Memory Compression je doporučená pro detekovanou RAM: {hw.Overview.Ram}.", version.Tested ? "Safe" : "Revalidate");
        if (id == "gpu.hags")
            return new(t.Id, true, true, $"HAGS je dostupný pro {hw.Overview.Gpu} na buildu {hw.WindowsBuild}.", version.Tested ? "Safe" : "Revalidate");
        if (id == "storage.trim")
            return new(t.Id, true, true, "TRIM je doporučený pro detekované moderní úložiště; read-back se ověří po změně.", version.Tested ? "Safe" : "Revalidate");
        if (t.Section == "network")
            return new(t.Id, true, true, "Aktivní síťový adaptér byl detekován; použije se pouze podporovaný read-back stav.", version.Tested ? "Safe" : "Revalidate");
        return new(t.Id, true, true,
            version.Tested
                ? flags.IsLaptop ? "Bezpečný Recommended kandidát; notebookové napájení je chráněné." : "Bezpečný Recommended kandidát pro tento hardware."
                : version.Message,
            version.Tested ? "Safe" : "Revalidate");
    }

    private static bool ProtectsDetectedFeature(string id, string group, HardwareFlags flags, out string reason)
    {
        static bool Any(string idValue, string groupValue, string[] needles)
        {
            foreach (string needle in needles)
                if (idValue.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    groupValue.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        if (flags.HasPrinter &&
            Any(id, group, PrinterNeedles))
        {
            reason = "Auto-protected: detected printer.";
            return true;
        }
        if (flags.HasBluetooth &&
            Any(id, group, BluetoothNeedles))
        {
            reason = "Auto-protected: detected Bluetooth hardware.";
            return true;
        }
        if (flags.HasCamera &&
            Any(id, group, CameraNeedles))
        {
            reason = "Auto-protected: detected camera/scanner.";
            return true;
        }
        if (flags.HasTouch &&
            Any(id, group, TouchNeedles))
        {
            reason = "Auto-protected: detected touch/pen device.";
            return true;
        }
        if (flags.IsLaptop &&
            Any(id, group, BatteryNeedles))
        {
            reason = "Auto-protected: detected battery/mobile platform.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool ProtectsUsedFeature(
        string id, string group, HashSet<string> keepFeatures, out string reason)
    {
        static bool Any(string value, params string[] needles) =>
            needles.Any(value.Contains);

        foreach (var rule in UsedFeatureRules)
        {
            if (keepFeatures.Contains(rule.Key) &&
                (Any(id, rule.Needles) || group.Contains(rule.Key, StringComparison.OrdinalIgnoreCase)))
            {
                reason = $"Chrání používanou funkci: {rule.Label}.";
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }
}
