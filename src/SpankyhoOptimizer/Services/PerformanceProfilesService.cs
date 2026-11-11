using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static class PerformanceProfilesService
{
    private static readonly char[] LineSeparators = { '\r', '\n' };
    private static readonly object NicCacheLock = new();
    private static string? _nicSummaryCache;
    private static DateTime _nicSummaryAt;
    private static IReadOnlyDictionary<string, string>? _nicCapabilityCache;
    private static DateTime _nicCapabilityAt;
    private static Task<IReadOnlyDictionary<string, string>>? _nicCapabilityInFlight;
    private static int _nicCapabilityGeneration;
    private static bool _nicCapabilityPersistentChecked;
    private const string NicCapabilityPersistentKey = "nic-capabilities-v1";
    private static readonly TimeSpan NicCapabilityPersistentTtl = TimeSpan.FromHours(6);
    private const string CpuSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string BoostMode = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string Epp = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string Cooling = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";
    private const string PerfAutonomous = "8baa4a8a-14c6-4451-8e8b-14bdbd197537";
    private const string PerfIncPolicy = "465e1f50-b610-473a-ab58-00d1077dc418";
    private const string PerfDecPolicy = "40fbefc7-2e9d-4d25-a185-0cfd8574bac6";
    private const string PerfIncThreshold = "06cadf0e-64ed-448a-8927-ce7bf90eb35d";
    private const string PerfDecThreshold = "12a0ab44-fe28-4fa9-b3bd-4b64f44960a6";
    private const string PerfCheck = "4d2b0152-7d5c-498b-88e2-34345392a2c5";
    private const string CoreParkMin = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string CoreParkMax = "ea062031-0e34-4ff1-9b6d-eb1059334028";
    private const string CoreParkIncPolicy = "c7be0679-2817-4d69-9d02-519a537ed0c6";
    private const string CoreParkDecPolicy = "71021b41-c749-4d21-be74-a00f335d582b";
    private const string CoreParkIncTime = "2ddd5a84-5a71-437e-912a-db0b8c788732";
    private const string CoreParkDecTime = "dfd10d17-d5eb-45dd-877a-9a34ddd15c82";
    private const string LatencyHintUnpark = "616cdaa5-695e-4545-97ad-97dc2d1bdd88";
    private const string LatencyHintEpp = "4b70f900-cdd9-4e66-aa26-ae8417f98173";
    private const string LatencyHintPerf = "619b7505-003b-4e82-b7a6-4dd29c300971";
    private const string PerfDutyCycling = "4e4450b3-6179-4e91-b8f1-5bb9938f81a1";
    private const string IdleDisable = "5d76a2ca-e8c0-402f-a133-2158492d58ad";
    private static readonly Lazy<string> CpuNameCache = new(() =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString")?.ToString() ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Neznámý CPU").Trim();
    });
    private static readonly Lazy<string> CpuVendorCache = new(() =>
    {
        string cpu = CpuNameCache.Value;
        if (cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) || cpu.Contains("Core", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "Generic";
    });

    public sealed record AdaptivePowerProfile(
        string CpuName, string Vendor, string Architecture, bool Mobile, int LogicalProcessors,
        int Epp, int Boost, int CoreMin, int IncThreshold, int DecThreshold,
        int IncPolicy, int DecPolicy, int PerfCheckMs);
    public sealed record PowerProfileProgress(int Current, int Total, int Percent, string Stage, string Detail);

    public static AdaptivePowerProfile GetAdaptivePowerProfile()
    {
        string cpu = GetCpuName();
        string vendor = GetCpuVendor();
        bool mobile = IsLaptop();
        int logical = Math.Max(1, Environment.ProcessorCount);
        string arch = DetectCpuArchitecture(cpu, vendor);

        int epp;
        int coreMin;
        int incThreshold;
        int decThreshold;
        int perfCheck;

        if (mobile)
        {
            epp = 28; coreMin = 10; incThreshold = 25; decThreshold = 15; perfCheck = 20;
        }
        else if (arch is "AMD Ryzen 7000/9000")
        {
            epp = 12; coreMin = 20; incThreshold = 10; decThreshold = 8; perfCheck = 15;
        }
        else if (arch is "AMD Ryzen 3000/5000")
        {
            // Slightly less performance-biased at idle while keeping 100% max state
            // and Efficient Aggressive boost under real load.
            epp = 20; coreMin = 25; incThreshold = 10; decThreshold = 8; perfCheck = 15;
        }
        else if (arch == "Intel Hybrid")
        {
            epp = 12; coreMin = 20; incThreshold = 10; decThreshold = 8; perfCheck = 15;
        }
        else if (vendor == "AMD")
        {
            epp = 18; coreMin = 25; incThreshold = 12; decThreshold = 8; perfCheck = 15;
        }
        else if (vendor == "Intel")
        {
            epp = 15; coreMin = 25; incThreshold = 12; decThreshold = 8; perfCheck = 15;
        }
        else
        {
            epp = 20; coreMin = 25; incThreshold = 15; decThreshold = 10; perfCheck = 15;
        }

        return new(cpu, vendor, arch, mobile, logical, epp, 4, coreMin, incThreshold, decThreshold, 2, 0, perfCheck);
    }

    private static string DetectCpuArchitecture(string cpu, string vendor)
    {
        if (vendor == "AMD")
        {
            if (Regex.IsMatch(cpu, @"Ryzen\s+(7|9|5|3)\s+[789]\d{3}", RegexOptions.IgnoreCase) ||
                cpu.Contains("Ryzen 9 9", StringComparison.OrdinalIgnoreCase))
                return "AMD Ryzen 7000/9000";
            if (Regex.IsMatch(cpu, @"Ryzen\s+(7|9|5|3)\s+[35]\d{3}", RegexOptions.IgnoreCase))
                return "AMD Ryzen 3000/5000";
            return "AMD";
        }

        if (vendor == "Intel")
        {
            if (cpu.Contains("Core Ultra", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(cpu, @"i[3579]-1[2-9]\d{3}", RegexOptions.IgnoreCase))
                return "Intel Hybrid";
            return "Intel";
        }
        return "Generic";
    }

    public static string GetCpuName() => CpuNameCache.Value;
    public static string GetCpuVendor() => CpuVendorCache.Value;
    public static bool IsLaptop() => NativeCore.HasSystemBattery();

    public static Task<string> ApplyPerformancePowerProfileAsync(CancellationToken token = default)
        => ApplyPerformancePowerProfileAsync(null, token);

    public static async Task<string> ApplyPerformancePowerProfileAsync(IProgress<PowerProfileProgress>? progress, CancellationToken token = default)
    {
        return await OperationCoordinator.RunExclusiveAsync("performance-power", async ct =>
        {
            var adaptive = GetAdaptivePowerProfile();
            const int totalSteps = 40;
            int progressStep = 0;
            void Report(string stage, string detail, int? percent = null)
            {
                int pct = percent ?? Math.Clamp((int)Math.Round(progressStep * 100d / totalSteps), 0, 100);
                progress?.Report(new PowerProfileProgress(progressStep, totalSteps, pct, stage, detail));
            }
            Report("Příprava", "Načítám aktivní power plán…", 1);
            string vendor = adaptive.Vendor;
            string cpu = adaptive.CpuName;
            bool mobile = adaptive.Mobile;
            int epp = adaptive.Epp;
            int boost = adaptive.Boost;
            string? originalGuid = null;
            string? newGuid = null;
            bool createdNew = false;
            bool guardCommitted = false;
            var duplicateGuids = new List<string>();
            try
            {
                var active = await ProcessRunner.RunAsync(
                    "powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), ct)
                    .ConfigureAwait(false);
                // Read-only preflight MUST establish the real active scheme
                // before creating or mutating a performance power plan.
                if (!active.Success)
                    throw new InvalidOperationException(
                        "Nelze zjistit původní aktivní plán; aplikace profilu byla zablokována.");
                var planList = await ProcessRunner.RunAsync(
                    "powercfg.exe", "/list", TimeSpan.FromSeconds(15), ct)
                    .ConfigureAwait(false);
                var preflight = BuildPerformanceApplyPreflight(
                    active.Success, active.StdOut + active.StdErr,
                    planList.Success, planList.StdOut + "\n" + planList.StdErr);
                originalGuid = preflight.ActiveGuid;
                duplicateGuids.AddRange(preflight.ReusableGuids);
                newGuid = duplicateGuids.FirstOrDefault(g => string.Equals(g, originalGuid, StringComparison.OrdinalIgnoreCase)) ?? duplicateGuids.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(newGuid))
                {
                    var duplicate = await PrivilegedProcessRunner.RunAsync("powercfg.exe", "/duplicatescheme SCHEME_BALANCED", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                    newGuid = Regex.Match(duplicate.StdOut + duplicate.StdErr, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value;
                    if (!duplicate.Success ||
                        !Guid.TryParse(newGuid, out Guid createdGuid) ||
                        createdGuid == Guid.Empty)
                        throw new InvalidOperationException(
                            "powercfg nepotvrdil vytvoření platného výkonového plánu.");
                    createdNew = true;
                    await RequirePowerCfgAsync($"/changename {newGuid} \"Spankyho Performance\"", ct);
                }
                var settings = new (string Args, string Label, string Target)[]
                {
                    ($"/setacvalueindex {newGuid} SUB_PROCESSOR PROCTHROTTLEMIN 5", "CPU minimum", "5 %"),
                    ($"/setacvalueindex {newGuid} SUB_PROCESSOR PROCTHROTTLEMAX 100", "CPU maximum", "100 %"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {BoostMode} {boost}", "Boost mode", "Efficient Aggressive"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {Epp} {epp}", "Energy Performance Preference", $"EPP {epp}"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {Cooling} 1", "System cooling policy", "Active"),
                    ($"/setacvalueindex {newGuid} SUB_PCIEXPRESS ASPM 0", "PCI Express ASPM", "Off"),
                    ($"/setacvalueindex {newGuid} SUB_DISK DISKIDLE 0", "Disk idle timeout", "Never"),
                    ($"/setacvalueindex {newGuid} SUB_VIDEO VIDEOIDLE 0", "Display idle timeout", "Never"),
                    ($"/setacvalueindex {newGuid} SUB_SLEEP STANDBYIDLE 0", "Sleep timeout", "Never"),
                    ($"/setacvalueindex {newGuid} SUB_SLEEP HIBERNATEIDLE 0", "Hibernate timeout", "Never"),
                    ($"/setacvalueindex {newGuid} 2a737441-1930-4402-8d77-b2bebba308a3 0853a681-27c8-4100-a2fd-82013e970683 {(mobile ? 1 : 0)}", "USB selective suspend", mobile ? "Windows default" : "Off"),
                    ($"/setacvalueindex {newGuid} 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0", "Wi-Fi power saving", "Maximum Performance"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {PerfAutonomous} 1", "CPPC autonomous mode", "Enabled"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {PerfIncPolicy} {adaptive.IncPolicy}", "Performance increase policy", "Rocket"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {PerfDecPolicy} {adaptive.DecPolicy}", "Performance decrease policy", "Ideal"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {PerfIncThreshold} {adaptive.IncThreshold}", "Increase threshold", $"{adaptive.IncThreshold} %"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {PerfDecThreshold} {adaptive.DecThreshold}", "Decrease threshold", $"{adaptive.DecThreshold} %"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {PerfCheck} {adaptive.PerfCheckMs}", "Performance check interval", $"{adaptive.PerfCheckMs} ms"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {CoreParkMin} {adaptive.CoreMin}", "Core Parking minimum", $"{adaptive.CoreMin} %"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {CoreParkMax} 100", "Core Parking maximum", "100 %"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {CoreParkIncPolicy} 2", "Core Parking increase policy", "Fast"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {CoreParkDecPolicy} 0", "Core Parking decrease policy", "Ideal"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {CoreParkIncTime} 1", "Core Parking increase time", "1 interval"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {CoreParkDecTime} 10", "Core Parking decrease time", "10 intervals"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {PerfDutyCycling} 0", "Processor duty cycling", "Disabled"),
                    ($"/setacvalueindex {newGuid} {CpuSubgroup} {IdleDisable} 0", "CPU idle states", "Enabled"),
                    ($"/setacvalueindex {newGuid} SUB_SLEEP RTCWAKE 2", "Wake timers", "Important only"),
                    ($"/setacvalueindex {newGuid} SUB_SLEEP HYBRIDSLEEP 0", "Hybrid Sleep", "Off"),
                    ($"/setacvalueindex {newGuid} SUB_SLEEP AWAYMODE 0", "Away Mode", "Off"),
                    ($"/setacvalueindex {newGuid} 9596fb26-9850-41fd-ac3e-f7c3c00afd4b 10778347-1370-4ee0-8bbd-33bdacaade49 1", "Video playback bias", "Performance"),
                    ($"/setacvalueindex {newGuid} 9596fb26-9850-41fd-ac3e-f7c3c00afd4b 34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4 0", "Video quality bias", "Optimize Quality")
                };
                var powerAliases = await ReadPowerAliasesAsync(ct).ConfigureAwait(false);
                int changedSettings = 0;
                int unchangedSettings = 0;
                int unsupportedSettings = 0;
                for (int i = 0; i < settings.Length; i++)
                {
                    var item = settings[i];
                    progressStep = 3 + i;
                    int pct = 6 + (int)Math.Round((i + 1) * 68d / settings.Length);

                    if (TryParseSetAcValueArgs(item.Args, out string subgroup, out string setting, out int desired))
                    {
                        if (!IsPowerSettingSupported(subgroup, setting, powerAliases))
                        {
                            unsupportedSettings++;
                            Report("Přeskočeno", $"{item.Label} není na tomto systému podporováno", pct);
                            continue;
                        }

                        if (TryReadPlanAcValueFromRegistry(newGuid, subgroup, setting, powerAliases, out int current) &&
                            current == desired)
                        {
                            unchangedSettings++;
                            Report("Kontrola", $"{item.Label} už odpovídá", pct);
                            continue;
                        }
                    }

                    Report("Aplikuji", $"{item.Label} → {item.Target}", pct);
                    await RequirePowerCfgAsync(item.Args, ct);
                    changedSettings++;
                }
                LogService.Info("performance", $"POWER_SELECTIVE_APPLY changed={changedSettings} unchanged={unchangedSettings} unsupported={unsupportedSettings} total={settings.Length}");
                progressStep = 37; Report("Aktivace", "Aktivuji Spankyho Performance…", 76);
                await RequirePowerCfgAsync($"/setactive {newGuid}", ct);
                await Task.Delay(450, ct);
                var verifyActive = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                EnsureAppliedPowerSchemeReadback(
                    newGuid, verifyActive.Success, verifyActive.StdOut + verifyActive.StdErr);
                progressStep = 38; Report("Ověření", "Aktivní plán potvrzen. Paralelně ověřuji CPU, sleep a latency hodnoty…", 82);
                var verifyItems = new (string Subgroup, string Setting, int Expected)[]
                {
                    ("SUB_PROCESSOR", "PROCTHROTTLEMIN", 5),
                    ("SUB_PROCESSOR", "PROCTHROTTLEMAX", 100),
                    (CpuSubgroup, BoostMode, boost),
                    (CpuSubgroup, Epp, epp),
                    (CpuSubgroup, Cooling, 1),
                    ("SUB_PCIEXPRESS", "ASPM", 0),
                    ("SUB_DISK", "DISKIDLE", 0),
                    ("SUB_VIDEO", "VIDEOIDLE", 0),
                    ("SUB_SLEEP", "STANDBYIDLE", 0),
                    ("SUB_SLEEP", "HIBERNATEIDLE", 0),
                    ("2a737441-1930-4402-8d77-b2bebba308a3", "0853a681-27c8-4100-a2fd-82013e970683", mobile ? 1 : 0),
                    ("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0),
                    (CpuSubgroup, PerfAutonomous, 1),
                    (CpuSubgroup, PerfIncPolicy, adaptive.IncPolicy),
                    (CpuSubgroup, PerfDecPolicy, adaptive.DecPolicy),
                    (CpuSubgroup, PerfIncThreshold, adaptive.IncThreshold),
                    (CpuSubgroup, PerfDecThreshold, adaptive.DecThreshold),
                    (CpuSubgroup, PerfCheck, adaptive.PerfCheckMs),
                    (CpuSubgroup, CoreParkMin, adaptive.CoreMin),
                    (CpuSubgroup, CoreParkMax, 100),
                    (CpuSubgroup, CoreParkIncPolicy, 2),
                    (CpuSubgroup, CoreParkDecPolicy, 0),
                    (CpuSubgroup, CoreParkIncTime, 1),
                    (CpuSubgroup, CoreParkDecTime, 10),
                    (CpuSubgroup, PerfDutyCycling, 0),
                    (CpuSubgroup, IdleDisable, 0),
                    ("SUB_SLEEP", "RTCWAKE", 2),
                    ("SUB_SLEEP", "HYBRIDSLEEP", 0),
                    ("SUB_SLEEP", "AWAYMODE", 0),
                    ("9596fb26-9850-41fd-ac3e-f7c3c00afd4b", "10778347-1370-4ee0-8bbd-33bdacaade49", 1),
                    ("9596fb26-9850-41fd-ac3e-f7c3c00afd4b", "34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4", 0)
                };
                var verifyPerf = System.Diagnostics.Stopwatch.StartNew();
                await VerifyPowerValuesBatchAsync(newGuid, verifyItems, ct);
                LogService.Info("performance", $"POWER_READBACK_BATCH count={verifyItems.Length} ms={verifyPerf.ElapsedMilliseconds}");
                progressStep = 39; Report("Ověření", "Read-back hlavních Adaptive CPU V3 hodnot je v pořádku.", 96);
                progressStep = 39;
                Report("Persistence", "Ověřuji Power Plan guard před úklidem duplicit…", 98);
                await EnsureGuardThenCleanupPerformanceDuplicatesAsync(
                    newGuid, duplicateGuids,
                    (guid, t) => PowerPlanGuardService.EnsureAsync(guid, t),
                    async t =>
                    {
                        var current = await ProcessRunner.RunAsync(
                            "powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), t)
                            .ConfigureAwait(false);
                        return (current.Success, current.StdOut + current.StdErr);
                    },
                    async (guid, t) =>
                    {
                        var del = await PrivilegedProcessRunner.RunAsync(
                            "powercfg.exe", $"/delete {guid}", TimeSpan.FromSeconds(12), t)
                            .ConfigureAwait(false);
                        if (del.Success)
                            LogService.Info("power", $"Removed duplicate Spankyho Performance guid={guid}");
                        else
                            LogService.Error("power", $"Duplicate cleanup failed guid={guid}: {del.StdErr}{del.StdOut}");
                        return del.Success;
                    },
                    failure => LogService.Error("power", failure),
                    () => guardCommitted = true, ct).ConfigureAwait(false);
                PowerPlanStateService.InvalidateCache();
                progressStep = 40; Report("Hotovo", "Power plán byl aplikován. Probíhá finální UI read-back…", 100);
                LogService.Info("power", $"Performance profile verified guid={newGuid} reused={!createdNew} cpu={cpu} vendor={vendor} arch={adaptive.Architecture} logical={adaptive.LogicalProcessors} epp={epp}");
                return $"CPU: {cpu}\nPlatforma: {(mobile ? "Notebook" : "Desktop")}\nHW profil: {adaptive.Architecture} • {adaptive.LogicalProcessors} logical CPU\nAdaptive CPU V3: {vendor}\nCPU minimum/max: 5 / 100 %\nEPP: {epp}\nBoost: Efficient Aggressive\nCPPC autonomous: Enabled\nPerformance increase/decrease: Rocket / Ideal\nThresholds: {adaptive.IncThreshold}% / {adaptive.DecThreshold}%\nCore parking min/max: {adaptive.CoreMin}% / 100%\nLatency hint: 100% unpark, EPP 0, perf 100\nDuty cycling: Disabled\nCPU idle states: Enabled\nWake timers: Important only\nHybrid sleep: Off\nAway mode: Off\nVideo playback: Performance / Quality\nDisplay/Disk/Sleep/Hibernate timeout: Never na AC\nUSB suspend: {(mobile ? "Windows/úsporný" : "Off na AC")}\nWi-Fi: Maximum Performance na AC\nPower plan byl aktivován a ověřen read-backem.\nPersistence: Power Plan guard ho 30 s po přihlášení znovu aktivuje.";
            }
            catch
            {
                // Once persistence has been committed, rolling back the
                // active scheme or removing its target would make the
                // configured guard inconsistent with the profile.
                if (guardCommitted)
                {
                    LogService.Error("power",
                        "Performance guard committed; rollback skipped after later failure.");
                    throw;
                }
                if (!string.IsNullOrWhiteSpace(originalGuid))
                {
                    try
                    {
                        var rollback = await PrivilegedProcessRunner.RunAsync("powercfg.exe", $"/setactive {originalGuid}", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                        if (!rollback.Success) LogService.Error("power", "Rollback active scheme failed: " + rollback.StdErr + rollback.StdOut);
                    }
                    catch (Exception ex) { LogService.Error("power", "Rollback active scheme threw", ex); }
                }
                if (createdNew && !string.IsNullOrWhiteSpace(newGuid))
                {
                    try
                    {
                        var cleanup = await PrivilegedProcessRunner.RunAsync("powercfg.exe", $"/delete {newGuid}", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                        if (!cleanup.Success) LogService.Error("power", "Rollback cleanup failed: " + cleanup.StdErr + cleanup.StdOut);
                    }
                    catch (Exception ex) { LogService.Error("power", "Rollback cleanup threw", ex); }
                }
                throw;
            }
        }, TimeSpan.FromSeconds(90), token);
    }

    private static bool TryParseSetAcValueArgs(string args, out string subgroup, out string setting, out int value)
    {
        subgroup = string.Empty;
        setting = string.Empty;
        value = 0;
        var match = Regex.Match(args ?? string.Empty,
            @"^/setacvalueindex\s+\S+\s+(\S+)\s+(\S+)\s+(-?\d+)\s*$",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[3].Value, out value)) return false;
        subgroup = match.Groups[1].Value;
        setting = match.Groups[2].Value;
        return true;
    }

    private static bool IsPowerSettingSupported(
        string subgroup,
        string setting,
        IReadOnlyDictionary<string, string> aliases)
    {
        string subgroupGuid = ResolvePowerToken(subgroup, aliases);
        string settingGuid = ResolvePowerToken(setting, aliases);
        if (!Guid.TryParse(subgroupGuid, out _) || !Guid.TryParse(settingGuid, out _)) return true;
        try
        {
            const string root = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";
            using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{subgroupGuid}\{settingGuid}");
            return key != null;
        }
        catch { return true; }
    }

    private static bool TryReadPlanAcValueFromRegistry(
        string guid,
        string subgroup,
        string setting,
        IReadOnlyDictionary<string, string> aliases,
        out int value)
    {
        value = 0;
        string subgroupGuid = ResolvePowerToken(subgroup, aliases);
        string settingGuid = ResolvePowerToken(setting, aliases);
        if (!Guid.TryParse(subgroupGuid, out _) || !Guid.TryParse(settingGuid, out _)) return false;
        try
        {
            const string root = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
            using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{guid}\{subgroupGuid}\{settingGuid}");
            object? raw = key?.GetValue("ACSettingIndex");
            if (raw == null) return false;
            value = Convert.ToInt32(raw);
            return true;
        }
        catch { return false; }
    }

    private static async Task RequirePowerCfgAsync(string args, CancellationToken token)
    {
        var r = await PrivilegedProcessRunner.RunAsync("powercfg.exe", args, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException((r.StdErr + " " + r.StdOut).Trim());
    }

    private static async Task VerifyPowerValuesBatchAsync(
        string guid,
        IReadOnlyList<(string Subgroup, string Setting, int Expected)> items,
        CancellationToken token)
    {
        var aliases = await ReadPowerAliasesAsync(token).ConfigureAwait(false);
        var fallback = new List<(string Subgroup, string Setting, int Expected)>();
        const string root = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

        foreach (var item in items)
        {
            if (!IsPowerSettingSupported(item.Subgroup, item.Setting, aliases))
                continue;

            string subgroup = ResolvePowerToken(item.Subgroup, aliases);
            string setting = ResolvePowerToken(item.Setting, aliases);
            if (!Guid.TryParse(subgroup, out _) || !Guid.TryParse(setting, out _))
            {
                fallback.Add(item);
                continue;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{guid}\{subgroup}\{setting}");
                object? raw = key?.GetValue("ACSettingIndex");
                if (raw == null)
                {
                    fallback.Add(item);
                    continue;
                }

                int actual = Convert.ToInt32(raw);
                if (actual != item.Expected)
                    throw new InvalidOperationException($"Read-back {item.Setting} nepotvrdil hodnotu {item.Expected}; načteno {actual}.");
            }
            catch (InvalidOperationException) { throw; }
            catch
            {
                fallback.Add(item);
            }
        }

        if (fallback.Count > 0)
        {
            await Task.WhenAll(fallback.Select(x =>
                VerifyPowerValueFallbackAsync(guid, x.Subgroup, x.Setting, x.Expected, token))).ConfigureAwait(false);
            LogService.Info("performance", $"POWER_READBACK_FALLBACK count={fallback.Count}");
        }
    }

    private static string ResolvePowerToken(string token, IReadOnlyDictionary<string, string> aliases)
    {
        if (Guid.TryParse(token, out Guid parsed)) return parsed.ToString("D");
        return aliases.TryGetValue(token, out string? resolved) ? resolved : token;
    }

    private static async Task VerifyPowerValueFallbackAsync(
        string guid, string subgroup, string setting, int expectedDecimal, CancellationToken token)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/qh {guid} {subgroup} {setting}", TimeSpan.FromSeconds(12), token).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException("Nelze ověřit power setting " + setting + ".");
        string expectedHex = "0x" + expectedDecimal.ToString("x8");
        if (!r.StdOut.Contains(expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Read-back {setting} nepotvrdil hodnotu {expectedDecimal}.");
    }

    private static readonly object PowerAliasCacheLock = new();
    private static IReadOnlyDictionary<string, string>? _powerAliasCache;

    private static async Task<IReadOnlyDictionary<string, string>> ReadPowerAliasesAsync(CancellationToken token)
    {
        lock (PowerAliasCacheLock)
            if (_powerAliasCache != null) return _powerAliasCache;

        var result = await ProcessRunner.RunAsync("powercfg.exe", "/aliases", TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("Nelze načíst powercfg aliasy.");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (result.StdOut + "\n" + result.StdErr).Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*([0-9a-fA-F-]{36})\s+([A-Za-z0-9_]+)\s*$");
            if (match.Success) map[match.Groups[2].Value] = match.Groups[1].Value;
        }

        lock (PowerAliasCacheLock)
            return _powerAliasCache ??= map;
    }

    // Pure preflight for destructive duplicate-plan cleanup. A failed or
    // unparsable /getactivescheme previously left activeGuid empty, selected
    // the first candidate and could DELETE the genuinely active scheme.
    // A failed /list could likewise be mistaken for a complete inventory.
    internal sealed record DuplicatePowerPlanCleanupPlan(
        string ActiveGuid, string? KeepGuid, string[] DeleteGuids);

    // The guard is the commit point of a performance profile. Never
    // remove an older plan before the new one is protected. All actual
    // Windows operations are passed in, enabling pure synthetic tests.
    internal static async Task<int> EnsureGuardThenCleanupPerformanceDuplicatesAsync(
        string appliedGuid,
        IReadOnlyCollection<string> knownPerformanceGuids,
        Func<string, CancellationToken, Task> ensureGuard,
        Func<CancellationToken, Task<(bool Success, string Output)>> readActive,
        Func<string, CancellationToken, Task<bool>> removeDuplicate,
        Action<string> reportFailure,
        Action onGuardCommitted,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(knownPerformanceGuids);
        ArgumentNullException.ThrowIfNull(ensureGuard);
        ArgumentNullException.ThrowIfNull(readActive);
        ArgumentNullException.ThrowIfNull(removeDuplicate);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(onGuardCommitted);
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParse(appliedGuid, out Guid selected) || selected == Guid.Empty)
            throw new InvalidOperationException(
                "Výkonový plán nemá platné GUID; Power Plan guard nebyl registrován.");

        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidateText in knownPerformanceGuids)
        {
            if (!Guid.TryParse(candidateText, out Guid candidate) ||
                candidate == Guid.Empty)
                throw new InvalidOperationException(
                    "Seznam starých plánů obsahuje neplatný GUID; úklid byl zablokován.");
            if (candidate != selected) stale.Add(candidate.ToString("D"));
        }
        string normalized = selected.ToString("D");
        var cleanupPlan = new DuplicatePowerPlanCleanupPlan(
            normalized, normalized, stale.ToArray());

        // Failure here is fatal and MUST prevent ANY duplicate deletion.
        await ensureGuard(normalized, token).ConfigureAwait(false);
        // Called directly after successfully verified registration. The
        // caller must not rollback or delete the registered target now.
        onGuardCommitted();

        int removed = 0;
        foreach (string candidate in cleanupPlan.DeleteGuids)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                (bool success, string output) = await readActive(token)
                    .ConfigureAwait(false);
                EnsureDuplicatePowerPlanDeletionStillSafe(
                    cleanupPlan, candidate, success, output);
                token.ThrowIfCancellationRequested();
                if (await removeDuplicate(candidate, token).ConfigureAwait(false))
                    removed++;
                else
                    reportFailure("Úklid duplicit neodstranil plán " + candidate);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // caller knows guard is already committed; no rollback
            }
            catch (Exception ex)
            {
                // Best effort AFTER persistence. A failed/changed active
                // read must stop all further deletes; it must not rollback
                // a successfully installed guard or active performance plan.
                reportFailure("Úklid duplicit byl bezpečně přerušen: " + ex.Message);
                break;
            }
        }
        return removed;
    }

    internal sealed record PerformanceApplyPreflight(
        string ActiveGuid, string[] ReusableGuids);

    // The APPLY path must use the same fail-closed inventory admission
    // as explicit duplicate cleanup. Otherwise a failed /getactivescheme
    // suppresses rollback, and a failed /list can create redundant plans.
    internal static PerformanceApplyPreflight BuildPerformanceApplyPreflight(
        bool activeReadSucceeded, string? activeOutput,
        bool listReadSucceeded, string? listOutput)
    {
        DuplicatePowerPlanCleanupPlan plan = BuildDuplicatePowerPlanCleanupPlan(
            activeReadSucceeded, activeOutput, listReadSucceeded, listOutput);
        var candidates = new List<string>();
        if (plan.KeepGuid is not null) candidates.Add(plan.KeepGuid);
        candidates.AddRange(plan.DeleteGuids);
        return new PerformanceApplyPreflight(plan.ActiveGuid, candidates.ToArray());
    }

    // Always compare the actual active GUID and successful command exit,
    // never a substring that could also occur in diagnostics or old text.
    internal static void EnsureAppliedPowerSchemeReadback(
        string intendedGuid, bool readSucceeded, string? output)
    {
        if (!Guid.TryParse(intendedGuid, out Guid expected) ||
            expected == Guid.Empty ||
            RequireActiveSchemeGuid(readSucceeded, output) != expected)
            throw new InvalidOperationException(
                "Read-back nepotvrdil aktivaci očekávaného power plánu.");
    }

    private const string PowerPlanGuidPattern =
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    private static bool TryParsePowerSchemeInventoryRow(
        string line, out Guid scheme, out string label)
    {
        scheme = Guid.Empty;
        label = string.Empty;
        // Powercfg /list has a localized prefix containing GUID, one scheme
        // identifier, and a parenthesized display name; "*" marks the active
        // scheme. Reject extra GUIDs, partial names and unrelated log lines.
        Match row = Regex.Match(line,
            @"^\s*[^:\r\n]*\bGUID\b[^:\r\n]*:\s*(?<id>" + PowerPlanGuidPattern +
            @")\s+\((?<name>[^\r\n]+)\)\s*\*?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!row.Success ||
            !Guid.TryParseExact(row.Groups["id"].Value, "D", out scheme) ||
            scheme == Guid.Empty ||
            Regex.IsMatch(row.Groups["name"].Value, PowerPlanGuidPattern))
            return false;
        label = row.Groups["name"].Value.Trim();
        return label.Length > 0;
    }

    private static Guid RequireActiveSchemeGuid(bool succeeded, string? output)
    {
        if (!succeeded)
            throw new InvalidOperationException(
                "Nelze ověřit aktivní napájecí plán; čištění duplicit bylo zablokováno.");
        // Fail closed when stdout/stderr contains contradictory GUIDs. A first-match
        // parser could otherwise treat an unrelated diagnostic as the active plan.
        Guid[] parsed = Regex.Matches(output ?? "", PowerPlanGuidPattern)
            .Select(match => Guid.TryParseExact(match.Value, "D", out Guid value)
                ? value : Guid.Empty)
            .Distinct().ToArray();
        if (parsed.Length != 1 || parsed[0] == Guid.Empty)
            throw new InvalidOperationException(
                "Nelze jednoznačně určit aktivní napájecí plán; mazání duplicit bylo zablokováno.");
        return parsed[0];
    }

    internal static DuplicatePowerPlanCleanupPlan BuildDuplicatePowerPlanCleanupPlan(
        bool activeReadSucceeded, string? activeOutput,
        bool listReadSucceeded, string? listOutput)
    {
        Guid active = RequireActiveSchemeGuid(activeReadSucceeded, activeOutput);
        if (!listReadSucceeded)
            throw new InvalidOperationException(
                "Nelze ověřit úplný seznam napájecích plánů; čištění duplicit bylo zablokováno.");
        // A successful command can still return incomplete or unrelated
        // output. The active scheme must appear in the inventory before
        // any candidate is considered safe to delete.
        // Parse only complete powercfg inventory rows, never GUIDs embedded in
        // diagnostics or a different scheme's descriptive text. The prefix
        // contains "GUID" on localized Windows; the active "*" suffix is valid.
        var guids = new List<string>();
        bool activeListed = false;
        foreach (string line in (listOutput ?? "").Split(
                     LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParsePowerSchemeInventoryRow(line, out Guid candidate, out string label))
                continue;
            if (candidate == active) activeListed = true;
            if (candidate == Guid.Empty ||
                !string.Equals(label, "Spankyho Performance", StringComparison.OrdinalIgnoreCase))
                continue;
            string candidateGuid = candidate.ToString("D");
            if (!guids.Contains(candidateGuid, StringComparer.OrdinalIgnoreCase))
                guids.Add(candidateGuid);
        }
        if (!activeListed)
            throw new InvalidOperationException(
                "Seznam napájecích plánů neobsahuje aktivní GUID; čištění duplicit bylo zablokováno.");

        string activeGuid = active.ToString("D");
        string? keep = guids.FirstOrDefault(g =>
                           string.Equals(g, activeGuid, StringComparison.OrdinalIgnoreCase))
                       ?? guids.FirstOrDefault();
        string[] deletions = guids.Where(g =>
            !string.Equals(g, keep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(g, activeGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new DuplicatePowerPlanCleanupPlan(activeGuid, keep, deletions);
    }

    // Revalidate immediately before EACH privileged /delete. This narrows
    // the race where someone changes the active power plan after /list.
    // No check can make a separate powercfg subprocess fully atomic.
    internal static void EnsureDuplicatePowerPlanDeletionStillSafe(
        DuplicatePowerPlanCleanupPlan plan, string candidateGuid,
        bool activeReadSucceeded, string? activeOutput)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Guid currentActive = RequireActiveSchemeGuid(activeReadSucceeded, activeOutput);
        if (!Guid.TryParse(candidateGuid, out Guid candidate) ||
            !Guid.TryParse(plan.ActiveGuid, out Guid originalActive) ||
            candidate == Guid.Empty ||
            currentActive != originalActive ||
            currentActive == candidate ||
            !plan.DeleteGuids.Contains(candidate.ToString("D"),
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Aktivní plán se změnil nebo nelze bezpečně ověřit mazací cíl; " +
                "čištění duplicit bylo přerušeno.");
    }

    // Compiled release smoke exercises the same commit orchestration with
    // in-memory delegates only; never creates/executes Scheduler tasks or
    // launches actual powercfg.exe.
    internal static bool RunPerformanceGuardCommit205Smoke()
    {
        const string target = "11111111-2222-3333-4444-555555555555";
        const string stale = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var steps = new List<string>();
        Task<int> completed = EnsureGuardThenCleanupPerformanceDuplicatesAsync(
            target, new[] { target, stale },
            (guid, _) => { steps.Add("guard:" + guid); return Task.CompletedTask; },
            _ =>
            {
                steps.Add("read");
                return Task.FromResult((true, "Power Scheme GUID: " + target));
            },
            (guid, _) =>
            {
                steps.Add("remove:" + guid);
                return Task.FromResult(true);
            },
            _ => steps.Add("failure"),
            () => steps.Add("committed"), CancellationToken.None);
        // Only inspect completion of synthetic in-memory operations.
        // Never synchronously wait for unfinished work.
        return completed.IsCompletedSuccessfully &&
               steps.SequenceEqual(new[]
        {
            "guard:" + target, "committed", "read", "remove:" + stale
        });
    }

    // Pure fixture runs through the exact admission/read-back used by APPLY.
    // No real powercfg, registry, schedule, process or power-plan changes.
    internal static bool RunPerformanceApplyPreflight204Smoke()
    {
        const string a = "11111111-2222-3333-4444-555555555555";
        const string b = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string c = "99999999-8888-7777-6666-555555555555";
        string listing = "Power Scheme GUID: " + c + " (Balanced)\n" +
            "Power Scheme GUID: " + a + " (Spankyho Performance)\n" +
            "Power Scheme GUID: " + b + " (Spankyho Performance)";
        bool failedActiveBlocked = false, failedListBlocked = false;
        bool wrongActivationBlocked = false;
        try { _ = BuildPerformanceApplyPreflight(false, "x", true, listing); }
        catch (InvalidOperationException) { failedActiveBlocked = true; }
        try { _ = BuildPerformanceApplyPreflight(true, c, false, listing); }
        catch (InvalidOperationException) { failedListBlocked = true; }
        try { EnsureAppliedPowerSchemeReadback(a, true, "Power Scheme GUID: " + b); }
        catch (InvalidOperationException) { wrongActivationBlocked = true; }
        var preflight = BuildPerformanceApplyPreflight(true, c, true, listing);
        EnsureAppliedPowerSchemeReadback(a, true, "Power Scheme GUID: " + a);
        return failedActiveBlocked && failedListBlocked && wrongActivationBlocked &&
               preflight.ActiveGuid == c && preflight.ReusableGuids.Length == 2 &&
               preflight.ReusableGuids[0] == a && preflight.ReusableGuids[1] == b;
    }

    // In-memory release service smoke. Never launches powercfg or mutates OS.
    internal static bool RunDuplicatePowerPlanPreflight203Smoke()
    {
        const string a = "11111111-2222-3333-4444-555555555555";
        const string b = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        bool invalidBlocked = false, failedBlocked = false;
        try { _ = BuildDuplicatePowerPlanCleanupPlan(true, "no guid", true, ""); }
        catch (InvalidOperationException) { invalidBlocked = true; }
        try { _ = BuildDuplicatePowerPlanCleanupPlan(false, a, true, ""); }
        catch (InvalidOperationException) { failedBlocked = true; }
        var plan = BuildDuplicatePowerPlanCleanupPlan(
            true, "Power Scheme GUID: " + a, true,
            "Power Scheme GUID: " + a + " (Spankyho Performance)\n" +
            "Power Scheme GUID: " + b + " (Spankyho Performance)");
        bool changedActiveBlocked = false;
        try
        {
            EnsureDuplicatePowerPlanDeletionStillSafe(
                plan, b, true, "Power Scheme GUID: " + b);
        }
        catch (InvalidOperationException) { changedActiveBlocked = true; }
        EnsureDuplicatePowerPlanDeletionStillSafe(
            plan, b, true, "Power Scheme GUID: " + a);
        return invalidBlocked && failedBlocked && changedActiveBlocked &&
               plan.ActiveGuid == a && plan.KeepGuid == a &&
               plan.DeleteGuids.Length == 1 && plan.DeleteGuids[0] == b;
    }

    public static async Task<string> CleanupDuplicatePowerPlansAsync(CancellationToken token = default)
    {
        return await OperationCoordinator.RunExclusiveAsync("power-duplicate-cleanup", async ct =>
        {
            var active = await ProcessRunner.RunAsync(
                "powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            // Do not read or trust a partial plan inventory when this call fails.
            if (!active.Success)
                throw new InvalidOperationException(
                    "Nelze ověřit aktivní napájecí plán; čištění duplicit bylo zablokováno.");
            var list = await ProcessRunner.RunAsync(
                "powercfg.exe", "/list", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var plan = BuildDuplicatePowerPlanCleanupPlan(
                active.Success, active.StdOut + active.StdErr,
                list.Success, list.StdOut + "\n" + list.StdErr);
            if (plan.DeleteGuids.Length == 0)
                return "Žádné duplicitní Spankyho Performance plány nebyly nalezené.";

            string keep = plan.KeepGuid
                ?? throw new InvalidOperationException("Nelze určit napájecí plán k zachování.");
            string? guarded = await PowerPlanGuardService.TryGetConfiguredGuidAsync(ct);
            if (!string.IsNullOrWhiteSpace(guarded) &&
                !string.Equals(guarded, keep, StringComparison.OrdinalIgnoreCase))
                await PowerPlanGuardService.EnsureAsync(keep, ct);

            int removed = 0;
            foreach (string guid in plan.DeleteGuids)
            {
                ct.ThrowIfCancellationRequested();
                var current = await ProcessRunner.RunAsync(
                    "powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), ct)
                    .ConfigureAwait(false);
                EnsureDuplicatePowerPlanDeletionStillSafe(
                    plan, guid, current.Success, current.StdOut + current.StdErr);
                ct.ThrowIfCancellationRequested();
                var del = await PrivilegedProcessRunner.RunAsync(
                    "powercfg.exe", $"/delete {guid}", TimeSpan.FromSeconds(12), ct)
                    .ConfigureAwait(false);
                if (!del.Success)
                    throw new InvalidOperationException(
                        "Nelze odstranit duplicitní power plan " + guid +
                        ": " + (del.StdErr + " " + del.StdOut).Trim());
                removed++;
            }
            return $"Odstraněno duplicit: {removed}. Zachovaný plán: {keep}. Aktivní plán nebyl změněn.";
        }, TimeSpan.FromSeconds(45), token);
    }

    public static Task<string> GetNicPerformanceSummaryAsync(CancellationToken token = default)
    {
        lock (NicCacheLock)
        {
            if (_nicSummaryCache != null && DateTime.UtcNow - _nicSummaryAt < TimeSpan.FromSeconds(30))
                return Task.FromResult(_nicSummaryCache);
        }
        token.ThrowIfCancellationRequested();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up &&
                        x.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .OrderByDescending(x => x.Speed)
            .ToArray();
        string summary = adapters.Length == 0
            ? "Nebyl nalezen aktivní síťový adaptér."
            : string.Join(Environment.NewLine, adapters.Select(x =>
                $"{x.Name} | {x.Description} | {(x.Speed > 0 ? FormatLinkSpeed(x.Speed) : "neznámá rychlost")}"));
        lock (NicCacheLock) { _nicSummaryCache = summary; _nicSummaryAt = DateTime.UtcNow; }
        return Task.FromResult(summary);
    }

    private static string FormatLinkSpeed(long bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? $"{bitsPerSecond / 1_000_000_000d:0.##} Gbps"
        : $"{bitsPerSecond / 1_000_000d:0.##} Mbps";


    public static Task<string> GetNicTuningReadbackAsync(CancellationToken token = default)
        => Task.Run(() => NativeNicTuningService.ReadSummary(token), token);

    public static void InvalidateNicCache()
    {
        lock (NicCacheLock)
        {
            _nicSummaryCache = null;
            _nicSummaryAt = default;
            _nicCapabilityCache = null;
            _nicCapabilityAt = default;
            _nicCapabilityInFlight = null;
            _nicCapabilityPersistentChecked = false;
            _nicCapabilityGeneration++;
        }
        NativeNicTuningService.InvalidateAdvancedPropertyCache();
        PersistentHardwareCacheService.Remove(NicCapabilityPersistentKey);
    }

    private static Task<string> RunNicMutationAsync(Func<CancellationToken, string> mutation,
        TimeSpan timeout, CancellationToken token)
        => OperationCoordinator.RunExclusiveAsync("nic-mutation", ct => Task.Run(() =>
        {
            try { return mutation(ct); }
            finally { InvalidateNicCache(); }
        }), timeout, token);

    public static Task<string> ApplyNicPerformanceProfileAsync(CancellationToken token = default)
        => RunNicMutationAsync(NativeNicTuningService.ApplyPerformanceProfile, TimeSpan.FromSeconds(90), token);

    public static Task<string> SetNicAdvancedPropertyAsync(string[] names, string value, CancellationToken token = default)
        => RunNicMutationAsync(ct => NativeNicTuningService.SetProperty(names, value, ct), TimeSpan.FromSeconds(85), token);

    public static Task<string> SetNicAdvancedToggleAsync(string[] names, bool enabled, CancellationToken token = default)
        => RunNicMutationAsync(ct => NativeNicTuningService.SetPropertyEnabled(names, enabled, ct), TimeSpan.FromSeconds(85), token);

    public static Task<string> SetNicRscEnabledAsync(bool enabled, CancellationToken token = default)
        => RunNicMutationAsync(ct => NativeNicTuningService.SetRscEnabled(enabled, ct), TimeSpan.FromSeconds(85), token);


    public static Task<NicPropertyOptionState?> GetNicPropertyOptionsAsync(
        string[] names,
        CancellationToken token = default)
        => Task.Run(() => NativeNicTuningService.ReadPropertyOptions(names, token), token);

    public static Task<string> GetNicDetailedSummaryAsync(CancellationToken token = default)
        => Task.Run(() => NativeNicTuningService.ReadDetailedSummary(token), token);

    internal static bool TryWarmNicCapabilityPersistentSnapshot()
    {
        lock (NicCacheLock)
        {
            if (_nicCapabilityCache != null &&
                DateTime.UtcNow - _nicCapabilityAt < TimeSpan.FromMinutes(10) &&
                CachedNicAdapterStillActive(_nicCapabilityCache))
                return true;

            if (PersistentHardwareCacheService.TryReadMachineStable<Dictionary<string, string>>(
                    NicCapabilityPersistentKey, NicCapabilityPersistentTtl, out var persisted) &&
                persisted is { Count: > 0 })
            {
                var cached = new Dictionary<string, string>(persisted, StringComparer.OrdinalIgnoreCase);
                if (CachedNicAdapterStillActive(cached))
                {
                    _nicCapabilityCache = cached;
                    _nicCapabilityAt = DateTime.UtcNow;
                    _nicCapabilityPersistentChecked = true;
                    LogService.Info("performance", $"NIC_CAPABILITY_PERSISTENT_PREWARM count={cached.Count}");
                    return true;
                }
            }
            return false;
        }
    }

    public static Task<IReadOnlyDictionary<string, string>> GetNicCapabilitiesAsync(CancellationToken token = default)
    {
        Task<IReadOnlyDictionary<string, string>> shared;
        lock (NicCacheLock)
        {
            // Capability discovery is WMI/CIM-heavy (~150-200 ms on the target machine). Mutations explicitly invalidate this cache,
            // so keep a longer warm window to make repeated Network navigation effectively free.
            if (_nicCapabilityCache != null && DateTime.UtcNow - _nicCapabilityAt < TimeSpan.FromMinutes(10))
            {
                if (CachedNicAdapterStillActive(_nicCapabilityCache))
                    return Task.FromResult(_nicCapabilityCache);
                _nicCapabilityCache = null;
                _nicCapabilityAt = default;
            }

            if (!_nicCapabilityPersistentChecked)
            {
                _nicCapabilityPersistentChecked = true;
                if (PersistentHardwareCacheService.TryReadMachineStable<Dictionary<string, string>>(
                        NicCapabilityPersistentKey, NicCapabilityPersistentTtl, out var persisted) &&
                    persisted is { Count: > 0 })
                {
                    var cached = new Dictionary<string, string>(persisted, StringComparer.OrdinalIgnoreCase);
                    if (CachedNicAdapterStillActive(cached))
                    {
                        _nicCapabilityCache = cached;
                        _nicCapabilityAt = DateTime.UtcNow;
                        LogService.Info("performance", $"NIC_CAPABILITY_PERSISTENT_HIT count={cached.Count}");
                        return Task.FromResult<IReadOnlyDictionary<string, string>>(cached);
                    }
                    NativeNicTuningService.InvalidateAdvancedPropertyCache();
        PersistentHardwareCacheService.Remove(NicCapabilityPersistentKey);
                    LogService.Info("performance", "NIC_CAPABILITY_PERSISTENT_STALE adapterChanged=true");
                }
            }

            if (_nicCapabilityInFlight == null)
            {
                int generation = _nicCapabilityGeneration;
                _nicCapabilityInFlight = LoadNicCapabilitiesSharedAsync(generation);
            }
            shared = _nicCapabilityInFlight;
        }

        return token.CanBeCanceled ? shared.WaitAsync(token) : shared;
    }

    private static bool CachedNicAdapterStillActive(IReadOnlyDictionary<string, string> cached)
        => cached.TryGetValue("Adapter", out string? adapter) && !string.IsNullOrWhiteSpace(adapter) &&
           string.Equals(adapter, NativeNicTuningService.FindPrimaryAdapterNameFast(), StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyDictionary<string, string>> LoadNicCapabilitiesSharedAsync(int generation)
    {
        try
        {
            var value = await Task.Run(() => NativeNicTuningService.ReadCapabilities(CancellationToken.None)).ConfigureAwait(false);
            lock (NicCacheLock)
            {
                if (generation == _nicCapabilityGeneration)
                {
                    _nicCapabilityCache = value;
                    _nicCapabilityAt = DateTime.UtcNow;
                    var persisted = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
                    PersistentHardwareCacheService.WriteMachineStable(NicCapabilityPersistentKey, persisted);
                }
            }
            return value;
        }
        finally
        {
            lock (NicCacheLock)
            {
                if (generation == _nicCapabilityGeneration)
                    _nicCapabilityInFlight = null;
            }
        }
    }

}
