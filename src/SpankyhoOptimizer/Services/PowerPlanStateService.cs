using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record PowerSettingState(
    string Id, string Title, string Description,
    string Subgroup, string Setting,
    int? CurrentValue, int RecommendedValue,
    string CurrentText, string RecommendedText)
{
    public bool IsApplied => CurrentValue == RecommendedValue;
}

public sealed record PowerPlanSnapshot(
    string ActivePlan, string? ActiveGuid,
    string? SpankyPlanGuid, int SpankyDuplicateCount,
    IReadOnlyList<PowerSettingState> Settings);

public static class PowerPlanStateService
{
    private static readonly char[] LineSeparators = { '\r', '\n' };
    private sealed record Def(string Id, string Title, string Description, string Group, string Setting, int Recommended);
    private sealed record SchemeIndex(bool Success, string? ActiveGuid, string ActivePlanName, List<string> NamedPlanGuids);
    private static readonly Def[] Definitions =
    {
        new("cpu-min", "CPU minimum 5 %", "Procesor může v idle normálně snižovat takt a napětí.", "54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", 5),
        new("cpu-max", "CPU maximum 100 %", "Povolí plný výkon procesoru pod zátěží.", "54533251-82be-4824-96c1-47b60b740d00", "bc5038f7-23e0-4960-96da-33abaf5935ec", 100),
        new("boost", "Processor boost — Efficient Aggressive", "Rychlý boost bez permanentního maxima.", "54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7", 4),
        new("epp", "Energy Performance Preference (EPP)", "HW-aware EPP; Ryzen 3000/5000 používá 20 pro nižší idle spotřebu bez omezení 100% výkonu.", "54533251-82be-4824-96c1-47b60b740d00", "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0),
        new("cppc-autonomous", "CPPC / Autonomous mode — Enabled", "Windows a CPU mohou autonomně řídit výkon přes moderní CPPC.", "54533251-82be-4824-96c1-47b60b740d00", "8baa4a8a-14c6-4451-8e8b-14bdbd197537", 1),
        new("perf-inc-policy", "Performance increase policy — Rocket", "Rychlá reakce procesoru na novou zátěž.", "54533251-82be-4824-96c1-47b60b740d00", "465e1f50-b610-473a-ab58-00d1077dc418", 2),
        new("perf-dec-policy", "Performance decrease policy — Ideal", "Po skončení zátěže umožní procesoru rychle vrátit spotřebu dolů.", "54533251-82be-4824-96c1-47b60b740d00", "40fbefc7-2e9d-4d25-a185-0cfd8574bac6", 0),
        new("perf-inc-threshold", "Performance increase threshold", "HW-aware hranice pro rychlejší zvýšení výkonu.", "54533251-82be-4824-96c1-47b60b740d00", "06cadf0e-64ed-448a-8927-ce7bf90eb35d", 0),
        new("perf-dec-threshold", "Performance decrease threshold", "HW-aware hranice pro návrat do úspornějšího stavu.", "54533251-82be-4824-96c1-47b60b740d00", "12a0ab44-fe28-4fa9-b3bd-4b64f44960a6", 0),
        new("perf-check", "Performance time check interval", "Rozumně rychlá kontrola výkonu bez extrémního polling intervalu.", "54533251-82be-4824-96c1-47b60b740d00", "4d2b0152-7d5c-498b-88e2-34345392a2c5", 0),
        new("core-park-min", "Core Parking minimum cores — Adaptive", "Nechá Windows parkovat část jader v idle, ale drží dostatečnou rezervu pro gaming.", "54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", 0),
        new("core-park-max", "Core Parking maximum cores — 100 %", "Pod zátěží jsou dostupná všechna jádra.", "54533251-82be-4824-96c1-47b60b740d00", "ea062031-0e34-4ff1-9b6d-eb1059334028", 100),
        new("core-park-inc-policy", "Core Parking increase policy — Fast unpark", "Při zátěži rychle zpřístupní další jádra.", "54533251-82be-4824-96c1-47b60b740d00", "c7be0679-2817-4d69-9d02-519a537ed0c6", 2),
        new("core-park-dec-policy", "Core Parking decrease policy — Ideal", "Po krátkém propadu zátěže neparkuje jádra zbytečně agresivně.", "54533251-82be-4824-96c1-47b60b740d00", "71021b41-c749-4d21-be74-a00f335d582b", 0),
        new("core-park-inc-time", "Core Parking increase time — 1", "Rychlé odparkování při nové zátěži.", "54533251-82be-4824-96c1-47b60b740d00", "2ddd5a84-5a71-437e-912a-db0b8c788732", 1),
        new("core-park-dec-time", "Core Parking decrease time — 10", "Pomalejší parkování po krátkém poklesu zátěže.", "54533251-82be-4824-96c1-47b60b740d00", "dfd10d17-d5eb-45dd-877a-9a34ddd15c82", 10),
        new("latency-unpark", "Latency hint unpark — 100 %", "Při latency-sensitive hintu umožní okamžitě použít všechna jádra.", "54533251-82be-4824-96c1-47b60b740d00", "616cdaa5-695e-4545-97ad-97dc2d1bdd88", 100),
        new("latency-epp", "Latency hint EPP — 0", "Při aktivním latency hintu preferuje maximální výkon.", "54533251-82be-4824-96c1-47b60b740d00", "4b70f900-cdd9-4e66-aa26-ae8417f98173", 0),
        new("latency-perf", "Latency hint performance — 100 %", "Při latency-sensitive workloadu dovolí plný výkon CPU.", "54533251-82be-4824-96c1-47b60b740d00", "619b7505-003b-4e82-b7a6-4dd29c300971", 100),
        new("duty-cycling", "Processor duty cycling — Disabled", "Na desktopu nepoužívá staré duty-cycle throttling chování.", "54533251-82be-4824-96c1-47b60b740d00", "4e4450b3-6179-4e91-b8f1-5bb9938f81a1", 0),
        new("cpu-idle", "CPU idle states — Enabled", "Zachová C/idle stavy; profil nedrží procesor zbytečně na plném výkonu.", "54533251-82be-4824-96c1-47b60b740d00", "5d76a2ca-e8c0-402f-a133-2158492d58ad", 0),
        new("cooling", "System cooling policy — Active", "Preferuje chlazení před omezením výkonu CPU.", "54533251-82be-4824-96c1-47b60b740d00", "94d3a615-a899-4ac5-ae2b-e4d8f634367f", 1),
        new("aspm", "PCI Express Link State Power Management — Off", "Nevypíná PCIe linky na AC.", "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", 0),
        new("disk", "Turn off hard disk after — Never", "Na AC nevypíná disk kvůli idle timeoutu.", "0012ee47-9041-4b5d-9b77-535fba8b1442", "6738e2c4-e8a5-4a42-b16a-e040e769756e", 0),
        new("display", "Turn off display after — Never", "Na AC nevypíná monitor kvůli idle timeoutu.", "7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", 0),
        new("sleep", "Sleep timeout on AC — Never", "Počítač se při napájení ze sítě automaticky neuspí.", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", 0),
        new("hibernate", "Hibernate timeout on AC — Never", "Počítač se na AC automaticky nepřepne do hibernace.", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "9d7815a6-7ee4-497e-8888-515a05f02364", 0),
        new("wake-timers", "Wake timers — Important only", "Povolí pouze důležitá probuzení systému.", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d", 2),
        new("hybrid-sleep", "Hybrid Sleep — Off", "Když je sleep na Never, hybridní sleep na desktopu není potřeba.", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "94ac6d29-73ce-41a6-809f-6363ba21b47e", 0),
        new("away-mode", "Away Mode — Off", "Zabrání starému media-center away režimu.", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "25dfa149-5dd1-4736-b5ab-e8a37b5b8187", 0),
        new("usb", "USB selective suspend — Adaptive", "Desktop: Disabled; notebook: Windows úsporné chování.", "2a737441-1930-4402-8d77-b2bebba308a3", "0853a681-27c8-4100-a2fd-82013e970683", 0),
        new("wifi", "Wireless Adapter — Maximum Performance", "Wi-Fi adaptér na AC neomezuje výkon kvůli úspoře.", "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0),
        new("video-bias", "Video playback quality bias — Performance", "Přehrávání videa na AC preferuje výkon.", "9596fb26-9850-41fd-ac3e-f7c3c00afd4b", "10778347-1370-4ee0-8bbd-33bdacaade49", 1),
        new("video-mode", "When playing video — Optimize quality", "Na AC preferuje kvalitu videa místo úspory energie.", "9596fb26-9850-41fd-ac3e-f7c3c00afd4b", "34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4", 0),
        new("core-park", "Core Parking minimum cores — 100 %", "Advanced: drží všechna jádra odparkovaná; může zvýšit idle spotřebu.", "54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", 100),
        new("idle-promote", "Idle promote threshold — 60 %", "Advanced: blízko Windows defaultu; neměnit bez konkrétního důvodu.", "54533251-82be-4824-96c1-47b60b740d00", "7b224883-b3cc-4d79-819f-8374152cbe7c", 60),
        new("idle-demote", "Idle demote threshold — 40 %", "Advanced: blízko Windows defaultu; neměnit bez konkrétního důvodu.", "54533251-82be-4824-96c1-47b60b740d00", "4b92d758-5a24-4851-a470-815d78aee119", 40),
        new("boost-aggressive", "Processor boost mode — Aggressive", "Advanced: agresivnější boost s vyšší spotřebou a teplotou.", "54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7", 2),
        new("epp-zero", "Energy Performance Preference (EPP) — 0", "Advanced absolute maximum performance.", "54533251-82be-4824-96c1-47b60b740d00", "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0)
    };

    private static readonly object CacheGate = new();
    private static PowerPlanSnapshot? _cache;
    private static DateTime _cacheAt;
    private static Task<PowerPlanSnapshot>? _inFlight;
    private static int _generation;

    public static PowerPlanSnapshot? TryGetFreshSnapshot()
    {
        lock (CacheGate)
            return _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(15) ? _cache : null;
    }

    public static void InvalidateCache()
    {
        lock (CacheGate)
        {
            _cache = null;
            _cacheAt = default;
            _inFlight = null;
            _generation++;
        }
    }

    public static Task<bool> TryPrewarmRegistrySnapshotAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        lock (CacheGate)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(15)) return true;
            if (_inFlight != null && !_inFlight.IsCompleted) return false;
        }

        int generation;
        lock (CacheGate) generation = _generation;

        var adaptive = PerformanceProfilesService.GetAdaptivePowerProfile();
        int usb = adaptive.Mobile ? 1 : 0;
        var schemeIndex = ReadSchemeIndexFromRegistry("Spankyho Performance");
        if (!schemeIndex.Success || string.IsNullOrWhiteSpace(schemeIndex.ActiveGuid)) return false;

        var values = ReadActiveAcValuesFromRegistry(schemeIndex.ActiveGuid);
        if (values.Count == 0) return false;

        var states = new List<PowerSettingState>(Definitions.Length);
        foreach (var def in Definitions)
        {
            int recommended = def.Id switch
            {
                "epp" => adaptive.Epp,
                "usb" => usb,
                "perf-inc-threshold" => adaptive.IncThreshold,
                "perf-dec-threshold" => adaptive.DecThreshold,
                "perf-check" => adaptive.PerfCheckMs,
                "core-park-min" => adaptive.CoreMin,
                _ => def.Recommended
            };
            string key = NormalizeSetting(def.Setting);
            int? current = values.TryGetValue(key, out int currentRaw) ? currentRaw : null;
            states.Add(new PowerSettingState(def.Id, def.Title, def.Description, def.Group, def.Setting,
                current, recommended, FormatValue(def.Id, current), FormatValue(def.Id, recommended)));
        }

        var snapshot = new PowerPlanSnapshot(
            schemeIndex.ActivePlanName,
            schemeIndex.ActiveGuid,
            schemeIndex.NamedPlanGuids.FirstOrDefault(),
            Math.Max(0, schemeIndex.NamedPlanGuids.Count - 1),
            states);

        lock (CacheGate)
        {
            if (generation != _generation) return false;
            _cache = snapshot;
            _cacheAt = DateTime.UtcNow;
        }
        LogService.Info("performance", $"POWER_REGISTRY_PREWARM settings={snapshot.Settings.Count} active={snapshot.ActiveGuid}");
        return true;
    }, token);

    public static async Task<PowerPlanSnapshot> ReadAsync(CancellationToken token = default, bool forceRefresh = false)
    {
        if (forceRefresh) InvalidateCache();

        lock (CacheGate)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(15)) return _cache;
        }

