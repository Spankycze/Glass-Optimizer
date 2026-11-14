using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed partial class AiThirdPartyIdleAutomationService
{
    // Synthetic identity and .NET 10 dictionary-enumerator test. The
    // production A2 loop updates/removes EXISTING references while retaining
    // its journal lease, so no extra copied array is required. Never inspect
    // actual services, tasks, profiles, or the user's ownership journal here.
    internal static bool RunOwnedReferenceEnumerationSmoke()
    {
        DateTime at = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        var worker = new AiThirdPartyIdleAutomationService();
        worker._owned["service|steam"] = new Owner(
            "service", "steam", "StEaM", at, true, true);
        worker._owned["startup|spotify"] = new Owner(
            "startup", "spotify", "spotify", at, true, false);
        worker._owned["task|firefox"] = new Owner(
            "task", "firefox", "FiReFoX", at, true, false);
        if (!HasOwnedResource(worker._owned) ||
            !HasOwnedResource(worker._owned, kind: "service") ||
            !HasOwnedResource(worker._owned, kind: "startup", appKey: "SPOTIFY") ||
            !HasOwnedResource(worker._owned, kind: "task", appKey: "firefox") ||
            !HasOwnedResource(worker._owned, appKey: "STEAM") ||
            HasOwnedResource(worker._owned, kind: "driver") ||
            HasOwnedResource(worker._owned, kind: "service", appKey: "discord") ||
            HasOwnedResource(worker._owned, kind: "SERVICE") ||
            HasOwnedResource(new Dictionary<string, Owner>(), appKey: "steam"))
            return false;
        worker._references["service|first"] = new ReferenceTrial(
            "first", "steam", at, 8, 8192, 7, 8300);
        worker._references["service|second"] = new ReferenceTrial(
            "second", "steam", at, 8, 8192, 7, 8300);
        worker._references["service|third"] = new ReferenceTrial(
            "third", "steam", at, 8, 8192, 7, 8300);
        int visited = 0;
        // The same operations are used inside the awaited A2 branch:
        // replacement on a fresh sample, removal after a completed trial.
        foreach (var (key, reference) in worker._references)
        {
            visited++;
            if (key == "service|first")
                worker._references[key] = reference with { Samples = 1 };
            else if (key == "service|second")
                worker._references.Remove(key);
        }
        return visited == 3 &&
            worker._references.Count == 2 &&
            worker._references["service|first"].Samples == 1 &&
            !worker._references.ContainsKey("service|second") &&
            worker._references["service|third"].Samples == 0 &&
            worker._owned.Count == 3;
    }

    // Pure parity against the original repeated scans. A leased tick
    // snapshots immutable owner identities before doing native inventory,
    // and any successful recovery returns before another owner decision.
    internal static bool RunOwnedPresenceParitySmoke()
    {
        DateTime at = new(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
        var owners = new Dictionary<string, Owner>(
            StringComparer.OrdinalIgnoreCase);
        bool Check()
        {
            OwnedPresence captured = CaptureOwnedPresence(owners);
            if (captured.HasService != HasOwnedResource(owners, kind: "service") ||
                captured.HasStartup != HasOwnedResource(owners, kind: "startup") ||
                captured.HasFirefoxTask != HasOwnedResource(
                    owners, kind: "task", appKey: "firefox") ||
                captured.HasSteamService != HasOwnedResource(
                    owners, kind: "service", appKey: "steam"))
                return false;
            foreach (string app in AiThirdPartyIdlePolicy.AppKeys)
                if (captured.HasApp(app) != HasOwnedResource(
                    owners, appKey: app) ||
                    captured.HasApp(app.ToUpperInvariant()) !=
                        HasOwnedResource(owners, appKey: app.ToUpperInvariant()))
                    return false;
            foreach (string app in new[]
                { "firefox", "FIREFOX", "unknown", "UNKNOWN", "windows", "chrome" })
                if (captured.HasApp(app) != HasOwnedResource(
                    owners, appKey: app))
                    return false;
            return true;
        }
        if (!Check()) return false;
        int id = 0;
        void Add(string kind, string app)
        {
            string identity = (++id).ToString(CultureInfo.InvariantCulture);
            owners[kind + "|" + identity] = new Owner(
                kind, identity, app, at, true, false);
        }
        foreach (string app in AiThirdPartyIdlePolicy.AppKeys)
        {
            Add("startup", app);
            Add("startup", app.ToUpperInvariant());
            if (!Check()) return false;
        }
        Add("task", "FiReFoX");
        Add("task", "firefox");
        Add("service", "StEaM");
        Add("service", "spotify");
        Add("other", "unknown");
        Add("other", "UNKNOWN");
        if (!Check()) return false;

        // All recognized names fit inside a ulong without using additional
        // dynamic collections, including the read-only Firefox presence bit.
        if (AiThirdPartyIdlePolicy.AppKeys.Length >= 63)
            return false;
        owners.Clear();
        return Check();
    }

    // Pure verification of stable recovery priority, no registry/SCM/tasks.
    // Reverse insertion order deliberately stresses the old OrderBy contract.
    internal static bool RunRestorePrioritySmoke()
    {
        var at = new DateTime(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        var owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase)
        {
            ["startup|one"] = new("startup", "one", "spotify", at, true, false),
            ["task|one"] = new("task", "one", "firefox", at, true, false),
            ["service|one"] = new("service", "one", "steam", at, true, true),
            ["startup|two"] = new("startup", "two", "discord", at, true, false),
            ["service|two"] = new("service", "two", "steam", at, true, true)
        };
        var order = new List<string>(owners.Count);
        for (int phase = 0; phase < 3; phase++)
            foreach (Owner owner in owners.Values)
                if (RecoveryPriority(owner.Kind) == phase)
                    order.Add(owner.Kind + ":" + owner.Id);
        int emptyChecks = 0;
        var empty = new Dictionary<string, Owner>();
        for (int phase = 0; phase < 3; phase++)
            foreach (Owner _ in empty.Values)
                emptyChecks++;
        // The live feedback path replaces an existing owner record in
        // place while the loop is open; this must not invalidate the
        // dictionary enumerator under .NET 10 semantics.
        int visitedAfterReplacement = 0;
        foreach (Owner owner in owners.Values)
        {
            if (owner.Kind == "service" && owner.Id == "one")
                owners["service|one"] = owner with { Pending = false };
            visitedAfterReplacement++;
        }
        return visitedAfterReplacement == owners.Count &&
            !owners["service|one"].Pending &&
            order.SequenceEqual(new[]
            {
                "service:one", "service:two", "task:one",
                "startup:one", "startup:two"
            }) &&
            emptyChecks == 0 &&
            RecoveryPriority("service") == 0 &&
            RecoveryPriority("task") == 1 &&
            RecoveryPriority("startup") == 2;
    }

    // Pure in-memory fixture; never queries SCM, startup inventory or files.
    internal static bool RunPreStopEvidenceSmoke()
    {
        var worker = new AiThirdPartyIdleAutomationService();
        DateTime start = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        static AiSystemSmoothnessHealth Sample(DateTime at, double cpu,
            double availableMiB) =>
            new(at, cpu, 42, availableMiB / 1024, "Normal",
                "pre-stop-test", false);
        if (worker.TryGetStableSteamBaseline(start, true,
                Sample(start, 8, 8000), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(3), true,
                Sample(start.AddMinutes(3), 8.2, 8020), out _, out _) ||
            !worker.TryGetStableSteamBaseline(start.AddMinutes(6), true,
                Sample(start.AddMinutes(6), 8.1, 8010),
                out double cpu, out double ramMiB) ||
            Math.Abs(cpu - 8.1) > 0.01 ||
            Math.Abs(ramMiB - 8010) > 0.01 ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(9), false,
                Sample(start.AddMinutes(9), 8.1, 8010), out _, out _) ||
            worker._steamPreStopSamples.Count != 0 ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(12), true,
                Sample(start.AddMinutes(10), 7.9, 8000), out _, out _) ||
            worker._steamPreStopSamples.Count != 0 ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(15), true,
                Sample(start.AddMinutes(15), 7, 8000), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(18), true,
                Sample(start.AddMinutes(18), 13, 8005), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(21), true,
                Sample(start.AddMinutes(21), 7, 8000), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(24), true,
                Sample(start.AddMinutes(24), 7, 8000), out _, out _) ||
            !worker.TryGetStableSteamBaseline(start.AddMinutes(27), true,
                Sample(start.AddMinutes(27), 7, 8000), out _, out _) ||
            worker._steamPreStopSamples.Count != 3)
            return false;
        // The real skip path discards both historical absence and an
        // incomplete Steam experiment. It must not count an unobserved
        // game/busy interval as confirmed app inactivity after wake-up.
        worker._absentSince["steam"] = start.AddMinutes(-30);
        worker._lastObservationUtc = start.AddMinutes(27);
        DateTime skippedAt = start.AddMinutes(30);
        worker.FinishUnownedNoActionScan(skippedAt, true, true, true);
        // Recovery probe must check one app per distinct case-insensitive
        // identity, short circuit on reopen, and never probe an empty journal.
        var owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase)
        {
            ["startup|a"] = new("startup", "a", "steam", start, true, false),
            ["startup|b"] = new("startup", "b", "STEAM", start, true, false),
            ["startup|c"] = new("startup", "c", "discord", start, true, false)
        };
        int checkedApps = 0;
        bool reopened = AnyOwnedAppReopened(owners, key =>
        {
            checkedApps++;
            return key.Equals("discord", StringComparison.OrdinalIgnoreCase);
        });
        if (!reopened || checkedApps != 2)
            return false;
        checkedApps = 0;
        if (AnyOwnedAppReopened(owners, _ => { checkedApps++; return false; }) ||
            checkedApps != 2 ||
            AnyOwnedAppReopened(new Dictionary<string, Owner>(), _ => true))
            return false;
        return worker._absentSince.Count == 0 &&
            worker._steamPreStopSamples.Count == 0 &&
            worker._lastObservationUtc == default &&
            worker._lastScanUtc == skippedAt &&
            worker._lastAiEnabled == true &&
            worker._lastServicesEnabled == true &&
            worker._lastTasksEnabled == true &&
            Interlocked.Read(ref worker._perfAppInventorySkips) == 1 &&
            Interlocked.Read(ref worker._perfStartupSkips) == 1 &&
            Interlocked.Read(ref worker._perfFirefoxSkips) == 1;
    }

    private bool TryGetStableSteamBaseline(
        DateTime now, bool eligible, AiSystemSmoothnessHealth? telemetry,
        out double cpu, out double ramMiB)
    {
        cpu = ramMiB = 0;
        bool comparableTelemetry = eligible &&
            telemetry is { GameActive: false, State: "Normal" } &&
            AiSystemSmoothnessGuardService.IsFresh(
                telemetry.UpdatedUtc, now, TimeSpan.FromSeconds(65)) &&
            IsTrustedResourceSample(
                telemetry.CpuLoadPercent, telemetry.FreeRamGiB);

        if (!comparableTelemetry)
        {
            _steamPreStopSamples.Clear();
            return false;
        }
        if (_steamPreStopSamples.Count > 0)
        {
            DateTime lastUtc = _steamPreStopSamples.Last().AtUtc;
            if (!AiThirdPartyIdlePolicy.IsContinuousObservation(lastUtc, now))
                _steamPreStopSamples.Clear();
            else if (telemetry!.UpdatedUtc <= lastUtc ||
                     telemetry.UpdatedUtc - lastUtc < TimeSpan.FromSeconds(50))
                return false;
        }
        _steamPreStopSamples.Enqueue((
            telemetry!.UpdatedUtc, telemetry.CpuLoadPercent,
            telemetry.FreeRamGiB * 1024));
        while (_steamPreStopSamples.Count > 3)
            _steamPreStopSamples.Dequeue();
        if (_steamPreStopSamples.Count != 3)
            return false;

        // Exactly three tiny records. One pass over the existing Queue
        // avoids ToArray, LINQ Min/Max/Average enumerators and their repeated
        // walks on the AI snapshot path; keep the same acceptance criteria.
        double minCpu = double.PositiveInfinity, maxCpu = double.NegativeInfinity;
        double minRam = double.PositiveInfinity, maxRam = double.NegativeInfinity;
        double cpuSum = 0, ramSum = 0;
        foreach (var sample in _steamPreStopSamples)
        {
            minCpu = Math.Min(minCpu, sample.Cpu);
            maxCpu = Math.Max(maxCpu, sample.Cpu);
            minRam = Math.Min(minRam, sample.RamMiB);
            maxRam = Math.Max(maxRam, sample.RamMiB);
            cpuSum += sample.Cpu;
            ramSum += sample.RamMiB;
        }
        if (!AiThirdPartyIdlePolicy.IsComparablePreStopWindow(
                _steamPreStopSamples.Count, minCpu, maxCpu, minRam, maxRam))
            return false;
        cpu = cpuSum / 3;
        ramMiB = ramSum / 3;
        return true;
    }

    // One validation contract for A/pre-stop, B/stopped helper and
    // A2/restored service. Even finite-but-absurd RAM values must never
    // overflow the persisted MiB sums or poison durable ownership.
    private static bool IsTrustedResourceSample(double cpuPercent, double freeRamGiB) =>
        double.IsFinite(cpuPercent) && cpuPercent is >= 0 and <= 100 &&
        double.IsFinite(freeRamGiB) && freeRamGiB is > 0 and <= 1_048_576 &&
        double.IsFinite(freeRamGiB * 1024);

    // The third persisted B sample already exhausts the bounded evidence
    // budget. If its companion auxiliary verdict was repaired away, finish
    // as inconclusive rather than generating a fourth invalid owner sample.
    private static bool MissingCompletedVerdict(int samples, bool hasRecentVerdict) =>
        samples >= 3 && !hasRecentVerdict;

    internal static bool RunResourceSampleRecoverySmoke() =>
        IsTrustedResourceSample(0, 8) &&
        IsTrustedResourceSample(100, 1_048_576) &&
        !IsTrustedResourceSample(-1, 8) &&
        !IsTrustedResourceSample(100.01, 8) &&
        !IsTrustedResourceSample(double.NaN, 8) &&
        !IsTrustedResourceSample(8, 0) &&
        !IsTrustedResourceSample(8, double.PositiveInfinity) &&
        !IsTrustedResourceSample(8, 1e100) &&
        !MissingCompletedVerdict(2, false) &&
        MissingCompletedVerdict(3, false) &&
        !MissingCompletedVerdict(3, true);

    private static bool HasNoActiveDependents(ServiceController service)
    {
        ServiceController[]? dependents = null;
        try
        {
            dependents = service.DependentServices;
            return AiThirdPartyIdlePolicy.HasOnlyStoppedDependents(
                dependents.Select(x => (ServiceControllerStatus?)x.Status));
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception)
        {
            return false; // Fail closed if SCM state cannot be confirmed.
        }
        finally
        {
            if (dependents != null)
                foreach (ServiceController dependency in dependents)
                    dependency.Dispose();
        }
    }

    private static bool IsServiceActuallyStopped(string id)
    {
        try
        {
            using var helper = new ServiceController(id);
            return AiThirdPartyIdlePolicy.MaySampleStoppedHelper(helper.Status);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                  System.ComponentModel.Win32Exception)
        {
            return false; // Unknown native state is never valid B evidence.
        }
    }
}
