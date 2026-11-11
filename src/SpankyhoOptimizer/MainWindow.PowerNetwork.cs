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

    private void BuildMtuOptimizerPanelCore(StackPanel host, int generation)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        var body = new StackPanel { Spacing = 11 };
        var status = new TextBlock
        {
            Text = "Načítám aktuální MTU…",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        _networkMtuStatus = status;
        body.Children.Add(status);
        var packetHost = new Grid { MinHeight = 62 };
        body.Children.Add(packetHost);
        body.Children.Add(new TextBlock
        {
            Text = "MTU mění pouze velikost packetu na aktivním adaptéru. DNS se touto operací nikdy nemění.",
            FontSize = 10.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        var controlsHost = new StackPanel { Spacing = 8 };
        body.Children.Add(controlsHost);

        body.Children.Add(new TextBlock
        {
            Text = "Automatika otestuje více internetových cílů pomocí DF ping, najde nejvyšší stabilní MTU a před změnou ukáže doporučení. VPN, tunely a virtuální adaptéry se ignorují.",
            Foreground = TextSecondaryBrush(),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        });

        host.Children.Add(BuildSectionSurface("network", "MTU Optimizer", "Aktuální stav • automatická detekce • ověření • obnova", body));
        long shellMs = perf.ElapsedMilliseconds;
        LogService.Info("performance", $"MTU_UI_DEFERRED staged=true shellMs={shellMs}");

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration || _section != "network" || !ReferenceEquals(_networkMtuStatus, status) || host.Parent == null || controlsHost.Children.Count > 0)
                return;

            var controlsSw = System.Diagnostics.Stopwatch.StartNew();
            var primary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            primary.Children.Add(PrimaryQuickButton("Najít nejlepší MTU", () => AutoOptimizeMtuAsync(status)));
            primary.Children.Add(QuickButton("Obnovit stav", () => RefreshMtuStatusAsync(status)));
            if (MtuOptimizerService.HasRollbackSnapshot)
                primary.Children.Add(QuickButton("Vrátit původní", () => RestoreOriginalMtuAsync(status)));
            controlsHost.Children.Add(primary);

            var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            presets.Children.Add(new TextBlock { Text = "Rychlé hodnoty:", Foreground = TextSecondaryBrush(), VerticalAlignment = VerticalAlignment.Center });
            foreach (int mtu in new[] { 1500, 1492, 1400 })
            {
                int captured = mtu;
                presets.Children.Add(QuickButton(captured.ToString(), () => ApplyMtuPresetAsync(captured, false, status)));
            }
            controlsHost.Children.Add(presets);

            var custom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var input = new TextBox { Width = 100, PlaceholderText = "Vlastní MTU", Text = "1500" };
            var ipv6 = new CheckBox { Content = "Také IPv6", VerticalAlignment = VerticalAlignment.Center };
            custom.Children.Add(input);
            custom.Children.Add(ipv6);
            custom.Children.Add(QuickButton("Použít", async () =>
            {
                if (!int.TryParse(input.Text, out int value))
                {
                    await ShowMessageAsync("MTU Optimizer", "Zadej platnou číselnou hodnotu MTU.");
                    return;
                }
                await ApplyMtuPresetAsync(value, ipv6.IsChecked == true, status);
            }));
            controlsHost.Children.Add(custom);
            LogService.Info("performance", $"MTU_CONTROLS_DEFERRED ms={controlsSw.ElapsedMilliseconds}");
        });

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration || _section != "network" || !ReferenceEquals(_networkMtuStatus, status) || host.Parent == null || packetHost.Children.Count > 0)
                return;
            var graphicSw = System.Diagnostics.Stopwatch.StartNew();
            packetHost.Children.Add(BuildMtuPacketGraphic());
            LogService.Info("performance", $"MTU_PACKET_GRAPHIC_DEFERRED ms={graphicSw.ElapsedMilliseconds}");
        });

        SafeTaskService.Forget(RefreshMtuStatusAsync(status, generation), "network", "MTU status");
    }

    private async Task RefreshMtuStatusAsync(TextBlock status, int generation = -1)
    {
        if (generation < 0) generation = _navigationGeneration;
        try
        {
            var adapter = await MtuOptimizerService.GetActiveAdapterAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            if (_section != "network") return;
            string ipv6Mtu = adapter.Ipv6Mtu > 0 ? adapter.Ipv6Mtu.ToString() : "N/A";
            status.Text = $"{adapter.Name} • {adapter.Description} • IPv4 MTU {adapter.Ipv4Mtu} • IPv6 MTU {ipv6Mtu}" +
                          (string.IsNullOrWhiteSpace(adapter.Gateway) ? "" : $" • Gateway {adapter.Gateway}") +
                          $" • Původní stav: {(MtuOptimizerService.HasRollbackSnapshot ? "uložen" : "zatím neuložen")}";
            status.Foreground = TextSecondaryBrush();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU status read failed", ex);
            status.Text = "MTU nelze načíst: " + ex.Message;
            status.Foreground = WarningBrush();
            ShowActionFeedback("MTU Optimizer", ActionFeedbackKind.Failed, ex.Message);
        }
    }

    private async Task RestoreOriginalMtuAsync(TextBlock status)
    {
        try
        {
            status.Text = "Obnovuji původní MTU a ověřuji výsledek…";
            var after = await MtuOptimizerService.RestoreOriginalAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            string ipv6Mtu = after.Ipv6Mtu > 0 ? after.Ipv6Mtu.ToString() : "N/A";
            string detail = $"Původní MTU obnoveno a ověřeno: {after.Name} • IPv4 {after.Ipv4Mtu} • IPv6 {ipv6Mtu}";
            status.Text = detail;
            InvalidateUiDataCache("network-live");
            RefreshNetworkLiveAfterMutation(_loadCts?.Token ?? _windowLifetimeCts.Token);
            ShowActionFeedback("MTU Restore", ActionFeedbackKind.Verified, detail);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU restore failed", ex);
            ShowActionFeedback("MTU Restore", ActionFeedbackKind.Failed, ex.Message);
            await ShowMessageAsync("MTU Optimizer", ex.Message);
            await RefreshMtuStatusAsync(status);
        }
    }

    private async Task AutoOptimizeMtuAsync(TextBlock status)
    {
        try
        {
            status.Text = "Probíhá vícecestný DF path-MTU test (Cloudflare / Google / Quad9)…";
            var result = await MtuOptimizerService.DetectRecommendedAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            if (!result.Conclusive)
            {
                status.Text = $"{result.Adapter.Name} • IPv4 MTU {result.CurrentMtu} • {result.Detail}";
                ShowActionFeedback("MTU Auto", ActionFeedbackKind.Unsupported, "DF test nebyl průkazný; MTU zůstalo beze změny.");
                return;
            }

            status.Text = $"{result.Adapter.Name} • Current {result.CurrentMtu} → Recommended {result.RecommendedMtu} • {result.Detail}";
            if (result.RecommendedMtu == result.CurrentMtu)
            {
                ShowActionFeedback("MTU Auto", ActionFeedbackKind.Verified, $"MTU {result.CurrentMtu} je už optimální pro testovanou cestu.");
                return;
            }

            if (!await ShowStyledConfirmAsync(
                "MTU Auto Optimizer",
                $"Adaptér: {result.Adapter.Name}\nAktuální IPv4 MTU: {result.CurrentMtu}\nDoporučené MTU: {result.RecommendedMtu}\nCíl testu: {result.Target}\n\n{result.Detail}",
                "Použít", "Zrušit", "Změna je persistentní a bude ověřena read-backem.")) return;

            await ApplyMtuPresetAsync(result.RecommendedMtu, false, status, skipConfirm: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU auto optimize failed", ex);
            ShowActionFeedback("MTU Auto", ActionFeedbackKind.Failed, ex.Message);
            await ShowMessageAsync("MTU Optimizer", ex.Message);
            await RefreshMtuStatusAsync(status);
        }
    }

    private async Task ApplyMtuPresetAsync(int mtu, bool applyIpv6, TextBlock status, bool skipConfirm = false)
    {
        if (mtu < 576 || mtu > 9000)
        {
            await ShowMessageAsync("MTU Optimizer", "Povolený rozsah je 576–9000. IPv6 vyžaduje minimálně 1280.");
            return;
        }
        if (applyIpv6 && mtu < 1280)
        {
            await ShowMessageAsync("MTU Optimizer", "IPv6 MTU nesmí být nižší než 1280.");
            return;
        }

        if (!skipConfirm && !await ShowStyledConfirmAsync(
            "Změnit MTU?",
            $"Nastavit IPv4 MTU na {mtu}" + (applyIpv6 ? " a stejnou hodnotu také pro IPv6." : "."),
            "Použít", "Zrušit", "Aktivní fyzický adaptér • trvalá změna • ověření")) return;

        try
        {
            status.Text = $"Aplikuji MTU {mtu}…";
            var after = await MtuOptimizerService.ApplyAsync(mtu, applyIpv6, _loadCts?.Token ?? _windowLifetimeCts.Token);
            string ipv6Mtu = after.Ipv6Mtu > 0 ? after.Ipv6Mtu.ToString() : "N/A";
            string detail = after.Ipv4Mtu == mtu
                ? $"Ověřeno: {after.Name} • IPv4 MTU {after.Ipv4Mtu} • IPv6 MTU {ipv6Mtu}"
                : $"Ověřeno: {after.Name} • požadováno IPv4 MTU {mtu} → Windows/driver přijal {after.Ipv4Mtu} • IPv6 MTU {ipv6Mtu}";
            status.Text = detail;
            InvalidateUiDataCache("network-live");
            RefreshNetworkLiveAfterMutation(_loadCts?.Token ?? _windowLifetimeCts.Token);
            ShowActionFeedback("MTU", ActionFeedbackKind.Verified, detail);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU apply failed", ex);
            string rollbackDetail = "Změna selhala; původní MTU bylo automaticky obnoveno. " + ex.Message;
            status.Text = rollbackDetail;
            ShowActionFeedback("MTU", ActionFeedbackKind.Failed, rollbackDetail);
            await ShowMessageAsync("MTU Optimizer", rollbackDetail);
            await RefreshMtuStatusAsync(status);
        }
    }

    internal async Task RunMtuReadbackSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result = await MtuOptimizerService.RunReadbackSmokeAsync();
        sw.Stop();
        LogService.Info($"MTU_READBACK_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} detail={result}");
    }

    internal async Task RunMtuProbeSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await MtuOptimizerService.DetectRecommendedAsync();
        sw.Stop();
        if (result.CurrentMtu <= 0 || result.RecommendedMtu < 576 || result.RecommendedMtu > 1500)
            throw new InvalidOperationException($"MTU probe vrátil neplatná data: current={result.CurrentMtu} recommended={result.RecommendedMtu}.");
        LogService.Info("network", $"MTU_PROBE_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} adapter={result.Adapter.Name} current={result.CurrentMtu} recommended={result.RecommendedMtu} target={result.Target} conclusive={result.Conclusive}");
    }

    private void ScheduleNetworkAdvancedControls(StackPanel tcp)
    {
        if (tcp.Children.Count >= 3) return;
        if (_networkAdvancedLoadQueued)
            return;

        int generation = _networkAdvancedLoadGeneration;
        _networkAdvancedLoadQueued = true;
        tcp.Children.Clear();
        tcp.Children.Add(new TextBlock { Text = "Načítám capability aktivního NIC driveru…", Foreground = TextSecondaryBrush() });

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_section != "network" || generation != _networkAdvancedLoadGeneration)
            {
                if (generation == _networkAdvancedLoadGeneration)
                    _networkAdvancedLoadQueued = false;
                return;
            }

            SafeTaskService.Forget(
                LoadNetworkAdvancedControlsTrackedAsync(tcp, _loadCts?.Token ?? _windowLifetimeCts.Token, generation),
                "network",
                "NIC capability controls");
        })) _networkAdvancedLoadQueued = false;
    }

    private async Task LoadNetworkAdvancedControlsTrackedAsync(StackPanel tcp, CancellationToken token, int generation)
    {
        try
        {
            await LoadNetworkAdvancedControlsAsync(tcp, token, generation);
        }
        finally
        {
            if (generation == _networkAdvancedLoadGeneration)
                _networkAdvancedLoadQueued = false;
        }
    }

    private async Task LoadNetworkAdvancedControlsAsync(StackPanel tcp, CancellationToken token, int generation)
    {
        var phase = Stopwatch.StartNew();
        try
        {
            // The nominally async capability service performs a synchronous
            // NIC route/adapter identity lookup and persisted-cache file read
            // before its first await. Keep those cold operations off the
            // WinUI dispatcher; their shared cache is already lock-protected.
            var capsTask = Task.Run(
                () => PerformanceProfilesService.GetNicCapabilitiesAsync(token), token);
            // This is read-only UI presentation, not an optimization decision.
            // Reuse the verified TCP snapshot and refresh stale values off-thread
            // rather than starting netsh.exe on every Network navigation after
            // its 45-second live TTL. Mutations explicitly invalidate the cache.
            // A first UI snapshot may synchronously read the persistent
            // netsh result from disk, so keep this pre-await work off UI too.
            var autoTuningTask = Task.Run(
                () => TcpGlobalStateService.GetValueForUiAsync("autotuninglevel", token), token);
            await Task.WhenAll(capsTask, autoTuningTask);
            var caps = await capsTask;
            string autoTuning = await autoTuningTask ?? "Unknown";
            if (token.IsCancellationRequested || generation != _networkAdvancedLoadGeneration || _section != "network" || !ReferenceEquals(_networkAdvancedBody, tcp)) return;
            QueueNetworkAdvancedCapabilityUiBatches(tcp, caps, autoTuning, phase);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "NIC capability controls failed", ex);
            if (_section == "network" && generation == _networkAdvancedLoadGeneration)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_section != "network" || generation != _networkAdvancedLoadGeneration || !ReferenceEquals(_networkAdvancedBody, tcp)) return;
                    tcp.Children.Clear();
                    tcp.Children.Add(new TextBlock
                    {
                        Text = "NIC capability se nepodařilo načíst: " + ex.Message,
                        Foreground = UiBrush("SpankyWarningBrush"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    tcp.Children.Add(QuickButton("Načíst znovu", () =>
                    {
                        _networkAdvancedLoadGeneration++;
                        _networkAdvancedLoadQueued = false;
                        tcp.Children.Clear();
                        ScheduleNetworkAdvancedControls(tcp);
                        return Task.CompletedTask;
                    }));
                    ShowActionFeedback("Síťová karta", ActionFeedbackKind.Failed, ex.Message);
                });
        }
    }

    private void QueueNetworkAdvancedCapabilityUiBatches(
        StackPanel tcp,
        IReadOnlyDictionary<string, string> caps,
        string autoTuning,
        Stopwatch phase)
    {
        int generation = _networkAdvancedLoadGeneration;
        tcp.Children.Clear();

        void QueueBatch1()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                AddNicAdvancedCapabilityCard(tcp, caps, "Energy Efficient Ethernet",
                    "Úsporné uspávání linky může přidat wake latency na desktopu.",
                    new[] { "Energy-Efficient Ethernet", "Energy Efficient Ethernet", "EEE", "Advanced EEE" },
                    recommendedOn: false);
                AddNicAdvancedCapabilityCard(tcp, caps, "Green Ethernet",
                    "Driverové šetření linky není potřeba pro desktopový low-latency profil.",
                    new[] { "Green Ethernet", "Green Ethernet Lite", "EnableGreenEthernet" },
                    recommendedOn: false);
                AddNicAdvancedCapabilityCard(tcp, caps, "Power Saving Mode",
                    "NIC power saving může přepínat adaptér do úsporných stavů při nečinnosti.",
                    new[] { "Power Saving Mode", "System Idle Power Saver", "Ultra Low Power Mode", "PowerSavingMode" },
                    recommendedOn: false);
                AddNicAdvancedCapabilityCard(tcp, caps, "Interrupt Moderation",
                    "Nižší batching může pomoci latenci, ale zvýšit CPU load; výsledek závisí na driveru a síti.",
                    new[] { "Interrupt Moderation", "InterruptModeration" },
                    allowAbTest: true);

                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=1 ms={sw.ElapsedMilliseconds}");
                QueueBatch2();
            });
        }

        void QueueBatch2()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                AddNicAdvancedCapabilityCard(tcp, caps, "Flow Control",
                    "Při zahlcení může být výhodné ON i OFF; Optimizer proto rozhodne jen měřením.",
                    new[] { "Flow Control", "FlowControl" },
                    allowAbTest: true);
                AddNicAdvancedCapabilityCard(tcp, caps, "Packet Coalescing",
                    "Coalescing snižuje počet interruptů, ale může měnit latency/CPU trade-off.",
                    new[] { "Packet Coalescing", "DMA Coalescing", "PacketCoalescing", "DMACoalescing" },
                    allowAbTest: true);
                AddNicAdvancedCapabilityCard(tcp, caps, "LSO IPv4",
                    "Large Send Offload může zlepšit throughput nebo naopak latency podle NIC/driveru.",
                    new[] { "Large Send Offload v2 (IPv4)", "Large Send Offload V2 (IPv4)", "*LsoV2IPv4" },
                    allowAbTest: true);
                AddNicAdvancedCapabilityCard(tcp, caps, "LSO IPv6",
                    "IPv6 LSO se netuní naslepo; změna se má nechat pouze pokud A/B měření vyjde lépe.",
                    new[] { "Large Send Offload v2 (IPv6)", "Large Send Offload V2 (IPv6)", "*LsoV2IPv6" },
                    allowAbTest: true);

                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=2 ms={sw.ElapsedMilliseconds}");
                QueueBatch3();
            });
        }

        void QueueBatch3()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                string selective = CapabilityValue(caps, "Selective Suspend");
                AddNicValueAdvisor(tcp, "Selective Suspend", selective, "Disabled (desktop)",
                    "Síťový adaptér nebude uspáván během nečinnosti.",
                    selective.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? OptimizationAdvisorKind.Ok : OptimizationAdvisorKind.Recommended);

                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=3 ms={sw.ElapsedMilliseconds}");
                QueueBatch4();
            });
        }

        void QueueBatch4()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                AddNicValueAdvisor(tcp, "TCP Auto-Tuning", autoTuning, "Normal",
                    "Windows receive-window autotuning má zůstat v Normal; Disabled může omezit throughput.",
                    autoTuning.Equals("normal", StringComparison.OrdinalIgnoreCase) ? OptimizationAdvisorKind.Ok : OptimizationAdvisorKind.Recommended);

                string selective = CapabilityValue(caps, "Selective Suspend");
                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=4 ms={sw.ElapsedMilliseconds}");
                LogService.Info("performance",
                    $"NETWORK_ADVANCED_CAPABILITY totalMs={phase.ElapsedMilliseconds} uiBatched=true interrupt={CapabilityValue(caps, "Interrupt Moderation")} flow={CapabilityValue(caps, "Flow Control")} packet={CapabilityValue(caps, "Packet Coalescing")} selective={selective} autotuning={autoTuning}");
            });
        }

        QueueBatch1();
    }

    private static string CapabilityValue(IReadOnlyDictionary<string, string> caps, string key) =>
        caps.TryGetValue(key, out var value) ? value : "Unsupported";

    private void AddNicAdvancedCapabilityCard(StackPanel host, IReadOnlyDictionary<string, string> caps,
        string title, string description, string[] aliases, bool? recommendedOn = null, bool allowAbTest = false)
    {
        string current = CapabilityValue(caps, title);
        if (current.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info("network", $"NIC capability hidden-unsupported={title}");
            return;
        }

        bool isOn = NicCapabilityIsOn(current);

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        string recommendation = recommendedOn.HasValue
            ? $"Recommended: {(recommendedOn.Value ? "ON" : "OFF")}."
            : allowAbTest ? "Doporučené: TEST / A/B." : "Recommended: driver default.";
        var detail = new TextBlock
        {
            Text = $"Aktuální: {current} • {recommendation} Důvod: {description}",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        textStack.Children.Add(detail);
        row.Children.Add(textStack);

        var stateText = new TextBlock
        {
            Text = isOn ? "ON" : "OFF",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = isOn ? SuccessBrush() : TextSecondaryBrush()
        };
        var switchTrack = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = isOn ? UnifiedToggleOnTrack() : UnifiedToggleOffTrack(),
            BorderBrush = isOn
                ? ThemeColor(158, 116, 207, 250)
                : ThemeColor(96, 130, 171, 209),
            BorderThickness = new Thickness(1)
        };
        var switchKnob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = ThemeColor(252, 252, 255),
            HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(1)
        };
        var switchVisual = new Grid { Width = 44, Height = 22 };
        switchVisual.Children.Add(switchTrack);
        switchVisual.Children.Add(switchKnob);
        var switchButton = new Button
        {
            Content = switchVisual,
            Padding = new Thickness(0),
            MinWidth = 44,
            MinHeight = 22,
            Width = 44,
            Height = 22,
            Background = null,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // This Button is a transparent HIT TARGET around a custom 44x22 switch,
        // not an action button. Prevent global blue Button visual-state resources
        // from covering the glass track during hover/press.
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            switchButton.Resources["ButtonBackground" + state] = ThemeColor(0, 0, 0, 0);
            switchButton.Resources["ButtonBorderBrush" + state] = ThemeColor(0, 0, 0, 0);
        }
        ToolTipService.SetToolTip(switchButton, title);
        EnsureAccessibleButton(switchButton, title);

        bool busy = false;
        bool currentState = isOn;

        void SetToggleState(bool value)
        {
            currentState = value;
            switchTrack.Background = value ? UnifiedToggleOnTrack() : UnifiedToggleOffTrack();
            switchTrack.BorderBrush = value
                ? ThemeColor(158, 116, 207, 250)
                : ThemeColor(96, 130, 171, 209);
            switchKnob.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            stateText.Text = value ? "ON" : "OFF";
            stateText.Foreground = value ? SuccessBrush() : TextSecondaryBrush();
        }

        async Task ApplyToggleAsync(bool requested)
        {
            try
            {
                switchButton.IsEnabled = false;
                detail.Text = requested ? "Zapínám a ověřuji skutečný stav…" : "Vypínám a ověřuji skutečný stav…";
                LogService.Info("network", $"NIC_TOGGLE_CLICK name={title} requested={requested}");
                string result = await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, requested);
                PerformanceProfilesService.InvalidateNicCache();

                var freshCaps = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                string freshValue = CapabilityValue(freshCaps, title);
                bool actual = NicCapabilityIsOn(freshValue);
                SetToggleState(actual);

                if (actual != requested)
                    throw new InvalidOperationException($"{title}: driver změnu nepotvrdil ({freshValue}).");

                detail.Text = $"Aktuální: {freshValue} • {recommendation} Důvod: {description}";
                ShowActionFeedback(title, ActionFeedbackKind.Verified, result + " • Ověřeno: " + freshValue);
                LogService.Info("network", $"NIC_TOGGLE_VERIFIED name={title} requested={requested} actual={actual} value={freshValue}");
            }
            catch (Exception ex)
            {
                LogService.Error("network", $"Advanced NIC toggle failed: {title}", ex);
                PerformanceProfilesService.InvalidateNicCache();
                try
                {
                    var freshCaps = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                    string freshValue = CapabilityValue(freshCaps, title);
                    SetToggleState(NicCapabilityIsOn(freshValue));
                    detail.Text = $"Aktuální: {freshValue} • změna se nepodařila: {ex.Message}";
                }
                catch
                {
                    SetToggleState(isOn);
                    detail.Text = "Změna se nepodařila: " + ex.Message;
                }
                ShowActionFeedback(title, ActionFeedbackKind.Failed, ex.Message);
            }
            finally
            {
                switchButton.IsEnabled = true;
                busy = false;
            }
        }

        switchButton.Click += (_, _) =>
        {
            if (busy) return;
            bool requested = !currentState;
            SetToggleState(requested);
            busy = true;
            SafeTaskService.Forget(ApplyToggleAsync(requested), "network", "Advanced NIC toggle " + title);
        };

        var rightActions = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        rightActions.Children.Add(switchButton);
        rightActions.Children.Add(stateText);
        if (allowAbTest)
        {
            rightActions.Children.Add(QuickButton("A/B test", async () =>
            {
                if (busy) return;
                bool original = currentState;
                var mutation = new NicAbMutationGuard();
                if (!await ShowStyledConfirmAsync(
                    "NIC A/B test",
                    $"{title}\n\nOptimizer změří baseline, přepne pouze tuto vlastnost, restartuje síťový adaptér a změří latency/jitter/loss znovu. Pokud výsledek nebude lepší, původní hodnota se automaticky obnoví.",
                    "Spustit test", "Zrušit", "Síť může na několik sekund vypadnout.")) return;

                busy = true;
                switchButton.IsEnabled = false;
                try
                {
                    detail.Text = "A/B: měřím baseline…";
                    NetworkDiagnosticsService.Invalidate();
                    var baseline = await NetworkDiagnosticsService.MeasureAsync(samples: 10);

                    if (baseline.Received == 0)
                        throw new InvalidOperationException("ICMP baseline nemá odpovědi; A/B test nelze spolehlivě vyhodnotit.");

                    bool trialValue = !original;
                    detail.Text = $"A/B: přepínám na {(trialValue ? "ON" : "OFF")}…";
                    await mutation.ApplyTrialAsync(async () =>
                        { await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, trialValue); });
                    PerformanceProfilesService.InvalidateNicCache();
                    NetworkDiagnosticsService.Invalidate();
                    await Task.Delay(800);

                    detail.Text = "A/B: měřím testovací variantu…";
                    var trial = await NetworkDiagnosticsService.MeasureAsync(samples: 10);
                    if (trial.Received == 0)
                        throw new InvalidOperationException("Testovací varianta nemá ICMP odpovědi.");

                    double baselineScore = NicAbScore(baseline);
                    double trialScore = NicAbScore(trial);
                    double requiredGain = Math.Max(0.5, baselineScore * 0.03);
                    bool improved =
                        trial.LossPercent <= baseline.LossPercent &&
                        trialScore + requiredGain < baselineScore;

                    if (!improved)
                    {
                        detail.Text = "A/B: test nebyl lepší, obnovuji původní stav…";
                        await mutation.RestoreAsync(async () =>
                            { await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, original); });
                        PerformanceProfilesService.InvalidateNicCache();
                    }

                    NetworkDiagnosticsService.Invalidate();
                    var fresh = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                    string freshValue = CapabilityValue(fresh, title);
                    if (string.IsNullOrWhiteSpace(freshValue) ||
                        freshValue.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                        freshValue.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"{title}: NIC driver read-back is unavailable.");
                    bool actual = NicCapabilityIsOn(freshValue);
                    mutation.ConfirmReadback(actual, expected: improved ? !original : original,
                        restoredOriginal: !improved);
                    SetToggleState(actual);

                    string result = improved
                        ? $"TEST LEPŠÍ — ponecháno {(actual ? "ON" : "OFF")}. Baseline {baseline.AvgMs:0.0} ms / jitter {baseline.JitterMs:0.0} / loss {baseline.LossPercent:0.0}% → test {trial.AvgMs:0.0} ms / jitter {trial.JitterMs:0.0} / loss {trial.LossPercent:0.0}%."
                        : $"TEST HORŠÍ/STEJNÝ — vráceno na {(actual ? "ON" : "OFF")}. Baseline score {baselineScore:0.0}, test score {trialScore:0.0}.";
                    detail.Text = result;
                    ShowActionFeedback(title + " A/B", ActionFeedbackKind.Verified, result);
                    LogService.Info("network", $"NIC_AB_TEST name={title} original={original} keptTrial={improved} baselineScore={baselineScore:0.00} trialScore={trialScore:0.00}");
                }
                catch (Exception ex)
                {
                    Exception? rollbackError = null;
                    if (mutation.MayHaveChanged)
                    {
                        try
                        {
                            await mutation.RestoreAsync(async () =>
                                { await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, original); });
                        }
                        catch (Exception restoreEx)
                        {
                            rollbackError = restoreEx;
                            LogService.Error("network", "NIC A/B rollback failed: " + title, restoreEx);
                        }
                    }

                    PerformanceProfilesService.InvalidateNicCache();
                    NetworkDiagnosticsService.Invalidate();
                    string readbackResult;
                    try
                    {
                        var verifiedCaps = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                        string verifiedValue = CapabilityValue(verifiedCaps, title);
                        if (string.IsNullOrWhiteSpace(verifiedValue) ||
                            verifiedValue.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                            verifiedValue.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Driver value cannot be verified.");
                        bool verifiedOn = NicCapabilityIsOn(verifiedValue);
                        SetToggleState(verifiedOn);
                        if (verifiedOn == original)
                        {
                            mutation.ConfirmReadback(verifiedOn, original, restoredOriginal: true);
                            readbackResult = "Původní stav ověřen.";
                        }
                        else
                            readbackResult = "VAROVÁNÍ: původní stav síťové karty nebyl obnoven.";
                    }
                    catch (Exception readbackEx)
                    {
                        LogService.Error("network", "NIC A/B recovery read-back failed: " + title, readbackEx);
                        readbackResult = "VAROVÁNÍ: stav síťové karty nelze ověřit.";
                    }
                    if (rollbackError != null)
                        readbackResult += " Obnovení ovladače selhalo: " + rollbackError.Message;
                    detail.Text = "A/B test selhal: " + ex.Message + " " + readbackResult;
                    ShowActionFeedback(title + " A/B", ActionFeedbackKind.Failed, detail.Text);
                }
                finally
                {
                    switchButton.IsEnabled = true;
                    busy = false;
                }
            }));
        }

        Grid.SetColumn(rightActions, 1);
        row.Children.Add(rightActions);

        host.Children.Add(new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = row
        });
    }
    private void AddNicValueAdvisor(
        StackPanel host,
        string title,
        string current,
        string recommended,
        string reason,
        OptimizationAdvisorKind kind = OptimizationAdvisorKind.Test)
    {
        if (current.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            kind = OptimizationAdvisorKind.Info;
            recommended = "Driver nepodporuje";
        }
        host.Children.Add(BuildOptimizationAdvisorRow(title, current, recommended, reason, kind));
    }

    private static bool NicCapabilityIsOn(string value) =>
        !value.Contains("Disabled", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Off", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("No", StringComparison.OrdinalIgnoreCase);

    // NIC property mutations may succeed at driver level and then throw during
    // verification. Mark the attempt *before* awaiting, and retain that state
    // until the original setting has been confirmed by an independent read.
    private sealed class NicAbMutationGuard
    {
        internal bool MayHaveChanged { get; private set; }

        internal async Task ApplyTrialAsync(Func<Task> apply)
        {
            MayHaveChanged = true;
            await apply();
        }

        internal async Task RestoreAsync(Func<Task> restore)
        {
            if (MayHaveChanged)
                await restore();
        }

        internal void ConfirmReadback(bool actual, bool expected, bool restoredOriginal)
        {
            if (actual != expected)
                throw new InvalidOperationException(
                    "NIC A/B read-back did not confirm the expected driver setting.");
            if (restoredOriginal)
                MayHaveChanged = false;
        }
    }

    private static async Task RunNicAbSafetySmokeAsync()
    {
        var guard = new NicAbMutationGuard();
        bool driverState = false;
        try
        {
            await guard.ApplyTrialAsync(() =>
            {
                driverState = true;
                throw new InvalidOperationException("synthetic post-write driver failure");
            });
            throw new InvalidOperationException("Synthetic post-write failure was ignored.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic post-write driver failure") { }
        if (!guard.MayHaveChanged || !driverState)
            throw new InvalidOperationException("NIC A/B missed partially applied driver mutation.");

        bool simulatedRollbackFailed = false;
        try
        {
            await guard.RestoreAsync(() =>
            {
                simulatedRollbackFailed = true;
                throw new InvalidOperationException("synthetic rollback failure");
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic rollback failure") { }
        if (!simulatedRollbackFailed || !guard.MayHaveChanged)
            throw new InvalidOperationException("NIC A/B lost pending rollback after restore failure.");
        await guard.RestoreAsync(() => { driverState = false; return Task.CompletedTask; });
        guard.ConfirmReadback(driverState, expected: false, restoredOriginal: true);
        if (guard.MayHaveChanged)
            throw new InvalidOperationException("NIC A/B retained a confirmed completed rollback.");

        await guard.ApplyTrialAsync(() => { driverState = true; return Task.CompletedTask; });
        bool mismatchRejected = false;
        try { guard.ConfirmReadback(driverState, expected: false, restoredOriginal: true); }
        catch (InvalidOperationException) { mismatchRejected = true; }
        if (!mismatchRejected || !guard.MayHaveChanged)
            throw new InvalidOperationException("NIC A/B accepted incorrect driver read-back.");

        await guard.RestoreAsync(() => { driverState = false; return Task.CompletedTask; });
        guard.ConfirmReadback(driverState, expected: false, restoredOriginal: true);
        LogService.Info("network",
            "NIC_AB_SAFETY_SMOKE_OK partialWriteRollback=true rollbackRetry=true readbackRequired=true nicWrites=0");
    }

    private static double NicAbScore(NetworkLatencyReport report) =>
        report.AvgMs + (report.JitterMs * 1.5) + (report.LossPercent * 10.0);
    private async Task LoadNetworkLiveStatusAsync(StackPanel live, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            var nicTask = SystemInsightService.GetActiveNetworkAsync(token);
            var capsTask = PerformanceProfilesService.GetNicCapabilitiesAsync(token);
            // Sdílí stejný 4-sample cache key jako hlavní network telemetry,
            // takže jedna návštěva stránky nespouští dvě oddělené ICMP série.
            var latencyTask = NetworkDiagnosticsService.MeasureAsync(samples: 4, token: token);
            await Task.WhenAll(nicTask, latencyTask);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "network" ||
                !ReferenceEquals(_networkLiveBody, live)) return;
            var nic = await nicTask;
            var latency = await latencyTask;
            IReadOnlyDictionary<string, string>? caps = null;
            try { caps = await capsTask; }
            catch (Exception capEx) { LogService.Error("network", "Live NIC capability read failed", capEx); }
            string liveRss = caps != null && caps.TryGetValue("RSS", out var rssState) ? rssState : "Unknown";
            string liveRsc = caps != null && caps.TryGetValue("RSC", out var rscState) ? rscState : "Unknown";
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "network" ||
                    !ReferenceEquals(_networkLiveBody, live)) return;
                live.Children.Clear();
                live.Children.Add(BuildPageSummaryStrip(
                    ("Link", nic == null ? "N/A" : $"{nic.SpeedMbps:0} Mb/s", nic == null ? "SpankyWarningBrush" : "SpankySuccessBrush"),
                    ("Latency", latency.Received == 0 ? "N/A" : $"{latency.AvgMs:0.0} ms", latency.Received == 0 ? "SpankyWarningBrush" : "SpankySuccessBrush"),
                    ("Jitter", latency.Received == 0 ? "N/A" : $"{latency.JitterMs:0.0} ms", latency.JitterMs > 8 ? "SpankyWarningBrush" : "SpankySuccessBrush"),
                    ("Loss", latency.Received == 0 ? "ICMP blokováno" : $"{latency.LossPercent:0.0} %", latency.LossPercent > 0 ? "SpankyWarningBrush" : "SpankySuccessBrush")));
                if (nic != null)
                    live.Children.Add(new TextBlock { Text = $"{nic.Description} • MTU {nic.Mtu} • RSS {liveRss} • RSC {liveRsc} • {nic.DriverProvider} {nic.DriverVersion}", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Network V3 live status failed", ex);
            if (!token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested ||
                        generation != _navigationGeneration ||
                        _section != "network" ||
                        !ReferenceEquals(_networkLiveBody, live)) return;
                    live.Children.Clear();
                    live.Children.Add(new TextBlock
                    {
                        Text = "Aktuální stav sítě se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    live.Children.Add(QuickButton("Načíst znovu", () =>
                        LoadNetworkLiveStatusAsync(live, _loadCts?.Token ?? _windowLifetimeCts.Token)));
                    ShowActionFeedback("Síť — live stav", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }
    }

    private async Task ApplyNicPerformanceAsync()
    {
        string current = await PerformanceProfilesService.GetNicPerformanceSummaryAsync();
        if (!await ShowStyledConfirmAsync("NIC Max Performance", current + "\n\nSíťový adaptér se krátce restartuje a spojení může na několik sekund vypadnout.", "Použít profil", "Zrušit", "Realtek / Intel síťový profil")) return;
        int exitCode = await RunNicProgressPopupAsync();
        if (exitCode == 0)
        {
            InvalidateUiDataCache("network");
            RefreshNetworkLiveAfterMutation(_loadCts?.Token ?? _windowLifetimeCts.Token);
            if (_section == "network" && _networkAdvancedBody != null)
            {
                _networkAdvancedLoadGeneration++;
                _networkAdvancedLoadQueued = false;
                _networkAdvancedBody.Children.Clear();
                ScheduleNetworkAdvancedControls(_networkAdvancedBody);
            }
            try
            {
                string readback = await PerformanceProfilesService.GetNicTuningReadbackAsync();
                LogService.Info("NIC Max Performance completed OK");
                ShowActionFeedback("NIC Recommended", ActionFeedbackKind.Verified, "Profil byl aplikován a ověřen.");
                await ShowStyledInfoAsync("NIC Max Performance — ověřeno", readback);
            }
            catch (Exception ex)
            {
                LogService.Error("network", "NIC post-apply read-back failed", ex);
                ShowActionFeedback("NIC Recommended", ActionFeedbackKind.Failed, "Profil byl aplikován, ale finální ověření selhalo.");
                await ShowStyledInfoAsync("NIC Max Performance — ověření selhalo", ex.Message);
            }
        }
        else if (exitCode == -2)
        {
            LogService.Info("network", "NIC Max Performance was cancelled from the progress popup.");
        }
        else
        {
            LogService.Error("NIC Max Performance failed | ExitCode=" + exitCode);
            ShowActionFeedback("NIC Recommended", ActionFeedbackKind.Failed, "Profil se nepodařilo aplikovat nebo ověřit.");
        }
    }

    internal async Task RunNetworkDeferredRenderSmokeAsync()
    {
        await ShowSectionAsync("network");
        // Force navigation-generation churn while low-priority DNS and tweak
        // callbacks may still be queued. Cached hosts must recover without
        // stale callbacks clearing the new generation's queue ownership.
        for (int hop = 0; hop < 3; hop++)
        {
            await ShowSectionAsync("settings");
            await ShowSectionAsync("network");
        }
        int expectedTweaks = _tweaks.Count;
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            bool advancedReady = _networkAdvancedBody != null &&
                !(_networkAdvancedBody.Children.Count == 1 &&
                  _networkAdvancedBody.Children[0] is TextBlock loading &&
                  loading.Text.StartsWith("Načítám", StringComparison.OrdinalIgnoreCase));
            if (advancedReady &&
                (_networkDnsPanelHost?.Children.Count ?? 0) == 1 &&
                (_networkTelemetryShellHost?.Children.Count ?? 0) == 1 &&
                _networkToolsCard != null && ContentStack.Children.Contains(_networkToolsCard) &&
                _networkToolsRow?.Children.Count == 3 &&
                (UseCompactSectionChrome || _networkHeroArtHost?.Children.Count == 1) &&
                CountClassicTweakCards(_networkTweakHost) == expectedTweaks &&
                !_networkDnsPanelQueued && !_networkTweakQueued &&
                !_networkTelemetryShellQueued && !_networkToolsQueued && !_networkHeroArtQueued)
                break;
            await Task.Delay(50);
        }
        if (_networkAdvancedBody == null ||
            (_networkAdvancedBody.Children.Count == 1 &&
             _networkAdvancedBody.Children[0] is TextBlock pending &&
             pending.Text.StartsWith("Načítám", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Network deferred Advanced NIC controls did not complete.");
        if ((_networkDnsPanelHost?.Children.Count ?? 0) != 1 ||
            (_networkTelemetryShellHost?.Children.Count ?? 0) != 1 ||
            _networkToolsCard == null || !ContentStack.Children.Contains(_networkToolsCard) ||
            _networkToolsRow?.Children.Count != 3 ||
            (!UseCompactSectionChrome && _networkHeroArtHost?.Children.Count != 1) ||
            CountClassicTweakCards(_networkTweakHost) != expectedTweaks ||
            _networkDnsPanelQueued || _networkTweakQueued ||
            _networkTelemetryShellQueued || _networkToolsQueued || _networkHeroArtQueued)
            throw new InvalidOperationException(
                $"Network generation handoff incomplete: dns={_networkDnsPanelHost?.Children.Count ?? -1} telemetry={_networkTelemetryShellHost?.Children.Count ?? -1} tools={_networkToolsCard != null && ContentStack.Children.Contains(_networkToolsCard)} toolButtons={_networkToolsRow?.Children.Count ?? -1} hero={_networkHeroArtHost?.Children.Count ?? -1} tweaks={CountClassicTweakCards(_networkTweakHost)}/{expectedTweaks} queued={_networkDnsPanelQueued}/{_networkTweakQueued}/{_networkTelemetryShellQueued}/{_networkToolsQueued}/{_networkHeroArtQueued}.");
        LogService.Info("NETWORK_DEFERRED_SMOKE_OK hops=3 dns=1 telemetry=1 tools=1 queued=false");
    }

    internal async Task RunNicUiSmokeAsync()
    {
        await RunNicAbSafetySmokeAsync();
        LogService.Info("NIC_UI_SMOKE begin");
        await ShowSectionAsync("network");
        int expectedTweaks = _tweaks.Count;
        for (int i = 0; i < 40 &&
             (CountClassicTweakCards(_networkTweakHost) < expectedTweaks ||
              (_networkTelemetryShellHost?.Children.Count ?? 0) == 0 ||
              _networkDnsChartHost == null); i++)
            await Task.Delay(25);
        int actualTweaks = CountClassicTweakCards(_networkTweakHost);
        if (actualTweaks < expectedTweaks)
            throw new InvalidOperationException($"Network deferred tweak cards incomplete: expected={expectedTweaks} actual={actualTweaks}.");
        if ((_networkTelemetryShellHost?.Children.Count ?? 0) == 0)
            throw new InvalidOperationException("Network telemetry shell did not complete.");
        if (_networkDnsChartHost == null)
            throw new InvalidOperationException("Network DNS dashboard chart did not materialize.");

        var tweakHost = _networkTweakHost;
        var telemetryHost = _networkTelemetryShellHost;
        var dnsChartHost = _networkDnsChartHost;
        await ShowSectionAsync("settings");
        await ShowSectionAsync("network");
        if (!ReferenceEquals(_networkTweakHost, tweakHost) ||
            !ReferenceEquals(_networkTelemetryShellHost, telemetryHost) ||
            !ReferenceEquals(_networkDnsChartHost, dnsChartHost))
            throw new InvalidOperationException("Network cached dashboard hosts were rebuilt instead of reattached.");
        if (CountClassicTweakCards(_networkTweakHost) < expectedTweaks ||
            (_networkTelemetryShellHost?.Children.Count ?? 0) == 0 ||
            _networkDnsChartHost == null)
            throw new InvalidOperationException("Network cached dashboard content was not preserved after reattach.");

        await Task.Delay(100);
        int overlaysBefore = RootGrid.Children.Count;
        // This is an interface lifecycle gate; never tune/restart the user's live NIC.
        int code = await RunNicProgressPopupAsync(true, async token =>
        {
            await Task.Delay(600, token);
            return "NIC_UI_SMOKE_BACKEND_OK";
        });
        if (RootGrid.Children.Count != overlaysBefore)
            throw new InvalidOperationException("NIC UI smoke leaked its progress overlay.");
        if (code != 0) throw new InvalidOperationException("NIC UI smoke failed: " + code);
        LogService.Info("NIC_UI_SMOKE_OK dashboard=true dnsChart=true diagnosticsRemoved=true mutation=false progressLifecycle=true");
    }
}

