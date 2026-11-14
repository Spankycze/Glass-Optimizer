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

internal static partial class AiThirdPartyIdlePolicy
{
    internal static bool HasObservedPositiveTrend(
        double baselineCpuPercent, double baselineAvailableRamMiB,
        double candidateCpuPercent, double candidateAvailableRamMiB, int samples)
    {
        if (samples < 3 ||
            !double.IsFinite(baselineCpuPercent) || !double.IsFinite(baselineAvailableRamMiB) ||
            !double.IsFinite(candidateCpuPercent) || !double.IsFinite(candidateAvailableRamMiB) ||
            baselineCpuPercent is < 0 or > 100 || candidateCpuPercent is < 0 or > 100 ||
            baselineAvailableRamMiB < 0 || candidateAvailableRamMiB < 0)
            return false;
        double cpuDrop = baselineCpuPercent - candidateCpuPercent;
        double ramIncrease = candidateAvailableRamMiB - baselineAvailableRamMiB;
        return (cpuDrop >= 0.8 && ramIncrease >= -64) ||
               (ramIncrease >= 96 && cpuDrop >= -0.5);
    }

    // UI explanation stays explicit that CPU/RAM trends are observational.
    internal static string DescribeFeedback(
        string verdict, int samples, double? cpuDrop, double? ramGain) => verdict switch
    {
        "positive-system-trend" when cpuDrop is double cpu && ramGain is double ram =>
            $"Pozorovaný trend CPU {cpu:+0.00;-0.00;0.00} p. b., " +
            $"dostupná RAM {ram:+0;-0;0} MiB ({samples} měření; příčina nepotvrzena)",
        "aba-matched-system-trend" when cpuDrop is double cpu && ramGain is double ram =>
            $"A/B/A: CPU {cpu:+0.00;-0.00;0.00} p. b., RAM {ram:+0;-0;0} MiB " +
            "(opakovaný systémový trend, nikoli důkaz příčiny)",
        "aba-unconfirmed" =>
            "A/B/A nepotvrdilo rozdíl po obnovení služby • další pokus nejdříve za 24 h",
        "aba-inconclusive" =>
            $"A/B/A neúplné ({samples}/3 měření) • příčina nepotvrzena • odklad 4 h",
        "no-observed-gain" =>
            $"Bez měřitelného přínosu ({samples} měření) • obnova • další pokus nejdříve za 24 h",
        "inconclusive" =>
            $"Neprůkazná telemetrie ({samples}/3 měření) • obnova • odklad 4 h",
        "deferred-until-next-startup" =>
            "Omezeno automatické spouštění • úspora při tomto běhu se neměří",
        "deferred-until-task-trigger" =>
            "Odložena volitelná úloha • úspora CPU/RAM zatím nezjištěna",
        _ => "Výsledek AI není ověřen."
    };

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var genuineSpotify = new StartupEntry(
            $"{Microsoft.Win32.RegistryHive.CurrentUser}|{Microsoft.Win32.RegistryView.Registry64}|{run}|Spotify",
            "Spotify", @"""C:\Apps\Spotify\Spotify.exe"" --minimized",
            Microsoft.Win32.RegistryHive.CurrentUser, run, true,
            "Spotify", @"C:\Apps\Spotify\Spotify.exe");
        var fakeUpdater = genuineSpotify with
        {
            Name = "Spotify updater",
            FilePath = @"C:\Apps\Spotify\Update.exe"
        };
        if (!IsFailedOffRestoreCoolingDown(false, 1,
                now.AddSeconds(-29), now) ||
            IsFailedOffRestoreCoolingDown(false, 1,
                now.AddSeconds(-30), now) ||
            IsFailedOffRestoreCoolingDown(true, 1,
                now.AddSeconds(-1), now) ||
            IsFailedOffRestoreCoolingDown(false, 0,
                now.AddSeconds(-1), now) ||
            IsFailedOffRestoreCoolingDown(false, 2,
                now.AddSeconds(1), now) ||
            IsFailedOffRestoreCoolingDown(false, 2,
                DateTime.SpecifyKind(now, DateTimeKind.Local), now) ||
            !CanThrottleDisabledIdleTick(false, 0, 0,
                now.AddSeconds(-29), now) ||
            CanThrottleDisabledIdleTick(false, 0, 0,
                now.AddSeconds(-30), now) ||
            CanThrottleDisabledIdleTick(true, 0, 0,
                now.AddSeconds(-1), now) ||
            CanThrottleDisabledIdleTick(false, 1, 0,
                now.AddSeconds(-1), now) ||
            CanThrottleDisabledIdleTick(false, 0, 1,
                now.AddSeconds(-1), now) ||
            CanThrottleDisabledIdleTick(false, 0, 0,
                now.AddSeconds(1), now) ||
            CanThrottleDisabledIdleTick(false, 0, 0, default, now) ||
            NeedsStartupInventory(false, false) ||
            !NeedsStartupInventory(false, true) ||
            !NeedsStartupInventory(true, false) ||
            NeedsStartupInventory(true, false, false) ||
            !NeedsStartupInventory(false, true, false) ||
            !NeedsStartupInventory(true, false, true) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, false, true, 0, false, false),
                false) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 3, false, false),
                false) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 0, true, false),
                false) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 0, false, true),
                false) ||
            !NeedsStartupInventory(
                MayIntroduceChange(true, false, true, 3, true, true),
                true) ||
            !NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 0, false, false),
                false) ||
            !CanSkipUnownedInventory(true, false, true, 0, 0) ||
            !CanSkipUnownedInventory(true, true, false, 0, 0) ||
            CanSkipUnownedInventory(true, true, true, 0, 0) ||
            CanSkipUnownedInventory(false, false, false, 0, 0) ||
            CanSkipUnownedInventory(true, false, true, 1, 0) ||
            CanSkipUnownedInventory(true, false, true, 0, 1) ||
            CanSkipUnownedInventory(true, true, false, 1, 0) ||
            CanSkipUnownedInventory(true, true, false, 0, 1) ||
            NeedsFirefoxPresence(false, true, true, true, true) ||
            !NeedsFirefoxPresence(true, true, false, false, false) ||
            NeedsFirefoxPresence(true, false, true, false, true) ||
            NeedsFirefoxPresence(true, false, true, true, false) ||
            NeedsFirefoxPresence(true, false, false, true, true) ||
            !NeedsFirefoxPresence(true, false, true, true, true) ||
            IsOwnedRecoveryProbeDue(0, default, now) ||
            !IsOwnedRecoveryProbeDue(1, default, now) ||
            IsOwnedRecoveryProbeDue(1, now.AddSeconds(-9), now) ||
            !IsOwnedRecoveryProbeDue(1, now.AddSeconds(-10), now) ||
            !IsOwnedRecoveryProbeDue(3, now.AddSeconds(1), now) ||
            !MayObserveReference(true, true, true) ||
            MayObserveReference(true, false, true) ||
            MayObserveReference(false, true, true) ||
            MayObserveReference(true, true, false) ||
            !CanAccumulateIdleEvidence(true, true) ||
            CanAccumulateIdleEvidence(true, false) ||
            CanAccumulateIdleEvidence(false, true) ||
            CanAccumulateIdleEvidence(false, false) ||
            !MayKeepOwnedHold("service", true, true, true, now.AddHours(-5), now) ||
            MayKeepOwnedHold("service", true, false, true, now, now) ||
            MayKeepOwnedHold("task", true, true, false, now, now) ||
            MayKeepOwnedHold("startup", false, true, true, now, now) ||
            MayKeepOwnedHold("service", true, true, true, now.AddHours(-6), now) ||
            !MayKeepOwnedHold("startup", true, true, true, now.AddHours(-7), now) ||
            !MayKeepOwnedHold("startup", true, true, true, now.AddDays(-2), now) ||
            MayKeepOwnedHold("startup", true, true, true,
                DateTime.SpecifyKind(default, DateTimeKind.Utc), now) ||
            MayKeepOwnedHold("task", true, true, true, now.AddHours(-7), now) ||
            MayKeepOwnedHold("startup", true, true, true, now.AddMinutes(1), now) ||
            MayKeepOwnedHold("startup", true, true, true,
                DateTime.SpecifyKind(now, DateTimeKind.Local), now) ||
            !MayKeepOwnedHold("startup", true, false, false, now, now) ||
            !IsContinuousObservation(now.AddMinutes(-3), now) ||
            !IsContinuousObservation(now.AddMinutes(-5), now) ||
            IsContinuousObservation(now.AddMinutes(-6), now) ||
            IsContinuousObservation(now.AddMinutes(-11), now) ||
            IsContinuousObservation(now.AddMinutes(1), now) ||
            IsContinuousObservation(default, now) ||
            !MayRetryJournalRead(default, now) ||
            MayRetryJournalRead(now.AddSeconds(-30), now) ||
            !MayRetryJournalRead(now.AddMinutes(-1), now) ||
            !MayRetryJournalRead(now.AddMinutes(1), now) ||
            !MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 3, 3) ||
            !MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 2, null) ||
            MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 4, 3) ||
            MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 2, 3) ||
            MayRestoreServiceStart(true, ServiceControllerStatus.Running, 3, 3) ||
            MayRestoreServiceStart(false, ServiceControllerStatus.Stopped, 3, 3) ||
            !MayRestoreFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB",
                true, false) ||
            MayRestoreFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB",
                true, true) ||
            MayRestoreFirefoxTask(
                @"\Mozilla\Firefox Update Agent 308046B0AF4A39CB",
                true, false) ||
            !HasOnlyStoppedDependents(Array.Empty<ServiceControllerStatus?>()) ||
            !HasOnlyStoppedDependents(new ServiceControllerStatus?[]
                { ServiceControllerStatus.Stopped }) ||
            HasOnlyStoppedDependents(new ServiceControllerStatus?[]
                { ServiceControllerStatus.Paused }) ||
            HasOnlyStoppedDependents(new ServiceControllerStatus?[]
                { ServiceControllerStatus.Running }) ||
            HasOnlyStoppedDependents(new ServiceControllerStatus?[] { null }) ||
            !BackgroundActivityService.HasExactAiInventoryScope() ||
            !BackgroundActivityService.RunAiPreferenceSnapshotSmoke() ||
            StartupManagerService.NeedsPublisherMetadata(aiInventory: true) ||
            !StartupManagerService.NeedsPublisherMetadata(aiInventory: false) ||
            BackgroundProcessSnapshotService.ShouldReadResourceCounters(true) ||
            !BackgroundProcessSnapshotService.ShouldReadResourceCounters(false) ||
            MaySampleStoppedHelper(ServiceControllerStatus.Running) ||
            MaySampleStoppedHelper(ServiceControllerStatus.Paused) ||
            MaySampleStoppedHelper(null) ||
            !MaySampleStoppedHelper(ServiceControllerStatus.Stopped) ||
            !MayTouchApp(new UserFeatureProfile(Array.Empty<string>(), now), "spotify") ||
            MayTouchApp(new UserFeatureProfile(new[] { "bg:spotify:keep" }, now), "spotify") ||
            MayTouchApp(new UserFeatureProfile(Array.Empty<string>(), now)
                { IsUntrusted = true }, "spotify") ||
            MayTouchApp(new UserFeatureProfile(Array.Empty<string>(), now), "") ||
            !CanManageStartup("spotify", genuineSpotify) ||
            !CanManageStartup("steam", genuineSpotify with
            {
                FilePath = @"C:\Apps\Steam\steam.exe", Name = "Steam",
                Command = @"""C:\Apps\Steam\steam.exe"" -silent"
            }) ||
            CanManageStartup("spotify", fakeUpdater) ||
            CanManageStartup("spotify", genuineSpotify with { Name = "Spotify updater" }) ||
            CanManageStartup("spotify", genuineSpotify with
                { RunPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce" }) ||
            CanManageStartup("spotify", genuineSpotify with
                { Hive = Microsoft.Win32.RegistryHive.LocalMachine }) ||
            CanManageStartup("spotify", genuineSpotify with { Enabled = false }) ||
            !AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(genuineSpotify) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Command = null! }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Command = "  " }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { View = Microsoft.Win32.RegistryView.Default }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Id = null! }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Id = "" }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Id = "startup|foreign" }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Name = "Foreign" }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { View = Microsoft.Win32.RegistryView.Registry32 }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Enabled = false }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(null) ||
            CanManageStartup("spotify", genuineSpotify with
                { FilePath = "Spotify.exe" }) ||
            CanManageStartup("discord", genuineSpotify) ||
            !MatchesOptionalFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB") ||
            MatchesOptionalFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CZ") ||
            MatchesOptionalFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB extra") ||
            MatchesOptionalFirefoxTask(@"\Mozilla\Firefox Update Agent 308046B0AF4A39CB"))
            throw new InvalidOperationException("AI third-party startup/task identity regressed.");
        if (!MayIntroduceChange(true, true, false, 0, false, false) ||
            !MayIntroduceChange(true, true, false, 2, false, false) ||
            MayIntroduceChange(true, true, false, 3, false, false) ||
            MayIntroduceChange(true, true, true, 0, false, false) ||
            MayIntroduceChange(true, false, false, 0, false, false) ||
            MayIntroduceChange(false, true, false, 0, false, false) ||
            MayIntroduceChange(true, true, false, 0, true, false) ||
            MayIntroduceChange(true, true, false, 0, false, true) ||
            !OwnedPresenceRequiresRestore(false, false) ||
            !OwnedPresenceRequiresRestore(false, true) ||
            !OwnedPresenceRequiresRestore(true, true) ||
            OwnedPresenceRequiresRestore(true, false) ||
            // Late CPU/game/disk quiet-evidence loss must deny the LAST
            // prewrite gate for each of the three optional native lanes.
            !AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "startup", true) ||
            !AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "service", true) ||
            !AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "task", true) ||
            AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "startup", false) ||
            AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(ServicesEnabled: false), "service", true) ||
            AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(ScheduledTasksEnabled: false), "task", true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Keep, false, false, true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Unknown, true, false, true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Unknown, false, true, true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Unknown, false, false, false) ||
            CanReduce(TimeSpan.FromMinutes(59), BackgroundAppPreference.Unknown, false, false, true) ||
            !CanReduce(TimeSpan.FromMinutes(60), BackgroundAppPreference.Unknown, false, false, true) ||
            !CanReduce(TimeSpan.FromMinutes(20), BackgroundAppPreference.Unused, false, false, true) ||
            AppKeys.Length < 12 ||
            !AppKeys.Contains("steam", StringComparer.OrdinalIgnoreCase) ||
            !AppKeys.Contains("spotify", StringComparer.OrdinalIgnoreCase) ||
            Services.Length != 1 || Tasks.Length != 1 ||
            AppKeys.Contains("chrome", StringComparer.OrdinalIgnoreCase) ||
            Services.Any(x => x.Name.Contains("update", StringComparison.OrdinalIgnoreCase)) ||
            Tasks.Any(x => x.Prefix.Contains("Update", StringComparison.OrdinalIgnoreCase)) ||
            HasObservedPositiveTrend(8, 8000, 5, 8200, 2) ||
            !HasObservedPositiveTrend(8, 8000, 6.5, 7990, 3) ||
            !HasObservedPositiveTrend(8, 8000, 8.2, 8130, 3) ||
            HasObservedPositiveTrend(8, 8000, 8, 8000, 3) ||
            HasObservedPositiveTrend(double.NaN, 8000, 2, 9000, 3) ||
            HasObservedPositiveTrend(8, 8000, 6, 7000, 3) ||
            !IsComparableObservedWindow(3, 4, 5.2, 8190, 8220) ||
            !IsComparablePreStopWindow(3, 4, 5.2, 8190, 8220) ||
            IsComparablePreStopWindow(2, 4, 5.2, 8190, 8220) ||
            IsComparablePreStopWindow(3, 4, 6, 8190, 8220) ||
            IsComparablePreStopWindow(3, 4, 5.2, 8100, 8300) ||
            !RunStartupExecutableAllocationParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunPreStopEvidenceSmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedProbeBitsetSmoke() ||
            !AiThirdPartyIdleAutomationService.RunSteamRetryLookupParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunRestorePrioritySmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedReferenceEnumerationSmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedPresenceParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupDirectLookupParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupDualStateRestore164Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupOccupiedDestination165Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupRestoreSlotCollision166Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupExternalResolution168Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupRegistryRace180Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupRegistryAdmission181Smoke() ||
            !TaskSchedulerService.RunTaskMutationSafety187Smoke() ||
            !TaskSchedulerComService.RunTaskRollbackOwnership188Smoke() ||
            !TaskSchedulerService.RunTaskBatchConflict189Smoke() ||
            !TaskSchedulerComService.RunTaskMissingVsUnreadable190Smoke() ||
            !PowerPlanGuardService.RunOwnedGuardTask191Smoke() ||
            !PerformanceProfilesService.RunDuplicatePowerPlanPreflight203Smoke() ||
            !PerformanceProfilesService.RunPerformanceApplyPreflight204Smoke() ||
            !PerformanceProfilesService.RunPerformanceGuardCommit205Smoke() ||
            !AiThirdPartyIdleAutomationService.RunJournalIdentitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunJournalAppIndexParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedJournalValidationParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunUtf8JournalWriteParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunBoundedJournalByteSliceSmoke() ||
            !AiThirdPartyIdleAutomationService.RunLearningRepairSmoke() ||
            !AiThirdPartyIdleAutomationService.RunLearningNormalizationParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunFeedbackProjectionSmoke() ||
            !AiThirdPartyIdleAutomationService.RunFeedbackTopThreeParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunFeedbackRetentionSmoke() ||
            !AiThirdPartyIdleAutomationService.RunFailedJournalWriteSmoke() ||
            !AiThirdPartyIdleAutomationService.RunPhaseTimingSmoke() ||
            !AiThirdPartyIdleAutomationService.RunResourceSampleRecoverySmoke() ||
            !AiThirdPartyIdleAutomationService.RunTelemetryCultureSmoke() ||
            !HasMatchedAbaTrend(8, 8000, 6.4, 8120, 8.1, 8020, 3,
                7.9, 8.2, 8000, 8030) ||
            HasMatchedAbaTrend(8, 8000, 6.4, 8120, 5, 8020, 3,
                4.9, 5.2, 8000, 8030) ||
            HasMatchedAbaTrend(8, 8000, 6.4, 8120, 8.1, 8300, 3,
                7.9, 8.2, 8280, 8320) ||
            HasMatchedAbaTrend(8, 8000, 6.4, 8120, 8.1, 8020, 2,
                7.9, 8.2, 8000, 8030) ||
            !DeferServiceRetry("aba-matched-system-trend", now.AddDays(-5), now) ||
            DeferServiceRetry("aba-matched-system-trend", now.AddDays(-8), now) ||
            !DeferServiceRetry("aba-unconfirmed", now.AddHours(-6), now) ||
            DeferServiceRetry("aba-unconfirmed", now.AddHours(-25), now) ||
            !DeferServiceRetry("aba-inconclusive", now.AddHours(-3), now) ||
            DeferServiceRetry("aba-inconclusive", now.AddHours(-5), now) ||
            !DescribeFeedback("aba-inconclusive", 1, null, null).Contains("neúplné") ||
            !DescribeFeedback("aba-matched-system-trend", 3, 1.3, 120).Contains("A/B/A") ||
            IsComparableObservedWindow(2, 4, 5.2, 8190, 8220) ||
            IsComparableObservedWindow(3, 4, 6.1, 8190, 8220) ||
            IsComparableObservedWindow(3, 4, 5.2, 8000, 8200) ||
            IsComparableObservedWindow(3, 4, double.NaN, 8190, 8220) ||
            !DescribeFeedback("positive-system-trend", 3, 1.2, 140)
                .Contains("příčina nepotvrzena") ||
            !DescribeFeedback("no-observed-gain", 3, -0.2, 0).Contains("24 h") ||
            !DescribeFeedback("inconclusive", 2, null, null).Contains("odklad 4 h") ||
            DescribeFeedback("deferred-until-next-startup", 0, null, null)
                .Contains("pozorovaný trend") ||
            MayAcceptFeedbackSample(now.AddMinutes(-1), default, now, now) ||
            MayAcceptFeedbackSample(now.AddMinutes(-5), now.AddSeconds(-30), now, now) ||
            !MayAcceptFeedbackSample(now.AddMinutes(-5), now.AddMinutes(-2), now, now) ||
            MayAcceptFeedbackSample(now.AddMinutes(-5), default, now.AddMinutes(-2), now))
            throw new InvalidOperationException("AI app inactivity decision regressed.");
        return "AI_THIRD_PARTY_IDLE_SMOKE_OK unused20m unknown60m keepNever=true " +
            "runningRestored=true gameDeferred=true updatersUntouched=true " +
            "cpuRamThreeSampleTrend=true comparableWindow=true feedbackFreshness=true " +
            "feedbackDescriptions=true passiveAbaMatched=true retryLearning=true " +
            "feedbackTop3StableBounded=true feedbackPruneNoSnapshot=true " +
            "learningOrderedSinglePassFastPath=true a2ReferencesDirectValidation=true " +
            "isolatedMutations=true measuredStopRequiresBaseline=true " +
            "strictStartupIdentity=true optionalTaskHexIdentity=true " +
            "startupExecutableSpanNoCopy=true canonicalStaticAppNoLowerAllocation=true " +
            "targetedProcessRecheck=true taskReadback=true " +
            "lateKeepPreferenceGuard=true abortedPrewriteOwnershipClean=true " +
            "journalReadRetry=true nativeStoppedServiceFeedback=true " +
            "subFeatureRestore=true invalidHoldTimeRollback=true " +
            "configChangeBypassesThrottle=true absenceGapResets=true " +
            "stableThreeSamplePreStopBaseline=true baselineSpikeRejected=true " +
            "urgentOwnedRecoveryProbe10s=true recoveryFailureNoStarvation=true " +
            "allocationFreeDistinctRecoveryApps=true " +
            "ownerProbeBitsetNoDuplicateNativeChecks=true steamCooldownDirectLookup=true " +
            "stableThreePhaseRecoveryNoSortAllocation=true " +
            "ownedAdmissionNoLinq=true a2ReferenceNoArraySnapshot=true " +
            "leasedOwnerPresenceSinglePass=true ownerAppBitset=true " +
            "firefoxPresenceReusesAiSnapshot=true prewriteRecheckPreserved=true " +
            "referenceCancelledOnServicesOff=true aiOff30sNoScan=true " +
            "failedLeaseFastRetry=true selectiveAiOffInventory=true " +
            "crossHostJournalStamp10s=true strictPersistedOwnerIdentity=true " +
            "restoreStartupExecutableRecheck=true perf15mBounded=true " +
            "monotonicTickDurationMs=true diagnosticExceptionIsolated=true " +
            "startupRestoreNativeReadback=true aiPresenceOnlyNoCpuRamCounters=true " +
            "startupRecoveryNoLinqPredicates=true syntheticStartupParity=true " +
            "startupDisableReadbackNoLinq=true " +
            "startupAdmissionAndPrewriteNoLinq=true " +
            "scmDependentsDisposed=true pausedDependentsBlockStop=true " +
            "narrowHkcuRunInventory=true aiInventoryOnlyManagedApps=true " +
            "aiStartupPublisherMetadataSkipped=true fullUiCountersPreserved=true " +
            "onePreferenceSnapshotPerScan=true failedInventoryRetriesNextTick=true " +
            "untrustedPrefsSkipHkcuInventory=true ownedStartupStillRestored=true " +
            "skippedNativeInventoriesReported15m=true " +
            "unownedBusyInventoryShortCircuit=true " +
            "untrustedPrefsAppInventorySkipped=true " +
            "ownedAndReferenceRecoveryNotSkipped=true " +
            "skippedIdleCreditReset=true " +
            "offRecovery30sBackoff=true exactStartupCommandRecheck=true " +
            "serviceStartModeExternalOverridePreserved=true " +
            "firefoxTaskRunningRestoreBlocked=true " +
            "actualJournalBytesBounded256KiB=true feedbackValidated=true " +
            "referenceValidated=true repairedLearningPreservesOwner=true " +
            "writeBounded256KiB=true staleJournalOverwriteBlocked=true " +
            "feedbackResourceIdentity=true referenceSampleFinite=true " +
            "uiProjectionValidatesFeedback=true missingJournalFailClosed=true " +
            "failedCommitForcesLeasedReload=true tempCleanupNonMasking=true " +
            "sha256SameStampTamperBlocked=true noExtraProbeHashes=true " +
            "singleUtf8JournalBuffer=true boundedHashOnlyConflict=true " +
            "uiJournalDecodeWithoutUnusedDigest=true singlePassOwnerValidation=true " +
            "journalReadZeroBufferCopy=true journalExactSliceSha256=true " +
            "journalSeekableOneAllocation=true ownerProbeSharedKeyIndex=true " +
            "journalTrustedAppSharedIndex=true steamJournalKeyReused=true " +
            "strictUtf8DecoderReused=true " +
            "duplicateJsonOwnerKeysRejectedBeforeDeserialize=true " +
            "phaseAvgMaxMsNoExtraInventory=true " +
            "cpuIntervalAllCoresNoExtraSample=true " +
            "trustedResourceTelemetrySharedAcrossA_B_A2=true " +
            "completedThreeSampleOwnerNeverOversampled=true " +
            "verdictResourceSampleConsistency=true " +
            "perfLogInvariantDecimals=true phaseNoScanReportedNA=true " +
            "monotonicCpuWindow=true noGainDeferred=true writes=0";
    }
}
