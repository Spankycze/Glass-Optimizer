using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task RenderAiRuntimeBodyAsync(
        StackPanel host, AiRuntimePageSnapshot snapshot, int generation, CancellationToken token)
    {
        async Task NextStageAsync(string _)
        {
            token.ThrowIfCancellationRequested();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => tcs.TrySetResult(true)))
                throw new OperationCanceledException("Runtime dispatcher is shutting down.", token);
            await tcs.Task.WaitAsync(token);
            ThrowIfNavigationStale(generation, token);
        }

        FanProfile profile = snapshot.Profile;
        FanAgentHealth? health = snapshot.FanHealth;
        AiSystemRuntimeConfig systemConfig = snapshot.Config;
        AiSystemRuntimeHealth? systemHealth = snapshot.SystemHealth;
        AiCloudProfileStatus cloudStatus = snapshot.CloudStatus;
        GoogleDriveAuthStatus googleAuth = snapshot.GoogleAuth;
        AdaptivePowerRuntimeStatus? powerStatus = snapshot.PowerStatus;
        // Persisted health may be future-dated after a clock correction.
        // Reuse the existing strict UTC freshness policy: fail closed, never
        // display a frozen future sample as ONLINE / current CPU evidence.
        DateTime nowUtc = DateTime.UtcNow;
        bool fresh = health != null && AiSystemSmoothnessGuardService.IsFresh(
            health.UpdatedUtc, nowUtc, TimeSpan.FromSeconds(10));
        bool systemFresh = systemHealth != null && AiSystemSmoothnessGuardService.IsFresh(
            systemHealth.UpdatedUtc, nowUtc, TimeSpan.FromMinutes(5));
        bool powerFresh = AdaptivePowerRuntimeService.IsStatusFresh(
            powerStatus, nowUtc, TimeSpan.FromMinutes(2));
        string workload = powerFresh ? powerStatus!.Workload : "—";
        string agentState = fresh ? "ONLINE" : "STALE";
        host.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Fan Agent", agentState, fresh ? "Spuštěno" : "Stav není aktuální", fresh ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric("Uspané", systemFresh ? systemHealth!.Sleeping.ToString() : "—", "Služby a úlohy", "SpankySuccessBrush"),
            BuildDashboardMetric("Napájení", systemConfig.PowerPlanEnabled ? "AI" : "Ručně", "Správa plánu", "SpankyInfoBrush"),
            BuildDashboardMetric("CPU", fresh ? $"{health!.LoadPercent:0} %" : "—", workload, "SpankyInfoBrush")));

        await NextStageAsync("metrics");
        var runtimePanel = new StackPanel { Spacing = 10 };
        var master = new ToggleSwitch
        {
            Header = "Automatická správa AI",
            OnContent = "AUTO",
            OffContent = "OFF",
            IsOn = profile.AiSmartRuntimeEnabled && systemConfig.Enabled
        };
        NormalizeToggleAppearance(master);
        master.Toggled += (_, _) => SafeTaskService.Forget(
            SaveAiMasterToggleVerifiedAsync(master), "ai-runtime", "Save AI master");
        host.Children.Add(new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 10, 0, 10),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    master,
                    new TextBlock
                    {
                        Text = "Jeden přepínač AI řízení. Nastavení jednotlivých funkcí zůstane uložené.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = TextSecondaryBrush()
                    }
                }
            }
        });
        runtimePanel.Children.Add(new TextBlock
        {
            Text = AiActivePowerRuntimeSummaryPolicy.Describe(
                powerStatus, systemConfig.Enabled,
                systemConfig.PowerPlanEnabled, DateTime.UtcNow),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });

        // Honest decisions, not a claimed live AI action: the GUI only has
        // the latest persisted Agent health snapshot. A stale report is
        // explicitly unknown, and an active game prevents new experiments.
        string decisionSummary = !systemConfig.Enabled
            ? "Vypnuto • automatické zásahy zastavené"
            : !systemFresh
                ? "Čekám na aktuální údaje RuntimeHostu • žádné domnělé úspory"
                : systemHealth!.LastGameActive
                    ? "Hraní • chráním hru a odkládám nové experimenty"
                    : !systemHealth.LastRealIdle
                        ? "Práce na PC • sleduji stav, nové experimenty odkládám"
                        : "Klidový režim • případné změny vyžadují platné měření A/B/A";
        runtimePanel.Children.Add(new TextBlock
        {
            Text = "Rozhodnutí AI (poslední vzorek): " + decisionSummary,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        if (systemFresh && !string.IsNullOrWhiteSpace(systemHealth!.LastAction) &&
            !string.Equals(systemHealth.LastAction, "none", StringComparison.OrdinalIgnoreCase))
            runtimePanel.Children.Add(new TextBlock
            {
                Text = "Poslední akce: " + systemHealth.LastAction,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            });

        // Only current, timestamped A/B/A evidence is credited. A historical
        // trial is a single observation, never accumulated whole-PC savings.
        if (systemFresh && systemHealth!.RecentVerifiedWins > 0 &&
            systemHealth.LatestVerifiedAtUtc is DateTime verifiedUtc &&
            verifiedUtc.Kind == DateTimeKind.Utc &&
            verifiedUtc <= DateTime.UtcNow &&
            DateTime.UtcNow - verifiedUtc <= TimeSpan.FromDays(7) &&
            double.IsFinite(systemHealth.LatestMeasuredCpuBenefitPp) &&
            systemHealth.LatestMeasuredCpuBenefitPp > 0)
        {
            string ramBenefit = systemHealth.LatestMeasuredAvailableRamBenefitMiB is double ramMiB &&
                double.IsFinite(ramMiB) && ramMiB >= AiPassiveMemoryTrialPolicy.MinimumObservedDifferenceMiB
                ? $" • RAM: +{ramMiB:0.#} MiB (ověřené pozorování)" : "";
            runtimePanel.Children.Add(new TextBlock
            {
                Text = $"Ověřený přínos AI: {systemHealth.LatestVerifiedTarget}" +
                       $" • CPU -{systemHealth.LatestMeasuredCpuBenefitPp:0.###} procentního bodu" +
                       ramBenefit + $" • {verifiedUtc.ToLocalTime():g}" +
                       " (jeden ověřený A/B/A pokus, ne trvalá úspora ani součet výkonu PC).",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = TextSecondaryBrush()
            });
        }

        // Power/verified-benefit evidence belongs with the status area,
        // not inside another visually nested section.
        host.Children.Add(runtimePanel);

        await NextStageAsync("runtime");
        var scope = new StackPanel { Spacing = 8 };
        scope.Children.Add(BuildRuntimeToggleRow(
            "Power Plan",
            "Plán napájení podle aktuální zátěže; po vypnutí obnova původního stavu.",
            systemConfig.PowerPlanEnabled,
            enabled => SaveAiScopeAsync(c => c with { PowerPlanEnabled = enabled }, enabled)));
        scope.Children.Add(BuildRuntimeToggleRow(
            "Automatické priority procesů (CPU)",
            "Bezpečné snížení priority vybraných procesů; po vypnutí obnova.",
            systemConfig.AutomaticProcessPriorityEnabled,
            enabled => SaveAiRuntimeConfigVerified(
                c => c with { AutomaticProcessPriorityEnabled = enabled },
                c => c.AutomaticProcessPriorityEnabled, enabled, restartAgent: true)));
        scope.Children.Add(QuickButton("Otevřít síť", () => ShowSectionAsync("network")));
        scope.Children.Add(new TextBlock
        {
            Text = "System/UI/Explorer/Security tweaky zůstávají ruční. AI je automaticky nemění.",
            FontSize = 12,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        scope.Children.Add(BuildRuntimeToggleRow(
            "Služby",
            "Jen povolené volitelné služby Windows a pomocná služba Steamu při delší nečinnosti. AI ověřuje přínos a původní stav umí obnovit.",
            systemConfig.ServicesEnabled,
            enabled => SaveAiRuntimeConfigVerified(
                c => c with { ServicesEnabled = enabled }, c => c.ServicesEnabled, enabled, restartAgent: true)));
        scope.Children.Add(BuildRuntimeToggleRow(
            "Plánované úlohy",
            "Jen bezpečně vybrané úlohy Windows a volitelný agent Firefoxu. Zásah až po ověření nečinnosti; při návratu používání obnova.",
            systemConfig.ScheduledTasksEnabled,
            enabled => SaveAiRuntimeConfigVerified(
                c => c with { ScheduledTasksEnabled = enabled }, c => c.ScheduledTasksEnabled, enabled, restartAgent: true)));
        scope.Children.Add(BuildRuntimeToggleRow(
            "Ovladače / zařízení",
            "Jen volitelné neaktivní ovladače; změny případně až po restartu Windows.",
            systemConfig.OptionalDevicesEnabled,
            enabled => SaveAiRuntimeConfigVerified(
                c => c with { OptionalDevicesEnabled = enabled }, c => c.OptionalDevicesEnabled, enabled, restartAgent: true)));

        var maintenanceCard = BuildAiRuntimeFlatCard(
            "Správa Windows",
            "Všechny funkce mají vlastní AUTO/OFF a bezpečné vrácení změn.",
            scope,
            expanded: true);

        var actionsHost = new Grid();
        actionsHost.Children.Add(new TextBlock
        {
            Text = "Načítám AI Actions…",
            FontSize = 12,
            Foreground = TextSecondaryBrush(),
            Margin = new Thickness(2, 4, 2, 4)
        });
        SafeTaskService.Forget(
            LoadAiRuntimeActionsDeferredAsync(actionsHost, generation, token),
            "ai-runtime",
            "Deferred AI actions render");

        await NextStageAsync("maintenance");
        var cloud = new StackPanel { Spacing = 8 };
        cloud.Children.Add(BuildRuntimeToggleRow(
            "Google Drive cloud profil",
            !string.IsNullOrEmpty(googleAuth.LastError)
                ? googleAuth.LastError
                : googleAuth.SignedIn
                    ? $"Přihlášeno • {cloudStatus.ItemCount} položek • Drive / Glass Optimizer / AI"
                    : googleAuth.Configured
                    ? "OAuth připraven • přihlas se přes Google"
                    : "Google přihlášení zatím není v buildu nakonfigurované.",
            systemConfig.CloudSyncEnabled,
            enabled => SaveAiRuntimeConfigVerified(
                c => c with { CloudSyncEnabled = enabled }, c => c.CloudSyncEnabled, enabled)));
        var cloudButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        cloudButtons.Children.Add(PrimaryQuickButton(
            googleAuth.SignedIn ? "Google přihlášen" : "Přihlásit přes Google",
            async () =>
            {
                if (!googleAuth.SignedIn)
                {
                    bool signedIn = await GoogleDriveOAuthService.SignInAsync(token);
                    if (signedIn && AiSystemResourceManagerService.ReadItems().Count == 0)
                        await AiCloudProfileService.RestoreNowAsync(token);
                }
                await RefreshAiRuntimePageAsync();
            }));
        cloudButtons.Children.Add(QuickButton("Odhlásit", async () =>
        {
            // Revoking the local token involves disk operations. Keep the
            // entire sign-out transaction off the WinUI dispatcher.
            bool signedOut = await Task.Run(GoogleDriveOAuthService.SignOut,
                _windowLifetimeCts.Token);
            await RefreshAiRuntimePageAsync();
            if (!signedOut)
                await ShowMessageAsync("Google Drive",
                    "Odhlášení se nepodařilo. Uložené přihlášení může být stále aktivní.");
        }));
        cloud.Children.Add(cloudButtons);
        // Keep the cancellation boundary, but the auth button group is tiny.
        // A separate low-priority dispatcher turn here delays completed cards.
        ThrowIfNavigationStale(generation, token);

        var syncButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        syncButtons.Children.Add(QuickButton("Synchronizovat teď", async () =>
        {
            AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
            await AiCloudProfileService.SyncNowAsync(cfg, AiSystemResourceManagerService.ReadItems(), token);
            await RefreshAiRuntimePageAsync();
        }));
        syncButtons.Children.Add(QuickButton("Načíst cloud paměť", async () =>
        {
            await AiCloudProfileService.RestoreNowAsync(token);
            await RefreshAiRuntimePageAsync();
        }));
        cloud.Children.Add(syncButtons);
        var cloudCard = BuildAiRuntimeFlatCard(
            "Cloud a záloha",
            cloudStatus.LastSyncUtc is DateTime sync ? $"Synchronizováno {sync.ToLocalTime():g}" : "Volitelná záloha nastavení",
            cloud,
            expanded: true);

        await NextStageAsync("cloud");
        var self = new StackPanel { Spacing = 8 };
        self.Children.Add(BuildRuntimeToggleRow(
            "Self-optimalizace Optimizeru",
            "Automaticky omezuje vlastní zbytečnou práci na pozadí. Ostatní aplikace ani tvoje RAM nemění.",
            systemConfig.SelfOptimizationEnabled,
            enabled => SaveAiRuntimeConfigVerified(
                c => c with { SelfOptimizationEnabled = enabled }, c => c.SelfOptimizationEnabled, enabled)));
        var selfCard = BuildAiRuntimeFlatCard(
            "Výkon Optimizeru",
            "Sleduje a šetří prostředky pouze vlastní aplikace",
            self,
            expanded: true);

        // The self-optimization card is already materialized locally;
        // build the adjacent safety card without another dispatcher round trip.
        ThrowIfNavigationStale(generation, token);
        var safety = new StackPanel { Spacing = 8 };
        safety.Children.Add(new TextBlock
        {
            Text = "Povolené změny se ověřují a lze je vrátit. Kritické součásti Windows jsou chráněné.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        safety.Children.Add(QuickButton("Zrušit naučené blokace", async () =>
        {
            // Removing learned protection allows previously failed candidates
            // into future trials. It must be an explicit confirmed action.
            var explanation = new TextBlock
            {
                Text = "Dříve neúspěšné volitelné optimalizace se mohou znovu vyhodnocovat. " +
                       "Aktuální nastavení Windows se tímto krokem nemění.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextPrimaryBrush()
            };
            if (!await ShowStyledContentAsync("Ochrana AI",
                    "Opravdu chceš povolit nové pokusy u dříve zablokovaných položek?",
                    explanation, "Zrušit blokace"))
                return;
            try
            {
                int changed = await Task.Run(
                    AiSystemResourceManagerService.ResetLearnedProtection,
                    _windowLifetimeCts.Token);
                if (_section == "ai-runtime")
                    await RefreshAiRuntimePageAsync();
                await ShowMessageAsync("Ochrana AI",
                    changed == 0 ? "Žádné naučené blokace nebyly změněny." :
                    $"Naučené blokace byly zrušeny u {changed} položek.");
            }
            catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogService.Error("ai-runtime", "Learned protection reset failed", ex);
                if (!_windowLifetimeCts.IsCancellationRequested)
                    await ShowMessageAsync("Ochrana AI",
                        "Naučené blokace se nepodařilo bezpečně změnit. " + ex.Message);
            }
        }));
        var safetyCard = BuildAiRuntimeFlatCard(
            "Ochrana a obnova",
            "Bez zásahů do kritických služeb, sítě, úložiště nebo startu systému",
            safety,
            expanded: true);

        // One readable vertical hierarchy, not four uneven nested expanders
        // squeezed into two columns. Everything is always visible.
        var dashboard = new StackPanel { Spacing = 10 };
        dashboard.Children.Add(maintenanceCard);
        dashboard.Children.Add(cloudCard);
        dashboard.Children.Add(selfCard);
        dashboard.Children.Add(safetyCard);

        host.Children.Add(dashboard);
        host.Children.Add(actionsHost);

        // One verified switch controls only the registry guard. Network/NIC
        // evidence is deliberately excluded from AI Runtime and from this toggle.
        try
        {
            var registrySnapshot = await Task.Run(() =>
            {
                var values = WindowsDefaultGuardService.ScanReadOnly(token,
                    includeNetworkObservation: false);
                token.ThrowIfCancellationRequested();
                var approvedGroups = values.Where(value => value.Approved)
                    .Select(value => value.GroupId)
                    .Distinct(StringComparer.Ordinal).Count();
                var health = WindowsDefaultGuardService.DescribeAiWindowsRegistryHealth(
                    values, WindowsDefaultGuardService.CanRepairMachineRegistry())
                    .Split('\n')[0].Trim();
                return (Health: health, ApprovedGroups: approvedGroups);
            }, token);
            ThrowIfNavigationStale(generation, token);
            scope.Children.Add(BuildRuntimeToggleRow(
                "Hlídání registrů Windows",
                registrySnapshot.Health + " • 11 pravidel / 7 skupin. " +
                (registrySnapshot.ApprovedGroups is > 0 and < 7
                    ? $"Dříve zapnuto {registrySnapshot.ApprovedGroups}/7; přepínač sjednotí nastavení. "
                    : "") +
                "Kontrola každých 5 minut při běžícím Optimizeru. HKLM vyžaduje správce.",
                registrySnapshot.ApprovedGroups == 7,
                async enabled =>
                {
                    await Task.Run(() =>
                    {
                        WindowsDefaultGuardService.SetRegistryGuardEnabled(enabled);
                        int count = WindowsDefaultGuardService.CountApprovedRegistryGroups();
                        if (count != (enabled ? 7 : 0))
                            throw new System.IO.IOException(
                                "Společné schválení hlídání registrů se nepodařilo ověřit.");
                    }, _windowLifetimeCts.Token);
                }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Info("default-guard", "AI registry evidence unavailable: " + ex.GetType().Name);
        }

        // The only manual repair action for this guard sits beside its master
        // approval in AI Runtime, never duplicated in Settings or Network.
        scope.Children.Add(QuickButton("Opravit schválené registry (UAC)",
            RepairApprovedRegistryDefaultsAsync));
    }

    private async Task RepairApprovedRegistryDefaultsAsync()
    {
        string[] groups = WindowsDefaultGuardService.ApprovedMachineGroups();
        if (groups.Length == 0)
        {
            await ShowMessageAsync("Hlídání registrů Windows",
                "Nemáš schválené žádné systémové registry (HKLM). Zapni nejprve společné hlídání.");
            return;
        }

        try
        {
            // Elevation only after this explicit button press, with readback
            // and recovery handled by the existing guarded worker operation.
            var result = await WorkerClientService.RunAsync<DefaultGuardMachineRepairResult>(
                "default-guard-repair",
                WorkerPayloads.Encode(new DefaultGuardMachineRepairRequest(groups)),
                timeout: TimeSpan.FromSeconds(90),
                token: _windowLifetimeCts.Token);
            bool incomplete = result.RemainingAfter != 0 || result.UnverifiedAfter != 0;
            await ShowMessageAsync(incomplete
                    ? "Registry – oprava není úplná"
                    : "Registry – oprava dokončena",
                "Sledováno: " + result.TrackedValues +
                "\nOpraveno: " + result.AppliedChanges +
                "\nZbývá odchylek: " + result.RemainingAfter +
                "\nNelze ověřit: " + result.UnverifiedAfter +
                (incomplete ? "\nNěkteré hodnoty nelze potvrdit jako výchozí." : "") +
                "\nSíťová karta ani Green Ethernet nebyly měněny.");
        }
        catch (Exception ex)
        {
            LogService.Error("default-guard", "Manual AI registry restore failed", ex);
            await ShowMessageAsync("Registry – oprava se nezdařila",
                ex.Message + "\nNepotvrzené změny nejsou označené jako opravené.");
        }
    }

    private sealed record AiActionSnapshot(
        int ItemCount, int Managed, int Sleeping, int Blocked,
        int ManualRecoveryRequired,
        AiRuntimeBlockedSummaryPolicy.Summary BlockBreakdown,
        AiSystemRuntimeItemState[] VisibleItems, AiSystemActionHistoryEntry[] History);

    // Build counts and top 24 on the worker, not via repeated UI LINQ Count
    // passes. Sorting a Take-limited view leaves cached source data untouched.
    private static AiActionSnapshot BuildAiActionsSnapshot(
        IReadOnlyList<AiSystemRuntimeItemState> source,
        IReadOnlyList<AiSystemActionHistoryEntry> history)
    {
        int managed = 0, sleeping = 0, blocked = 0;
        int recoveryRequired = 0;
        int protectedItems = 0, unavailable = 0, unreadable = 0, other = 0;
        foreach (var item in source)
        {
            if (item.Managed) managed++;
            if (item.Sleeping) sleeping++;
            if (AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(item.BlockedReason))
                recoveryRequired++;
            // Optional Wi-Fi/printer/per-user observations are inventory,
            // not failed mutation attempts. Agree with the overview counter.
            if (AiOptionalFeatureSummaryPolicy.IsPassiveObservation(item))
                continue;
            switch (AiRuntimeBlockedSummaryPolicy.Classify(item.BlockedReason))
            {
                case AiRuntimeBlockedSummaryPolicy.Category.Protected: protectedItems++; blocked++; break;
                // Optional component absent from this Windows installation is
                // an inventory fact, NOT a failed/blocked AI intervention.
                case AiRuntimeBlockedSummaryPolicy.Category.Unavailable:
                    if (item.Managed || item.Sleeping) { protectedItems++; blocked++; }
                    else unavailable++;
                    break;
                case AiRuntimeBlockedSummaryPolicy.Category.Unreadable:
                    if (item.Managed || item.Sleeping) { protectedItems++; blocked++; }
                    else unreadable++;
                    break;
                case AiRuntimeBlockedSummaryPolicy.Category.Other: other++; blocked++; break;
            }
        }
        // Retain missing/unreadable items in the visible inventory; do not
        // pretend they never existed or that a user removed them. Prefer real
        // AI-owned actions, vetoes and failures when the panel has only 8 rows.
        AiSystemRuntimeItemState[] visible = source
            .OrderByDescending(x => AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(x.BlockedReason))
            .ThenByDescending(x => x.Managed || x.Sleeping)
            .ThenByDescending(AiOptionalFeatureSummaryPolicy.CountsAsBlocked)
            .ThenBy(x => x.Kind)
            .ThenBy(x => x.Id)
            .Take(8).ToArray();
        return new(source.Count, managed, sleeping, blocked,
            recoveryRequired, new(protectedItems, unavailable, other, Unreadable: unreadable),
            visible, history.ToArray());
    }

    // Read-only explanation tailored to the actual Windows generation.
    // AI discovery lists historically included retired Windows services;
    // those entries are NOT proof the user removed a service manually.
    private static string ExplainMissingWindowsComponent(
        string kind, string id, int windowsBuild)
    {
        if (!string.Equals(kind, "service", StringComparison.OrdinalIgnoreCase))
            return "Windows tuto položku pod uvedeným názvem nenašel. Není to důkaz ručního odstranění.";
        if (windowsBuild >= 26100 &&
            string.Equals(id, "AJRouter", StringComparison.OrdinalIgnoreCase))
            return "AllJoyn Router byl Microsoftem odstraněn ve Windows 11 24H2; ve 26H2 se již neočekává.";
        if (windowsBuild >= 26100 &&
            string.Equals(id, "SharedRealitySvc", StringComparison.OrdinalIgnoreCase))
            return "Souvisí s prostorovými funkcemi starších Windows. Windows Mixed Reality bylo odstraněno od 24H2; chybějící služba nemusí znamenat závadu.";
        if (string.Equals(id, "Fax", StringComparison.OrdinalIgnoreCase))
            return "Windows Fax and Scan je volitelná funkce a od Windows 11 22H2 není standardně předinstalovaná.";
        if (string.Equals(id, "WMPNetworkSvc", StringComparison.OrdinalIgnoreCase))
            return "Sdílení médií klasického Windows Media Playeru. Jeho služba není nezbytná pro běžný provoz systému.";
        if (string.Equals(id, "diagnosticshub.standardcollector.service", StringComparison.OrdinalIgnoreCase))
            return "Sběrač Microsoft Diagnostics Hub; v tomto sestavení jeho povinnou přítomnost nelze potvrdit.";
        return "Windows tuto službu pod daným názvem nenalezl; neznamená to, že ji uživatel odstranil.";
    }
}
