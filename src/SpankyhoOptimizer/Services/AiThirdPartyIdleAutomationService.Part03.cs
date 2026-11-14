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
    private async Task TickCoreAsync(
        AiSystemRuntimeConfig config, bool canOptimizeNow,
        bool gameActive, CancellationToken token,
        AiSystemSmoothnessHealth? telemetry, bool allowInactiveAppStartup,
        bool allowInactiveAppObservation)
    {
        DateTime now = DateTime.UtcNow;
        bool enabled = config.Enabled && config.SelfOptimizationEnabled;
        // The user playing, the computer becoming busy or AI being switched
        // off breaks a continuous idle window IMMEDIATELY. Do not let the
        // ordinary 1-3 minute inventory throttle retain stale evidence.
        if (!enabled || (!canOptimizeNow && !allowInactiveAppObservation) || gameActive)
        {
            _absentSince.Clear();
            _steamPreStopSamples.Clear();
            _lastObservationUtc = default;
        }
        else if (_absentSince.Count != 0 || _steamPreStopSamples.Count != 0 ||
                 _owned.Count != 0)
        {
            // Only metadata is read on unchanged profiles. Skip the extra
            // check when nothing is being observed or recovered.
            ObserveFeatureProfile(UserFeatureProfileService.Load());
        }
        if (!_journalHealthy &&
            !AiThirdPartyIdlePolicy.MayRetryJournalRead(_lastJournalErrorUtc, now))
            return;
        // The startup lane may observe Steam even with service management
        // turned OFF. Its hour of absent-process evidence must NOT be reused
        // to stop Steam Client Service immediately after services are enabled.
        // Start a fresh service-eligible window at each service-switch change;
        // do not discard other applications' independent startup evidence.
        if (_lastServicesEnabled != config.ServicesEnabled)
        {
            _absentSince.Remove("steam");
            _steamPreStopSamples.Clear();
        }
        // The optional Firefox task has its own idle clock. Switching task
        // automation off/on invalidates earlier absence evidence, including
        // when the disabled tick skipped native inventories entirely.
        if (_lastTasksEnabled != config.ScheduledTasksEnabled)
            _absentSince.Remove("firefox");
        // Changes to global AI/service/task switches must not be delayed
        // by the normal 3-minute idle inventory throttle.
        bool configurationChanged =
            _lastAiEnabled != enabled ||
            _lastServicesEnabled != config.ServicesEnabled ||
            _lastTasksEnabled != config.ScheduledTasksEnabled;
        // AI disabled with no owned changes: lightweight bounded periodic
        // shared-journal refresh, not expensive Windows inventory every 5s.
        if (!configurationChanged && _journalHealthy &&
            AiThirdPartyIdlePolicy.IsFailedOffRestoreCoolingDown(
                enabled, _owned.Count, _lastFailedOffRestoreUtc, now) &&
            !ExternalJournalChangeDue(now))
            return;
        if (!configurationChanged && _journalHealthy &&
            AiThirdPartyIdlePolicy.CanThrottleDisabledIdleTick(
                enabled, _owned.Count, _references.Count, _lastScanUtc, now) &&
            !ExternalJournalChangeDue(now))
            return;
        // Continue owned recovery promptly, but native inventory only once
        // per minute during otherwise unchanged FanAgent 5-second cycles.
        if (!configurationChanged && _journalHealthy && enabled &&
            now - _lastScanUtc < (_owned.Count > 0
                ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(3)) &&
            !ExternalJournalChangeDue(now))
        {
            if (!AiThirdPartyIdlePolicy.IsOwnedRecoveryProbeDue(
                    _owned.Count, _lastUrgentProbeUtc, now))
                return;
            _lastUrgentProbeUtc = now;
            bool recoveryRequested = AnyOwnedAppReopened(
                _owned, BackgroundActivityService.IsRunningForAi);
            if (!recoveryRequested)
            {
                // Saving a "Ponechat" preference must restore already
                // AI-owned startup/task/service resources promptly, even
                // when the app has not been launched yet. Load() reuses its
                // synchronized in-memory snapshot after a verified Save().
                UserFeatureProfile currentPreferences = UserFeatureProfileService.Load();
                if (currentPreferences.IsUntrusted)
                {
                    // Lost/corrupt consent is not permission to keep a
                    // previously disabled service/task until minute-long
                    // inventory cooldown expires. Restore owned state now.
                    recoveryRequested = true;
                }
                else
                {
                    recoveryRequested = AnyOwnedAppReopened(_owned,
                        app => currentPreferences.Keeps("bg:" + app + ":keep"));
                }
            }
            if (!recoveryRequested)
                return;
            // User activity OR a new KEEP choice: the normal serialized
            // journal/inventory transaction below performs the restoration.
        }
        // Never grant scan-cooldown credit before acquiring the lease:
        // another RuntimeHost may hold it, and our next 5s tick must retry.
        // Two RuntimeHosts may be alive simultaneously. A disk-based lease
        // serializes load, decision, write-ahead journal and restore. Re-read
        // the journal under that lease: another host might have acted first.
        FileStream? lease = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            lease = new FileStream(JournalPath + ".lease", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        using (lease)
        try
        {
            Interlocked.Increment(ref _perfLeaseAcquired);
            long journalStarted = Stopwatch.GetTimestamp();
            try { ReloadJournalUnderLease(); }
            finally
            {
                RecordPhaseTicks(ref _perfJournalLoadTicks,
                    ref _perfJournalLoadMaxTicks,
                    Stopwatch.GetTimestamp() - journalStarted);
            }
            if (!_journalHealthy) return;
            if (_learningRepairPending)
            {
                // Only auxiliary measurement data was damaged. Preserve ALL
                // validated owners; atomically repair learning while leased,
                // and DO NOT attempt any Windows mutation in this cycle.
                try
                {
                    await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                    _learningRepairPending = false;
                    // Resume valid owner recovery on the NEXT normal host
                    // tick, not after a stale 1-3min or failed-off cooldown.
                    _lastScanUtc = DateTime.MinValue;
                    _lastFailedOffRestoreUtc = default;
                }
                catch (Exception ex)
                {
                    _journalHealthy = false;
                    _lastJournalErrorUtc = DateTime.UtcNow;
                    LogService.Error("ai-system", "AI journal learning repair failed", ex);
                }
                return;
            }
            if (!enabled && _owned.Count == 0 && _references.Count == 0)
            {
                // This is a completed journal-only inventory; no native
                // startup or process work was required.
                _lastScanUtc = now;
                _lastAiEnabled = enabled;
                _lastServicesEnabled = config.ServicesEnabled;
                _lastTasksEnabled = config.ScheduledTasksEnabled;
                return;
            }
            // AI-off still restores owned entries, but needs native startup
            // inventory only if one of our actual owners is a startup row.
            // Other shutdown recovery does not need a full process snapshot.
            OwnedPresence ownedPresence = CaptureOwnedPresence(_owned);
            bool mayIntroduceAtScan = AiThirdPartyIdlePolicy.MayIntroduceChange(
                enabled, canOptimizeNow, gameActive, _owned.Count,
                ownedPresence.HasService,
                _references.Count != 0);
            // Startup-only observation is permitted during normal
            // interactive desktop use when the target app itself is absent.
            // It NEVER allows service stops, Firefox task changes or a game
            // overlay interruption. Those still require whole-PC real idle.
            bool mayObserveUnusedStartup = AiThirdPartyIdlePolicy.MayIntroduceChange(
                enabled, allowInactiveAppObservation, gameActive, _owned.Count,
                ownedPresence.HasService, _references.Count != 0);
            bool mayObserveAny = mayIntroduceAtScan || mayObserveUnusedStartup;
            if (!mayObserveAny)
            {
                _absentSince.Clear();
                _steamPreStopSamples.Clear();
                _lastObservationUtc = default;
            }
            else if (!mayIntroduceAtScan)
            {
                // An app-specific startup absence hour must never be
                // recycled as Steam SERVICE idle evidence.
                _absentSince.Remove("steam");
                _steamPreStopSamples.Clear();
            }
            // An unowned, reference-free AI has nothing to recover while
            // a game is active or the machine is not in a quiet window.
            // The journal was freshly validated above; no profile, registry
            // or process inventory is required in this path.
            if (AiThirdPartyIdlePolicy.CanSkipUnownedInventory(
                    enabled, mayObserveAny, true,
                    _owned.Count, _references.Count))
            {
                FinishUnownedNoActionScan(
                    now, enabled, config.ServicesEnabled, config.ScheduledTasksEnabled);
                return;
            }
            // The preference snapshot is shared by all remaining decisions.
            UserFeatureProfile? profile = enabled
                ? UserFeatureProfileService.Load() : null;
            if (profile is not null)
                ObserveFeatureProfile(profile);
            bool trustedPreferences = profile is { IsUntrusted: false };
            if (AiThirdPartyIdlePolicy.CanSkipUnownedInventory(
                    enabled, mayObserveAny, trustedPreferences,
                    _owned.Count, _references.Count))
            {
                FinishUnownedNoActionScan(
                    now, enabled, config.ServicesEnabled, config.ScheduledTasksEnabled);
                return;
            }
            bool needStartup = AiThirdPartyIdlePolicy.NeedsStartupInventory(
                mayObserveAny,
                ownedPresence.HasStartup,
                trustedPreferences);
            IReadOnlyList<StartupEntry> startup = Array.Empty<StartupEntry>();
            if (!needStartup)
                Interlocked.Increment(ref _perfStartupSkips);
            if (needStartup)
            {
                Interlocked.Increment(ref _perfStartupScans);
                long scanStarted = Stopwatch.GetTimestamp();
                try
                {
                    startup = await StartupManagerService.GetAiUserRunAsync(token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    RecordPhaseTicks(ref _perfStartupLoadTicks,
                        ref _perfStartupLoadMaxTicks,
                        Stopwatch.GetTimestamp() - scanStarted);
                }
            }
            IReadOnlyList<BackgroundActivityState> apps =
                Array.Empty<BackgroundActivityState>();
            if (enabled)
            {
                Interlocked.Increment(ref _perfAppScans);
                long scanStarted = Stopwatch.GetTimestamp();
                try
                {
                    apps = await BackgroundActivityService.ReadForAiAsync(
                        startup, profile!, token).ConfigureAwait(false);
                }
                finally
                {
                    RecordPhaseTicks(ref _perfAppLoadTicks,
                        ref _perfAppLoadMaxTicks,
                        Stopwatch.GetTimestamp() - scanStarted);
                }
            }
            // Do not grant 1/3-minute scan credit or swallow a config
            // transition until ALL native reads have succeeded. Transient
            // inventory errors must retry next FanAgent tick.
            _lastScanUtc = now;
            _lastAiEnabled = enabled;
            _lastServicesEnabled = config.ServicesEnabled;
            _lastTasksEnabled = config.ScheduledTasksEnabled;
            if (AiThirdPartyIdlePolicy.CanAccumulateIdleEvidence(
                    enabled, trustedPreferences))
            {
                if (!AiThirdPartyIdlePolicy.IsContinuousObservation(
                        _lastObservationUtc, now))
                    _absentSince.Clear();
                _lastObservationUtc = now;
            }
            else
            {
                // No trusted preferences means no admissible idle evidence.
                // Discard historical credits so a future repaired profile
                // must observe a fresh quiet period before any new change.
                _absentSince.Clear();
                _steamPreStopSamples.Clear();
                _lastObservationUtc = default;
            }
            // Fixed AI inventory order has a prebuilt static key index.
            // This value-type view avoids the second per-scan Dictionary
            // allocation while retaining fast, identity-checked lookup.
            var observed = new BackgroundActivityService.AiObservedSnapshot(apps);
            await AdvancePassiveReferenceAsync(now,
                AiThirdPartyIdlePolicy.MayObserveReference(
                    enabled, config.ServicesEnabled, trustedPreferences),
                canOptimizeNow && !gameActive,
                observed.TryGetValue("steam", out var observedSteam) &&
                observedSteam.ProcessCount == 0, telemetry, token).ConfigureAwait(false);
            // Firefox browser-agent task is a narrowly scoped optional task.
            // It has no startup preference entry in the background app list.
            bool shouldReadFirefoxPresence =
                AiThirdPartyIdlePolicy.NeedsFirefoxPresence(
                    enabled,
                    ownedPresence.HasFirefoxTask,
                    AiThirdPartyIdlePolicy.MayIntroduceChange(
                        enabled, canOptimizeNow, gameActive, _owned.Count,
                        ownedPresence.HasService,
                        _references.Count != 0),
                    config.ScheduledTasksEnabled, trustedPreferences);
            if (shouldReadFirefoxPresence)
                Interlocked.Increment(ref _perfFirefoxScans);
            else
                Interlocked.Increment(ref _perfFirefoxSkips);
            // Read the Firefox presence bit from the ALREADY captured
            // single AI process-table snapshot. Keep the independent
            // immediate prewrite recheck below before any task mutation.
            // Missing process telemetry is UNKNOWN, not proof of absence.
            // The existing pre-write recheck remains mandatory, but cannot
            // make a stale hour-long idle clock trustworthy retroactively.
            BackgroundActivityState firefoxPresence = null!;
            bool firefoxPresenceKnown = shouldReadFirefoxPresence &&
                observed.TryGetValue("firefox", out firefoxPresence);
            bool firefoxRunning = firefoxPresenceKnown &&
                firefoxPresence.ProcessCount > 0;

            bool Activity(string app) =>
                app.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                    ? AiThirdPartyIdlePolicy.OwnedPresenceRequiresRestore(
                        firefoxPresenceKnown, firefoxRunning)
                    : !observed.TryGetValue(app, out var state) ||
                      AiThirdPartyIdlePolicy.OwnedPresenceRequiresRestore(
                          true, state.ProcessCount > 0);

            // Always restore before discovering anything new. An owned entry
            // remains in the journal if the original state cannot be restored.
            bool restoreAttempted = false;
            for (int recoveryPhase = 0; recoveryPhase < 3; recoveryPhase++)
            foreach (Owner owner in _owned.Values)
            {
                if (RecoveryPriority(owner.Kind) != recoveryPhase) continue;
                token.ThrowIfCancellationRequested();
                bool externallyRestored = await WasExternallyRestoredAsync(owner, token)
                    .ConfigureAwait(false);
                bool withinHold = AiThirdPartyIdlePolicy.MayKeepOwnedHold(
                    owner.Kind, enabled, config.ServicesEnabled,
                    config.ScheduledTasksEnabled, owner.AppliedUtc, now);
                // The shared inventory can be older than a just-relaunched
                // app. While a managed change is still being held, make a
                // narrow native presence check before deciding to keep it.
                // The urgent 10-second probe may have detected the reopen
                // after the shared inventory was sampled.
                bool appActive = Activity(owner.AppKey) ||
                    (withinHold && BackgroundActivityService.IsRunningForAi(owner.AppKey));
                // Firefox's optional task is intentionally presence-only:
                // its snapshot preference is always Unknown, so consult
                // the trusted feature profile for ALL owned kinds.
                bool userKeepsApp = trustedPreferences &&
                    profile!.Keeps("bg:" + owner.AppKey + ":keep");
                bool noGainRollback = enabled && owner.Kind == "service" &&
                    config.ServicesEnabled && !userKeepsApp &&
                    !externallyRestored && !appActive &&
                    await UpdateServiceFeedbackAsync(owner, telemetry, now,
                        canOptimizeNow && !gameActive, token)
                        .ConfigureAwait(false);
                if (withinHold && trustedPreferences &&
                    !noGainRollback && !owner.Pending && !appActive &&
                    !userKeepsApp && !externallyRestored)
                    continue;
                // Only a natural hold expiry, not user activity/AI-off/no
                // benefit, permits collecting restored reference A2. No
                // extra Windows service toggle is made for this experiment.
                bool passiveReference = enabled && config.ServicesEnabled &&
                    trustedPreferences && !userKeepsApp && !noGainRollback &&
                    !appActive &&
                    canOptimizeNow && !gameActive && owner.Kind == "service" &&
                    !owner.Pending && owner.AppliedUtc.Kind == DateTimeKind.Utc &&
                    owner.AppliedUtc <= now &&
                    now - owner.AppliedUtc >= TimeSpan.FromHours(6) &&
                    !externallyRestored &&
                    _feedback.TryGetValue(Key(owner.Kind, owner.Id), out var prior) &&
                    prior.Verdict == "positive-system-trend" &&
                    owner.BaselineCpuPercent.HasValue && owner.BaselineRamMiB.HasValue &&
                    owner.FeedbackSamples == 3;
                restoreAttempted = true;
                if (await RestoreAsync(owner, token).ConfigureAwait(false))
                {
                    if (passiveReference)
                    {
                        string id = Key(owner.Kind, owner.Id);
                        _references[id] = new ReferenceTrial(owner.Id, owner.AppKey, now,
                            owner.BaselineCpuPercent!.Value, owner.BaselineRamMiB!.Value,
                            owner.SumCpuPercent / 3, owner.SumRamMiB / 3);
                        LogService.Info("ai-system",
                            $"AI_APP_IDLE_ABA_REFERENCE_START app={owner.AppKey} " +
                            "afterNaturalRestore=true causalProof=false");
                    }
                    _owned.Remove(Key(owner.Kind, owner.Id));
                    _absentSince.Remove(owner.AppKey);
                    _externallyOverriddenUntil[owner.AppKey] = now.AddMinutes(30);
                    await PersistAsync(token).ConfigureAwait(false);
                    LogService.Info("ai-system",
                        $"AI_APP_IDLE_RESTORE kind={owner.Kind} app={owner.AppKey} reason=" +
                        (userKeepsApp ? "user-keeps-app" :
                         noGainRollback ? "no-measurable-resource-trend" :
                         appActive ? "app-in-use" :
                         externallyRestored ? "external-native-override" :
                         "AI-off-or-hold-expired"));
                    _lastFailedOffRestoreUtc = default;
                    // Restore only one completed resource per tick.
                    return;
                }
                // A missing or externally modified entry cannot starve
                // later independently owned resources.
            }
            if (restoreAttempted)
            {
                if (!enabled)
                    _lastFailedOffRestoreUtc = now;
                return; // Never apply while recovery failed.
            }

            // A single newly applied mutation per pass; while the service's
            // CPU/RAM response or restored A2 is being observed, changing
            // any other app would contaminate the experiment.
            if (!mayObserveAny || !trustedPreferences) return;
            foreach (string app in AiThirdPartyIdlePolicy.AppKeys)
            {
                token.ThrowIfCancellationRequested();
                if (!observed.TryGetValue(app, out var state))
                {
                    // An incomplete process inventory breaks continuous
                    // absence evidence. Never spend an earlier idle credit
                    // when this resource becomes visible again later.
                    _absentSince.Remove(app);
                    continue;
                }
                if (ownedPresence.HasApp(app)) continue;
                if (state.ProcessCount > 0)
                {
                    _absentSince.Remove(app);
                    continue;
                }
                // The user's explicit KEEP or a recent external override is
                // a break in eligible idle observation. Never accumulate the
                // hour/20-minute credit while this resource is protected:
                // removing KEEP later must start a fresh quiet period.
                if (state.Preference == BackgroundAppPreference.Keep ||
                    (_externallyOverriddenUntil.TryGetValue(app, out DateTime until) &&
                     until > now))
                {
                    _absentSince.Remove(app);
                    continue;
                }
                if (!_absentSince.TryGetValue(app, out DateTime absent))
                    _absentSince[app] = absent = now;
                if (!AiThirdPartyIdlePolicy.CanReduce(
                    now - absent, state.Preference, false, gameActive,
                    canOptimizeNow || (allowInactiveAppStartup && app != "steam")))
                    continue;

                // First do not start/stop ANY currently running app.
                // Disable one optional HKCU user-start item only, never
                // auto-disable Windows/Google/Edge/Firefox security updates.
                // Select the first ACTUALLY movable startup, not merely the
                // first loosely recognized inventory row. A malformed first
                // row must not mask a later safe independent registry view.
                StartupEntry? entry = FindAdmissibleStartup(
                    app, startup, requireNativeAdmission: true);
                if (entry != null)
                {
                    Owner owner = new("startup", entry.Id, app, now, true, false,
                        entry.Name, entry.Command, entry.RunPath, (int)entry.View);
                    if (await ApplyAsync(owner, async () =>
                    {
                        // A single targeted process recheck is substantially
                        // cheaper than enumerating all app CPU/RAM metrics
                        // and startup matches a second time before each write.
                        if (AiThirdPartyIdlePolicy.HasAnyRunningProcess(state.ProcessNames))
                            throw new PrewriteAdmissionException(
                                "Application relaunched before startup change");
                        var beforeWrite = await StartupManagerService
                            .GetAiUserRunAsync(token).ConfigureAwait(false);
                        if (!HasCurrentAdmissibleStartup(app, owner, beforeWrite,
                                requireNativeAdmission: true))
                            throw new PrewriteAdmissionException(
                                "Startup changed externally before AI write");
                        if (!AiThirdPartyIdlePolicy.MayTouchApp(
                                UserFeatureProfileService.Load(), app) ||
                            AiThirdPartyIdlePolicy.HasAnyRunningProcess(state.ProcessNames))
                            throw new PrewriteAdmissionException(
                                "App became active or protected before AI startup write");
                        RequireLiveAiWriteScope("startup");
                        await StartupManagerService.SetEnabledAsync(entry, false, token)
                            .ConfigureAwait(false);
                        var fresh = await StartupManagerService
                            .GetAiUserRunAsync(token).ConfigureAwait(false);
                        if (!IsStartupDisableConfirmed(owner, entry, fresh,
                                requireNativeAdmission: true))
                            throw new InvalidOperationException(
                                "AI startup disable readback left an enabled copy or lost the disabled owner");
                    }, token).ConfigureAwait(false))
                        return;
                }
            }

            // Steam helper is not Steam itself. It is demand-started and only
            // considered after a full hour with no Steam process activity.
            bool steamServiceCandidate = enabled && canOptimizeNow &&
                config.ServicesEnabled && _owned.Count < 3 &&
                !_references.ContainsKey(SteamServiceOwnerKey) &&
                !IsSteamServiceRetryDeferred(_feedback, now) &&
                !ownedPresence.HasSteamService &&
                observed.TryGetValue("steam", out var steam) &&
                steam.ProcessCount == 0 &&
                _absentSince.TryGetValue("steam", out DateTime lastSteam) &&
                AiThirdPartyIdlePolicy.CanReduce(
                    now - lastSteam, steam.Preference, false, gameActive, canOptimizeNow);
            if (!steamServiceCandidate)
                _steamPreStopSamples.Clear();
            if (steamServiceCandidate &&
                TryGetStableSteamBaseline(now, true, telemetry,
                    out double baselineCpu, out double baselineRamMiB))
            {
                using var service = new ServiceController("Steam Client Service");
                try
                {
                    bool freshTelemetry = AiSystemSmoothnessGuardService.IsFresh(
                        telemetry?.UpdatedUtc ?? default, now, TimeSpan.FromSeconds(65)) &&
                        telemetry is { GameActive: false, State: "Normal" } &&
                        double.IsFinite(telemetry.CpuLoadPercent) &&
                        telemetry.CpuLoadPercent is >= 0 and <= 100 &&
                        double.IsFinite(telemetry.FreeRamGiB) &&
                        telemetry.FreeRamGiB > 0;
                    // Do not stop a live service unless its baseline can
                    // actually be measured and any benefit/rollback assessed.
                    if (freshTelemetry &&
                        service.Status == ServiceControllerStatus.Running &&
                        service.CanStop &&
                        (int)service.StartType is 2 or 3 &&
                        HasNoActiveDependents(service))
                    {
                        Owner owner = new("service", "Steam Client Service",
                            "steam", now, true, true,
                            BaselineCpuPercent: baselineCpu,
                            BaselineRamMiB: baselineRamMiB,
                            OriginalServiceStartMode: (int)service.StartType);
                        if (await ApplyAsync(owner, () =>
                        {
                            if (AiThirdPartyIdlePolicy.HasAnyRunningProcess(
                                    "steam", "steamwebhelper") ||
                                !AiThirdPartyIdlePolicy.MayTouchApp(
                                    UserFeatureProfileService.Load(), "steam"))
                                throw new PrewriteAdmissionException(
                                    "Steam became active or protected before helper stop");
                            service.Refresh();
                            // The earlier dependency scan may be stale after
                            // write-ahead journal admission. Revalidate SCM
                            // dependents immediately before the native stop.
                            // Any newly running or paused dependency blocks
                            // the write, preserving the user's live service.
                            if (service.Status != ServiceControllerStatus.Running ||
                                !service.CanStop ||
                                (int)service.StartType != owner.OriginalServiceStartMode ||
                                !HasNoActiveDependents(service))
                                throw new PrewriteAdmissionException(
                                    "Steam helper or dependent service changed before stop");
                            RequireLiveAiWriteScope("service");
                            service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped,
                                TimeSpan.FromSeconds(8));
                            return Task.CompletedTask;
                        }, token).ConfigureAwait(false))
                            return; // Exactly one new mutation in this tick.
                    }
                }
                catch (InvalidOperationException) { /* Service not registered. */ }
            }

            // Optional third-party usage agent: never alter updates/security
            // schedulers or the user's ASUS peripheral/GearLink tasks.
            // Unlike the other 12 managed apps, Firefox uses a separate
            // optional task instead of the background-app inventory. Its
            // idle timer must also honor KEEP, a disabled Tasks switch and
            // the post-restore external-override grace period. Otherwise
            // re-enabling tasks could immediately reuse old idle credit.
            bool firefoxProtected = !firefoxPresenceKnown ||
                !config.ScheduledTasksEnabled ||
                profile!.Keeps("bg:firefox:keep") ||
                (_externallyOverriddenUntil.TryGetValue("firefox", out DateTime firefoxOverride) &&
                 firefoxOverride > now);
            if (firefoxRunning || firefoxProtected)
                _absentSince.Remove("firefox");
            if (enabled && canOptimizeNow && !firefoxProtected && !firefoxRunning &&
                !ownedPresence.HasApp("firefox"))
            {
                if (!_absentSince.TryGetValue("firefox", out var idle))
                    _absentSince["firefox"] = idle = now;
                if (now - idle >= TimeSpan.FromHours(1))
                {
                    foreach (var (_, prefix) in AiThirdPartyIdlePolicy.Tasks)
                    {
                        var task = await Task.Run(() => GetMatchingTask(prefix, token), token)
                            .ConfigureAwait(false);
                        if (task is null) continue;
                        // A process can launch between the periodic snapshot
                        // and actual COM write; recheck just before mutation.
                        bool liveFirefox = ProcessSnapshotCleanup.Inspect(
                            () => Process.GetProcessesByName("firefox"),
                            processes => processes.Length != 0);
                        if (liveFirefox) { _absentSince.Remove("firefox"); break; }
                        var current = TaskSchedulerComService.GetRuntimeState(task, token);
                        if (!current.Enabled || current.Running ||
                            AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(current, now) != null)
                            continue;
                        Owner owner = new("task", task, "firefox", now, true, false);
                        await ApplyAsync(owner, () =>
                        {
                            if (AiThirdPartyIdlePolicy.HasAnyRunningProcess("firefox") ||
                                !AiThirdPartyIdlePolicy.MayTouchApp(
                                    UserFeatureProfileService.Load(), "firefox"))
                                throw new PrewriteAdmissionException(
                                    "Firefox became active or protected before optional task");
                            var freshTask = TaskSchedulerComService.GetRuntimeState(task, token);
                            if (!freshTask.Enabled || freshTask.Running ||
                                AiMaintenanceRevalidationPolicy
                                    .TaskReapplyBlockReason(freshTask, DateTime.UtcNow) != null)
                                throw new PrewriteAdmissionException(
                                    "Optional Firefox task changed before AI write");
                            RequireLiveAiWriteScope("task");
                            TaskSchedulerComService.SetEnabled(task, false, token);
                            if (TaskSchedulerComService.GetRuntimeState(task, token).Enabled)
                                throw new InvalidOperationException(
                                    "Optional Firefox task disable readback failed");
                            return Task.CompletedTask;
                        }, token).ConfigureAwait(false);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI third-party idle cycle failed", ex);
        }
    }
}
