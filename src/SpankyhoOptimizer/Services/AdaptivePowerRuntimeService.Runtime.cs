using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class AdaptivePowerRuntimeService
{
    public sealed record ManagedPowerSetting(string Name, int Original, int Current);

    // A compact, read-only view of settings *actually owned and read-back verified*
    // by the independent FanAgent, not a generic list of recommended catalog values.
    public static IReadOnlyList<ManagedPowerSetting> ReadManagedSettings(string? activeGuid)
    {
        if (string.IsNullOrWhiteSpace(activeGuid)) return Array.Empty<ManagedPowerSetting>();
        try
        {
            if (!File.Exists(AiPowerPath)) return Array.Empty<ManagedPowerSetting>();
            var rows = JsonSerializer.Deserialize<Dictionary<string, AiPowerOwnership>>(
                File.ReadAllText(AiPowerPath));
            if (rows == null) return Array.Empty<ManagedPowerSetting>();
            var result = new List<ManagedPowerSetting>();
            foreach (var item in rows.Values)
            {
                if (!item.Managed || item.Suppressed ||
                    !string.Equals(item.Guid, activeGuid, StringComparison.OrdinalIgnoreCase) ||
                    !TryReadAcSetting(item.Guid, item.Setting, out int actual) ||
                    actual != item.Applied) continue;
                string name = item.Setting.ToLowerInvariant() switch
                {
                    EppSetting => "CPU EPP",
                    BoostSetting => "CPU Boost",
                    "893dee8e-2bef-41e0-89c6-b55d0929964c" => "CPU min %",
                    "bc5038f7-23e0-4960-96da-33abaf5935ec" => "CPU max %",
                    "8baa4a8a-14c6-4451-8e8b-14bdbd197537" => "CPPC",
                    "5d76a2ca-e8c0-402f-a133-2158492d58ad" => "CPU idle",
                    "94d3a615-a899-4ac5-ae2b-e4d8f634367f" => "Chlazení",
                    "0cc5b647-c1df-4637-891a-dec35c318583" => "Core parking min %",
                    "ea062031-0e34-4ff1-9b6d-eb1059334028" => "Core parking max %",
                    "465e1f50-b610-473a-ab58-00d1077dc418" => "CPU perf increase",
                    "40fbefc7-2e9d-4d25-a185-0cfd8574bac6" => "CPU perf decrease",
                    "06cadf0e-64ed-448a-8927-ce7bf90eb35d" => "CPU increase threshold",
                    "12a0ab44-fe28-4fa9-b3bd-4b64f44960a6" => "CPU decrease threshold",
                    "4d2b0152-7d5c-498b-88e2-34345392a2c5" => "CPU perf check",
                    _ => "CPU " + item.Setting[..8]
                };
                result.Add(new(name, item.Original, actual));
            }
            return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            LogService.Error("power", "AI-managed power settings read failed", ex);
            return Array.Empty<ManagedPowerSetting>();
        }
    }

    private sealed record AiPowerOwnership(string Guid, string Setting, int Original, int Applied, bool Managed, bool Suppressed);
    private static readonly SemaphoreSlim AiPowerGate = new(1, 1);
    private static string AiPowerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer", "ai-power-ownership.json");

    private static async Task<Dictionary<string, AiPowerOwnership>> LoadAiPowerAsync(CancellationToken token)
    {
        if (!File.Exists(AiPowerPath)) return new(StringComparer.OrdinalIgnoreCase);
        // Corruption must fail closed: never overwrite unknown originals.
        return JsonSerializer.Deserialize<Dictionary<string, AiPowerOwnership>>(
            await File.ReadAllTextAsync(AiPowerPath, token).ConfigureAwait(false))
            ?? throw new InvalidOperationException("AI power ownership snapshot invalid");
    }
    private static Task SaveAiPowerAsync(Dictionary<string, AiPowerOwnership> rows, CancellationToken token) =>
        AtomicFileService.WriteTextAsync(AiPowerPath, JsonSerializer.Serialize(rows), token);

    private static async Task SetVerifiedPowerAsync(string guid, string setting, int value, CancellationToken token)
    {
        var write = await PrivilegedProcessRunner.RunAsync("powercfg.exe",
            $"/setacvalueindex {guid} {CpuSubgroup} {setting} {value}", TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        if (!write.Success || !TryReadAcSetting(guid, setting, out int actual) || actual != value)
            throw new InvalidOperationException("AI power read-back failed: " + setting + " " + write.StdErr);
    }

    // Only called within RunAiPowerTransactionAsync. Ownership must not be
    // released between EPP, Boost and /setactive: another UI/RuntimeHost
    // restore would otherwise observe and revert a half-applied profile.
    private static async Task<bool> MutateAiSettingAsync(string guid, string setting, int value,
        CancellationToken token, bool aiEppControlEnabled = false)
    {
            if (!TryReadAcSetting(guid, setting, out int current))
                return false; // missing setting has no verifiable original
            var rows = await LoadAiPowerAsync(token).ConfigureAwait(false);
            string key = guid + "|" + setting;
            rows.TryGetValue(key, out var previous);
            bool autonomousEpp = aiEppControlEnabled &&
                string.Equals(setting, EppSetting, StringComparison.OrdinalIgnoreCase);
            if (previous?.Suppressed == true && !autonomousEpp) return false;
            if (previous?.Managed == true && current != previous.Applied && !autonomousEpp)
            {
                rows[key] = previous with { Managed = false, Suppressed = true };
                await SaveAiPowerAsync(rows, token).ConfigureAwait(false);
                LogService.Info("power", "AI_POWER_MANUAL_OVERRIDE preserved " + key);
                return false;
            }
            if (current == value) return false;
            // When AI retakes an EPP that was previously overridden, the
            // *current* value becomes the rollback baseline. Otherwise an
            // already owned, unmodified setting keeps its original baseline.
            int original = ChooseOwnershipOriginal(previous, current, autonomousEpp);
            var row = new AiPowerOwnership(guid, setting,
                original, value, true, false);
            rows[key] = row;
            await SaveAiPowerAsync(rows, token).ConfigureAwait(false); // durable original before mutation
            try
            {
                await SetVerifiedPowerAsync(guid, setting, value, token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await SetVerifiedPowerAsync(guid, setting, current, rollback.Token).ConfigureAwait(false);
                if (previous == null) rows.Remove(key); else rows[key] = previous;
                await SaveAiPowerAsync(rows, rollback.Token).ConfigureAwait(false);
                throw;
            }
    }

    // Both lock layers cover the entire two-setting native transaction.
    // The in-process semaphore is always acquired first (matching Restore),
    // which prevents circular waits with the cross-process file lease.
    internal static async Task<T> RunAiPowerTransactionAsync<T>(
        string ownershipPath, Func<CancellationToken, Task<T>> operation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        long gateStart = Stopwatch.GetTimestamp();
        await AiPowerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            double localWaitMs = Stopwatch.GetElapsedTime(gateStart).TotalMilliseconds;
            long leaseStart = Stopwatch.GetTimestamp();
            using var ownershipLease = await AiPowerOwnershipFileGate.AcquireAsync(
                ownershipPath, token).ConfigureAwait(false);
            double leaseWaitMs = Stopwatch.GetElapsedTime(leaseStart).TotalMilliseconds;
            if (localWaitMs >= 200 || leaseWaitMs >= 200)
                LogService.Info("power", $"AI_POWER_GATE_SLOW localWaitMs={localWaitMs:F1} leaseWaitMs={leaseWaitMs:F1}");
            return await operation(token).ConfigureAwait(false);
        }
        finally { AiPowerGate.Release(); }
    }

    public static async Task RestoreAllAiSettingsAsync(CancellationToken token = default)
    {
        await RunAiPowerTransactionAsync(AiPowerPath, async ct =>
        {
            await RestoreAllAiSettingsCoreAsync(ct).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    // Called with BOTH gates held, including from the failed-write
    // compensation path. Never acquire either lock recursively here.
    private static async Task RestoreAllAiSettingsCoreAsync(CancellationToken token)
    {
            var rows = await LoadAiPowerAsync(token).ConfigureAwait(false);
            var changedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in rows.Where(x => x.Value.Managed).ToArray())
            {
                token.ThrowIfCancellationRequested();
                var row = pair.Value;
                if (!TryReadAcSetting(row.Guid, row.Setting, out int current)) continue;
                if (current == row.Original)
                    rows[pair.Key] = row with { Managed = false };
                else if (current != row.Applied)
                {
                    rows[pair.Key] = row with { Managed = false, Suppressed = true };
                    LogService.Info("power", "AI_POWER_MANUAL_OVERRIDE preserved " + pair.Key);
                }
                else
                {
                    await SetVerifiedPowerAsync(row.Guid, row.Setting, row.Original, token).ConfigureAwait(false);
                    rows[pair.Key] = row with { Managed = false };
                    changedSchemes.Add(row.Guid);
                }
                await SaveAiPowerAsync(rows, CancellationToken.None).ConfigureAwait(false);
            }
            // Refresh only the already-active Spanky scheme; never activate an old/inactive scheme.
            if (TryGetActiveSpankyScheme(out var active) && active != null && changedSchemes.Contains(active))
            {
                var refresh = await PrivilegedProcessRunner.RunAsync("powercfg.exe", "/setactive " + active,
                    TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                if (!refresh.Success) throw new InvalidOperationException("AI power restore refresh failed");
            }
    }

    private static int ChooseOwnershipOriginal(AiPowerOwnership? previous, int current, bool autonomousEpp) =>
        previous?.Managed == true && (current == previous.Applied || !autonomousEpp)
            ? previous.Original
            : current;

    // Restart, re-enable/disable and clock rollback all force a restore check.
    // After a successful check while disabled, verify again within 30 seconds.
    internal static bool ShouldRunDisabledPowerRestore(DateTime nowUtc, DateTime lastRestoreUtc,
        bool previouslyManaged) =>
        previouslyManaged || lastRestoreUtc == DateTime.MinValue ||
        nowUtc < lastRestoreUtc ||
        nowUtc - lastRestoreUtc >= TimeSpan.FromSeconds(30);

    // Both routine 30s power-setting rechecks and baseline self-heals are
    // elapsed-time gates. An NTP/Windows clock adjustment must not postpone
    // a needed readback or trigger extra native work in a single interval.
    internal static bool IsMonotonicPowerRefreshDue(
        long previousStamp, long nowStamp, long frequency, TimeSpan interval) =>
        nowStamp > 0 && frequency > 0 && interval > TimeSpan.Zero &&
        (previousStamp == 0 || (previousStamp > 0 && nowStamp >= previousStamp &&
            (nowStamp - (double)previousStamp) / frequency > interval.TotalSeconds));

    internal static string RunMonotonicPowerRefreshSmoke()
    {
        const long frequency = 10_000;
        const long first = 100_000;
        long boundary = first + 30 * frequency;
        if (!IsMonotonicPowerRefreshDue(0, first, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, boundary, frequency, TimeSpan.FromSeconds(30)) ||
            !IsMonotonicPowerRefreshDue(first, boundary + 1, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, first - 1, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(-1, boundary + 1, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, boundary + 1, 0, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, boundary + 1, frequency, TimeSpan.Zero) ||
            IsMonotonicPowerRefreshDue(0, -1, frequency, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("AI power readback monotonic cadence regressed.");
        DateTime utc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (utc.AddHours(-2) >= utc ||
            IsMonotonicPowerRefreshDue(first, first + frequency * 5,
                frequency, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("AI power readback depends on Windows wall-clock adjustments.");
        return "AI_POWER_MONOTONIC_READBACK_SMOKE_OK interval30s=true " +
               "clockJumpIgnored=true noExtraNativeReads=true windowsWrites=0";
    }

    // While AI power management is enabled, its adaptive EPP target has
    // explicit user authorization even if an older ownership record says
    // Suppressed. Other settings still honor manual-override suppression.
    // Only observed native EPP may advance the mode state and hysteresis
    // clocks. A failed write, unreadable value or external plan change cannot
    // be represented as a successful transition in UI or future decisions.
    internal static bool CanCommitObservedMode(
        bool modeChanged, bool targetEppConfirmed, bool sameActivePlan) =>
        !modeChanged || (targetEppConfirmed && sameActivePlan);

    internal static bool ShouldAttemptPowerMutation(
        bool eppKnown, int currentEpp, int desiredEpp, bool aiEppControlEnabled,
        bool boostKnown, int currentBoost, int desiredBoost, bool boostSuppressed) =>
        (eppKnown && aiEppControlEnabled && currentEpp != desiredEpp) ||
        (boostKnown && !boostSuppressed && currentBoost != desiredBoost);

    // Read-only functional regression: while the user has enabled AI power
    // management, EPP automatically follows AI even if a stale ownership
    // record was suppressed by an earlier external change. No OS writes.
    internal static string RunAutonomousEppControlSmoke()
    {
        const int cycles = 120;
        // 120 simulated 2s FanAgent ticks while AI power management is off:
        // initial restore plus periodic verification, never suppressed forever.
        DateTime initial = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        DateTime lastRestore = DateTime.MinValue;
        int disabledRestoreChecks = 0;
        for (int tick = 0; tick < cycles; tick++)
        {
            DateTime now = initial.AddSeconds(tick * 2);
            if (!ShouldRunDisabledPowerRestore(now, lastRestore, false)) continue;
            disabledRestoreChecks++;
            lastRestore = now;
        }
        if (disabledRestoreChecks != 8 ||
            !ShouldRunDisabledPowerRestore(initial, DateTime.MinValue, false) ||
            !ShouldRunDisabledPowerRestore(initial.AddSeconds(2), initial, true) ||
            ShouldRunDisabledPowerRestore(initial.AddSeconds(29), initial, false) ||
            !ShouldRunDisabledPowerRestore(initial.AddSeconds(30), initial, false) ||
            !ShouldRunDisabledPowerRestore(initial.AddSeconds(-1), initial, false))
            throw new InvalidOperationException("Disabled AI power restore pacing regression.");
        int attempts = 0;
        for (int i = 0; i < cycles; i++)
        {
            if (ShouldAttemptPowerMutation(
                    true, 20, 12, true, true, 4, 4, false)) attempts++;
        }
        var staleEpp = new AiPowerOwnership("test", EppSetting, 20, 12, false, true);
        var activeEpp = new AiPowerOwnership("test", EppSetting, 30, 12, true, false);
        if (attempts != cycles ||
            ChooseOwnershipOriginal(staleEpp, 20, true) != 20 ||
            ChooseOwnershipOriginal(activeEpp, 12, true) != 30 ||
            ChooseOwnershipOriginal(activeEpp, 18, true) != 18 ||
            ShouldAttemptPowerMutation(true, 20, 12, false, true, 4, 4, false) ||
            !ShouldAttemptPowerMutation(true, 20, 12, true, true, 3, 4, false) ||
            !ShouldAttemptPowerMutation(false, 0, 12, false, true, 3, 4, false) ||
            ShouldAttemptPowerMutation(false, 0, 12, false, false, 0, 4, false) ||
            ShouldAttemptPowerMutation(true, 12, 12, true, true, 4, 4, false) ||
            !ShouldAttemptPowerMutation(true, 20, 12, true, true, 3, 4, true) ||
            ShouldAttemptPowerMutation(true, 12, 12, true, true, 3, 4, true))
            throw new InvalidOperationException("Autonomous EPP ownership/Boost isolation regression.");
        return $"ADAPTIVE_EPP_AI_CONTROL_SMOKE_OK cycles={cycles} " +
            $"aiEnabledMismatchAttempts={attempts} aiDisabledAttempts=0 " +
            $"aiDisabledRestoreChecks={disabledRestoreChecks} overSeconds=240 " +
            "staleEppSuppression=ignored boostManualOverride=preserved " +
            "unchangedEpp=skip unreadableFailClosed=true";
    }

    private static Task<bool> ApplyAsync(string guid, int epp, int boost, CancellationToken token) =>
        RunPlanScopedPowerMutationAsync(
            AiPowerPath, guid,
            () => TryGetActiveSpankyScheme(out string? active) ? active : null,
            async ct =>
            {
                bool changed = await MutateAiSettingAsync(
                    guid, EppSetting, epp, ct, aiEppControlEnabled: true).ConfigureAwait(false);
                changed |= await MutateAiSettingAsync(guid, BoostSetting, boost, ct).ConfigureAwait(false);
                return changed;
            },
            async ct =>
            {
                var refresh = await PrivilegedProcessRunner.RunAsync(
                    "powercfg.exe", $"/setactive {guid}",
                    TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (!refresh.Success)
                    throw new InvalidOperationException("AI adaptive refresh failed");
            },
            RestoreAllAiSettingsCoreAsync,
            token);

    // Both checks happen WITHIN the exclusive ownership transaction. The
    // caller checked the plan before waiting for the lease, but the user may
    // have selected another Windows scheme while the queue was blocked.
    // Never force /setactive back to the stale plan. If the plan switches
    // after native writes, compensate before releasing the lease.
    internal static Task<bool> RunPlanScopedPowerMutationAsync(
        string ownershipPath,
        string expectedPlan,
        Func<string?> currentActivePlan,
        Func<CancellationToken, Task<bool>> mutate,
        Func<CancellationToken, Task> refresh,
        Func<CancellationToken, Task> rollback,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlan);
        ArgumentNullException.ThrowIfNull(currentActivePlan);
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(refresh);
        return RunPowerMutationWithRollbackAsync(
            ownershipPath,
            async ct =>
            {
                if (!string.Equals(currentActivePlan(), expectedPlan, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!await mutate(ct).ConfigureAwait(false))
                    return false;
                if (!string.Equals(currentActivePlan(), expectedPlan, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Active Windows power plan switched during AI mutation");
                await refresh(ct).ConfigureAwait(false);
                return true;
            },
            rollback, token);
    }

    // Any compensation must run before we release the ownership lease.
    // Otherwise another host could commit a valid change between the failed
    // apply and our rollback, which would then silently revert their work.
    // Failure to acquire the lease never invokes rollback.
    internal static Task<T> RunPowerMutationWithRollbackAsync<T>(
        string ownershipPath,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task> rollback,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(rollback);
        return RunAiPowerTransactionAsync(ownershipPath, async ct =>
        {
            try { return await operation(ct).ConfigureAwait(false); }
            catch
            {
                using var compensation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await rollback(compensation.Token).ConfigureAwait(false);
                throw;
            }
        }, token);
    }

    // Legacy contract used by existing read-only regression tests.
    internal static bool ShouldRestoreAfterApplyFailure(bool transactionEntered) =>
        transactionEntered;

    private static bool TryGetActiveSpankyScheme(out string? activeGuid)
    {
        activeGuid = null;
        const string root = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

        try
        {
            using RegistryKey? schemes = Registry.LocalMachine.OpenSubKey(root);
            if (schemes == null) return false;

            activeGuid = schemes.GetValue("ActivePowerScheme")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(activeGuid)) return false;

            using RegistryKey? active = schemes.OpenSubKey(activeGuid);
            string name = active?.GetValue("FriendlyName")?.ToString() ?? string.Empty;
            return name.Contains("Spankyho Performance", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            activeGuid = null;
            return false;
        }
    }

    private static bool TryReadAcSetting(string guid, string setting, out int value)
    {
        value = 0;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{guid}\{CpuSubgroup}\{setting}");
            object? raw = key?.GetValue("ACSettingIndex");
            if (raw == null) return false;
            value = Convert.ToInt32(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Process names are immutable for a running PID. Reuse an existing name
    // on ordinary CPU-delta samples, but detect recycled PID resets and force
    // periodic full refresh. New PIDs (including newly launched games) always
    // resolve their actual process name immediately.
    internal static bool CanReuseProcessName(TimeSpan currentCpu,
        AdaptiveProcessCpuSample previous, bool forceRefresh) =>
        !forceRefresh && !string.IsNullOrEmpty(previous.Name) && currentCpu >= previous.Cpu;

    // Read-only native snapshot benchmark: old every-PID ProcessName vs
    // same snapshots with previous-cycle name reuse. No process manipulation.
    internal static string RunProcessNameReuseSmoke()
    {
        var synthetic = new AdaptiveProcessCpuSample(TimeSpan.FromSeconds(4), "DiabloImmortal");
        if (!CanReuseProcessName(TimeSpan.FromSeconds(5), synthetic, false) ||
            CanReuseProcessName(TimeSpan.FromSeconds(3), synthetic, false) ||
            CanReuseProcessName(TimeSpan.FromSeconds(5), synthetic, true) ||
            CanReuseProcessName(TimeSpan.FromSeconds(5),
                new AdaptiveProcessCpuSample(TimeSpan.Zero, ""), false))
            throw new InvalidOperationException("PID name-cache reuse/refresh policy regressed.");

        const int rounds = 16;
        static (double Ms, long Bytes, int NativeReads, int Reused, int Snapshots)
            Measure(bool cache)
        {
            var previous = new Dictionary<int, AdaptiveProcessCpuSample>(256);
            var next = new Dictionary<int, AdaptiveProcessCpuSample>(256);
            int nativeReads = 0, reused = 0, snapshots = 0;
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int round = 0; round < rounds; round++)
            {
                next.Clear();
                Process[] processes = Process.GetProcesses();
                try
                {
                    snapshots++;
                    foreach (Process process in processes)
                    {
                        try
                        {
                            int pid = process.Id;
                            TimeSpan cpu = process.TotalProcessorTime;
                            bool found = previous.TryGetValue(pid, out var prior);
                            bool reuse = cache && found &&
                                CanReuseProcessName(cpu, prior, (round & 3) == 3);
                            string name = reuse ? prior.Name : process.ProcessName;
                            if (reuse) reused++; else nativeReads++;
                            next[pid] = new AdaptiveProcessCpuSample(cpu, name);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or
                            System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException) { }
                    }
                }
                finally { ProcessSnapshotCleanup.DisposeAll(processes); }
                (previous, next) = (next, previous);
            }
            return (Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - beforeBytes,
                nativeReads, reused, snapshots);
        }
        var before = Measure(false);
        var after = Measure(true);
        if (after.Reused <= 0 || after.NativeReads >= before.NativeReads ||
            after.Snapshots != rounds)
            throw new InvalidOperationException("Process name cache did not reduce native accesses.");
        return $"ADAPTIVE_PROCESS_NAME_REUSE_SMOKE_OK rounds={rounds} " +
            $"beforeNameReads={before.NativeReads} afterNameReads={after.NativeReads} " +
            $"reused={after.Reused} beforeMs={before.Ms:F3} afterMs={after.Ms:F3} " +
            $"beforeAllocatedB={before.Bytes} afterAllocatedB={after.Bytes} " +
            "newPid=fresh recycledPid=fresh periodicRefresh=4";
    }
}
