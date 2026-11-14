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

public static partial class PerformanceProfilesService
{
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
