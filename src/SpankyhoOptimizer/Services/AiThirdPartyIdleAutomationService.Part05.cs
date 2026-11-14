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
    private async Task<bool> UpdateServiceFeedbackAsync(
        Owner owner, AiSystemSmoothnessHealth? telemetry,
        DateTime now, bool comparableIdle, CancellationToken token)
    {
        if (owner.Kind != "service" || owner.Pending) return false;
        string key = Key(owner.Kind, owner.Id);
        bool hasRecentVerdict = _feedback.TryGetValue(key, out var previous) &&
            previous!.UpdatedUtc >= owner.AppliedUtc;
        if (hasRecentVerdict)
            return previous!.Verdict is "no-observed-gain" or "inconclusive";
        if (MissingCompletedVerdict(owner.FeedbackSamples, hasRecentVerdict))
        {
            // A journal repair can intentionally quarantine an invalid
            // auxiliary verdict while preserving the already collected
            // three durable B samples. Do NOT add a fourth sample and then
            // fail the entire owner journal on the next leased read.
            _feedback[key] = new FeedbackOutcome(now, "inconclusive",
                owner.FeedbackSamples, AppKey: owner.AppKey);
            await PersistAsync(token).ConfigureAwait(false);
            return true; // Recover the previously stopped helper safely.
        }

        // A startup Run entry only saves next-logon work. By contrast the
        // Steam helper actually stops now, so ONLY the service receives a
        // live CPU/available-RAM trend estimate, using the existing cached
        // FanAgent telemetry (no extra hardware polling/process spawning).
        if (owner.BaselineCpuPercent is double baselineCpu &&
            owner.BaselineRamMiB is double baselineRam &&
            comparableIdle && telemetry is { GameActive: false, State: "Normal" } &&
            IsTrustedResourceSample(
                telemetry.CpuLoadPercent, telemetry.FreeRamGiB) &&
            IsServiceActuallyStopped(owner.Id) &&
            AiThirdPartyIdlePolicy.MayAcceptFeedbackSample(
                owner.AppliedUtc, owner.LastFeedbackSampleUtc,
                telemetry.UpdatedUtc, now))
        {
            var sampled = owner with {
                FeedbackSamples = owner.FeedbackSamples + 1,
                SumCpuPercent = owner.SumCpuPercent + telemetry.CpuLoadPercent,
                SumRamMiB = owner.SumRamMiB + telemetry.FreeRamGiB * 1024,
                LastFeedbackSampleUtc = telemetry.UpdatedUtc,
                MinCpuPercent = Math.Min(owner.MinCpuPercent ?? telemetry.CpuLoadPercent,
                    telemetry.CpuLoadPercent),
                MaxCpuPercent = Math.Max(owner.MaxCpuPercent ?? telemetry.CpuLoadPercent,
                    telemetry.CpuLoadPercent),
                MinRamMiB = Math.Min(owner.MinRamMiB ?? telemetry.FreeRamGiB * 1024,
                    telemetry.FreeRamGiB * 1024),
                MaxRamMiB = Math.Max(owner.MaxRamMiB ?? telemetry.FreeRamGiB * 1024,
                    telemetry.FreeRamGiB * 1024)
            };
            _owned[key] = sampled;
            // Keep completed samples across both hosts and restarts so the
            // other host never awards artificial extra evidence.
            if (sampled.FeedbackSamples >= 3)
            {
                double avgCpu = sampled.SumCpuPercent / sampled.FeedbackSamples;
                double avgRam = sampled.SumRamMiB / sampled.FeedbackSamples;
                bool comparable = AiThirdPartyIdlePolicy.IsComparableObservedWindow(
                    sampled.FeedbackSamples, sampled.MinCpuPercent, sampled.MaxCpuPercent,
                    sampled.MinRamMiB, sampled.MaxRamMiB);
                bool positive = comparable && AiThirdPartyIdlePolicy.HasObservedPositiveTrend(
                    baselineCpu, baselineRam, avgCpu, avgRam, sampled.FeedbackSamples);
                string verdict = !comparable ? "inconclusive" :
                    positive ? "positive-system-trend" : "no-observed-gain";
                _feedback[key] = new FeedbackOutcome(now, verdict,
                    sampled.FeedbackSamples,
                    comparable ? Math.Round(baselineCpu - avgCpu, 2) : null,
                    comparable ? Math.Round(avgRam - baselineRam, 1) : null,
                    owner.AppKey);
                LogService.Info("ai-system",
                    $"AI_APP_IDLE_FEEDBACK app={owner.AppKey} verdict={_feedback[key].Verdict} " +
                    $"cpuDeltaPp={_feedback[key].CpuDropPercentPoints:0.00} " +
                    $"availableRamDeltaMiB={_feedback[key].AvailableRamGainMiB:0.0} " +
                    "samples=3 causalProof=false");
            }
            await PersistAsync(token).ConfigureAwait(false);
            return sampled.FeedbackSamples >= 3 &&
                   _feedback[key].Verdict is "no-observed-gain" or "inconclusive";
        }

        // If the telemetry cannot produce three distinct fresh readings,
        // restore without inventing any performance benefit; re-try only
        // after a cooling period, never immediately oscillate this service.
        if (now - owner.AppliedUtc >= TimeSpan.FromMinutes(12))
        {
            _feedback[key] = new FeedbackOutcome(now, "inconclusive",
                owner.FeedbackSamples, AppKey: owner.AppKey);
            await PersistAsync(token).ConfigureAwait(false);
            LogService.Info("ai-system",
                $"AI_APP_IDLE_FEEDBACK app={owner.AppKey} verdict=inconclusive " +
                $"samples={owner.FeedbackSamples} causalProof=false");
            return true;
        }
        return false;
    }

    // A2 is observed ONLY after the normal six-hour auto-restore. Never
    // restart the service merely to manufacture a counterfactual reading.
    // Reuse FanAgent's cached health; at most three persisted samples.
    private async Task AdvancePassiveReferenceAsync(
        DateTime now, bool enabled, bool comparableIdle, bool steamAbsent,
        AiSystemSmoothnessHealth? telemetry, CancellationToken token)
    {
        if (_references.Count == 0) return;
        // .NET 10 permits replacing/removing existing dictionary entries
        // during enumeration. No reference is added in this leased A2 pass;
        // avoid allocating a copied KeyValuePair[] on every measured tick.
        foreach (var (key, reference) in _references)
        {
            token.ThrowIfCancellationRequested();
            // If AI is switched off, Steam is used, or the persisted clock
            // is invalid, abandon comparison without rewriting old evidence.
            if (!enabled || !steamAbsent ||
                reference.RestoredUtc.Kind != DateTimeKind.Utc ||
                reference.RestoredUtc > now)
            {
                _references.Remove(key);
                await PersistAsync(token).ConfigureAwait(false);
                continue;
            }

            if (reference.Samples >= 3)
            {
                // Recovery of a legacy/partially completed A2 trial must
                // not add a fourth sample (Samples is durably capped at 3).
                _feedback[key] = new FeedbackOutcome(now, "aba-inconclusive",
                    reference.Samples, AppKey: reference.AppKey);
                _references.Remove(key);
                await PersistAsync(token).ConfigureAwait(false);
                continue;
            }

            bool expired = now - reference.RestoredUtc >= TimeSpan.FromMinutes(12);
            bool sampleReady = !expired && comparableIdle &&
                telemetry is { GameActive: false, State: "Normal" } &&
                IsTrustedResourceSample(
                    telemetry.CpuLoadPercent, telemetry.FreeRamGiB) &&
                AiThirdPartyIdlePolicy.MayAcceptFeedbackSample(
                    reference.RestoredUtc, reference.LastSampleUtc,
                    telemetry.UpdatedUtc, now);
            if (sampleReady)
            {
                // A stopped helper is not a valid running-service reference.
                bool actuallyRestored = false;
                try
                {
                    using var helper = new ServiceController(reference.Id);
                    actuallyRestored = helper.Status == ServiceControllerStatus.Running;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Unknown reference state is inconclusive, never positive.
                }
                if (!actuallyRestored)
                {
                    _feedback[key] = new FeedbackOutcome(now, "aba-inconclusive",
                        reference.Samples, AppKey: reference.AppKey);
                    _references.Remove(key);
                    await PersistAsync(token).ConfigureAwait(false);
                    continue;
                }

                double cpu = telemetry!.CpuLoadPercent;
                double ram = telemetry.FreeRamGiB * 1024;
                var sampled = reference with
                {
                    Samples = reference.Samples + 1,
                    CpuSum = reference.CpuSum + cpu,
                    RamSum = reference.RamSum + ram,
                    LastSampleUtc = telemetry.UpdatedUtc,
                    MinCpu = Math.Min(reference.MinCpu ?? cpu, cpu),
                    MaxCpu = Math.Max(reference.MaxCpu ?? cpu, cpu),
                    MinRamMiB = Math.Min(reference.MinRamMiB ?? ram, ram),
                    MaxRamMiB = Math.Max(reference.MaxRamMiB ?? ram, ram)
                };
                _references[key] = sampled;
                if (sampled.Samples == 3)
                {
                    double refCpu = sampled.CpuSum / sampled.Samples;
                    double refRam = sampled.RamSum / sampled.Samples;
                    bool matched = AiThirdPartyIdlePolicy.HasMatchedAbaTrend(
                        sampled.BaselineCpu, sampled.BaselineRamMiB,
                        sampled.CandidateCpu, sampled.CandidateRamMiB,
                        refCpu, refRam, sampled.Samples,
                        sampled.MinCpu, sampled.MaxCpu,
                        sampled.MinRamMiB, sampled.MaxRamMiB);
                    string verdict = matched
                        ? "aba-matched-system-trend" : "aba-unconfirmed";
                    _feedback[key] = new FeedbackOutcome(now, verdict, sampled.Samples,
                        matched ? Math.Round(refCpu - sampled.CandidateCpu, 2) : null,
                        matched ? Math.Round(sampled.CandidateRamMiB - refRam, 1) : null,
                        sampled.AppKey);
                    _references.Remove(key);
                    LogService.Info("ai-system",
                        $"AI_APP_IDLE_ABA_REFERENCE_COMPLETE app={sampled.AppKey} " +
                        $"verdict={verdict} samples={sampled.Samples} causalProof=false");
                }
                await PersistAsync(token).ConfigureAwait(false);
            }
            else if (expired)
            {
                _feedback[key] = new FeedbackOutcome(now, "aba-inconclusive",
                    reference.Samples, AppKey: reference.AppKey);
                _references.Remove(key);
                await PersistAsync(token).ConfigureAwait(false);
                LogService.Info("ai-system",
                    $"AI_APP_IDLE_ABA_REFERENCE_TIMEOUT app={reference.AppKey} " +
                    $"samples={reference.Samples} causalProof=false");
            }
        }
    }

    private static string? GetMatchingTask(string prefix, CancellationToken token)
    {
        // A narrow, read-only registry task inventory. No arbitrary task
        // enumeration with mutation rights and no create/register call.
        token.ThrowIfCancellationRequested();
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\Mozilla");
        if (root == null) return null;
        foreach (string name in root.GetSubKeyNames())
        {
            string path = @"\Mozilla\" + name;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                AiThirdPartyIdlePolicy.MatchesOptionalFirefoxTask(path))
                return path;
        }
        return null;
    }

    // Decisions begin with the tick's quiet CPU/game/disk evidence, but
    // asynchronous native inventories and write-ahead persistence can delay
    // the actual mutation. Revalidate BOTH the selected automation scope and
    // the latest in-process quiet evidence at the final native write boundary.
    // Recovery/restoration never calls this gate; it must still work on AI-off.
    internal static bool MayMakeNewIdleNativeWrite(
        AiSystemRuntimeConfig current, string kind, bool quietEvidence)
    {
        bool scopeEnabled = kind switch
        {
            "startup" => true,
            "service" => current.ServicesEnabled,
            "task" => current.ScheduledTasksEnabled,
            _ => false
        };
        return current.Enabled && current.SelfOptimizationEnabled &&
            scopeEnabled && quietEvidence;
    }

    private static void RequireLiveAiWriteScope(string kind)
    {
        AiSystemRuntimeConfig current = AiSystemResourceManagerService.LoadConfig();
        // The cached FanAgent measurement and recovery cooldown are checked
        // afresh, not the tick's old canOptimizeNow boolean. Missing or stale
        // measurements fail closed without extra process/hardware polling.
        bool quietEvidence = AiSystemSmoothnessGuardService.AllowsBackgroundExperiment();
        if (!MayMakeNewIdleNativeWrite(current, kind, quietEvidence))
            throw new PrewriteAdmissionException(
                "AI scope or fresh CPU/game/disk quiet evidence lost before native write");
    }

    private async Task<bool> ApplyAsync(Owner owner, Func<Task> apply, CancellationToken token)
    {
        string key = Key(owner.Kind, owner.Id);
        _owned[key] = owner; // Durable ownership BEFORE the first Windows write.
        await PersistAsync(token).ConfigureAwait(false);
        try
        {
            await apply().ConfigureAwait(false);
            _owned[key] = owner with { Pending = false };
            if (owner.Kind == "startup")
                _feedback[key] = new FeedbackOutcome(DateTime.UtcNow,
                    "deferred-until-next-startup", 0, AppKey: owner.AppKey);
            else if (owner.Kind == "task")
                _feedback[key] = new FeedbackOutcome(DateTime.UtcNow,
                    "deferred-until-task-trigger", 0, AppKey: owner.AppKey);
            await PersistAsync(token).ConfigureAwait(false);
            LogService.Info("ai-system",
                $"AI_APP_IDLE_APPLIED kind={owner.Kind} app={owner.AppKey} id={owner.Id}");
            return true;
        }
        catch (PrewriteAdmissionException ex)
        {
            // Admission failed BEFORE the first native mutation. Keeping the
            // write-ahead owner here would let a later restore override an
            // entry the user disabled independently in the meantime.
            _owned.Remove(key);
            try
            {
                // Recovery cleanup is important even if a runtime tick was
                // cancelled after the original journal was committed.
                await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                _owned[key] = owner; // Journal still contains pending owner.
                throw;
            }
            LogService.Info("ai-system",
                $"AI_APP_IDLE_PREWRITE_ABORT kind={owner.Kind} app={owner.AppKey} " +
                $"reason={ex.Message} pendingOwnerCleared=true windowsWrites=0");
            return false;
        }
        catch
        {
            // A Windows write may have partially succeeded. Keep durable
            // ownership for safe recovery rather than assuming no mutation.
            throw;
        }
    }

    // The tick's startup inventory is captured BEFORE awaited process and
    // telemetry reads. A user may re-enable an AI-owned HKCU Run entry in the
    // meantime; consulting only that old snapshot would keep it 'owned'
    // indefinitely. Query fresh native state before deciding whether
    // to keep the hold, not just later inside RestoreAsync. An unreadable
    // startup snapshot must abort this tick and retry instead of being
    // silently misclassified as an idle resource.
    private static async Task<bool> HasFreshEnabledOwnedStartupAsync(
        Owner owner,
        Func<CancellationToken, Task<IReadOnlyList<StartupEntry>>> read,
        CancellationToken token)
    {
        IReadOnlyList<StartupEntry> current = await read(token).ConfigureAwait(false);
        // A user can re-enable the SAME registry value and simultaneously
        // edit its command. That still ends our right to hold the startup
        // disabled: detect an occupied Run SLOT, not only byte-identical
        // owner commands. RestoreAsync will separately reject any ambiguous
        // or repointed target without modifying external registry values.
        return HasOwnedStartupSlot(owner, current, enabled: true);
    }

    private static async Task<bool> WasExternallyRestoredAsync(
        Owner owner, CancellationToken token)
    {
        if (owner.Kind == "startup")
            return await HasFreshEnabledOwnedStartupAsync(
                owner, StartupManagerService.GetAiUserRunAsync, token)
                .ConfigureAwait(false);
        try
        {
            if (owner.Kind == "service")
            {
                using var service = new ServiceController(owner.Id);
                return service.Status == ServiceControllerStatus.Running;
            }
            if (owner.Kind == "task")
                return TaskSchedulerComService.GetRuntimeState(owner.Id, token).Enabled;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }
        return false;
    }
}
