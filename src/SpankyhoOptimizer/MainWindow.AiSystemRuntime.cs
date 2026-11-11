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
    // Pure UI-regression assertions: no current or future-dated JSON snapshot
    // may show ONLINE, CPU activity or a valid AI action after clock changes.
    private static string RunAiRuntimeUiFreshnessSmoke()
    {
        DateTime now = new(2026, 10, 22, 7, 0, 0, DateTimeKind.Utc);
        var fanTtl = TimeSpan.FromSeconds(10);
        var systemTtl = TimeSpan.FromMinutes(5);
        var powerPageTtl = TimeSpan.FromSeconds(12);
        bool Fresh(DateTime value, TimeSpan ttl) =>
            AiSystemSmoothnessGuardService.IsFresh(value, now, ttl);
        foreach (var ttl in new[] { fanTtl, systemTtl, powerPageTtl })
        {
            if (!Fresh(now, ttl) || !Fresh(now - ttl, ttl) ||
                Fresh(now - ttl - TimeSpan.FromTicks(1), ttl) ||
                Fresh(now.AddTicks(1), ttl) ||
                Fresh(new DateTime(now.Ticks, DateTimeKind.Unspecified), ttl) ||
                Fresh(default, ttl))
                throw new InvalidOperationException(
                    "AI Runtime current/future status freshness policy regressed.");
        }
        return "AI_RUNTIME_UI_FRESHNESS_SMOKE_OK fan10s=true system5m=true " +
               "power12s=true futureRejected=true utcRequired=true writes=0";
    }

    private readonly SemaphoreSlim _aiRuntimeRestoreGate = new(1, 1);

    private Task BuildAiSystemRuntimePageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(PrimaryQuickButton("Ventilátory", () => ShowSectionAsync("lianli")));
        heroActions.Children.Add(QuickButton("Obnovit vše", async () =>
        {
            // Restoration must not be cancelled midway merely because the
            // user navigates away. Reject duplicate presses while a restore
            // transaction is already in flight.
            if (!await _aiRuntimeRestoreGate.WaitAsync(0)) return;
            try
            {
                await Task.Run(() =>
                    AiSystemResourceManagerService.RestoreAllNowAsync(_windowLifetimeCts.Token),
                    _windowLifetimeCts.Token);
                if (_section == "ai-runtime")
                    await RefreshAiRuntimePageAsync();
            }
            catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogService.Error("ai-runtime", "Restore all failed", ex);
                if (!_windowLifetimeCts.IsCancellationRequested)
                    await ShowMessageAsync("Obnova AI nastavení",
                        "Obnova nebyla dokončena. Původní stav se musí ještě ověřit. " + ex.Message);
            }
            finally { _aiRuntimeRestoreGate.Release(); }
        }));

        ContentStack.Children.Add(BuildModernSectionHero(
            "ai-runtime",
            "AI System Runtime",
            "Přehled, řízení a ochrana na jedné stránce.",
            "AI RUNTIME",
            "PŘEHLED • SPRÁVA WINDOWS • CLOUD • OCHRANA",
            heroActions));

        var bodyHost = new StackPanel { Spacing = ContentStack.Spacing, Tag = "AiRuntimeBodyHost" };
        bodyHost.Children.Add(new TextBlock
        {
            Text = "Načítám stav AI runtime…",
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        ContentStack.Children.Add(bodyHost);

        SafeTaskService.Forget(
            LoadAiRuntimeBodyDeferredAsync(bodyHost, _navigationGeneration, token),
            "ai-runtime",
            "Deferred runtime status");
        return Task.CompletedTask;
    }

    private sealed record AiRuntimePageSnapshot(
        FanProfile Profile,
        FanAgentHealth? FanHealth,
        AiSystemRuntimeConfig Config,
        AiSystemRuntimeHealth? SystemHealth,
        AiCloudProfileStatus CloudStatus,
        GoogleDriveAuthStatus GoogleAuth,
        AdaptivePowerRuntimeStatus? PowerStatus);

    // Two independent read-only groups can load concurrently. The cold
    // health/profile JSON and cloud/power/baseline metadata were previously
    // read in series (227 ms in the last cold AI Runtime UI smoke), though
    // their providers share no mutable state. Keep all IO off the UI thread.
    private static async Task<AiRuntimePageSnapshot> ReadAiRuntimePageSnapshotAsync(
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var systemGroup = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            FanProfile profile = FanProfileService.Load();
            FanAgentHealth? fanHealth = FanProfileService.ReadHealthStatus();
            token.ThrowIfCancellationRequested();
            AiSystemRuntimeConfig config = AiSystemResourceManagerService.LoadConfig();
            AiSystemRuntimeHealth? systemHealth = AiSystemResourceManagerService.ReadHealth();
            token.ThrowIfCancellationRequested();
            return (Profile: profile, FanHealth: fanHealth, Config: config,
                Health: systemHealth);
        }, token);
        var externalGroup = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            GoogleDriveAuthStatus google = GoogleDriveOAuthService.ReadStatus();
            AiCloudProfileStatus cloud = AiCloudProfileService.ReadStatus(google);
            token.ThrowIfCancellationRequested();
            AdaptivePowerRuntimeStatus? power = AdaptivePowerRuntimeService.ReadStatus();
            token.ThrowIfCancellationRequested();
            return (Cloud: cloud, Auth: google, Power: power);
        }, token);
        await Task.WhenAll(systemGroup, externalGroup).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var a = await systemGroup.ConfigureAwait(false);
        var b = await externalGroup.ConfigureAwait(false);
        return new(a.Profile, a.FanHealth, a.Config, a.Health,
            b.Cloud, b.Auth, b.Power);
    }

    private async Task LoadAiRuntimeBodyDeferredAsync(
        StackPanel host, int generation, CancellationToken token)
    {
        try
        {
            // Profile IO and first-use JSON/JIT work must never occupy the dispatcher.
            AiRuntimePageSnapshot snapshot =
                await ReadAiRuntimePageSnapshotAsync(token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime")
                return;

            host.Children.Clear();
            await RenderAiRuntimeBodyAsync(host, snapshot, generation, token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime") return;
            SchedulePageScrollPolicyRefresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "Runtime status load failed", ex);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "ai-runtime")
                return;
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Text = "Stav AI runtime se nepodařilo načíst.",
                Foreground = WarningBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            host.Children.Add(QuickButton("Načíst znovu", () =>
                LoadAiRuntimeBodyDeferredAsync(host, generation, token)));
        }
    }

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
        scope.Children.Add(BuildRuntimeToggleRow(
            "Automatická GPU priorita her",
            "Aktivní hra: Normal → Above Normal; obnova při opuštění hry. FPS nejsou zaručené.",
            systemConfig.AutomaticGameGpuPriorityEnabled,
            enabled => SaveAiRuntimeConfigVerified(
                c => c with { AutomaticGameGpuPriorityEnabled = enabled },
                c => c.AutomaticGameGpuPriorityEnabled, enabled, restartAgent: true)));
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

    private static string RunAiActionsSnapshotSmoke()
    {
        // Exercise all three Windows component catalogs with synthetic Windows
        // 23H2/26H2 snapshots before the AI activity rendering regression.
        LogService.Info("release-ui-smoke", WindowsComponentCatalogPolicy.RunFunctionalSmoke());
        AiSystemRuntimeItemState[] fixture = Enumerable.Range(0, 96)
            .Select(i => new AiSystemRuntimeItemState(
                i % 3 == 0 ? "Service" : i % 3 == 1 ? "Task" : "Driver",
                $"Fixture-{i:D3}", i % 5 == 0, i % 7 == 0,
                i % 11 == 0 ? "Blocked" : "",
                null, null, null, null, false, 0, "Observed"))
            .ToArray();
        AiSystemActionHistoryEntry[] history = [];
        var actual = BuildAiActionsSnapshot(fixture, history);
        var expected = fixture.OrderByDescending(x => x.Managed || x.Sleeping)
            .ThenByDescending(AiOptionalFeatureSummaryPolicy.CountsAsBlocked)
            .ThenBy(x => x.Kind).ThenBy(x => x.Id).Take(8).ToArray();
        if (actual.ItemCount != fixture.Length ||
            actual.Managed != fixture.Count(x => x.Managed) ||
            actual.Sleeping != fixture.Count(x => x.Sleeping) ||
            actual.Blocked != fixture.Count(x => !string.IsNullOrWhiteSpace(x.BlockedReason)) ||
            actual.BlockBreakdown.Total != actual.Blocked ||
            actual.BlockBreakdown.Other != fixture.Count(x => !string.IsNullOrWhiteSpace(x.BlockedReason)) ||
            actual.BlockBreakdown.Unavailable != 0 ||
            actual.BlockBreakdown.Protected != 0 ||
            !expected.SequenceEqual(actual.VisibleItems) ||
            !fixture.Select(x => x.Id).SequenceEqual(Enumerable.Range(0, 96).Select(i => $"Fixture-{i:D3}")))
            throw new InvalidOperationException("AI Actions order/count/source immutability regression.");

        // Windows 26H2 must not treat discontinued AllJoyn as a mysteriously
        // removed user service; similarly don't pretend a legacy optional
        // feature like Fax is mandatory, nor overstate why Diagnostics Hub
        // is absent. These explanations are read-only and version-aware.
        if (!ExplainMissingWindowsComponent("service", "AJRouter", 26300).Contains("24H2") ||
            ExplainMissingWindowsComponent("service", "AJRouter", 22621).Contains("24H2") ||
            !ExplainMissingWindowsComponent("service", "Fax", 26300).Contains("volitelná") ||
            !ExplainMissingWindowsComponent("service", "SharedRealitySvc", 26300).Contains("Mixed Reality") ||
            !ExplainMissingWindowsComponent("service", "WMPNetworkSvc", 26300).Contains("Media Playeru") ||
            !ExplainMissingWindowsComponent("service", "diagnosticshub.standardcollector.service", 26300).Contains("nelze potvrdit") ||
            !ExplainMissingWindowsComponent("driver", "parvdm", 26300).Contains("není to důkaz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI missing Windows components explanations are inaccurate.");

        // Preserve all absent/unknown items in the inventory, without
        // misreporting missing optional resources as failed AI changes.
        // Real owned items must lead the eight visible cards.
        AiSystemRuntimeItemState[] missingFixture = Enumerable.Range(0, 24)
            .Select(i => new AiSystemRuntimeItemState("service", $"Missing-{i:D2}",
                false, false, i % 2 == 0 ? "not installed" : "not installed/unreadable",
                null, null, null, null, false, 0, "none"))
            .Append(new AiSystemRuntimeItemState("service", "ProtectedReal",
                false, false, "critical denylist",
                null, null, null, null, false, 0, "none"))
            .Append(new AiSystemRuntimeItemState("driver", "MissingOwned",
                true, true, "not installed",
                null, null, 3, null, false, 0, "managed"))
            .ToArray();
        var absent = BuildAiActionsSnapshot(missingFixture, history);
        if (absent.Blocked != 2 || absent.BlockBreakdown.Unavailable != 12 ||
            absent.BlockBreakdown.Unreadable != 12 ||
            absent.BlockBreakdown.Protected != 2 || absent.BlockBreakdown.Other != 0 ||
            absent.ItemCount != 26 || absent.VisibleItems.Length != 8 ||
            absent.VisibleItems[0].Id != "MissingOwned" ||
            absent.VisibleItems[1].Id != "ProtectedReal" ||
            !absent.VisibleItems.Skip(2).All(x => x.Id.StartsWith("Missing-", StringComparison.Ordinal)) ||
            missingFixture.Length != 26 ||
            AiRuntimeBlockedSummaryPolicy.Classify("not installed/unreadable") !=
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable ||
            AiRuntimeBlockedSummaryPolicy.Classify("task status unreadable") !=
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable ||
            AiRuntimeBlockedSummaryPolicy.Classify("service status unreadable") !=
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable ||
            AiOptionalFeatureSummaryPolicy.CountsAsBlocked(missingFixture[0]) ||
            AiOptionalFeatureSummaryPolicy.CountsAsBlocked(missingFixture[1]) ||
            !AiOptionalFeatureSummaryPolicy.CountsAsBlocked(missingFixture[^1]) ||
            !TaskSchedulerComService.IsMissingTaskError(unchecked((int)0x80070002)) ||
            !TaskSchedulerComService.IsMissingTaskError(unchecked((int)0x8004130F)) ||
            TaskSchedulerComService.IsMissingTaskError(unchecked((int)0x80070005)))
            throw new InvalidOperationException(
                "AI activity miscounted absent components as blockers or hid a real managed resource.");

        // Read-only optional-feature observations can greatly outnumber
        // actual vetoes. The limited panel must show real blocked candidates
        // first, and never mislabel passive inventory as attempted actions.
        var passiveInventory = Enumerable.Range(0, 32).Select(i =>
            new AiSystemRuntimeItemState("service", $"Passive-{i:D2}",
                false, false, "Sledování [Wi-Fi]: stav není ověřen",
                null, null, null, null, false, 0, "none",
                FeatureObservationSamples: 4))
            .Append(new AiSystemRuntimeItemState("service", "ProtectedImportant",
                false, false, "critical denylist",
                null, null, null, null, false, 0, "none"))
            .ToArray();
        var ranked = BuildAiActionsSnapshot(passiveInventory, history);
        if (ranked.Blocked != 1 || ranked.BlockBreakdown.Protected != 1 ||
            ranked.BlockBreakdown.Unavailable != 0 ||
            ranked.BlockBreakdown.Other != 0 ||
            ranked.VisibleItems[0].Id != "ProtectedImportant" ||
            !AiOptionalFeatureSummaryPolicy.IsPassiveObservation(ranked.VisibleItems[1]))
            throw new InvalidOperationException(
                "AI Actions passive observation counted as blocker or hid a real safety veto.");

        // Two owned, stopped Windows resources needing MANUAL restoration
        // cannot disappear behind a crowded 8-row preview, nor be presented
        // as safely stopped or restored. All fixtures remain in memory only.
        const string manualCz = "cesta spravované služby se změnila; ruční obnova nutná, AI záznam zachován";
        const string manualEn = "missing safe original start value; manual recovery required";
        AiSystemRuntimeItemState[] recoveryFixture = Enumerable.Range(0, 24)
            .Select(i => new AiSystemRuntimeItemState(
                "service", $"Owned-{i:D2}", true, i % 2 == 0,
                string.Empty, null, null, 3, null, false, 0, "managed"))
            .Append(new AiSystemRuntimeItemState(
                "service", "RestoreService", true, true, manualCz,
                null, null, 3, null, false, 0, "manual"))
            .Append(new AiSystemRuntimeItemState(
                "driver", "RestoreDriver", true, true, manualEn,
                null, null, 3, null, false, 0, "manual"))
            .ToArray();
        var recoveries = BuildAiActionsSnapshot(recoveryFixture, history);
        if (recoveries.ManualRecoveryRequired != 2 ||
            recoveries.BlockBreakdown.Protected != 2 ||
            recoveries.Blocked != 2 ||
            recoveries.VisibleItems.Length != 8 ||
            recoveries.VisibleItems[0].Id != "RestoreDriver" ||
            recoveries.VisibleItems[1].Id != "RestoreService" ||
            !AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(
                recoveries.VisibleItems[0].BlockedReason) ||
            !AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(
                recoveries.VisibleItems[1].BlockedReason) ||
            AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery("not installed") ||
            recoveryFixture.Length != 26)
            throw new InvalidOperationException(
                "AI manual-recovery warning was hidden, miscounted or mistaken for a routine stop.");

        static int Legacy(AiSystemRuntimeItemState[] items)
        {
            var all = items.ToArray();
            var visible = all.OrderByDescending(x => x.Managed || x.Sleeping)
                .ThenBy(x => x.Kind).ThenBy(x => x.Id).Take(24).ToArray();
            return all.Count(x => x.Managed) + all.Count(x => x.Sleeping) +
                all.Count(x => !string.IsNullOrWhiteSpace(x.BlockedReason)) +
                Math.Min(8, visible.Length);
        }
        _ = Legacy(fixture);
        _ = BuildAiActionsSnapshot(fixture, history);
        const int iterations = 1024;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int oldChecksum = 0;
        for (int i = 0; i < iterations; i++) oldChecksum += Legacy(fixture);
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        oldBytes = GC.GetAllocatedBytesForCurrentThread() - oldBytes;

        long newBytes = GC.GetAllocatedBytesForCurrentThread();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        int newChecksum = 0;
        for (int i = 0; i < iterations; i++)
        {
            var snapshot = BuildAiActionsSnapshot(fixture, history);
            newChecksum += snapshot.Managed + snapshot.Sleeping + snapshot.Blocked + snapshot.VisibleItems.Length;
        }
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        newBytes = GC.GetAllocatedBytesForCurrentThread() - newBytes;
        if (oldChecksum != newChecksum || newBytes >= oldBytes)
            throw new InvalidOperationException(
                $"AI Actions data preparation regression oldChecksum={oldChecksum} newChecksum={newChecksum} oldAllocB={oldBytes} newAllocB={newBytes}.");

        return $"AI_ACTIONS_SNAPSHOT_BENCH_OK rows={fixture.Length} iterations={iterations} " +
            $"oldMs={oldMs:F3} newMs={newMs:F3} oldAllocB={oldBytes} newAllocB={newBytes} " +
            "orderIdentical=true countsIdentical=true missingInventoryVisible=true " +
            "taskReadFailureDistinguished=true serviceReadFailureDistinguished=true " +
            "missingOwnedRetained=true sourceUnchanged=true writes=0";
    }
    private async Task LoadAiRuntimeActionsDeferredAsync(
        Grid host,
        int generation,
        CancellationToken token)
    {
        try
        {
        var loaded = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return (
                Actions: BuildAiActionsSnapshot(
                    AiSystemResourceManagerService.ReadItems(),
                    AiSystemResourceManagerService.ReadActionHistory()),
                Feedback: AiThirdPartyIdleAutomationService.ReadFeedbackForUi());
        }, token);
        var snapshot = loaded.Actions;

        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "ai-runtime")
            return;

        var actions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());

        Border shell = BuildAiRuntimeFlatCard(
            "Aktivita AI",
            $"Spravované {snapshot.Managed} • pozastavené {snapshot.Sleeping} • blokované {snapshot.Blocked}" +
            (snapshot.BlockBreakdown.Unavailable > 0
                ? $" • nenalezené {snapshot.BlockBreakdown.Unavailable}" : string.Empty) +
            (snapshot.BlockBreakdown.Unreadable > 0
                ? $" • stav neověřen {snapshot.BlockBreakdown.Unreadable}" : string.Empty) +
            (snapshot.ManualRecoveryRequired > 0
                ? $" • RUČNÍ OBNOVA {snapshot.ManualRecoveryRequired}" : string.Empty),
            actions,
            expanded: true);

        host.Children.Clear();
        host.Children.Add(shell);

        if (snapshot.VisibleItems.Length == 0)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actions.Children.Add(new TextBlock
            {
                Text = "AI zatím neprovedla ani nezablokovala žádný zásah.",
                FontSize = 12,
                Foreground = TextSecondaryBrush()
            });
        }

        const int batchSize = 4;
        int actionIndex = 0;

        // Compact activity preview: totals still cover the full inventory;
        // rendering dozens of full cards inflated the entire always-open page.
        foreach (AiSystemRuntimeItemState item in snapshot.VisibleItems)
        {
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime")
                return;

            string status = AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(item.BlockedReason)
                ? "RUČNÍ OBNOVA"
                : item.Sleeping
                    ? "ZASTAVENO"
                : AiOptionalFeatureSummaryPolicy.IsPassiveObservation(item)
                    ? "SLEDOVÁNO"
                : AiRuntimeBlockedSummaryPolicy.Classify(item.BlockedReason) switch
                {
                    AiRuntimeBlockedSummaryPolicy.Category.Unavailable => "NENÍ REGISTROVÁNO",
                    AiRuntimeBlockedSummaryPolicy.Category.Unreadable => "NEOVĚŘENO",
                    AiRuntimeBlockedSummaryPolicy.Category.Protected => "CHRÁNĚNÉ",
                    AiRuntimeBlockedSummaryPolicy.Category.Other => "BLOKOVÁNO",
                    _ => item.Managed ? "SPRAVOVÁNO" : "SLEDOVÁNO"
                };
            string detail = AiRuntimeBlockedSummaryPolicy.Classify(item.BlockedReason) switch
            {
                AiRuntimeBlockedSummaryPolicy.Category.Unavailable =>
                    ExplainMissingWindowsComponent(item.Kind, item.Id,
                        Environment.OSVersion.Version.Build),
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable =>
                    "Stav se nepodařilo spolehlivě načíst. Nelze potvrdit, zda je položka přítomná.",
                _ => !string.IsNullOrWhiteSpace(item.BlockedReason)
                    ? item.BlockedReason : item.LastAction
            };
            if (item.LastChangedUtc is DateTime changed)
                detail += $" • {changed.ToLocalTime():g}";
            if (actionIndex % 2 == 0)
                actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border actionCard = BuildRuntimeScopeRow(
                $"{item.Kind.ToUpperInvariant()} • {item.Id}",
                string.IsNullOrWhiteSpace(detail) ? "Bez zásahu." : detail,
                status);
            Grid.SetColumn(actionCard, actionIndex % 2);
            Grid.SetRow(actionCard, actionIndex / 2);
            actions.Children.Add(actionCard);
            actionIndex++;

            if (actionIndex % batchSize == 0)
                await Task.Yield();
        }

        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "ai-runtime")
            return;

        if (snapshot.ItemCount > snapshot.VisibleItems.Length)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var more = new TextBlock
            {
                Text = $"Zobrazeno 8 nejdůležitějších z {snapshot.ItemCount} sledovaných položek.",
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetColumnSpan(more, 2);
            Grid.SetRow(more, actions.RowDefinitions.Count - 1);
            actions.Children.Add(more);
        }

        // Honest live automatic-program outcomes, read from the existing
        // atomic ownership journal ONCE per page navigation. Display only
        // the latest two lines so Dark Glass stays compact and scroll-free.
        if (loaded.Feedback.Length > 0)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var feedbackLabel = new TextBlock
            {
                Text = "AI programy • poslední výsledky (neprokazují příčinu): " +
                    string.Join("  |  ", loaded.Feedback.Take(2).Select(x =>
                        $"{x.AppKey}: {x.Detail}")),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetColumnSpan(feedbackLabel, 2);
            Grid.SetRow(feedbackLabel, actions.RowDefinitions.Count - 1);
            actions.Children.Add(feedbackLabel);
        }

        if (snapshot.History.Length > 0)
        {
            AiSystemActionHistoryEntry latest = snapshot.History[^1];
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var latestText = new TextBlock
            {
                Text = $"Poslední zásah: {latest.Kind.ToUpperInvariant()} • {latest.Id} • {latest.Action} • {latest.TimestampUtc.ToLocalTime():g}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetColumnSpan(latestText, 2);
            Grid.SetRow(latestText, actions.RowDefinitions.Count - 1);
            actions.Children.Add(latestText);
        }

        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "AI Actions snapshot or render failed", ex);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration || _section != "ai-runtime")
                return;
            host.Children.Clear();
            var error = new StackPanel { Spacing = 8 };
            error.Children.Add(new TextBlock
            {
                Text = "Akce AI se nepodařilo načíst. Ostatní AI Runtime zůstává funkční.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = WarningBrush()
            });
            error.Children.Add(QuickButton("Zkusit znovu", () =>
                LoadAiRuntimeActionsDeferredAsync(host, generation, token)));
            host.Children.Add(error);
        }
    }

    private static async Task SaveAiRuntimeConfigVerified(
        Func<AiSystemRuntimeConfig, AiSystemRuntimeConfig> update,
        Func<AiSystemRuntimeConfig, bool> readBack,
        bool requested,
        bool restartAgent = false)
    {
        await AiScopeSaveGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                AiSystemRuntimeConfig? original = null;
                AiSystemRuntimeConfig actualConfig = AiSystemResourceManagerService.UpdateConfig(current =>
                {
                    original = current;
                    return update(current);
                });
                bool actual = readBack(actualConfig);
                if (actual != requested)
                    throw new InvalidOperationException(
                        $"AI Runtime config read-back nesouhlasí. Požadováno={requested}, skutečnost={actual}.");
                if (actualConfig == original)
                    return; // Idempotent: neither JSON rewrite nor agent startup.
                if (original!.AutomaticProcessPriorityEnabled &&
                    !actualConfig.AutomaticProcessPriorityEnabled)
                    AiSystemResourceManagerService.RestoreProcessPriorityNow();
                if (restartAgent) FanProfileService.StartAgentIfNeeded();
            });
        }
        finally { AiScopeSaveGate.Release(); }
    }

    // No collapsible panels anywhere on the AI Runtime page. Flat headings,
    // slim padding and always-visible content match the modern Windows toggles.
    private Border BuildAiRuntimeFlatCard(string title, string subtitle,
        UIElement content, bool expanded = false)
    {
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            body.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush()
            });
        body.Children.Add(content);
        return BuildDashboardCard(body, new Thickness(12, 10, 12, 10));
    }

    private Border BuildRuntimeToggleRow(
        string title,
        string description,
        bool initialValue,
        Func<bool, Task> onChanged)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = TextSecondaryBrush()
        });
        grid.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsOn = initialValue,
            OnContent = "AUTO",
            OffContent = "OFF",
            VerticalAlignment = VerticalAlignment.Center
        };
        NormalizeToggleAppearance(toggle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, title);
        bool suppressToggle = false;
        bool busy = false;
        bool lastAccepted = initialValue;
        toggle.Toggled += (_, _) =>
        {
            if (suppressToggle) return;
            if (busy)
            {
                suppressToggle = true;
                try { toggle.IsOn = lastAccepted; }
                finally { suppressToggle = false; }
                return;
            }

            bool requested = toggle.IsOn;
            SafeTaskService.Forget(ApplyAsync(), "ai-runtime", "Verified runtime toggle " + title);

            async Task ApplyAsync()
            {
                busy = true;
                toggle.IsEnabled = false;
                try
                {
                    await onChanged(requested);
                    lastAccepted = requested;
                    LogService.Info("ai-runtime", $"RUNTIME_TOGGLE_OK title={title} requested={requested}");
                }
                catch (Exception ex)
                {
                    suppressToggle = true;
                    try { toggle.IsOn = lastAccepted; }
                    finally { suppressToggle = false; }
                    LogService.Error("ai-runtime", $"RUNTIME_TOGGLE_FAIL title={title} requested={requested}", ex);
                    try
                    {
                        await ShowMessageAsync(title, ex.Message);
                    }
                    finally
                    {
                        // The write may have succeeded before a restore/agent failure.
                        // Reload persisted state even if the error dialog itself fails.
                        if (_section == "ai-runtime")
                            await RefreshAiRuntimePageAsync();
                    }
                }
                finally
                {
                    toggle.IsEnabled = true;
                    busy = false;
                }
            }
        };
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = null,
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(4, 7, 4, 7),
            Child = grid
        };
    }

    private Border BuildRuntimeScopeRow(string title, string description, string badgeText)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        grid.Children.Add(text);

        var badge = Badge(badgeText, badgeText == "GUARDED" ? "SpankyWarningBrush" : "SpankySuccessBrush");
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid
        };
    }
}
