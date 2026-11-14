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
    internal async Task TickAsync(
        AiSystemRuntimeConfig config, bool canOptimizeNow,
        bool gameActive, CancellationToken token,
        AiSystemSmoothnessHealth? telemetry = null,
        bool allowInactiveAppStartup = false,
        bool allowInactiveAppObservation = false)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await TickCoreAsync(config, canOptimizeNow, gameActive,
                token, telemetry, allowInactiveAppStartup,
                allowInactiveAppObservation).ConfigureAwait(false);
        }
        finally
        {
            RecordTickPerformance(Stopwatch.GetTimestamp() - started);
        }
    }

    // Explicit "Restore all" differs from the ordinary background tick:
    // the normal AI limits recovery to ONE owner per scan. Inspect its
    // durable journal between bounded restore attempts so the user is never
    // told that all third-party entries were restored when some remain.
    internal async Task RestoreOwnedNowAsync(CancellationToken token)
    {
        const int maxRestorePasses = 16;
        int priorOwners = int.MaxValue;
        for (int pass = 0; pass < maxRestorePasses; pass++)
        {
            token.ThrowIfCancellationRequested();
            // A previously unsuccessful OFF tick may be cooling down;
            // explicit restoration is a fresh user-requested attempt.
            _lastScanUtc = DateTime.MinValue;
            _lastFailedOffRestoreUtc = default;
            await TickAsync(new AiSystemRuntimeConfig(Enabled: false),
                canOptimizeNow: false, gameActive: false, token)
                .ConfigureAwait(false);

            // TickCore intentionally logs recoverable errors. Verify durable
            // ownership under the same cross-process journal lease before
            // reporting ANY success, including when another host was busy.
            int remaining;
            bool learningRepairPending;
            string leasePath = JournalPath + ".lease";
            try
            {
                using var lease = new FileStream(leasePath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                ReloadJournalUnderLease();
                if (!_journalHealthy)
                    throw new InvalidDataException(
                        "Záznam obnovení aplikací nelze bezpečně přečíst.");
                remaining = _owned.Count;
                learningRepairPending = _learningRepairPending;
            }
            catch (IOException ex)
            {
                throw new IOException(
                    "Záznam obnovy aplikací právě používá jiná instance nebo není dostupný.", ex);
            }
            if (remaining == 0) return;
            if (remaining >= priorOwners && !learningRepairPending)
                throw new InvalidOperationException(
                    $"Obnova aplikací není úplná: zbývá {remaining} spravovaných položek. " +
                    "Záznam byl zachován pro další pokus.");
            priorOwners = remaining;
        }
        throw new InvalidOperationException(
            $"Obnova aplikací nedokončena v bezpečném limitu. " +
            $"Zbývá {_owned.Count} spravovaných položek.");
    }

    // In the absence of owned state, an ineligible cycle is deliberately
    // journal-only; it must not carry stale idle or Steam A/B/A evidence.
    private void ObserveFeatureProfile(UserFeatureProfile current)
    {
        UserFeatureProfile? previous = _lastObservedFeatureProfile;
        if (previous is not null && !ReferenceEquals(previous, current) &&
            (previous.IsUntrusted != current.IsUntrusted ||
             previous.UpdatedUtc != current.UpdatedUtc ||
             !previous.KeepFeatures.SequenceEqual(
                 current.KeepFeatures, StringComparer.OrdinalIgnoreCase)))
        {
            // The user's KEEP protections changed while our 1-3 minute
            // native-inventory cooldown was active. Never spend idle credit
            // accumulated under the old preference snapshot.
            _absentSince.Clear();
            _steamPreStopSamples.Clear();
            _lastObservationUtc = default;
        }
        _lastObservedFeatureProfile = current;
    }

    private void FinishUnownedNoActionScan(
        DateTime now, bool enabled, bool servicesEnabled, bool tasksEnabled)
    {
        _absentSince.Clear();
        _steamPreStopSamples.Clear();
        _lastObservationUtc = default;
        _lastScanUtc = now;
        _lastAiEnabled = enabled;
        _lastServicesEnabled = servicesEnabled;
        _lastTasksEnabled = tasksEnabled;
        Interlocked.Increment(ref _perfStartupSkips);
        Interlocked.Increment(ref _perfFirefoxSkips);
        Interlocked.Increment(ref _perfAppInventorySkips);
    }

    // Every owned app is already indexed by the one immutable AI
    // presence inventory. Reuse those slots for the urgent recovery bitset
    // instead of comparing every owner with all 12 allowed names each tick.
    // Unknown identities still use the original conservative prior-key scan.
    private static int OwnerProbeIndex(string key) =>
        BackgroundActivityService.GetAiRecoveryProbeIndex(key);

    private static bool AnyOwnedAppReopened(
        Dictionary<string, Owner> owners, Func<string, bool> isRunning)
    {
        ulong checkedNames = 0;
        int index = 0;
        foreach (Owner owner in owners.Values)
        {
            // Must advance even for a known duplicate that is skipped, so
            // any later unknown-key fallback sees ALL earlier owners.
            int priorLimit = index++;
            int slot = OwnerProbeIndex(owner.AppKey);
            if (slot >= 0 && slot < 64)
            {
                ulong flag = 1UL << slot;
                if ((checkedNames & flag) != 0) continue;
                checkedNames |= flag;
            }
            else
            {
                // Defensive fallback for malformed or synthetic owner keys.
                bool seen = false;
                int priorIndex = 0;
                foreach (Owner earlier in owners.Values)
                {
                    if (priorIndex++ >= priorLimit) break;
                    if (owner.AppKey.Equals(earlier.AppKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        seen = true;
                        break;
                    }
                }
                if (seen) continue;
            }
            if (isRunning(owner.AppKey)) return true;
        }
        return false;
    }

    // These fixtures are in memory only. Probe every allowed identity,
    // Firefox, mixed-case duplicates and foreign names without rechecking
    // any process name. Confirm earliest-match short circuit semantics.
    internal static bool RunOwnedProbeBitsetSmoke()
    {
        DateTime at = new(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc);
        if (AiThirdPartyIdlePolicy.AppKeys.Length >= 63 ||
            OwnerProbeIndex("FIREFOX") != AiThirdPartyIdlePolicy.AppKeys.Length ||
            OwnerProbeIndex("windows") != -1)
            return false;
        var distinctSlots = new HashSet<int>();
        foreach (string key in AiThirdPartyIdlePolicy.AppKeys)
        {
            int slot = OwnerProbeIndex(key);
            if (slot < 0 || slot >= 63 || !distinctSlots.Add(slot) ||
                slot != OwnerProbeIndex(key.ToUpperInvariant()))
                return false;
        }
        if (distinctSlots.Contains(OwnerProbeIndex("firefox")))
            return false;
        var owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        int ordinal = 0;
        void Add(string name)
        {
            string id = (++ordinal).ToString(CultureInfo.InvariantCulture);
            owners["startup|" + id] =
                new Owner("startup", id, name, at, true, false);
        }
        Add("steam");
        Add("STEAM"); // A known duplicate must still count as an earlier row.
        Add("custom");
        Add("CUSTOM"); // Untrusted fallback preserves distinct probes.
        Add("firefox");
        Add("FIREFOX");
        foreach (string name in AiThirdPartyIdlePolicy.AppKeys)
        {
            Add(name);
            Add(name.ToUpperInvariant());
        }
        int count = 0;
        bool any = AnyOwnedAppReopened(owners, _ => { count++; return false; });
        int expected = AiThirdPartyIdlePolicy.AppKeys.Length + 2;
        if (any || count != expected) return false;
        count = 0;
        if (!AnyOwnedAppReopened(owners, x =>
            {
                count++;
                return x.Equals("custom", StringComparison.OrdinalIgnoreCase);
            }) || count != 2)
            return false;
        count = 0;
        if (!AnyOwnedAppReopened(owners, x =>
            {
                count++;
                return x.Equals("firefox", StringComparison.OrdinalIgnoreCase);
            }) || count != 3)
            return false;
        return !AnyOwnedAppReopened(new Dictionary<string, Owner>(), _ => true);
    }

    // A direct exact journal-key lookup must preserve the old case-insensitive
    // LINQ cooldown logic and may not adopt cooldown from unrelated feedback.
    internal static bool RunSteamRetryLookupParitySmoke()
    {
        DateTime now = new(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc);
        var feedback = new Dictionary<string, FeedbackOutcome>(
            StringComparer.OrdinalIgnoreCase);
        static bool Old(IReadOnlyDictionary<string, FeedbackOutcome> rows, DateTime time) =>
            rows.Any(x => x.Key.Equals(SteamServiceOwnerKey,
                StringComparison.OrdinalIgnoreCase) &&
                AiThirdPartyIdlePolicy.DeferServiceRetry(
                    x.Value.Verdict, x.Value.UpdatedUtc, time));
        bool Equal() => IsSteamServiceRetryDeferred(feedback, now) ==
                        Old(feedback, now);
        if (!Equal()) return false;
        feedback["service|Other"] =
            new FeedbackOutcome(now, "positive-system-trend", 3, AppKey: "steam");
        if (!Equal() || IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback["SERVICE|steam client service"] =
            new FeedbackOutcome(now.AddHours(-2), "no-observed-gain", 3,
                AppKey: "steam");
        if (!Equal() || !IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback[SteamServiceOwnerKey] =
            new FeedbackOutcome(now.AddDays(-8), "aba-matched-system-trend", 3,
                AppKey: "steam");
        if (!Equal() || IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback[SteamServiceOwnerKey] =
            new FeedbackOutcome(now.AddMinutes(1), "inconclusive", 2,
                AppKey: "steam");
        if (!Equal() || !IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback.Remove(SteamServiceOwnerKey);
        return Equal() && !IsSteamServiceRetryDeferred(feedback, now);
    }

    // Bound to the durable, validated owner journal (at most 64 entries).
    // A short direct dictionary walk avoids per-app captured LINQ predicates
    // during 1-3 minute scans, without changing kind/identity admission.
    // Case-insensitive app identity is intentionally conservative: it must
    // not permit a second mutation for an already-owned differently-cased app.
    private const string SteamServiceOwnerKey = "service|Steam Client Service";

    // Restrict loaded owner/feedback identities to the same immutable
    // allowlist as native AI observations. The shared index includes one
    // extra Firefox PRESENCE slot, which is intentionally NOT an automatic
    // manageable-app identity. Null or forged strings always fail closed.
    private static bool IsManagedJournalApp(string? appKey)
    {
        if (string.IsNullOrEmpty(appKey)) return false;
        int slot = BackgroundActivityService.GetAiRecoveryProbeIndex(appKey);
        return slot >= 0 && slot < AiThirdPartyIdlePolicy.AppKeys.Length;
    }

    // Journal feedback map is always OrdinalIgnoreCase once validated and
    // loaded under the lease. A direct key lookup avoids enumerating every
    // historical outcome and rebuilding the same "service|..." string.
    private static bool IsSteamServiceRetryDeferred(
        IReadOnlyDictionary<string, FeedbackOutcome> feedback, DateTime now) =>
        feedback.TryGetValue(SteamServiceOwnerKey, out var outcome) &&
        AiThirdPartyIdlePolicy.DeferServiceRetry(
            outcome.Verdict, outcome.UpdatedUtc, now);

    private static bool HasOwnedResource(
        Dictionary<string, Owner> owned, string? kind = null,
        string? appKey = null)
    {
        foreach (Owner owner in owned.Values)
        {
            if ((kind is null || owner.Kind == kind) &&
                (appKey is null || owner.AppKey.Equals(
                    appKey, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    // A validated owner map is immutable in its resource identities during
    // this leased AI pass. Restoring/removing an owner returns immediately;
    // feedback samples may replace a value but never alter its kind/app.
    // Capture kind flags and known-app ownership bits ONCE, rather than
    // re-enumerating up to 64 owners for every policy gate and app candidate.
    private readonly struct OwnedPresence(
        Dictionary<string, Owner> owners, ulong appBits,
        bool service, bool startup, bool firefoxTask, bool steamService)
    {
        internal bool HasService => service;
        internal bool HasStartup => startup;
        internal bool HasFirefoxTask => firefoxTask;
        internal bool HasSteamService => steamService;

        internal bool HasApp(string appKey)
        {
            int slot = OwnerProbeIndex(appKey);
            if (slot is >= 0 and < 64)
                return (appBits & (1UL << slot)) != 0;
            // Exact legacy fallback for synthetic/unknown case-variant
            // identities. No broadened resource admission is possible.
            return HasOwnedResource(owners, appKey: appKey);
        }
    }

    private static OwnedPresence CaptureOwnedPresence(
        Dictionary<string, Owner> owners)
    {
        ulong appBits = 0;
        bool service = false, startup = false;
        bool firefoxTask = false, steamService = false;
        foreach (Owner owner in owners.Values)
        {
            int slot = OwnerProbeIndex(owner.AppKey);
            if (slot is >= 0 and < 64)
                appBits |= 1UL << slot;
            if (owner.Kind == "service")
            {
                service = true;
                if (owner.AppKey.Equals("steam",
                    StringComparison.OrdinalIgnoreCase))
                    steamService = true;
            }
            else if (owner.Kind == "startup")
                startup = true;
            else if (owner.Kind == "task" &&
                owner.AppKey.Equals("firefox", StringComparison.OrdinalIgnoreCase))
                firefoxTask = true;
        }
        return new OwnedPresence(owners, appBits, service, startup,
            firefoxTask, steamService);
    }

    // Preserve original stable OrderBy precedence without allocating a
    // sorter/enumerator snapshot on every 3-minute AI scan (including the
    // common empty-owner case). The validated journal permits only these
    // three resource kinds. Explicit priority phases keep relative insertion
    // order intact while allowing a failed recovery to try later owners.
    private static int RecoveryPriority(string kind) =>
        kind == "service" ? 0 : kind == "task" ? 1 : 2;
}