        Task<PowerPlanSnapshot> task;
        lock (CacheGate)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(15)) return _cache;
            if (_inFlight == null)
            {
                int generation = _generation;
                var created = Task.Run(() => LoadFreshAsync(generation));
                _inFlight = created;
                task = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (CacheGate)
                        if (ReferenceEquals(_inFlight, created)) _inFlight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                task = _inFlight;
            }
        }

        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<PowerPlanSnapshot> LoadFreshAsync(int generation)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var adaptive = PerformanceProfilesService.GetAdaptivePowerProfile();
        int usb = adaptive.Mobile ? 1 : 0;

        var schemeIndex = ReadSchemeIndexFromRegistry("Spankyho Performance");
        string? activeGuid = schemeIndex.ActiveGuid;
        string activePlanName = schemeIndex.ActivePlanName;
        List<string> named = schemeIndex.NamedPlanGuids;
        string planSource = schemeIndex.Success ? "registry" : "powercfg";

        if (!schemeIndex.Success)
        {
            var list = await ProcessRunner.RunAsync("powercfg.exe", "/list", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            string listText = list.StdOut + "\n" + list.StdErr;
            string activeText = listText.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains('*') && ExtractGuid(line) != null) ?? string.Empty;
            activeGuid = ExtractGuid(activeText);

            if (string.IsNullOrWhiteSpace(activeGuid))
            {
                var active = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                activeText = (active.StdOut + " " + active.StdErr).Trim();
                activeGuid = ExtractGuid(activeText);
            }

            named = ParseNamedPlans(listText, "Spankyho Performance");
            activePlanName = ParsePlanName(activeText, activeGuid, listText);
        }

        var values = ReadActiveAcValuesFromRegistry(activeGuid);
        if (values.Count == 0)
        {
            var query = await ProcessRunner.RunAsync("powercfg.exe", "/qh SCHEME_CURRENT", TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            values = ParseAllValues(query.StdOut + "\n" + query.StdErr);
            planSource += "+qh";
        }
        var states = new List<PowerSettingState>(Definitions.Length);
        foreach (var def in Definitions)
        {
            int recommended = def.Id switch
            {
                "epp" => adaptive.Epp,
                "usb" => usb,
                "perf-inc-threshold" => adaptive.IncThreshold,
                "perf-dec-threshold" => adaptive.DecThreshold,
                "perf-check" => adaptive.PerfCheckMs,
                "core-park-min" => adaptive.CoreMin,
                _ => def.Recommended
            };
            string key = NormalizeSetting(def.Setting);
            int? current = values.TryGetValue(key, out int currentRaw) ? currentRaw : null;
            states.Add(new PowerSettingState(def.Id, def.Title, def.Description, def.Group, def.Setting,
                current, recommended, FormatValue(def.Id, current), FormatValue(def.Id, recommended)));
        }

        var snapshot = new PowerPlanSnapshot(activePlanName, activeGuid, named.FirstOrDefault(), Math.Max(0, named.Count - 1), states);
        bool publish;
        lock (CacheGate)
        {
            publish = generation == _generation;
            if (publish)
            {
                _cache = snapshot;
                _cacheAt = DateTime.UtcNow;
            }
        }
        sw.Stop();
        LogService.Info("power", $"POWER_SNAPSHOT_FRESH ms={sw.Elapsed.TotalMilliseconds:0.0} source={planSource} active={snapshot.ActiveGuid ?? "none"} spanky={snapshot.SpankyPlanGuid ?? "none"} duplicates={snapshot.SpankyDuplicateCount}");
        return snapshot;
    }

    private static SchemeIndex ReadSchemeIndexFromRegistry(string targetName)
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        try
        {
            using var schemes = Registry.LocalMachine.OpenSubKey(root);
            if (schemes == null)
                return new SchemeIndex(false, null, "Aktivní power plán", new List<string>());

            string? activeGuid = schemes.GetValue("ActivePowerScheme")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(activeGuid))
                return new SchemeIndex(false, null, "Aktivní power plán", new List<string>());

            string activeName = "Aktivní power plán";
            var named = new List<string>();
            foreach (string guid in schemes.GetSubKeyNames())
            {
                using var plan = schemes.OpenSubKey(guid);
                string displayName = NormalizeFriendlyName(plan?.GetValue("FriendlyName")?.ToString());
                if (guid.Equals(activeGuid, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(displayName))
                    activeName = displayName;
                if (!string.IsNullOrWhiteSpace(displayName) &&
                    displayName.Contains(targetName, StringComparison.OrdinalIgnoreCase) &&
                    !named.Contains(guid, StringComparer.OrdinalIgnoreCase))
                    named.Add(guid);
            }

            return new SchemeIndex(true, activeGuid, activeName, named);
        }
        catch (Exception ex)
        {
            LogService.Error("power", "Power scheme registry index failed", ex);
            return new SchemeIndex(false, null, "Aktivní power plán", new List<string>());
        }
    }

    private static string NormalizeFriendlyName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string value = raw.Trim();
        if (value.StartsWith('@'))
        {
            int comma = value.LastIndexOf(',');
            if (comma >= 0 && comma + 1 < value.Length)
            {
                string fallback = value[(comma + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
            }
        }
        return value;
    }

    private static Dictionary<string, int> ReadActiveAcValuesFromRegistry(string? activeGuid)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(activeGuid)) return map;
        const string root = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        foreach (var def in Definitions)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{activeGuid}\{def.Group}\{def.Setting}");
                object? raw = key?.GetValue("ACSettingIndex");
                if (raw != null) map[NormalizeSetting(def.Setting)] = Convert.ToInt32(raw);
            }
            catch (Exception ex)
            {
                LogService.Error("power", $"Power registry read failed: {def.Id}", ex);
            }
        }
        return map;
    }

    private static string ParsePlanName(string activeText, string? activeGuid, string listText)
    {
        static string? FromLine(string line)
        {
            var m = Regex.Match(line ?? string.Empty, @"\(([^()]*)\)\s*\*?\s*$");
            return m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value.Trim() : null;
        }
        string? direct = FromLine(activeText);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (!string.IsNullOrWhiteSpace(activeGuid))
        {
            foreach (string line in (listText ?? string.Empty).Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                if (line.Contains(activeGuid, StringComparison.OrdinalIgnoreCase))
                {
                    string? name = FromLine(line);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
        }
        return "Aktivní power plán";
    }

    private static Dictionary<string, int> ParseAllValues(string text)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? setting = null;
        foreach (string line in text.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var sg = Regex.Match(line, @"Power Setting GUID:\s*([0-9a-fA-F-]{36})(?:\s+\(([^)]+)\))?", RegexOptions.IgnoreCase);
            if (sg.Success) { setting = !string.IsNullOrWhiteSpace(sg.Groups[2].Value) ? sg.Groups[2].Value : sg.Groups[1].Value; continue; }
            if (setting == null) continue;
            var ac = Regex.Match(line, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            if (ac.Success && int.TryParse(ac.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out int value)) map[NormalizeSetting(setting)] = value;
        }
        return map;
    }
    private static string NormalizeSetting(string value) => value.Trim().ToUpperInvariant();

    private static string? ExtractGuid(string text)
    {
        var m = Regex.Match(text ?? string.Empty,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }

    private static List<string> ParseNamedPlans(string text, string name)
    {
        var result = new List<string>();
        foreach (string line in (text ?? string.Empty).Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            string? guid = ExtractGuid(line);
            if (!string.IsNullOrWhiteSpace(guid) && !result.Contains(guid, StringComparer.OrdinalIgnoreCase))
                result.Add(guid);
        }
        return result;
    }

    private static string FormatValue(string id, int? value)
    {
        if (value is null) return "Nedostupné";
        return id switch
        {
            "cpu-min" or "cpu-max" or "core-park-min" or "core-park-max" or "core-park" or "perf-inc-threshold" or "perf-dec-threshold" or "latency-unpark" or "latency-perf" or "idle-promote" or "idle-demote" => value + " %",
            "boost" => value == 4 ? "Efficient Aggressive" : value == 2 ? "Aggressive" : "Hodnota " + value,
            "epp" or "latency-epp" or "epp-zero" => value.ToString(),
            "cppc-autonomous" => value == 1 ? "Enabled" : "Disabled",
            "perf-inc-policy" => value == 2 ? "Rocket" : value == 0 ? "Ideal" : value == 1 ? "Single" : "Policy " + value,
            "perf-dec-policy" => value == 0 ? "Ideal" : value == 2 ? "Rocket" : value == 1 ? "Single" : "Policy " + value,
            "perf-check" => value + " ms",
            "core-park-inc-policy" => value == 2 ? "All possible cores" : "Policy " + value,
            "core-park-dec-policy" => value == 0 ? "Ideal cores" : "Policy " + value,
            "core-park-inc-time" or "core-park-dec-time" => value + " interval",
            "duty-cycling" => value == 0 ? "Disabled" : "Enabled",
            "cpu-idle" => value == 0 ? "Enabled" : "Disabled",
            "cooling" => value == 1 ? "Active" : "Passive",
            "aspm" => value == 0 ? "Off" : "Power saving",
            "disk" or "display" or "sleep" or "hibernate" => value == 0 ? "Never" : value + " s",
            "wake-timers" => value == 2 ? "Important only" : value == 1 ? "Enabled" : "Disabled",
            "hybrid-sleep" or "away-mode" => value == 0 ? "Off" : "On",
            "usb" => value == 0 ? "Disabled" : "Enabled",
            "wifi" => value == 0 ? "Maximum Performance" : "Power saving " + value,
            "video-bias" => value == 1 ? "Performance" : "Power saving",
            "video-mode" => value == 0 ? "Optimize quality" : value == 1 ? "Balanced" : "Power saving",
            "boost-aggressive" => value == 2 ? "Aggressive" : value == 4 ? "Efficient Aggressive" : "Hodnota " + value,
            _ => value.ToString()
        };
    }
}
