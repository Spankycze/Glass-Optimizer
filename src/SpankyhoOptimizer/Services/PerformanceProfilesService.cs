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
            var duplicateGuids = new List<string>();
            try
            {
                var active = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), ct);
                originalGuid = Regex.Match(active.StdOut + active.StdErr, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value;
                var planList = await ProcessRunner.RunAsync("powercfg.exe", "/list", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                foreach (string line in (planList.StdOut + "\n" + planList.StdErr).Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("Spankyho Performance", StringComparison.OrdinalIgnoreCase)) continue;
                    string guid = Regex.Match(line, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value;
                    if (!string.IsNullOrWhiteSpace(guid) && !duplicateGuids.Contains(guid, StringComparer.OrdinalIgnoreCase)) duplicateGuids.Add(guid);
                }
                newGuid = duplicateGuids.FirstOrDefault(g => string.Equals(g, originalGuid, StringComparison.OrdinalIgnoreCase)) ?? duplicateGuids.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(newGuid))
                {
                    var duplicate = await ProcessRunner.RunAsync("powercfg.exe", "/duplicatescheme SCHEME_BALANCED", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                    newGuid = Regex.Match(duplicate.StdOut + duplicate.StdErr, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value;
                    if (string.IsNullOrWhiteSpace(newGuid)) throw new InvalidOperationException("Nepodařilo se vytvořit výkonový power plan.");
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
                if (!verifyActive.StdOut.Contains(newGuid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Read-back nepotvrdil aktivaci nového power plánu.");
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
                foreach (string staleGuid in duplicateGuids.Where(g => !string.Equals(g, newGuid, StringComparison.OrdinalIgnoreCase)))
                {
                    var del = await ProcessRunner.RunAsync("powercfg.exe", $"/delete {staleGuid}", TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
                    if (!del.Success) LogService.Error("power", $"Duplicate cleanup failed guid={staleGuid}: {del.StdErr}{del.StdOut}");
                    else LogService.Info("power", $"Removed duplicate Spankyho Performance guid={staleGuid}");
                }
                progressStep = 39; Report("Persistence", "Nastavuji Power Plan guard po přihlášení…", 98);
                await PowerPlanGuardService.EnsureAsync(newGuid, ct);
                PowerPlanStateService.InvalidateCache();
                progressStep = 40; Report("Hotovo", "Power plán byl aplikován. Probíhá finální UI read-back…", 100);
                LogService.Info("power", $"Performance profile verified guid={newGuid} reused={!createdNew} cpu={cpu} vendor={vendor} arch={adaptive.Architecture} logical={adaptive.LogicalProcessors} epp={epp}");
                return $"CPU: {cpu}\nPlatforma: {(mobile ? "Notebook" : "Desktop")}\nHW profil: {adaptive.Architecture} • {adaptive.LogicalProcessors} logical CPU\nAdaptive CPU V3: {vendor}\nCPU minimum/max: 5 / 100 %\nEPP: {epp}\nBoost: Efficient Aggressive\nCPPC autonomous: Enabled\nPerformance increase/decrease: Rocket / Ideal\nThresholds: {adaptive.IncThreshold}% / {adaptive.DecThreshold}%\nCore parking min/max: {adaptive.CoreMin}% / 100%\nLatency hint: 100% unpark, EPP 0, perf 100\nDuty cycling: Disabled\nCPU idle states: Enabled\nWake timers: Important only\nHybrid sleep: Off\nAway mode: Off\nVideo playback: Performance / Quality\nDisplay/Disk/Sleep/Hibernate timeout: Never na AC\nUSB suspend: {(mobile ? "Windows/úsporný" : "Off na AC")}\nWi-Fi: Maximum Performance na AC\nPower plan byl aktivován a ověřen read-backem.\nPersistence: Power Plan guard ho 30 s po přihlášení znovu aktivuje.";
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(originalGuid))
                {
                    try
                    {
                        var rollback = await ProcessRunner.RunAsync("powercfg.exe", $"/setactive {originalGuid}", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                        if (!rollback.Success) LogService.Error("power", "Rollback active scheme failed: " + rollback.StdErr + rollback.StdOut);
                    }
                    catch (Exception ex) { LogService.Error("power", "Rollback active scheme threw", ex); }
                }
                if (createdNew && !string.IsNullOrWhiteSpace(newGuid))
                {
                    try
                    {
                        var cleanup = await ProcessRunner.RunAsync("powercfg.exe", $"/delete {newGuid}", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
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
        var r = await ProcessRunner.RunAsync("powercfg.exe", args, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
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

    public static async Task<string> CleanupDuplicatePowerPlansAsync(CancellationToken token = default)
    {
        return await OperationCoordinator.RunExclusiveAsync("power-duplicate-cleanup", async ct =>
        {
            var active = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), ct);
            string activeGuid = Regex.Match(active.StdOut + active.StdErr, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value;
            var list = await ProcessRunner.RunAsync("powercfg.exe", "/list", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var guids = new List<string>();
            foreach (string line in (list.StdOut + "\n" + list.StdErr).Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("Spankyho Performance", StringComparison.OrdinalIgnoreCase)) continue;
                string guid = Regex.Match(line, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value;
                if (!string.IsNullOrWhiteSpace(guid) && !guids.Contains(guid, StringComparer.OrdinalIgnoreCase)) guids.Add(guid);
            }
            if (guids.Count <= 1) return "Žádné duplicitní Spankyho Performance plány nebyly nalezené.";
            string keep = guids.FirstOrDefault(g => string.Equals(g, activeGuid, StringComparison.OrdinalIgnoreCase)) ?? guids[0];
            string? guarded = await PowerPlanGuardService.TryGetConfiguredGuidAsync(ct);
            if (!string.IsNullOrWhiteSpace(guarded) && !string.Equals(guarded, keep, StringComparison.OrdinalIgnoreCase))
                await PowerPlanGuardService.EnsureAsync(keep, ct);
            int removed = 0;
            foreach (string guid in guids.Where(g => !string.Equals(g, keep, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(guid, activeGuid, StringComparison.OrdinalIgnoreCase)) continue;
                var del = await ProcessRunner.RunAsync("powercfg.exe", $"/delete {guid}", TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
                if (!del.Success) throw new InvalidOperationException("Nelze odstranit duplicitní power plan " + guid + ": " + (del.StdErr + " " + del.StdOut).Trim());
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
        PersistentHardwareCacheService.Remove(NicCapabilityPersistentKey);
    }

    public static Task<string> ApplyNicPerformanceProfileAsync(CancellationToken token = default)
        => OperationCoordinator.RunExclusiveAsync("nic-profile",
            ct => Task.Run(() => NativeNicTuningService.ApplyPerformanceProfile(ct), ct),
            TimeSpan.FromSeconds(90), token);

    public static Task<string> SetNicAdvancedPropertyAsync(string[] names, string value, CancellationToken token = default)
        => OperationCoordinator.RunExclusiveAsync("nic-advanced",
            ct => Task.Run(() => NativeNicTuningService.SetProperty(names, value, ct), ct),
            TimeSpan.FromSeconds(85), token);

    public static Task<string> SetNicAdvancedToggleAsync(string[] names, bool enabled, CancellationToken token = default)
        => OperationCoordinator.RunExclusiveAsync("nic-advanced",
            ct => Task.Run(() => NativeNicTuningService.SetPropertyEnabled(names, enabled, ct), ct),
            TimeSpan.FromSeconds(85), token);

    public static Task<string> SetNicRscEnabledAsync(bool enabled, CancellationToken token = default)
        => OperationCoordinator.RunExclusiveAsync("nic-rsc",
            ct => Task.Run(() => NativeNicTuningService.SetRscEnabled(enabled, ct), ct),
            TimeSpan.FromSeconds(85), token);

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
    {
        if (!cached.TryGetValue("Adapter", out string? cachedAdapter) || string.IsNullOrWhiteSpace(cachedAdapter))
            return false;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;
            if (nic.Name.Equals(cachedAdapter, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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
