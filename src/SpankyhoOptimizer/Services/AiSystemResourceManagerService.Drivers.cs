using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class AiSystemResourceManagerService
{
    private void EvaluateSafeDrivers(
        AiSystemRuntimeConfig config, bool quietEnough,
        AdaptivePowerRuntimeStatus? candidatePowerStatus)
    {
        int driverStart = _driverCandidateCursor;
        _driverCandidateCursor = AiMaintenanceCandidatePolicy.NextCursor(
            driverStart, DriverAllowlist.Length);
        for (int offset = 0; offset < DriverAllowlist.Length; offset++)
        {
            int index = AiMaintenanceCandidatePolicy.IndexAt(
                driverStart, offset, DriverAllowlist.Length);
            string name = DriverAllowlist[index];
            // A driver absent from the current Services registry is not a
            // valid NEW experiment. Keep any old owned/pending journal item
            // on its original recovery path instead of deleting history.
            if (!WindowsComponentCatalogPolicy.DriverRegistered(name) &&
                (!_items.TryGetValue(DriverAllowlistKeys[index], out var tracked) ||
                 (!tracked.Managed && !tracked.Sleeping &&
                  string.IsNullOrEmpty(tracked.PendingMutation))))
                continue;
            AiSystemRuntimeItemState state;
            if (!quietEnough)
            {
                // No durable owner/observation: don't fill the journal
                // with dummy driver rows while user activity forbids trials.
                if (!_items.TryGetValue(DriverAllowlistKeys[index], out state))
                    continue;
            }
            else
                state = GetState("driver", name);
            if (AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
            {
                _recoverInterruptedTrials = true;
                SetBlocked("driver", name,
                    "pending journal without confirmed AI ownership; no Windows write");
                continue;
            }
            if (AiMaintenanceCandidatePolicy.ShouldDeferForCooldown(state, DateTime.UtcNow))
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
            {
                SetBlocked("driver", name, "AI learned protection • opakovaně obnoveno systémem");
                continue;
            }

            // During foreground work the AI can neither start nor continue
            // unowned driver-disabling observations. Avoid a native registry
            // + Enum probe for every unowned candidate. Managed drivers still
            // ALWAYS reach the live readback/restore branches below.
            if (!AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
                    quietEnough, state.Managed, state.Sleeping,
                    !string.IsNullOrEmpty(state.PendingMutation)))
            {
                AiSystemRuntimeItemState suspended =
                    AiMaintenanceSchedulingPolicy.SuspendUnownedDriverObservation(state);
                if (!ReferenceEquals(state, suspended))
                    _items[Key("driver", name)] = suspended;
                continue;
            }

            try
            {
                DriverRegistrySnapshot driver = ReadDriverRegistrySnapshot(name);
                if (!driver.Installed)
                {
                    SetBlocked("driver", name, "not installed");
                    continue;
                }
                if (!driver.WindowsOwned)
                {
                    // A tightened image-path policy must not erase older
                    // durable AI ownership. Block new writes and flag owned
                    // disabled drivers for manual recovery when untrusted.
                    SetBlocked("driver", name, state.Managed && state.Sleeping
                        ? "cesta spravovaného ovladače se změnila; ruční obnova nutná, AI záznam zachován"
                        : "není potvrzený Windows/System32 driver");
                    continue;
                }

                if (!AiDriverAutomationPolicy.IsEligibleDriverType(driver.Type))
                {
                    SetBlocked("driver", name, "jen volitelné kernel ovladače (Type=1)");
                    continue;
                }

                int start = driver.Start;
                if (state.Managed && state.Sleeping &&
                    AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(start))
                {
                    SetBlocked("driver", name,
                        "driver Start unreadable; retain original AI disable ownership");
                    continue;
                }

                if (state.Managed && state.Sleeping && start == 4 &&
                    !driver.AttachedDevice &&
                    AiDriverAutomationPolicy.ShouldRelease(state, DateTime.UtcNow))
                {
                    if (state.OriginalServiceStart is int original && original == 3)
                    {
                        DriverRegistrySnapshot freshRestore = ReadDriverRegistrySnapshot(name);
                        if (!CanRestoreDriverProvenance(
                                freshRestore.Installed, freshRestore.WindowsOwned,
                                freshRestore.Type, freshRestore.Start, start))
                        {
                            SetBlocked("driver", name,
                                "obnova po expiraci blokována: identita ovladače se změnila; AI záznam zachován");
                            continue;
                        }
                        WriteServiceStartType(name, original);
                        int confirmedDriverStart = ReadServiceStartType(name);
                        if (AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                                original, confirmedDriverStart) is string restoreFailure)
                            throw new InvalidOperationException(
                                "AI driver hold-expiry readback: " + restoreFailure);
                        _items[Key("driver", name)] = AiMaintenanceOwnershipPolicy.Relinquish(
                            state, DateTime.UtcNow, "driver hold expired; original demand start restored",
                            TimeSpan.FromHours(12));
                        _restoredCount++;
                        AppendAction("driver", name, "restored", "maximum AI hold expired");
                    }
                    else
                        SetBlocked("driver", name, "missing safe original start value; manual recovery required");
                    continue;
                }

                if (state.Managed && state.Sleeping && start == 4 && driver.AttachedDevice)
                {
                    // The durable original must be known. A corrupt/legacy
                    // journal must not cause AI to invent a start value.
                    if (!AiDriverAutomationPolicy.CanRestoreAttachedDevice(state, start, true))
                    {
                        SetBlocked("driver", name,
                            "device attached but original driver start mode unknown; manual recovery required");
                        continue;
                    }
                    // A newly attached device must not override a driver whose
                    // ImagePath/Type was changed after this tick's first read.
                    DriverRegistrySnapshot freshRestore = ReadDriverRegistrySnapshot(name);
                    if (!CanRestoreDriverProvenance(
                            freshRestore.Installed, freshRestore.WindowsOwned,
                            freshRestore.Type, freshRestore.Start, start))
                    {
                        SetBlocked("driver", name,
                            "obnova připojeného ovladače blokována: identita se změnila; ruční kontrola nutná");
                        continue;
                    }
                    int restoreStart = state.OriginalServiceStart!.Value;
                    WriteServiceStartType(name, restoreStart);
                    int confirmedDriverStart = ReadServiceStartType(name);
                    if (AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                            restoreStart, confirmedDriverStart) is string restoreFailure)
                        throw new InvalidOperationException(
                            "AI reattached driver restore readback: " + restoreFailure);
                    DateTime restoredAt = DateTime.UtcNow;
                    int learned = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                        state.Rollbacks, externallyNeeded: true);
                    _items[Key("driver", name)] =
                        AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                            AiMaintenanceOwnershipPolicy.Relinquish(
                                state, restoredAt,
                                "device attached again; AI restored driver",
                                TimeSpan.FromHours(24)),
                            externallyNeeded: true) with
                        {
                            Rollbacks = learned,
                            LastAction = "restored because device became attached"
                        };
                    _restoredCount++;
                    _lastAction = $"Driver {name}: attached device detected -> restored";
                    AppendAction("driver", name, "restored", "device attached after AI disable");
                    LogService.Info("ai-system",
                        $"AI_SYSTEM_DRIVER_RESTORE name={name} reason=device_attached start={restoreStart}");
                    continue;
                }

                if (state.Managed && state.Sleeping && start != 4)
                {
                    int rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                        state.Rollbacks, externallyNeeded: true);
                    DateTime relinquishedAt = DateTime.UtcNow;
                    TimeSpan externalCooldown = rollbacks >= 2
                        ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(30);
                    _items[Key("driver", name)] =
                        AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                            AiMaintenanceOwnershipPolicy.Relinquish(
                                state, relinquishedAt,
                                rollbacks >= 2
                                    ? "opakovaně obnoveno systémem / pravděpodobně potřebné"
                                    : "systém driver znovu povolil; AI předala řízení",
                                externalCooldown),
                            externallyNeeded: true) with
                        {
                            Rollbacks = rollbacks,
                            LastAction = "externally restored / assumed needed"
                        };
                    _restoredCount++;
                    _lastAction = $"Driver {name}: needed again";
                    AppendAction("driver", name, "restored externally", rollbacks >= 2
                        ? "opakovaně obnoveno systémem"
                        : "systém driver znovu povolil");
                    continue;
                }

                if (!quietEnough || start == 4)
                {
                    ResetObservation("driver", name);
                    continue;
                }

                if (!AiDriverAutomationPolicy.IsEligibleStartType(start))
                {
                    SetBlocked("driver", name, AiDriverAutomationPolicy.IneligibleReason(start));
                    continue;
                }

                if (driver.AttachedDevice)
                {
                    SetBlocked("driver", name, "připojené zařízení / driver je aktivně používaný");
                    continue;
                }

                DateTime observationNow = DateTime.UtcNow;
                DateTime observed = AiDriverAutomationPolicy.ObservationStart(
                    state.ObservationSinceUtc, state.DriverObservationLastUtc, observationNow);
                state = state with
                {
                    ObservationSinceUtc = observed,
                    DriverObservationLastUtc = observationNow,
                    BlockedReason = string.Empty
                };
                _items[Key("driver", name)] = state;
                if (DateTime.UtcNow - observed < AiDriverAutomationPolicy.RequiredObservation(config.ObservationSeconds))
                    continue;
                if (_maintenanceMutationCommitted)
                    continue;

                // The long observation is only a preliminary candidate. Read
                // driver registry/device occupancy again at the write boundary;
                // never disable after a device arrives or Windows changes Start.
                DriverRegistrySnapshot latest = ReadDriverRegistrySnapshot(name);
                DateTime trialNow = DateTime.UtcNow;
                AiSystemSmoothnessHealth? freshHealth =
                    AiSystemSmoothnessGuardService.ReadAgentSnapshot(trialNow);
                bool freshQuiet = AiMaintenanceBoundaryEvidencePolicy.CanStartNewTrial(
                    candidatePowerStatus, trialNow,
                    AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(
                        freshHealth, trialNow), _recoverInterruptedTrials);
                if (AiDriverAutomationPolicy.MutationBlockReason(
                        latest.Installed, latest.WindowsOwned, latest.Type,
                        latest.Start, latest.AttachedDevice, freshQuiet)
                    is string mutationBlock)
                {
                    SetBlocked("driver", name, "write boundary: " + mutationBlock);
                    continue;
                }

                // Registry Enum Count alone cannot prove a demand-start
                // driver is inactive. Read the live Service Control Manager
                // driver state before journaling any next-boot disable.
                if (AiKernelDriverRuntimeProbe.BlockReason(name)
                    is string liveDriverBlock)
                {
                    SetBlocked("driver", name, "write boundary: " + liveDriverBlock);
                    continue;
                }

                AiSystemRuntimeItemState intent = state with
                {
                    Managed = true,
                    Sleeping = true,
                    OriginalServiceStart = start,
                    PendingMutation = AiMaintenanceWriteAheadPolicy.DriverDisable,
                    LastChangedUtc = DateTime.UtcNow
                };
                BeginWriteAheadMutation(state, intent);

                WriteServiceStartType(name, 4);
                int disabledDriverStart = ReadServiceStartType(name);
                if (AiMaintenanceWriteAheadPolicy.DriverDisableReadbackReason(
                        disabledDriverStart) is string disableFailure)
                    throw new InvalidOperationException(
                        "AI driver disable readback: " + disableFailure);

                AiSystemRuntimeItemState completed = intent with
                {
                    Managed = true,
                    Sleeping = true,
                    OriginalServiceStart = start,
                    ObservationSinceUtc = null,
                    DriverObservationLastUtc = null,
                    CooldownUntilUtc = DateTime.UtcNow.AddSeconds(config.ActionCooldownSeconds),
                    LastAction = "disabled for next boot after long idle observation",
                    LastChangedUtc = DateTime.UtcNow,
                    PerformanceVerified = false
                };
                CommitWriteAheadMutation(intent, completed);
                _maintenanceMutationCommitted = true;
                _lastAction = $"Driver {name}: optional demand-start driver disabled for next boot";
                AppendAction("driver", name, "disabled",
                    $"long idle observation; no attached device; demand-start only; originalStart={start}; effect next boot");
                LogService.Info("ai-system",
                    $"AI_SYSTEM_DRIVER_DISABLE name={name} originalStart={start} measuredPerformance=false nextBoot=true");
                return;
            }
            catch (Exception ex)
            {
                if (AiMaintenanceWriteAheadPolicy.IsPending(GetState("driver", name)))
                    _recoverInterruptedTrials = true;
                SetBlocked("driver", name, ex.GetBaseException().Message);
            }
        }
    }

    // Runtime polling, cloud backoff and read-only discovery cadence use
    // elapsed monotonic time. A Windows/NTP clock correction must not cause
    // a burst of extra scans or premature cloud sync uploads.
    internal static bool IsWithinMonotonicInterval(
        long lastStamp, long nowStamp, long frequency, TimeSpan interval) =>
        lastStamp > 0 && nowStamp >= lastStamp && frequency > 0 &&
        interval > TimeSpan.Zero &&
        (nowStamp - (double)lastStamp) / frequency < interval.TotalSeconds;

    internal static bool ShouldRunMonotonicDeepScan(
        ref long lastStamp, long nowStamp, long frequency, TimeSpan interval)
    {
        // Invalid monotonic evidence must not cause repeated expensive
        // service/task/driver discovery on every RuntimeHost tick.
        if (nowStamp <= 0 || frequency <= 0 || interval <= TimeSpan.Zero)
            return false;
        if (lastStamp < 0 || lastStamp > nowStamp)
        {
            // A corrupt or discontinuous counter is not elapsed time.
            // Rebaseline and defer until a full real interval passes.
            lastStamp = nowStamp;
            return false;
        }
        if (IsWithinMonotonicInterval(lastStamp, nowStamp, frequency, interval))
            return false;
        lastStamp = nowStamp;
        return true;
    }

    internal static string RunMonotonicRuntimeCadenceSmoke()
    {
        const long freq = 10_000;
        const long t0 = 100_000;
        long fiveSeconds = t0 + 5 * freq;
        long fifteenMinutes = t0 + 15 * 60 * freq;
        if (!IsWithinMonotonicInterval(t0, fiveSeconds - 1, freq,
                TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(t0, fiveSeconds, freq,
                TimeSpan.FromSeconds(5)) ||
            !IsWithinMonotonicInterval(t0, fifteenMinutes - 1, freq,
                TimeSpan.FromMinutes(15)) ||
            IsWithinMonotonicInterval(t0, fifteenMinutes, freq,
                TimeSpan.FromMinutes(15)) ||
            IsWithinMonotonicInterval(t0, t0 - 1, freq, TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(0, t0, freq, TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(t0, t0, 0, TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(t0, t0, freq, TimeSpan.Zero))
            throw new InvalidOperationException("AI runtime monotonic cadence bounds failed.");
        long scan = 0;
        if (!ShouldRunMonotonicDeepScan(ref scan, t0, freq,
                TimeSpan.FromMinutes(5)) || scan != t0 ||
            ShouldRunMonotonicDeepScan(ref scan, t0 + freq * 5, freq,
                TimeSpan.FromMinutes(5)) ||
            !ShouldRunMonotonicDeepScan(ref scan, t0 + freq * 301, freq,
                TimeSpan.FromMinutes(5)) || scan != t0 + freq * 301)
            throw new InvalidOperationException("AI deep-scan monotonic scheduling failed.");
        long validStamp = scan;
        if (ShouldRunMonotonicDeepScan(ref scan, 0, freq, TimeSpan.FromMinutes(5)) ||
            ShouldRunMonotonicDeepScan(ref scan, -1, freq, TimeSpan.FromMinutes(5)) ||
            ShouldRunMonotonicDeepScan(ref scan, validStamp + freq, 0, TimeSpan.FromMinutes(5)) ||
            ShouldRunMonotonicDeepScan(ref scan, validStamp + freq, freq, TimeSpan.Zero) ||
            ShouldRunMonotonicDeepScan(ref scan, validStamp + freq, freq, TimeSpan.FromSeconds(-1)) ||
            scan != validStamp)
            throw new InvalidOperationException("AI invalid scheduler clock triggered an unexpected scan.");
        long rebaseline = t0 + freq * 301;
        if (ShouldRunMonotonicDeepScan(ref rebaseline, t0, freq, TimeSpan.FromSeconds(5)) ||
            rebaseline != t0 ||
            ShouldRunMonotonicDeepScan(ref rebaseline, t0 + 5 * freq - 1, freq,
                TimeSpan.FromSeconds(5)) ||
            !ShouldRunMonotonicDeepScan(ref rebaseline, t0 + 5 * freq, freq,
                TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("AI backward counter failed to rebaseline/defer scans.");
        long corruptStamp = -200;
        if (ShouldRunMonotonicDeepScan(ref corruptStamp, t0, freq, TimeSpan.FromSeconds(5)) ||
            corruptStamp != t0)
            throw new InvalidOperationException("AI corrupted deep-scan stamp was not quarantined.");
        // A two-hour wall-clock correction does not affect elapsed stamps.
        DateTime wall = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (wall.AddHours(2) <= wall ||
            !IsWithinMonotonicInterval(t0, t0 + freq * 4, freq,
                TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("AI runtime scheduler coupled to wall clock.");
        return "AI_RUNTIME_MONOTONIC_CADENCE_SMOKE_OK tick=true deepScans=true " +
               "cloud15m=true clockJumpIgnored=true windowsWrites=0";
    }

    // A wall-clock correction can place the last tick ahead of 'now'.
    // Never defer maintenance, deep scans or cloud sync until that old wall
    // clock catches up. A zero/negative interval is immediately due.
    internal static bool IsWithinRecentInterval(
        DateTime lastUtc, DateTime nowUtc, TimeSpan interval) =>
        lastUtc != DateTime.MinValue &&
        interval > TimeSpan.Zero &&
        nowUtc >= lastUtc &&
        nowUtc - lastUtc < interval;

    // Cached observations speed up inventory screens but must never become
    // an authorization token at the point of a service mutation.
    internal static bool MayReuseProbeCache(bool requireFresh) => !requireFresh;

    // Only two boolean probe families exist today. Keep a fixed memory bound:
    // forcing a fresh read when the oldest entry is evicted is safer than
    // indefinitely retaining stale classification from removed services.
    internal static void StoreBoundedProbe(
        Dictionary<string, (long CollectedStamp, bool Value)> cache,
        string key, bool value, long stamp, int maxEntries = 256)
    {
        if (maxEntries < 1 || stamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries),
                "Monotonic probe stamp and capacity must be positive.");
        if (!cache.ContainsKey(key) && cache.Count >= maxEntries)
        {
            string? oldestKey = null;
            long oldestStamp = long.MaxValue;
            foreach (var entry in cache)
            {
                if (entry.Value.CollectedStamp < oldestStamp)
                {
                    oldestStamp = entry.Value.CollectedStamp;
                    oldestKey = entry.Key;
                }
            }
            if (oldestKey != null) cache.Remove(oldestKey);
        }
        cache[key] = (stamp, value);
    }

    internal static bool IsMonotonicProbeFresh(
        long collectedStamp, long nowStamp, long frequency, TimeSpan ttl) =>
        collectedStamp > 0 && nowStamp >= collectedStamp && frequency > 0 &&
        ttl > TimeSpan.Zero &&
        (nowStamp - (double)collectedStamp) / frequency < ttl.TotalSeconds;

    internal static string RunMonotonicProbeCacheSmoke()
    {
        const long frequency = 10_000;
        const long stored = 100_000;
        long thirtySeconds = stored + 30 * frequency;
        long tenMinutes = stored + 10 * 60 * frequency;
        if (!IsMonotonicProbeFresh(stored, stored, frequency, TimeSpan.FromSeconds(30)) ||
            !IsMonotonicProbeFresh(stored, thirtySeconds - 1, frequency,
                TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(stored, thirtySeconds, frequency,
                TimeSpan.FromSeconds(30)) ||
            !IsMonotonicProbeFresh(stored, tenMinutes - 1, frequency,
                TimeSpan.FromMinutes(10)) ||
            IsMonotonicProbeFresh(stored, tenMinutes, frequency,
                TimeSpan.FromMinutes(10)) ||
            IsMonotonicProbeFresh(stored, stored - 1, frequency,
                TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(0, stored, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(stored, stored, 0, TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(stored, stored, frequency, TimeSpan.Zero))
            throw new InvalidOperationException("AI service probe cache monotonic TTL regressed.");
        // A long-lived RuntimeHost must not accumulate unbounded service
        // identities. Evicting only the oldest invokes a fresh safety probe.
        var bounded = new Dictionary<string, (long CollectedStamp, bool Value)>(
            StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= 260; i++)
            StoreBoundedProbe(bounded, "service-" + i, i % 2 == 0, i);
        if (bounded.Count != 256 || bounded.ContainsKey("service-1") ||
            bounded.ContainsKey("service-4") || !bounded.ContainsKey("service-5") ||
            !bounded.TryGetValue("service-260", out _))
            throw new InvalidOperationException("AI service probe cache exceeded capacity or evicted a fresh entry.");
        StoreBoundedProbe(bounded, "SERVICE-260", false, 500);
        if (bounded.Count != 256 || bounded["service-260"].Value)
            throw new InvalidOperationException("AI service probe cache case-insensitive refresh failed.");
        // All three driver restore paths (manual Restore all, hold expiry,
        // device attachment) must read a fresh Type=kernel, Start=Disabled
        // and trusted path before they restore demand startup (3).
        if (!CanRestoreDriverProvenance(true, true, 1, 4, 4) ||
            CanRestoreDriverProvenance(false, true, 1, 4, 4) ||
            CanRestoreDriverProvenance(true, false, 1, 4, 4) ||
            CanRestoreDriverProvenance(true, true, 0x20, 4, 4) ||
            CanRestoreDriverProvenance(true, true, 1, 3, 4) ||
            CanRestoreDriverProvenance(true, true, 1, 4, 3) ||
            CanRestoreDriverProvenance(true, true, 1, -1, 4))
            throw new InvalidOperationException(
                "AI driver restore could modify a changed/unverified driver identity.");
        // A stored Windows-ownership positive is only advisory. Revalidation
        // at the actual mutation boundary MUST bypass even fresh entries.
        if (!MayReuseProbeCache(requireFresh: false) ||
            MayReuseProbeCache(requireFresh: true))
            throw new InvalidOperationException("AI final service identity check reused cached authorization.");
        // Only a recognizable System32 binary path can be considered for an
        // automatic trial. No filesystem/registry accesses in these cases.
        const string root = @"C:\Windows";
        if (!IsWindowsServiceBinaryPath(@"C:\Windows\System32\svchost.exe -k netsvcs", root) ||
            !IsWindowsServiceBinaryPath("\"C:\\Windows\\System32\\svchost.exe\" -k netsvcs", root) ||
            !IsWindowsKernelDriverBinaryPath(@"\SystemRoot\System32\drivers\sample.sys", root) ||
            !IsWindowsKernelDriverBinaryPath(@"System32\drivers\device.sys", root) ||
            !IsWindowsServiceBinaryPath(@"System32\svchost.exe -k local", root) ||
            !IsWindowsServiceBinaryPath(@"System32\command.com", root) ||
            IsWindowsServiceBinaryPath(@"System32\drivers\device.sys", root) ||
            IsWindowsKernelDriverBinaryPath(@"System32\svchost.exe -k local", root) ||
            IsWindowsKernelDriverBinaryPath(@"System32\command.com", root) ||
            IsWindowsKernelDriverBinaryPath(@"System32\drivers\device.sys -load", root) ||
            IsWindowsKernelDriverBinaryPath("\"System32\\drivers\\device.sys\" /flag", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\Temp\malware.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\DriverStore\malware.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\drivers\malware.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\drivers\nested\malware.sys", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\driver.sys", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\\svchost.exe", root) ||
            IsWindowsServiceBinaryPath(string.Empty, root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\Temp\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\SysWOW64\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\..\Temp\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"\SystemRoot\System32\..\Temp\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32.evil\agent.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\helper.exe:evil", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\", root) ||
            IsWindowsServiceBinaryPath("\"C:\\Windows\\System32\\svchost.exe\"junk", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\program.bat", root) ||
            IsWindowsServiceBinaryPath(null, root))
            throw new InvalidOperationException("AI service image path provenance is too permissive.");
        // A discontinuous OS clock never enters the live cached-value gate.
        DateTime utc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (utc.AddHours(-2) >= utc ||
            !IsMonotonicProbeFresh(stored, stored + frequency * 3,
                frequency, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("AI cache TTL depends on Windows clock time.");
        return "AI_PROBE_CACHE_MONOTONIC_SMOKE_OK service30s=true ownership10m=true " +
            "clockIndependent=true invalidCountersFailClosed=true boundedEntries=256 windowsWrites=0";
    }

    // Legacy wall-clock helper is retained for existing diagnostic tests;
    // the live service dependency/ownership cache never calls this path.
}
