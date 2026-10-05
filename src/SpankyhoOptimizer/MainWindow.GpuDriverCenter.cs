using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private GpuDriverInfo? _gpuInfo;
    private DriverReleaseInfo? _gpuOfficialRelease;
    private DriverReleaseInfo? _gpuExostenzaRelease;
    private string? _gpuDownloadedPackage;
    private TextBlock? _gpuNameText;
    private TextBlock? _gpuInstalledText;
    private TextBlock? _gpuOfficialText;
    private TextBlock? _gpuCommunityText;
    private TextBlock? _gpuDriverStatus;
    private TextBlock? _gpuUpdateBadge;
    private Border? _gpuNameTile;
    private Border? _gpuDriverTile;
    private Border? _gpuExostenzaCard;
    private Task<GpuDriverInfo?>? _gpuDeferredDetectionTask;
    private void BuildGpuDriverCenter(CancellationToken token = default)
    {
        var gpuPhase = System.Diagnostics.Stopwatch.StartNew();
        Task<GpuDriverInfo?>? deferredDetection = null;
        long detectionGeneration = _gpuDetectionGeneration;
        if (_gpuInfo == null)
        {
            if (_gpuPersistentWarmTask?.IsCompletedSuccessfully == true)
                _gpuInfo = _gpuPersistentWarmTask.Result;

            if (_gpuInfo == null)
                deferredDetection = _gpuDeferredDetectionTask ??= ResolveGpuInfoDeferredAsync();
        }

        token.ThrowIfCancellationRequested();
        long detectMs = gpuPhase.ElapsedMilliseconds;
        long liveHeaderStartMs = gpuPhase.ElapsedMilliseconds;

        var root = new StackPanel { Spacing = 12 };
        long rootMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs;

        string gpuName = _gpuInfo?.Name ?? "Detekuji GPU…";
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        long titleRowMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs - rootMs;

        _gpuNameText = new TextBlock { Text = gpuName, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(_gpuNameText);
        long nameMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs - rootMs - titleRowMs;

        _gpuUpdateBadge = new TextBlock { Text = "NEZKONTROLOVÁNO", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = InfoBrush(), VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new Border { Background = AccentSoftBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.1 : 1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Child = _gpuUpdateBadge });
        root.Children.Add(titleRow);
        long badgeMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs - rootMs - titleRowMs - nameMs;

        _gpuDriverStatus = new TextBlock { Text = _gpuInfo == null ? "Detekce GPU probíhá na pozadí." : "Připraveno.", FontSize = 12.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_gpuDriverStatus);
        long liveStatusMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs - rootMs - titleRowMs - nameMs - badgeMs;
        long liveHeaderMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs;
        if (liveHeaderMs >= 8)
            LogService.Info("performance", $"GPU_LIVE_HEADER_PHASE rootMs={rootMs} titleRowMs={titleRowMs} nameMs={nameMs} badgeMs={badgeMs} statusMs={liveStatusMs} totalMs={liveHeaderMs} detected={_gpuInfo != null}");

        long shellStartMs = gpuPhase.ElapsedMilliseconds;
        StackPanel actions;
        Border hero;
        if (_gpuInfo != null && _gpuStaticHero != null && _gpuHeroActionsHost != null)
        {
            actions = _gpuHeroActionsHost;
            ScheduleGpuHeroActions(actions, token);
            hero = _gpuStaticHero;
        }
        else
        {
            actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _gpuHeroActionsHost = actions;
            ScheduleGpuHeroActions(actions, token);
            hero = BuildModernSectionHero(
                "gpu",
                "Graphics",
                "GPU, ovladače a herní grafická nastavení na jednom místě.",
                _gpuInfo?.Vendor ?? "GPU",
                _gpuInfo == null ? "Detekce grafické karty probíhá…" : $"{_gpuInfo.Name} • driver {_gpuInfo.DisplayVersion}",
                actions);
            if (_gpuInfo != null)
                _gpuStaticHero = hero;
        }
        long actionsMs = gpuPhase.ElapsedMilliseconds - shellStartMs;
        AttachCachedElement(hero);
        long heroMs = gpuPhase.ElapsedMilliseconds - shellStartMs - actionsMs;

        _gpuStatsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var gpuStatsHost = _gpuStatsHost;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "gpu" || !ReferenceEquals(_gpuStatsHost, gpuStatsHost))
                return;

            if (!ContentStack.Children.Contains(gpuStatsHost))
                ContentStack.Children.Add(gpuStatsHost);
            ScheduleGpuStatsShell(gpuStatsHost, token);
        });
        long statsMs = gpuPhase.ElapsedMilliseconds - shellStartMs - actionsMs - heroMs;

        ContentStack.Children.Add(BuildSectionSurface("gpu", "Driver status", "Kontrola, stažení a instalace grafických ovladačů", root));
        long statusMs = gpuPhase.ElapsedMilliseconds - shellStartMs - actionsMs - heroMs - statsMs;

        long baseUiMs = gpuPhase.ElapsedMilliseconds;
        if (baseUiMs - detectMs >= 8)
            LogService.Info("performance", $"GPU_BASE_UI_PHASE actionsMs={actionsMs} heroMs={heroMs} statsMs={statsMs} statusMs={statusMs} totalMs={baseUiMs - detectMs}");
        _gpuDriverCardsHost = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(_gpuDriverCardsHost);
        ScheduleGpuDriverCards(token);
        long cardsMs = gpuPhase.ElapsedMilliseconds;
        long totalMs = cardsMs;
        if (detectMs > 0 || totalMs >= 20)
            LogService.Info("performance", $"GPU_UI_PHASE detectMs={detectMs} baseUiMs={baseUiMs - detectMs} cardsMs={cardsMs - baseUiMs} totalMs={totalMs} detected={_gpuInfo != null}");

        if (_gpuInfo == null && deferredDetection != null)
            SafeTaskService.Forget(HydrateGpuDriverCenterAsync(deferredDetection, detectionGeneration, token), "gpu", "Deferred GPU detection");
        return;
    }

    private void ScheduleGpuStatsShell(StackPanel host, CancellationToken token)
    {
        if (_gpuStatsQueued || host.Children.Count > 0) return;
        _gpuStatsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _gpuStatsQueued = false;
            if (token.IsCancellationRequested || _section != "gpu" || !ReferenceEquals(_gpuStatsHost, host) || host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_gpuStatsShell == null)
            {
                _gpuStatsShell = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 14) };
                for (int i = 0; i < 3; i++) _gpuStatsShell.ColumnDefinitions.Add(new ColumnDefinition());
                string name = _gpuInfo?.Name ?? "Detekuji GPU…";
                _gpuNameTile = BuildFeatureTile("gpu", "GPU", name, _gpuInfo?.Vendor ?? "Detekce probíhá");
                _gpuDriverTile = BuildFeatureTile("components", "ZDROJE OVLADAČŮ", "2", "NVIDIA + Exostenza");
                _gpuDriverCenterTile = BuildFeatureTile("system", "OVLADAČE GPU", "PŘIPRAVENO", "Oficiální ovladač + volitelná Exostenza");
                var gpuCards = new[] { _gpuNameTile, _gpuDriverTile, _gpuDriverCenterTile };
                for (int i = 0; i < gpuCards.Length; i++)
                {
                    Grid.SetColumn(gpuCards[i], i);
                    _gpuStatsShell.Children.Add(gpuCards[i]);
                }
            }
            else
            {
                UpdateGpuFeatureTile(_gpuNameTile, _gpuInfo?.Name ?? "Detekuji GPU…", _gpuInfo?.Vendor ?? "Detekce probíhá");
                UpdateGpuFeatureTile(_gpuDriverTile, "2", "NVIDIA + Exostenza");
                UpdateGpuFeatureTile(_gpuDriverCenterTile, "PŘIPRAVENO", "Oficiální ovladač + volitelná Exostenza");
            }

            if (_gpuStatsShell.Parent is Panel previousParent && !ReferenceEquals(previousParent, host))
                previousParent.Children.Remove(_gpuStatsShell);
            host.Children.Add(_gpuStatsShell);
            LogService.Info("performance", $"GPU_STATS_DEFERRED ms={sw.ElapsedMilliseconds}");
        });
    }

    private void ScheduleGpuHeroActions(StackPanel host, CancellationToken token)
    {
        if (_gpuHeroActionsQueued || host.Children.Count > 0) return;
        _gpuHeroActionsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _gpuHeroActionsQueued = false;
            if (token.IsCancellationRequested || _section != "gpu" || !ReferenceEquals(_gpuHeroActionsHost, host) || host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            host.Children.Add(PrimaryQuickButton("Zkontrolovat ovladače", CheckGpuDriverUpdatesAsync));
            host.Children.Add(QuickButton("Vlastní driver", PickAndInstallDriverAsync));
            host.Children.Add(QuickButton("Cache", () =>
            {
                Directory.CreateDirectory(GpuDriverService.CacheDir);
                ProcessRunner.Open(GpuDriverService.CacheDir);
                return Task.CompletedTask;
            }));
            LogService.Info("performance", $"GPU_HERO_ACTIONS_DEFERRED ms={sw.ElapsedMilliseconds}");
        });
    }

    private async Task<GpuDriverInfo?> ResolveGpuInfoDeferredAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_gpuPersistentWarmTask != null)
            {
                var persisted = await _gpuPersistentWarmTask.ConfigureAwait(false);
                if (persisted != null) return persisted;
            }

            return await GpuDriverService.DetectPrimaryGpuAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Error("GPU deferred detect failed", ex);
            return null;
        }
        finally
        {
            LogService.Info("performance", $"GPU_DEFERRED_DETECT ms={sw.ElapsedMilliseconds}");
        }
    }

    private async Task HydrateGpuDriverCenterAsync(Task<GpuDriverInfo?> detection, long detectionGeneration, CancellationToken navigationToken)
    {
        GpuDriverInfo? info;
        try { info = await detection.ConfigureAwait(false); }
        catch (Exception ex) { LogService.Error("GPU deferred hydration failed", ex); return; }
        if (info == null || detectionGeneration != GpuDriverService.DetectionCacheGeneration) return;

        _gpuInfo = info;
        _ = System.Threading.Interlocked.CompareExchange(ref _gpuDeferredDetectionTask, null, detection);
        if (navigationToken.IsCancellationRequested) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (navigationToken.IsCancellationRequested || _section != "gpu") return;
            ApplyGpuInfoToLiveUi(info);
        });
    }

    private void ApplyGpuInfoToLiveUi(GpuDriverInfo info)
    {
        if (_gpuNameText != null) _gpuNameText.Text = info.Name;
        if (_gpuDriverStatus != null) _gpuDriverStatus.Text = "Připraveno.";
        UpdateGpuFeatureTile(_gpuNameTile, info.Name, info.Vendor);
        UpdateGpuFeatureTile(_gpuDriverTile, "2", "NVIDIA + Exostenza");
        if (_gpuExostenzaCard != null)
            _gpuExostenzaCard.Visibility = GpuDriverService.IsExostenzaCompatible(info) ? Visibility.Visible : Visibility.Collapsed;
        LogService.Info("performance", $"GPU_DEFERRED_UI_APPLIED vendor={info.Vendor} version={info.DisplayVersion}");
    }

    private static void UpdateGpuFeatureTile(Border? tile, string value, string detail)
    {
        if (tile?.Child is not Grid grid || grid.Children.Count < 2 || grid.Children[1] is not StackPanel stack)
            return;
        if (stack.Children.Count > 1 && stack.Children[1] is TextBlock valueText) valueText.Text = value;
        if (stack.Children.Count > 2 && stack.Children[2] is TextBlock detailText) detailText.Text = detail;
    }

    private void ScheduleGpuDriverCards(CancellationToken token)
    {
        if (_gpuDriverCardsQueued || _gpuDriverCardsHost == null || _gpuDriverCardsHost.Children.Count > 0) return;
        _gpuDriverCardsQueued = true;
        long totalUiMs = 0;

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "gpu" || _gpuDriverCardsHost == null || _gpuDriverCardsHost.Children.Count > 0)
            {
                _gpuDriverCardsQueued = false;
                return;
            }

            var officialSw = System.Diagnostics.Stopwatch.StartNew();
            BuildOfficialDriverCard(_gpuDriverCardsHost);
            long officialMs = officialSw.ElapsedMilliseconds;
            totalUiMs += officialMs;
            LogService.Info("performance", $"GPU_DRIVER_CARDS_STAGE stage=official ms={officialMs}");

            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "gpu" || _gpuDriverCardsHost == null)
                {
                    _gpuDriverCardsQueued = false;
                    return;
                }

                var exostenzaSw = System.Diagnostics.Stopwatch.StartNew();
                BuildExostenzaCard(_gpuDriverCardsHost);
                long exostenzaMs = exostenzaSw.ElapsedMilliseconds;
                totalUiMs += exostenzaMs;
                _gpuDriverCardsQueued = false;
                LogService.Info("performance", $"GPU_DRIVER_CARDS_STAGE stage=exostenza ms={exostenzaMs}");
                LogService.Info("performance", $"GPU_DRIVER_CARDS_DEFERRED staged=true uiMs={totalUiMs} count={_gpuDriverCardsHost.Children.Count}");
            }))
                _gpuDriverCardsQueued = false;
        }))
            _gpuDriverCardsQueued = false;
    }

    private void BuildOfficialDriverCard(StackPanel host)
    {
        var body = new StackPanel { Spacing = 10 };
        string installed = _gpuInfo?.DisplayVersion ?? "zjišťuji…";
        string latest = _gpuOfficialRelease?.Version ?? "nekontrolováno";

        _gpuOfficialText = new TextBlock
        {
            Text = $"Nainstalováno {installed} • Nejnovější {latest}",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        body.Children.Add(_gpuOfficialText);

        body.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Nainstalováno", installed, "Aktuální ovladač"),
            BuildDashboardMetric("Nejnovější", latest, "NVIDIA"),
            BuildDashboardMetric("Zdroj", "NVIDIA", "Oficiální zdroj", "SpankySuccessBrush"),
            BuildDashboardMetric("Stav", _gpuOfficialRelease == null ? "Neověřeno" : "Připraveno", "Ověření / stažení")));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(PrimaryQuickButton("Zkontrolovat", CheckGpuDriverUpdatesAsync));
        row.Children.Add(QuickButton("Stáhnout nejnovější", DownloadOfficialGpuDriverAsync));
        row.Children.Add(QuickButton("Nainstalovat stažený", InstallDownloadedGpuDriverAsync));
        row.Children.Add(QuickButton("Oficiální stránka", () => { if (_gpuInfo != null) GpuDriverService.OpenSource(GpuDriverService.GetOfficialPage(_gpuInfo)); return Task.CompletedTask; }));
        body.Children.Add(row);

        host.Children.Add(BuildSectionSurface("gpu", "Oficiální ovladač", "Kontrola • stažení • instalace", body));
    }

    private void BuildExostenzaCard(StackPanel host)
    {
        var body = new StackPanel { Spacing = 10 };
        string installed = _gpuInfo?.DisplayVersion ?? "zjišťuji…";
        string latest = _gpuExostenzaRelease?.Version ?? "nekontrolováno";
        string? pendingExostenza = GpuDriverService.TryGetPendingExostenzaVersion();
        bool gpuInstallerRunning = GpuDriverService.IsKnownGpuInstallerRunning();
        string state = pendingExostenza != null ? $"Instaluje se {pendingExostenza}" : gpuInstallerRunning ? "Instalátor běží" : "Připraveno";

        body.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Nainstalováno", installed, "Aktuální ovladač"),
            BuildDashboardMetric("Nejnovější", latest, "Exostenza"),
            BuildDashboardMetric("Zdroj", "Guru3D", "Komunitní", "SpankyWarningBrush"),
            BuildDashboardMetric("Stav", state, "Jedna aktivní instalace")));

        _gpuCommunityText = new TextBlock
        {
            Text = pendingExostenza != null
                ? $"Ověřuji stav Exostenza {pendingExostenza}…"
                : $"Nainstalováno: {installed} • Nejnovější: {latest} • Zdroj: Exostenza / Guru3D",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            Visibility = pendingExostenza != null ? Visibility.Visible : Visibility.Collapsed
        };
        body.Children.Add(_gpuCommunityText);

        if (pendingExostenza != null)
        {
            SafeTaskService.Forget(Task.Run(async () =>
            {
                string? reconciled = await GpuDriverService.ReconcilePendingExostenzaAsync(CancellationToken.None);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_gpuCommunityText == null || string.IsNullOrWhiteSpace(reconciled)) return;
                    _gpuCommunityText.Text = reconciled;
                    _gpuCommunityText.Visibility = Visibility.Visible;
                });
            }), "drivers", "Exostenza install read-back");
        }

        body.Children.Add(new TextBlock
        {
            Text = "Community driver • před instalací se vytvoří restore point.",
            FontSize = 10.5,
            Foreground = UiBrush("SpankyWarningBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string installLabel = pendingExostenza != null
            ? $"Instalace běží ({pendingExostenza})"
            : gpuInstallerRunning ? "GPU instalátor už běží" : "Nainstalovat Exostenza";
        var installExostenza = PrimaryQuickButton(installLabel, InstallExostenzaOneClickAsync);
        installExostenza.IsEnabled = pendingExostenza == null && !gpuInstallerRunning;
        row.Children.Add(installExostenza);
        row.Children.Add(QuickButton("Guru3D", () => { GpuDriverService.OpenSource(_gpuExostenzaRelease?.DetailsUrl ?? GpuDriverService.ExostenzaThreadUrl); return Task.CompletedTask; }));
        body.Children.Add(row);

        _gpuExostenzaCard = BuildSectionSurface("gpu", "Exostenza Edition", "Community NVIDIA driver • bod obnovení • ověření", body);
        _gpuExostenzaCard.Visibility = GpuDriverService.IsExostenzaCompatible(_gpuInfo) ? Visibility.Visible : Visibility.Collapsed;
        host.Children.Add(_gpuExostenzaCard);
    }
    internal async Task RunGpuDeferredRenderSmokeAsync()
    {
        for (int i = 0; i < 40 && (!_mainTabsCompleted || !_deferredWindowSetupCompleted); i++)
            await Task.Delay(25);
        await Task.Delay(50);

        GpuDriverService.InvalidateDetectionCache();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("gpu");
        if (_gpuDetectionGeneration != GpuDriverService.DetectionCacheGeneration)
            throw new InvalidOperationException("GPU generation invalidation did not reach the UI cache.");
        sw.Stop();
        long renderMs = sw.ElapsedMilliseconds;

        if (_gpuNameText == null)
            throw new InvalidOperationException("GPU deferred smoke did not render the base shell.");

        for (int i = 0; i < 40 &&
            (_gpuNameTile == null || _gpuDriverTile == null || _gpuDriverCenterTile == null ||
             _gpuHeroActionsHost == null || _gpuHeroActionsHost.Children.Count < 3); i++)
            await Task.Delay(25);

        if (_gpuNameTile == null || _gpuDriverTile == null || _gpuDriverCenterTile == null ||
            _gpuHeroActionsHost == null || _gpuHeroActionsHost.Children.Count < 3)
            throw new InvalidOperationException("GPU deferred smoke did not materialize deferred stats/actions.");

        if (_tweaks.Count != 4)
            throw new InvalidOperationException($"GPU tweak catalog mismatch tweaks={_tweaks.Count}.");
        if (renderMs > 55)
            throw new InvalidOperationException($"GPU deferred shell render too slow: {renderMs} ms.");

        for (int i = 0; i < 40 && CountClassicTweakCards(_gpuTweakHost) < _tweaks.Count; i++)
            await Task.Delay(25);
        int renderedTweaks = CountClassicTweakCards(_gpuTweakHost);
        if (renderedTweaks != _tweaks.Count)
            throw new InvalidOperationException($"GPU deferred tweak cards incomplete: expected={_tweaks.Count} actual={renderedTweaks}.");

        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        while (_gpuInfo == null && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        if (_gpuInfo == null)
            throw new InvalidOperationException("GPU deferred smoke did not finish hardware detection.");
        await Task.Delay(50);

        if (!string.Equals(_gpuNameText.Text, _gpuInfo.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("GPU deferred smoke did not hydrate live UI.");

        bool exostenzaVisible = _gpuExostenzaCard?.Visibility == Visibility.Visible;
        if (exostenzaVisible != GpuDriverService.IsExostenzaCompatible(_gpuInfo))
            throw new InvalidOperationException("GPU deferred smoke has stale Exostenza visibility.");

        LogService.Info("performance", $"GPU_DEFERRED_RENDER_SMOKE_OK renderMs={renderMs} gpu={_gpuInfo.Name} version={_gpuInfo.DisplayVersion}");
    }

    private async Task CheckGpuDriverUpdatesAsync()
    {
        if (_gpuInfo == null) _gpuInfo = await GpuDriverService.DetectPrimaryGpuAsync();
        if (_gpuInfo == null)
        {
            if (_gpuDriverStatus != null) _gpuDriverStatus.Text = "GPU se nepodařilo detekovat.";
            SetGpuDriverUpdateBadge("DETEKCE SELHALA", updateAvailable: false);
            ShowActionFeedback("GPU Driver Center", ActionFeedbackKind.Failed, "GPU se nepodařilo detekovat. Použij Zkontrolovat ovladače pro nový pokus.");
            return;
        }
        try
        {
            _gpuOfficialRelease = await GpuDriverService.CheckOfficialAsync(_gpuInfo);
            bool updateAvailable = _gpuOfficialRelease != null && GpuDriverService.IsNewer(_gpuOfficialRelease.Version, _gpuInfo.DisplayVersion);
            if (_gpuOfficialText != null)
            {
                _gpuOfficialText.Text = _gpuOfficialRelease == null ? "Automatická kontrola pro tento model není dostupná — použij oficiální stránku nebo vlastní EXE." :
                    $"Nainstalováno: {_gpuInfo.DisplayVersion}  •  Nejnovější: {_gpuOfficialRelease.Version}  •  Zdroj: NVIDIA  •  {(updateAvailable ? "AKTUALIZACE DOSTUPNÁ" : "AKTUÁLNÍ")}";
            }
            SetGpuDriverUpdateBadge(_gpuOfficialRelease == null ? "RUČNÍ KONTROLA" : updateAvailable ? "AKTUALIZACE DOSTUPNÁ" : "AKTUÁLNÍ", updateAvailable);
            if (GpuDriverService.IsExostenzaCompatible(_gpuInfo))
            {
                _gpuExostenzaRelease = await GpuDriverService.CheckExostenzaAsync();
                if (_gpuCommunityText != null) _gpuCommunityText.Text = $"Nainstalováno: {_gpuInfo.DisplayVersion}  •  Nejnovější: {_gpuExostenzaRelease?.Version ?? "?"}  •  Zdroj: Exostenza / Guru3D";
            }
            ShowActionFeedback(
                "GPU Driver Center",
                ActionFeedbackKind.Verified,
                _gpuOfficialRelease == null
                    ? "GPU byl ověřen, ale automatická kontrola verze pro tento model není dostupná."
                    : updateAvailable
                        ? $"Dostupná aktualizace {_gpuOfficialRelease.Version}."
                        : $"Ovladač {_gpuInfo.DisplayVersion} je aktuální.");
        }
        catch (Exception ex)
        {
            LogService.Error("GPU driver update check failed", ex);
            SetGpuDriverUpdateBadge("KONTROLA SELHALA", updateAvailable: false);
            ShowActionFeedback("GPU Driver Center", ActionFeedbackKind.Failed, ex.Message);
            await ShowStyledInfoAsync("GPU Driver Center", "Kontrola aktualizací selhala: " + ex.Message);
        }
    }
    private async Task DownloadOfficialGpuDriverAsync()
    {
        if (_gpuInfo == null || _gpuOfficialRelease == null) await CheckGpuDriverUpdatesAsync();
        if (_gpuOfficialRelease == null) { await ShowStyledInfoAsync("Oficiální ovladač", "Automatické stažení pro tento GPU model zatím není dostupné. Použij oficiální stránku nebo vyber vlastní ovladač."); return; }
        try
        {
            SetGpuDriverStatus("Připravuji stažení oficiálního ovladače…");
            var progress = new Progress<DriverDownloadProgress>(p => SetGpuDriverStatus(p.Percent >= 0 ? $"Stahuji NVIDIA {_gpuOfficialRelease.Version}… {p.Percent} %" : $"Stahuji NVIDIA {_gpuOfficialRelease.Version}…"));
            _gpuDownloadedPackage = await GpuDriverService.DownloadAsync(_gpuOfficialRelease, progress);
            SetGpuDriverStatus("Staženo: " + Path.GetFileName(_gpuDownloadedPackage));
            ShowActionFeedback("GPU driver", ActionFeedbackKind.Verified, "Oficiální driver byl stažen a soubor byl uložen do cache.");
        }
        catch (Exception ex)
        {
            LogService.Error("Official GPU driver download failed", ex);
            SetGpuDriverStatus("Stažení selhalo.");
            ShowActionFeedback("GPU driver", ActionFeedbackKind.Failed, ex.Message);
            await ShowStyledInfoAsync("GPU Driver Center", ex.Message);
        }
    }

    private async Task InstallExostenzaOneClickAsync()
    {
        if (!GpuDriverService.IsExostenzaCompatible(_gpuInfo))
        {
            await ShowStyledInfoAsync("Exostenza Edition", "Exostenza je povolená jen pro podporované NVIDIA RTX GPU.");
            return;
        }
        await RunExostenzaInstallPopupAsync();
    }

    private async Task InstallDownloadedGpuDriverAsync()
    {
        if (string.IsNullOrWhiteSpace(_gpuDownloadedPackage) || !File.Exists(_gpuDownloadedPackage)) { await ShowStyledInfoAsync("GPU Driver Center", "Nejdřív driver stáhni nebo vyber vlastní instalační balík."); return; }
        bool community = _gpuDownloadedPackage.Contains("Exostenza", StringComparison.OrdinalIgnoreCase) || _gpuExostenzaRelease?.DownloadUrl?.Length > 0 && Path.GetExtension(_gpuDownloadedPackage).Equals(".rar", StringComparison.OrdinalIgnoreCase);
        string warning = community ? "Jde o Community / Unsupported driver. Před spuštěním vytvořím bod obnovení. Pokračovat?" : "Spustit stažený oficiální driver instalátor?";
        if (!await ShowStyledConfirmAsync("Instalace GPU driveru", warning, "Spustit instalaci", "Zrušit", community ? "Exostenza Edition" : "Official Driver")) return;
        try
        {
            if (community) await WindowsActions.CreateRestorePointAsync("Before community GPU driver");
            string package = _gpuDownloadedPackage;
            string ext = Path.GetExtension(package).ToLowerInvariant();
            string installer = ext == ".exe" ? package : await GpuDriverService.ExtractArchiveAndFindSetupAsync(package);
            SetGpuDriverStatus("Ověřuji podpis instalačního souboru…");
            var security = await FileSecurityService.InspectAsync(installer);
            LogService.Info("drivers", $"Installer security sha256={security.Sha256} status={security.SignatureStatus} signer={security.Signer}");
            if (!community && Path.GetFileName(package).StartsWith("NVIDIA_Official_", StringComparison.OrdinalIgnoreCase))
                FileSecurityService.RequireMicrosoftOrNvidia(security);
            SetGpuDriverStatus("Spouštím instalátor: " + Path.GetFileName(installer));
            GpuDriverService.LaunchInstaller(installer);
            ShowActionFeedback("GPU driver", ActionFeedbackKind.Info, "Instalátor byl spuštěn. Výsledek instalace se ověří až po dokončení instalátoru / restartu.");
        }
        catch (Exception ex)
        {
            LogService.Error("GPU driver install launch failed", ex);
            ShowActionFeedback("GPU driver", ActionFeedbackKind.Failed, ex.Message);
            await ShowStyledInfoAsync("GPU Driver Center", "Instalátor se nepodařilo spustit: " + ex.Message);
        }
    }

    private async Task PickAndInstallDriverAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add(".zip"); picker.FileTypeFilter.Add(".rar"); picker.FileTypeFilter.Add(".7z");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync(); if (file == null) return;
        _gpuDownloadedPackage = file.Path; SetGpuDriverStatus("Vybráno: " + file.Name);
        await InstallDownloadedGpuDriverAsync();
    }
    private void SetGpuDriverStatus(string text)
    {
        DispatcherQueue.TryEnqueue(() => { if (_gpuDriverStatus != null) _gpuDriverStatus.Text = text; });
    }
    private void SetGpuDriverUpdateBadge(string text, bool updateAvailable)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_gpuUpdateBadge == null) return;
            _gpuUpdateBadge.Text = text;
            _gpuUpdateBadge.Foreground = updateAvailable ? WarningBrush() : text == "AKTUÁLNÍ" ? SuccessBrush() : InfoBrush();
        });
    }
}
