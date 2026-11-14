using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private bool _powerProfileRunning;
    private StackPanel? _networkAdvancedBody;
    private bool _networkAdvancedLoadQueued;
    private int _networkAdvancedLoadGeneration;


    private async Task ApplyPerformancePowerAsync()
    {
        if (_powerProfileRunning) return;
        _powerProfileRunning = true;
        try
        {
            LogService.Info("POWER_RECOMMENDED_PROGRESS_START");
            int exitCode = await RunPowerProfileProgressPopupAsync();
            if (exitCode == -2)
            {
                LogService.Info("power", "Power Recommended was cancelled from the progress popup.");
                InvalidatePowerPageCache();
                PowerPlanStateService.InvalidateCache();
                try
                {
                    await PowerPlanStateService.ReadAsync(forceRefresh: true);
                }
                catch (Exception ex)
                {
                    LogService.Error("power", "Power cancel reconcile read-back failed", ex);
                }
                if (_section == "power")
                    await ShowSectionAsync("power");
                return;
            }
            if (exitCode != 0)
            {
                LogService.Error("Power Recommended progress finished with ExitCode=" + exitCode);
                InvalidatePowerPageCache();
                PowerPlanStateService.InvalidateCache();
                try
                {
                    await PowerPlanStateService.ReadAsync(forceRefresh: true);
                }
                catch (Exception ex)
                {
                    LogService.Error("power", "Power failed-apply reconcile read-back failed", ex);
                }
                if (_section == "power")
                    await ShowSectionAsync("power");
                return;
            }
            InvalidatePowerPageCache();
            PowerPlanStateService.InvalidateCache();
            var verify = await PowerPlanStateService.ReadAsync(forceRefresh: true);
            var state = EvaluateCriticalPowerState(verify);
            if (!state.ActiveOk || state.Drift > 0)
            {
                string detail = $"Aktivní plán: {verify.ActivePlan} • critical drift: {state.Drift}/{state.Total}";
                LogService.Error("POWER_RECOMMENDED_VERIFY_FAIL " + detail);
                ShowActionFeedback("Power Plan", ActionFeedbackKind.Failed, detail);
            }
            else
            {
                ShowActionFeedback("Power Plan", ActionFeedbackKind.Verified,
                    $"Spankyho Performance je aktivní • ověřeno {state.Total}/{state.Total}.");
            }
            if (_section == "power")
            {
                await Task.Delay(80);
                await ShowSectionAsync("power");
            }
            LogService.Info($"POWER_RECOMMENDED_PROGRESS_OK active={state.ActiveOk} criticalDrift={state.Drift}/{state.Total}");
        }
        catch (Exception ex)
        {
            LogService.Error("Hardware Max Performance UI failed", ex);
            ShowActionFeedback("Power Plan", ActionFeedbackKind.Failed, ex.Message);
            await ShowMessageAsync("Power Plan", "Akci se nepodařilo dokončit: " + ex.Message);
        }
        finally { _powerProfileRunning = false; }
    }

    internal async Task ShowPowerPreviewAsync()
    {
        _section = "power";
        await ShowSectionAsync("power");
        LogService.Info("POWER_PREVIEW_READY");
    }

    internal async Task RunPowerRenderSmokeAsync()
    {
        // The cached AI status is refreshed at a bounded cadence on rapid page
        // re-entry, but an empty first-render panel must always be populated.
        DateTime fixedUtc = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        if (ShouldRefreshCachedAiPowerControls(1, fixedUtc.AddSeconds(-1), fixedUtc) ||
            !ShouldRefreshCachedAiPowerControls(0, fixedUtc.AddSeconds(-1), fixedUtc) ||
            !ShouldRefreshCachedAiPowerControls(1, fixedUtc.AddSeconds(-4), fixedUtc))
            throw new InvalidOperationException("Cached AI Power controls refresh interval policy regressed.");
        if (!_powerCardFactoryPrewarmed)
            await PrewarmPowerCardFactoryAfterFirstRenderAsync();
        if (!_powerCardFactoryPrewarmed)
            throw new InvalidOperationException("Power card factory prewarm did not complete before render smoke.");

        await PowerPlanStateService.ReadAsync(forceRefresh: true);
        InvalidatePowerPageCache();
        _section = "power";

        var sw = Stopwatch.StartNew();
        await ShowSectionAsync("power");
        sw.Stop();

        if (sw.ElapsedMilliseconds > 75)
            throw new InvalidOperationException($"Power render too slow with warm snapshot: {sw.ElapsedMilliseconds} ms.");

        // A rapid switch must not lose unfinished deferred Power batches.
        // The cached tree may contain zero, partial, or completed card lists.
        await ShowSectionAsync("settings");
        await ShowSectionAsync("power");

        for (int i = 0; i < 40 && _powerActionsHost is not { Children.Count: >= 1 }; i++)
            await Task.Delay(25);
        if (_powerActionsHost is not { Children.Count: >= 1 })
            throw new InvalidOperationException("Power deferred Recommended action did not materialize after render.");

        for (int i = 0; i < 80 &&
             (_powerRecommendedSettings == null ||
              _powerRecommendedHost == null ||
              _powerRecommendedHost.Children.Count < _powerRecommendedSettings.Length); i++)
            await Task.Delay(25);

        int expectedRecommended = _powerRecommendedSettings?.Length ?? 0;
        int actualRecommended = _powerRecommendedHost?.Children.Count ?? 0;
        if (expectedRecommended <= 0 || actualRecommended != expectedRecommended ||
            _powerRecommendedRendered != expectedRecommended)
            throw new InvalidOperationException(
                $"Power Recommended render incomplete: expected={expectedRecommended} actual={actualRecommended} rendered={_powerRecommendedRendered}.");

        for (int i = 0; i < 80 &&
             (_powerAdvancedSettings == null ||
              _powerAdvancedHost == null ||
              _powerAdvancedHost.Children.Count < _powerAdvancedSettings.Length); i++)
            await Task.Delay(25);

        int expectedAdvanced = _powerAdvancedSettings?.Length ?? 0;
        int actualAdvanced = _powerAdvancedHost?.Children.Count ?? 0;
        if (expectedAdvanced <= 0 || actualAdvanced != expectedAdvanced ||
            _powerAdvancedRendered != expectedAdvanced)
            throw new InvalidOperationException(
                $"Power Advanced render incomplete: expected={expectedAdvanced} actual={actualAdvanced} rendered={_powerAdvancedRendered}.");

        if (_powerPageCache == null || _powerPageCache.Count == 0)
            throw new InvalidOperationException("Power page cache was not captured after warm render.");
        var firstRoot = _powerPageCache[0];
        var pinnedPowerHost = ContentStack;

        await ShowSectionAsync("settings");
        if (!ReferenceEquals(_persistentPowerHost, pinnedPowerHost) ||
            pinnedPowerHost.Visibility != Visibility.Collapsed ||
            !pinnedPowerHost.Children.Contains(firstRoot) ||
            !_contentLayerHost.Children.Contains(pinnedPowerHost))
            throw new InvalidOperationException("Power cached visual tree was not safely parked for rapid reuse.");
        var cacheSw = Stopwatch.StartNew();
        await ShowSectionAsync("power");
        cacheSw.Stop();
        if (!ReferenceEquals(ContentStack, pinnedPowerHost) ||
            pinnedPowerHost.Visibility != Visibility.Visible ||
            !ReferenceEquals(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(firstRoot), pinnedPowerHost))
            throw new InvalidOperationException("Power pinned visual tree was reparented or remained hidden.");
        if (!_powerPageCacheAttached)
            throw new InvalidOperationException("Power page cache was not reattached.");
        if (_powerPageCache == null || _powerPageCache.Count == 0 || !ReferenceEquals(_powerPageCache[0], firstRoot))
            throw new InvalidOperationException("Power cached UI tree was rebuilt instead of reused.");
        if (cacheSw.ElapsedMilliseconds > 20)
            throw new InvalidOperationException($"Power cached navigation too slow: {cacheSw.ElapsedMilliseconds} ms.");
        LogService.Info("performance", $"POWER_CACHE_SMOKE_OK attachMs={cacheSw.ElapsedMilliseconds} ttlMs={PowerPageCacheTtl.TotalMilliseconds:0}");

        // The cached whole host remains attached for rapid bounces, then its
        // Loaded lifetime ends after navigation settles without rebuilding cards.
        await ShowSectionAsync("settings");
        for (int i = 0; i < 30 && _contentLayerHost.Children.Contains(pinnedPowerHost); i++)
            await Task.Delay(100);
        if (_contentLayerHost.Children.Contains(pinnedPowerHost) ||
            !pinnedPowerHost.Children.Contains(firstRoot))
            throw new InvalidOperationException("Idle Power host failed to detach intact.");
        await ShowSectionAsync("power");
        if (!ReferenceEquals(ContentStack, pinnedPowerHost) ||
            !ReferenceEquals(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(firstRoot), pinnedPowerHost))
            throw new InvalidOperationException("Detached Power host was not restored intact.");

        await ShowSectionAsync("settings");
        _powerPageCacheAt = DateTime.UtcNow - PowerPageCacheTtl - TimeSpan.FromSeconds(1);
        await ShowSectionAsync("power");
        if (_persistentPowerHost != null || _contentLayerHost.Children.Contains(pinnedPowerHost) ||
            _powerPageCache is not { Count: > 0 } || ReferenceEquals(_powerPageCache[0], firstRoot))
            throw new InvalidOperationException("Expired hidden Power tree survived navigation or was reused.");

        var rebuiltPowerHost = ContentStack;
        await ShowSectionAsync("settings");
        InvalidatePowerPageCache();
        if (_persistentPowerHost != null || _contentLayerHost.Children.Contains(rebuiltPowerHost))
            throw new InvalidOperationException("Invalidated hidden Power tree remained attached.");
        await ShowSectionAsync("power");

        // Abandon the first deferred batches before they run, then return to the cached
        // tree. A stale callback must not clear the new generation's pending flags or
        // leave the page with missing Recommended/Advanced cards and action buttons.
        InvalidatePowerPageCache();
        await ShowSectionAsync("settings");
        await ShowSectionAsync("power");
        await ShowSectionAsync("settings");
        await ShowSectionAsync("power");
        for (int i = 0; i < 100; i++)
        {
            if (_powerSummaryHost is { Children.Count: > 0 } &&
                _powerActionsHost is { Children.Count: > 0 } &&
                _powerRecommendedSettings != null && _powerRecommendedHost != null &&
                _powerRecommendedHost.Children.Count == _powerRecommendedSettings.Length &&
                _powerAdvancedSettings != null && _powerAdvancedHost != null &&
                _powerAdvancedHost.Children.Count == _powerAdvancedSettings.Length)
                break;
            await Task.Delay(25);
        }
        if (_powerSummaryHost is not { Children.Count: > 0 } ||
            _powerActionsHost is not { Children.Count: > 0 } ||
            _powerRecommendedSettings == null || _powerRecommendedHost?.Children.Count != _powerRecommendedSettings.Length ||
            _powerAdvancedSettings == null || _powerAdvancedHost?.Children.Count != _powerAdvancedSettings.Length)
            throw new InvalidOperationException("Power bounce lost a deferred summary, action, or card batch.");
        LogService.Info("power", "POWER_DEFERRED_BOUNCE_SMOKE_OK");

        var adaptive = PerformanceProfilesService.GetAdaptivePowerProfile();
        if (adaptive.Epp is < 0 or > 100 || adaptive.CoreMin is < 1 or > 100 ||
            adaptive.IncThreshold is < 0 or > 100 || adaptive.DecThreshold is < 0 or > 100)
            throw new InvalidOperationException($"Adaptive power profile is invalid: EPP={adaptive.Epp} CoreMin={adaptive.CoreMin} Inc={adaptive.IncThreshold} Dec={adaptive.DecThreshold}.");
        LogService.Info("power", $"POWER_ADAPTIVE_PROFILE_SMOKE_OK arch={adaptive.Architecture} logical={adaptive.LogicalProcessors} epp={adaptive.Epp} coreMin={adaptive.CoreMin} inc={adaptive.IncThreshold} dec={adaptive.DecThreshold}");
        LogService.Info("performance", $"POWER_RENDER_SMOKE_OK renderMs={sw.ElapsedMilliseconds}");
    }

    internal async Task RunPowerUiSmokeAsync()
    {
        LogService.Info("POWER_UI_SMOKE begin");
        _section = "power";
        await ShowSectionAsync("power");
        await Task.Delay(750);
        LogService.Info("POWER_UI_SMOKE_LAYOUT_SETTLED");
        int exitCode = await RunPowerProfileProgressPopupAsync(true);
        if (exitCode != 0) throw new InvalidOperationException("Power progress popup smoke failed: " + exitCode);
        InvalidatePowerPageCache();
        PowerPlanStateService.InvalidateCache();
        await Task.Delay(120);
        await ShowSectionAsync("power");
        await Task.Delay(150);
        LogService.Info("POWER_UI_SMOKE_OK");
    }

    private void BuildNetworkPerformancePanel(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var networkUiPerf = System.Diagnostics.Stopwatch.StartNew();
        var heroHost = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Insert(0, heroHost);
        long shellMs = networkUiPerf.ElapsedMilliseconds;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "network" ||
                !ContentStack.Children.Contains(heroHost) || heroHost.Children.Count > 0) return;
            var deferred = System.Diagnostics.Stopwatch.StartNew();
            heroHost.Children.Add(BuildNetworkDashboardHero(token));
            LogService.Info("performance", $"NETWORK_HERO_DEFERRED ms={deferred.ElapsedMilliseconds}");
        });

        LogService.Info("performance", $"NETWORK_UI_PHASE heroMs={shellMs} dashboard=true deferredHero=true totalMs={networkUiPerf.ElapsedMilliseconds}");
    }

    private void BuildNetworkDashboardBody(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var perf = System.Diagnostics.Stopwatch.StartNew();
        long phase = 0;

        var dnsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        _networkDnsPanelHost = dnsHost;
        _networkDnsActionToken = token;
        ContentStack.Children.Add(dnsHost);
        ScheduleNetworkDnsControlPanel(token);
        long dnsMs = perf.ElapsedMilliseconds - phase; phase = perf.ElapsedMilliseconds;
        BuildMtuOptimizerPanel();
        ContentStack.Children.Add(BuildBufferbloatPanel());
        long mtuMs = perf.ElapsedMilliseconds - phase; phase = perf.ElapsedMilliseconds;

        ContentStack.Children.Add(BuildFlatSectionHeader(
            "Síťová karta",
            "Recommended profil a pokročilé capability aktivního adaptéru"));

        var nicHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(nicHost);
        ScheduleNetworkNicRecommendedPanel(nicHost, token);
        long nicMs = perf.ElapsedMilliseconds - phase; phase = perf.ElapsedMilliseconds;

        var advancedHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        advancedHost.Children.Add(BuildFlatSectionHeader(
            "Pokročilé volby síťové karty",
            "Ruční volby adaptéru • pouze podporované funkce driveru"));
        var tcp = new StackPanel { Spacing = 9 };
        _networkAdvancedBody = tcp;
        advancedHost.Children.Add(tcp);
        ContentStack.Children.Add(advancedHost);
        long advancedShellMs = perf.ElapsedMilliseconds - phase; phase = perf.ElapsedMilliseconds;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!token.IsCancellationRequested && generation == _navigationGeneration && _section == "network" &&
                ReferenceEquals(_networkAdvancedBody, tcp) && ContentStack.Children.Contains(advancedHost))
                ScheduleNetworkAdvancedControls(tcp);
        });
        long scheduleMs = perf.ElapsedMilliseconds - phase;

        LogService.Info("performance", $"NETWORK_DASHBOARD_BODY dnsMs={dnsMs} mtuMs={mtuMs} nicMs={nicMs} advancedShellMs={advancedShellMs} scheduleMs={scheduleMs} totalMs={perf.ElapsedMilliseconds}");
    }

    private void ScheduleNetworkNicRecommendedPanel(StackPanel host, CancellationToken token)
    {
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "network" ||
                !ContentStack.Children.Contains(host) ||
                host.Children.Count > 0)
                return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var body = new StackPanel { Spacing = 9 };
            body.Children.Add(new TextBlock
            {
                Text = "Jeden bezpečný profil pro aktivní adaptér. Použije jen funkce, které driver skutečně podporuje.",
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(ActionCard(
                "Doporučené",
                "Vypne úsporné funkce adaptéru, zachová citlivé volby latence a výsledek následně ověří.",
                "Použít",
                ApplyNicPerformanceAsync));
            host.Children.Add(BuildSectionSurface("network", "Výkon síťové karty", "Doporučený profil • ověřený skutečný stav", body));
            LogService.Info("performance", $"NETWORK_NIC_RECOMMENDED_DEFERRED ms={sw.ElapsedMilliseconds}");
        });
    }

    private void ScheduleNetworkTweakCards(CancellationToken token)
    {
        StackPanel? host = _networkTweakHost;
        int generation = _navigationGeneration;
        if (host == null || host.Children.Count > 0 ||
            (_networkTweakQueued && _networkTweakQueuedGeneration == generation)) return;
        // A queued callback from an earlier navigation must not suppress the
        // new page's work or clear the new generation's queued marker.
        _networkTweakQueued = true;
        _networkTweakQueuedGeneration = generation;
        bool accepted = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration ||
                _networkTweakQueuedGeneration != generation ||
                !ReferenceEquals(_networkTweakHost, host)) return;
            _networkTweakQueued = false;
            _networkTweakQueuedGeneration = -1;
            if (token.IsCancellationRequested || _section != "network" ||
                host.Children.Count > 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildClassicTweakCards(host);
            LogService.Info("performance", $"NETWORK_TWEAKS_DEFERRED ms={sw.ElapsedMilliseconds} count={_tweaks.Count}");
        });
        if (!accepted && _networkTweakQueuedGeneration == generation &&
            ReferenceEquals(_networkTweakHost, host))
        {
            _networkTweakQueued = false;
            _networkTweakQueuedGeneration = -1;
        }
    }

    private void ScheduleNetworkDnsControlPanel(CancellationToken token)
    {
        StackPanel? host = _networkDnsPanelHost;
        int generation = _navigationGeneration;
        if (host == null || host.Children.Count > 0 ||
            (_networkDnsPanelQueued && _networkDnsPanelQueuedGeneration == generation)) return;
        _networkDnsPanelQueued = true;
        _networkDnsPanelQueuedGeneration = generation;
        bool accepted = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // A stale callback cannot consume the latest navigation's queue
            // ownership, even when it targets the same cached host.
            if (generation != _navigationGeneration ||
                _networkDnsPanelQueuedGeneration != generation ||
                !ReferenceEquals(_networkDnsPanelHost, host)) return;
            _networkDnsPanelQueued = false;
            _networkDnsPanelQueuedGeneration = -1;
            if (token.IsCancellationRequested || _section != "network" ||
                host.Children.Count > 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildDnsControlPanel(token, host);
            LogService.Info("performance", $"NETWORK_DNS_PANEL_DEFERRED ms={sw.ElapsedMilliseconds}");
        });
        if (!accepted && _networkDnsPanelQueuedGeneration == generation &&
            ReferenceEquals(_networkDnsPanelHost, host))
        {
            _networkDnsPanelQueued = false;
            _networkDnsPanelQueuedGeneration = -1;
        }
    }

    private void BuildDnsControlPanel(CancellationToken token, StackPanel attachHost)
    {
        int generation = _navigationGeneration;
        var stage = System.Diagnostics.Stopwatch.StartNew();
        // Fixed 310px chart columns squeezed DNS actions on compact windows.
        // Stack results below the controls when there is insufficient width.
        bool compactDns = (RootGrid?.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth) < 1220;
        var grid = new Grid
        {
            Tag = compactDns ? "dns-compact-layout" : "dns-wide-layout",
            ColumnSpacing = compactDns ? 0 : 16,
            RowSpacing = compactDns ? 7 : 0
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = compactDns ? new GridLength(0) : new GridLength(310)
        });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new StackPanel { Spacing = 8 };
        var status = new TextBlock
        {
            Text = $"Načítám DNS… • {DnsBenchmarkCatalog.Servers.Length} resolverů v katalogu",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        _networkDnsStatus = status;
        body.Children.Add(status);
        long shellStartMs = stage.ElapsedMilliseconds;

        var actions = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, Tag = "dns-actions" };
        body.Children.Add(actions);
        body.Children.Add(new TextBlock
        {
            Text = "TOP výsledky se po testu zobrazí v grafu. Apply používá pouze skutečný aktivní Ethernet/Wi‑Fi adaptér a změnu následně ověří.",
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        grid.Children.Add(body);

        _networkDnsChartHost = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        if (compactDns) Grid.SetRow(_networkDnsChartHost, 1);
        else Grid.SetColumn(_networkDnsChartHost, 1);
        grid.Children.Add(_networkDnsChartHost);

        attachHost.Children.Add(BuildSectionSurface("network", "DNS Benchmark", "Rychlost • stabilita • Primary / Secondary • rollback", grid));
        long surfaceMs = stage.ElapsedMilliseconds - shellStartMs;
        LogService.Info("performance", $"NETWORK_DNS_SHELL_STAGE preSurfaceMs={shellStartMs} surfaceMs={surfaceMs} totalMs={stage.ElapsedMilliseconds}");

        SafeTaskService.Forget(Task.Run(() =>
        {
            string summary = DnsProfileService.GetActiveDnsSummary();
            bool rollback = DnsProfileService.HasRollbackSnapshot;
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "network" || !ReferenceEquals(_networkDnsStatus, status) || attachHost.Parent == null)
                    return;
                status.Text = summary + $" • {DnsBenchmarkCatalog.Servers.Length} resolverů v katalogu" +
                              (rollback ? " • rollback připraven" : string.Empty);
            });
        }, token), "network", "Deferred DNS summary");

        var actionsHost = actions;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "network" ||
                !ReferenceEquals(_networkDnsStatus, status) || attachHost.Parent == null || actionsHost.Children.Count > 0)
                return;

            var actionsSw = System.Diagnostics.Stopwatch.StartNew();
            var primaryActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            primaryActions.Children.Add(PrimaryQuickButton("Spustit benchmark", () => RunDnsBenchmarkPopupAsync(_networkDnsActionToken)));
            primaryActions.Children.Add(QuickButton("Aktualizovat katalog", () => UpdateDnsCatalogAsync(status, _networkDnsActionToken)));
            actionsHost.Children.Add(primaryActions);
            if (DnsProfileService.HasRollbackSnapshot)
            {
                actionsHost.Children.Add(QuickButton("Vrátit původní DNS", async () =>
                {
                    string result = await DnsProfileService.RollbackAsync(_networkDnsActionToken);
                    RefreshNetworkDnsDashboardStatus();
                    ShowActionFeedback("DNS", ActionFeedbackKind.Verified, result);
                }));
            }
            LogService.Info("performance", $"NETWORK_DNS_ACTIONS_DEFERRED ms={actionsSw.ElapsedMilliseconds}");
        });

        var chartHost = _networkDnsChartHost;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "network" ||
                !ReferenceEquals(_networkDnsChartHost, chartHost) || chartHost == null || attachHost.Parent == null)
                return;
            var chartSw = System.Diagnostics.Stopwatch.StartNew();
            RenderDnsDashboardChart(chartHost, _lastDnsDashboardResults);
            LogService.Info("performance", $"NETWORK_DNS_CHART_DEFERRED ms={chartSw.ElapsedMilliseconds}");
        });
    }

    private void BuildMtuOptimizerPanel()
    {
        int generation = _navigationGeneration;
        var host = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(host);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration || _section != "network" || !ContentStack.Children.Contains(host)) return;
            if (host.Children.Count == 0) BuildMtuOptimizerPanelCore(host, generation);
        });
    }
}
