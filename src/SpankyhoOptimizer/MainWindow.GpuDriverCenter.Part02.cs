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
    private void ScheduleGpuDriverCards(CancellationToken token)
    {
        if (_gpuDriverCardsHost == null || ReferenceEquals(_gpuDriverCardsQueuedHost, _gpuDriverCardsHost) || _gpuDriverCardsHost.Children.Count >= 2) return;
        int generation = _navigationGeneration;
        var cardsHost = _gpuDriverCardsHost;
        _gpuDriverCardsQueuedHost = cardsHost;
        long totalUiMs = 0;

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu" || !ReferenceEquals(_gpuDriverCardsHost, cardsHost) || cardsHost == null || cardsHost.Parent == null || cardsHost.Children.Count >= 2)
            {
                if (ReferenceEquals(_gpuDriverCardsQueuedHost, cardsHost)) _gpuDriverCardsQueuedHost = null;
                return;
            }

            var officialSw = System.Diagnostics.Stopwatch.StartNew();
            if (cardsHost.Children.Count == 0)
                BuildOfficialDriverCard(cardsHost);
            long officialMs = officialSw.ElapsedMilliseconds;
            totalUiMs += officialMs;
            LogService.Info("performance", $"GPU_DRIVER_CARDS_STAGE stage=official ms={officialMs}");

            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu" || !ReferenceEquals(_gpuDriverCardsHost, cardsHost) || cardsHost == null || cardsHost.Parent == null || cardsHost.Children.Count >= 2)
                {
                    if (ReferenceEquals(_gpuDriverCardsQueuedHost, cardsHost)) _gpuDriverCardsQueuedHost = null;
                    return;
                }

                var exostenzaSw = System.Diagnostics.Stopwatch.StartNew();
                BuildExostenzaCard(cardsHost);
                long exostenzaMs = exostenzaSw.ElapsedMilliseconds;
                totalUiMs += exostenzaMs;
                if (ReferenceEquals(_gpuDriverCardsQueuedHost, cardsHost)) _gpuDriverCardsQueuedHost = null;
                LogService.Info("performance", $"GPU_DRIVER_CARDS_STAGE stage=exostenza ms={exostenzaMs}");
                LogService.Info("performance", $"GPU_DRIVER_CARDS_DEFERRED staged=true uiMs={totalUiMs} count={cardsHost.Children.Count}");
            }))
            {
                if (ReferenceEquals(_gpuDriverCardsQueuedHost, cardsHost)) _gpuDriverCardsQueuedHost = null;
            }
        }))
        {
            if (ReferenceEquals(_gpuDriverCardsQueuedHost, cardsHost)) _gpuDriverCardsQueuedHost = null;
        }
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

        // Pair driver actions by workflow: verify/download, then install/source.
        // Never force four differently sized buttons onto one clipped row.
        var driverActions = new StackPanel { Spacing = 6, Tag = "gpu-official-actions" };
        var acquireActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        acquireActions.Children.Add(PrimaryQuickButton("Zkontrolovat", CheckGpuDriverUpdatesAsync));
        acquireActions.Children.Add(QuickButton("Stáhnout nejnovější", DownloadOfficialGpuDriverAsync));
        driverActions.Children.Add(acquireActions);
        var installActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        installActions.Children.Add(QuickButton("Nainstalovat stažený", InstallDownloadedGpuDriverAsync));
        installActions.Children.Add(QuickButton("Oficiální stránka", () => { if (_gpuInfo != null) GpuDriverService.OpenSource(GpuDriverService.GetOfficialPage(_gpuInfo)); return Task.CompletedTask; }));
        driverActions.Children.Add(installActions);
        body.Children.Add(driverActions);

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
        TextBlock communityText = _gpuCommunityText;
        int pageGeneration = _navigationGeneration;
        body.Children.Add(communityText);

        if (pendingExostenza != null)
        {
            SafeTaskService.Forget(ReconcilePendingExostenzaCardAsync(), "drivers", "Exostenza install read-back");

            async Task ReconcilePendingExostenzaCardAsync()
            {
                CancellationToken windowToken = _windowLifetimeCts.Token;
                try
                {
                    string? reconciled = await GpuDriverService.ReconcilePendingExostenzaAsync(windowToken).ConfigureAwait(false);
                    if (windowToken.IsCancellationRequested || string.IsNullOrWhiteSpace(reconciled)) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (windowToken.IsCancellationRequested ||
                            pageGeneration != _navigationGeneration ||
                            _section != "gpu" ||
                            !ReferenceEquals(_gpuCommunityText, communityText))
                            return;
                        communityText.Text = reconciled;
                        communityText.Visibility = Visibility.Visible;
                    });
                }
                catch (OperationCanceledException) when (windowToken.IsCancellationRequested) { }
            }
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
        bool prewarmedIntro = _gpuStaticHero != null && _gpuHeroActionsHost != null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("gpu");
        if (_gpuDetectionGeneration != GpuDriverService.DetectionCacheGeneration)
            throw new InvalidOperationException("GPU generation invalidation did not reach the UI cache.");
        sw.Stop();
        long renderMs = sw.ElapsedMilliseconds;
        // The stats host itself belongs to the synchronous page snapshot,
        // even though its expensive feature tiles are filled in later.
        // The ordering contract is Intro -> Stats -> Driver status -> Cards.
        if (_gpuStatsHost == null || _gpuDriverStatusHost == null || _gpuDriverCardsHost == null ||
            !ContentStack.Children.Contains(_gpuStatsHost) ||
            !ContentStack.Children.Contains(_gpuDriverStatusHost) ||
            !ContentStack.Children.Contains(_gpuDriverCardsHost) ||
            ContentStack.Children.IndexOf(_gpuStatsHost) >= ContentStack.Children.IndexOf(_gpuDriverStatusHost) ||
            ContentStack.Children.IndexOf(_gpuDriverStatusHost) >= ContentStack.Children.IndexOf(_gpuDriverCardsHost))
            throw new InvalidOperationException("GPU stats/driver-status placeholders were not attached in their original order during foreground navigation.");
        var initialStatsHost = _gpuStatsHost;
        var initialDriverStatusHost = _gpuDriverStatusHost;
        if (prewarmedIntro)
            LogService.Info("performance", "GPU_INTRO_PREWARM_SMOKE_OK");
        else
            LogService.Info("performance", "GPU_INTRO_PREWARM_SMOKE_MISS");

        for (int i = 0; i < 40 && _gpuNameText == null; i++)
            await Task.Delay(25);
        if (_gpuNameText == null)
            throw new InvalidOperationException("GPU deferred smoke did not render the deferred live header.");

        for (int i = 0; i < 40 && (initialDriverStatusHost.Children.Count == 0); i++)
            await Task.Delay(25);
        if (initialDriverStatusHost.Children.Count != 1 ||
            initialDriverStatusHost.Children[0] is not Border)
            throw new InvalidOperationException("GPU deferred Driver status surface did not materialize.");

        for (int i = 0; i < 40 &&
            (_gpuNameTile == null || _gpuDriverTile == null || _gpuDriverCenterTile == null ||
             _gpuHeroActionsHost == null || _gpuHeroActionsHost.Children.Count < 3); i++)
            await Task.Delay(25);

        if (_gpuNameTile == null || _gpuDriverTile == null || _gpuDriverCenterTile == null ||
            _gpuHeroActionsHost == null || _gpuHeroActionsHost.Children.Count < 3)
            throw new InvalidOperationException("GPU deferred smoke did not materialize deferred stats/actions.");

        // GPU tweak catalog is intentionally loaded after first paint.
        for (int i = 0; i < 80 && _tweaks.Count != 4; i++)
            await Task.Delay(25);

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

        // Cache is captured only after deferred tweak detection and the
        // hardware recommendations have finished. A quick return before that
        // is legitimately a new page, not a cached reattachment. Wait for
        // a real snapshot before asserting stable host identity.
        for (int i = 0; i < 120 && _gpuPageCache == null; i++)
            await Task.Delay(25);
        if (_gpuPageCache == null || !_gpuPageCache.Contains(initialStatsHost) ||
            !_gpuPageCache.Contains(initialDriverStatusHost) ||
            _gpuPageCache.IndexOf(initialStatsHost) >= _gpuPageCache.IndexOf(initialDriverStatusHost) ||
            _gpuPageCache.IndexOf(initialDriverStatusHost) >= _gpuPageCache.IndexOf(_gpuDriverCardsHost!))
            throw new InvalidOperationException("GPU snapshot omitted or reordered the deferred Driver status slot.");

        // Regression: GPU's small number of deep native roots must use
        // deferred host cleanup, not 61ms synchronous Children.Clear().
        // Cached stats/cards still must reattach correctly after the swap.
        var gpuHostBeforeExit = ContentStack;
        int gpuCompactClearBefore = _inPlaceNavigationClears;
        await ShowSectionAsync("overview");
        if (ReferenceEquals(ContentStack, gpuHostBeforeExit) ||
            _inPlaceNavigationClears != gpuCompactClearBefore)
            throw new InvalidOperationException("GPU exit synchronously cleared its deep native page instead of swapping hosts.");
        await ShowSectionAsync("gpu");
        for (int i = 0; i < 40 && (_gpuNameText == null || _gpuNameText.Text != _gpuInfo.Name); i++)
            await Task.Delay(25);
        if (_gpuNameText == null || !string.Equals(_gpuNameText.Text, _gpuInfo.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("GPU fast-return navigation left stale detected-device header.");
        if (!ReferenceEquals(_gpuStatsHost, initialStatsHost) ||
            !ReferenceEquals(_gpuDriverStatusHost, initialDriverStatusHost) ||
            initialDriverStatusHost.Children.Count != 1 ||
            _gpuPageCache == null || !_gpuPageCache.Contains(initialStatsHost) ||
            !_gpuPageCache.Contains(initialDriverStatusHost) ||
            !ContentStack.Children.Contains(initialStatsHost) ||
            !ContentStack.Children.Contains(initialDriverStatusHost) ||
            ContentStack.Children.IndexOf(initialStatsHost) >= ContentStack.Children.IndexOf(initialDriverStatusHost) ||
            ContentStack.Children.IndexOf(initialDriverStatusHost) >= ContentStack.Children.IndexOf(_gpuDriverCardsHost))
            throw new InvalidOperationException("GPU cached stats/Driver status hosts were omitted, reordered or recreated after quick return.");
        LogService.Info("performance", "GPU_FAST_RETURN_SMOKE_OK sections=gpu,overview,gpu statsHostCached=true ordered=true");

        // Force a superseded GPU navigation and check that Overview wins.
        await ShowSectionAsync("overview");
        Task pendingGpu = ShowSectionAsync("gpu");
        Task winningOverview = ShowSectionAsync("overview");
        await Task.WhenAll(pendingGpu, winningOverview);
        if (_section != "overview")
            throw new InvalidOperationException("Superseded GPU navigation replaced Overview.");
        await ShowSectionAsync("gpu");
        for (int i = 0; i < 40 && (_gpuNameText == null || _gpuNameText.Text != _gpuInfo.Name); i++)
            await Task.Delay(25);
        if (_gpuNameText == null || _gpuNameText.Text != _gpuInfo.Name)
            throw new InvalidOperationException("GPU navigation cancellation corrupted device header.");
        LogService.Info("performance", "GPU_CANCEL_NAV_SMOKE_OK");

        // Simulate the second deferred card being lost after the first stage.
        for (int i = 0; i < 40 && (_gpuDriverCardsHost?.Children.Count ?? 0) < 2; i++)
            await Task.Delay(25);
        if (_gpuDriverCardsHost?.Children.Count != 2)
            throw new InvalidOperationException("GPU cards were incomplete before partial-recovery smoke.");
        _gpuDriverCardsHost.Children.RemoveAt(1);
        await ShowSectionAsync("overview");
        await ShowSectionAsync("gpu");
        for (int i = 0; i < 40 && (_gpuDriverCardsHost?.Children.Count ?? 0) < 2; i++)
            await Task.Delay(25);
        if (_gpuDriverCardsHost?.Children.Count != 2)
            throw new InvalidOperationException("GPU partial driver cards were not recovered.");
        LogService.Info("performance", "GPU_PARTIAL_CARDS_RECOVERY_SMOKE_OK");

        // Simulate a rejected initial stats-host dispatch: no attached visual
        // and no rendered children when the GPU page is cached on exit.
        for (int i = 0; i < 40 && (_gpuStatsHost?.Children.Count ?? 0) == 0; i++)
            await Task.Delay(25);
        if (_gpuStatsHost == null || _gpuStatsHost.Children.Count == 0)
            throw new InvalidOperationException("GPU stats were missing before recovery smoke.");
        var detachedStatsHost = _gpuStatsHost;
        detachedStatsHost.Children.Clear();
        ContentStack.Children.Remove(detachedStatsHost);
        // Simulate a page snapshot captured during a rejected initial stats
        // callback, not only a missing visual from an otherwise valid cache.
        if (_gpuPageCache == null || !_gpuPageCache.Remove(detachedStatsHost))
            throw new InvalidOperationException("GPU stats recovery smoke expected an initial cached stats root.");
        await ShowSectionAsync("overview");
        await ShowSectionAsync("gpu");
        for (int i = 0; i < 40 && (detachedStatsHost.Children.Count == 0 ||
             !ContentStack.Children.Contains(detachedStatsHost)); i++)
            await Task.Delay(25);
        int statsIndex = ContentStack.Children.IndexOf(detachedStatsHost);
        int heroIndex = _gpuStaticHero == null ? -1 : ContentStack.Children.IndexOf(_gpuStaticHero);
        int driverIndex = _gpuDriverCardsHost == null ? -1 : ContentStack.Children.IndexOf(_gpuDriverCardsHost);
        if (!ReferenceEquals(_gpuStatsHost, detachedStatsHost) ||
            detachedStatsHost.Children.Count == 0 || statsIndex < 0 ||
            heroIndex < 0 || statsIndex != heroIndex + 1 ||
            driverIndex < 0 || statsIndex >= driverIndex ||
            _gpuPageCache == null || !_gpuPageCache.Contains(detachedStatsHost) ||
            _gpuPageCache.IndexOf(detachedStatsHost) != _gpuPageCache.IndexOf(_gpuStaticHero!) + 1)
            throw new InvalidOperationException("GPU omitted stats root was not recovered in its original visual and cached position.");
        LogService.Info("performance", "GPU_DETACHED_STATS_RECOVERY_SMOKE_OK cacheRestored=true orderRestored=true");
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
            if (string.IsNullOrWhiteSpace(_gpuDownloadedPackage) || !File.Exists(_gpuDownloadedPackage) || new FileInfo(_gpuDownloadedPackage).Length <= 0)
                throw new InvalidOperationException("Stažený GPU instalační balík nebyl po downloadu nalezen nebo je prázdný.");
            SetGpuDriverStatus("Staženo: " + Path.GetFileName(_gpuDownloadedPackage));
            ShowActionFeedback("GPU driver", ActionFeedbackKind.Info, "Oficiální instalační balík byl stažen, fyzicky ověřen v cache a čeká na instalaci.");
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
        int generation = _navigationGeneration;
        var status = _gpuDriverStatus;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _navigationGeneration && _section == "gpu" && status != null && ReferenceEquals(_gpuDriverStatus, status) && status.Parent != null)
                status.Text = text;
        });
    }
    private void SetGpuDriverUpdateBadge(string text, bool updateAvailable)
    {
        int generation = _navigationGeneration;
        var badge = _gpuUpdateBadge;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _navigationGeneration || _section != "gpu" || badge == null || !ReferenceEquals(_gpuUpdateBadge, badge) || badge.Parent == null) return;
            badge.Text = text;
            badge.Foreground = updateAvailable ? WarningBrush() : text == "AKTUÁLNÍ" ? SuccessBrush() : InfoBrush();
        });
    }
}
