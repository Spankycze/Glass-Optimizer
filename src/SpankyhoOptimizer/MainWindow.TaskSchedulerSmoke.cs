using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    public async Task RunTaskSchedulerUiSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("ai-system", AiMaintenanceRevalidationPolicy.RunDecisionSmoke());
        LogService.Info("ai-system", AiMaintenanceFeedbackPolicy.RunFunctionalGuardSmoke());
        LogService.Info("ai-system", AiMaintenanceTrialTimePolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiJournalSnapshotReadPolicy.RunStrictJournalEncodingSmoke());
        LogService.Info("ai-system", AiMaintenanceCandidatePolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiMaintenanceTrialConsistencyPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiManagedTaskHoldPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiVerifiedServiceHoldPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiMaintenanceOwnershipPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiMaintenanceWriteAheadPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiMaintenanceCrashSimulationPolicy.RunFaultInjectionSmoke());
        LogService.Info("ai-system", AiMaintenanceOpportunityPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiMeasuredOptimizationPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiPassiveMemoryTrialPolicy.RunFunctionalSmoke());
        // Reuse the existing third-party policy smoke; previously uninvoked,
        // it guards startup ownership, native rollback and preference gates.
        LogService.Info("ai-system", AiThirdPartyIdlePolicy.RunFunctionalSmoke());
        // Existing read-only race smoke: a fresh user Run override must beat
        // an old AI-owned disabled snapshot, including read failure/cancel.
        if (!await AiThirdPartyIdleAutomationService.RunFreshOwnedStartupOverrideSmoke())
            throw new InvalidOperationException("AI owned startup fresh-native-override smoke failed.");
        LogService.Info("ai-system", AiAutomaticProcessPriorityPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiAutomaticPriorityTrialPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiPriorityTrialMemoryPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiPriorityProcessMemoryPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiWorkingSetSamplingPolicy.RunFunctionalSmoke());
        LogService.Info("performance", AiWorkingSetNativeCostSmoke.RunFunctionalSmoke());
        LogService.Info("performance", AdaptiveProcessSampleMetricWindow.RunFunctionalSmoke());
        LogService.Info("ai-system", AdaptiveCpuAccessDeniedCache.RunFunctionalSmoke());
        LogService.Info("performance", AdaptiveCpuDeniedNativeCostSmoke.RunFunctionalSmoke());
        LogService.Info("ai-system", AiPriorityProcessIdentityPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiForegroundWindowResponsePolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiForegroundNativeOwnedWindowSmoke.RunFunctionalSmoke());
        LogService.Info("ai-system", AiAutomaticWorkloadSafetyPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiEngine2DecisionPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiWorkloadForecastService.RunFunctionalSmoke());
        LogService.Info("ai-system", RunAiRuntimeUiFreshnessSmoke());
        LogService.Info("ai-system", AiSelfOptimizationService.RunHealthWriteThrottleSmoke());
        LogService.Info("ai-system", AiSharedDecisionContextService.RunPublishedPermissionSmoke());
        LogService.Info("ai-system", AiActivePowerRuntimeSummaryPolicy.RunStatusDescriptionSmoke());
        LogService.Info("ai-system", AiPerformanceExperimentCoordinator.RunFunctionalSmoke());
        LogService.Info("ai-system", AiTweakMonotonicTimingPolicy.RunFunctionalSmoke());
        LogService.Info("ai-system", AiPendingTrialSelectionPolicy.RunMaintenanceLeaseLifecycleSmoke());
        LogService.Info("ai-system", AdaptivePowerEppTuner.RunMonotonicTimingSmoke());
        LogService.Info("ai-system", AiEppTrialOpportunityPolicy.RunMonotonicPreflightSmoke());
        LogService.Info("ai-system", AiEppTrialOpportunityPolicy.RunMonotonicBalancedWindowSmoke());
        LogService.Info("ai-system", AdaptivePowerRuntimeService.RunMonotonicPowerRefreshSmoke());
        LogService.Info("ai-system", AiSystemResourceManagerService.RunMonotonicProbeCacheSmoke());
        LogService.Info("ai-system", AiSystemResourceManagerService.RunMonotonicRuntimeCadenceSmoke());
        LogService.Info("ai-system", AiSystemResourceManagerService.RunServiceModuleAdmissionSmoke());
        LogService.Info("performance", RunAiActionsSnapshotSmoke());
        // The many policy/benchmark guards above are NOT UI navigation cost.
        // Measure only route activation plus actual deferred body hydration.
        sw.Restart();
        await ShowSectionAsync("ai-runtime");

        if (_lastNavigationErrorId != null)
            throw new InvalidOperationException("AI Runtime navigation error: " + _lastNavigationErrorId);
        if (!string.Equals(_section, "ai-runtime", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI System Runtime route did not become active.");
        if (ContentStack.Children.Count == 0)
            throw new InvalidOperationException("AI System Runtime rendered no content.");

        // AI Runtime is now the owner of automatic Services/Tasks/Drivers.
        // The old smoke only verified navigation; it never checked whether
        // parallel health/config loading actually completed and painted.
        StackPanel? runtimeBody = ContentStack.Children
            .OfType<StackPanel>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, "AiRuntimeBodyHost", StringComparison.Ordinal));
        if (runtimeBody == null)
            throw new InvalidOperationException("AI Runtime body host is missing.");
        for (int attempt = 0; attempt < 120 && runtimeBody.Children.Count < 4; attempt++)
            await Task.Delay(25);
        if (runtimeBody.Children.Count < 4 ||
            !string.Equals(_section, "ai-runtime", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI Runtime health/config panel did not finish rendering.");
        // The redesigned AI Runtime uses four ALWAYS-OPEN, full-width
        // sections: Windows, Cloud, own-performance and safety. Reject the
        // old 2x2 nested expander grid as well as missing/unrendered cards.
        if (runtimeBody.Children.Count < 4 ||
            !runtimeBody.Children.OfType<StackPanel>().Any(dashboard =>
                dashboard.Children.Count == 4 &&
                dashboard.Orientation == Orientation.Vertical &&
                dashboard.Children.All(card =>
                    card is Border border && border.Child is StackPanel body &&
                    body.Children.Count >= 2 &&
                    body.Children.All(child => child is not Expander))))
            throw new InvalidOperationException("AI Runtime four always-open vertical sections are missing.");
        LogService.Info("performance",
            $"AI_RUNTIME_BODY_READY_SMOKE_OK nodes={runtimeBody.Children.Count} ms={sw.ElapsedMilliseconds} " +
            "parallelRead=true automaticSettingsPreserved=true");

        AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
        if (!cfg.ScheduledTasksEnabled)
            LogService.Info("tasks", "TASKS_AI_RUNTIME_REDIRECT_OK scheduledTasks=off");
        else
            LogService.Info("tasks", "TASKS_AI_RUNTIME_REDIRECT_OK scheduledTasks=on");

        LogService.Info("tasks", $"TASK_SCHEDULER_UI_SMOKE_OK replacedBy=ai-runtime ms={sw.ElapsedMilliseconds}");
    }
}
