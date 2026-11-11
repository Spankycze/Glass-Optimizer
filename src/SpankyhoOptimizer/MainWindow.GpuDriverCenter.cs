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
        }

        token.ThrowIfCancellationRequested();
        long detectMs = gpuPhase.ElapsedMilliseconds;
        long liveHeaderStartMs = gpuPhase.ElapsedMilliseconds;

        var root = new StackPanel { Spacing = 12 };
        long rootMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs;

        var liveHeaderHost = new StackPanel { Spacing = 6 };
        root.Children.Add(liveHeaderHost);
        int liveHeaderGeneration = _navigationGeneration;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                liveHeaderGeneration != _navigationGeneration ||
                _section != "gpu" ||
                liveHeaderHost.Children.Count > 0)
                return;

            var liveHeaderSw = System.Diagnostics.Stopwatch.StartNew();
            string gpuName = _gpuInfo?.Name ?? "Detekuji GPU…";
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

            _gpuNameText = new TextBlock
            {
                Text = gpuName,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush(),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(_gpuNameText);

            _gpuUpdateBadge = new TextBlock
            {
                Text = "NEZKONTROLOVÁNO",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = InfoBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(new Border
            {
                Background = AccentSoftBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(_darkTheme ? 1.1 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Child = _gpuUpdateBadge
            });
            liveHeaderHost.Children.Add(titleRow);

            _gpuDriverStatus = new TextBlock
            {
                Text = _gpuInfo == null ? "Detekce GPU probíhá na pozadí." : "Připraveno.",
                FontSize = 12.5,
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            };
            liveHeaderHost.Children.Add(_gpuDriverStatus);
            LogService.Info("performance",
                $"GPU_LIVE_HEADER_DEFERRED totalMs={liveHeaderSw.ElapsedMilliseconds} detected={_gpuInfo != null}");
        }))
        {
            LogService.Info("performance", "GPU_LIVE_HEADER_DEFERRED queue=false");
        }
        long liveHeaderMs = gpuPhase.ElapsedMilliseconds - liveHeaderStartMs;

        long shellStartMs = gpuPhase.ElapsedMilliseconds;
        long actionQueueMs = 0;
        long heroCreateMs = 0;
        StackPanel actions;
        Border hero;
        if (_gpuStaticHero != null && _gpuHeroActionsHost != null && (UseCompactSectionChrome || _gpuInfo != null))
        {
            actions = _gpuHeroActionsHost;
            long actionStartMs = gpuPhase.ElapsedMilliseconds;
            ScheduleGpuHeroActions(actions, token);
            actionQueueMs = gpuPhase.ElapsedMilliseconds - actionStartMs;
            hero = _gpuStaticHero;
            // Cache reuse is normal; log only a suspicious still-attached
            // element or a costly prepare path. The standard GPU build phase
            // already captures slow navigations.
            if (hero.Parent != null || gpuPhase.ElapsedMilliseconds >= 8)
                LogService.Info("performance", $"GPU_INTRO_CACHE_HIT detached={hero.Parent == null} actions={actions.Children.Count}");
        }
        else
        {
            actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _gpuHeroActionsHost = actions;
            long actionStartMs = gpuPhase.ElapsedMilliseconds;
            ScheduleGpuHeroActions(actions, token);
            actionQueueMs = gpuPhase.ElapsedMilliseconds - actionStartMs;
            long heroStartMs = gpuPhase.ElapsedMilliseconds;
            // Compact chrome is the normal Graphics layout. Avoid allocating
            // the unused vendor/detail strings for the legacy visual hero.
            hero = UseCompactSectionChrome
                ? BuildNeutralSectionIntro("Graphics", "GPU, ovladače a herní grafická nastavení na jednom místě.", actions)
                : BuildModernSectionHero(
                    "gpu", "Graphics", "GPU, ovladače a herní grafická nastavení na jednom místě.",
                    _gpuInfo?.Vendor ?? "GPU",
                    _gpuInfo == null ? "Detekce grafické karty probíhá…" : $"{_gpuInfo.Name} • driver {_gpuInfo.DisplayVersion}",
                    actions);
            heroCreateMs = gpuPhase.ElapsedMilliseconds - heroStartMs;
            // Intro text does not depend on live detection in compact mode.
            // Reuse it even while GPU hardware detection is still pending.
            if (UseCompactSectionChrome || _gpuInfo != null)
                _gpuStaticHero = hero;
        }
        long actionsMs = gpuPhase.ElapsedMilliseconds - shellStartMs;
        AttachCachedElement(hero);
        long heroMs = gpuPhase.ElapsedMilliseconds - shellStartMs - actionsMs;

        _gpuStatsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var gpuStatsHost = _gpuStatsHost;
        int gpuGeneration = _navigationGeneration;
        // Attach the lightweight empty host at its final location BEFORE
        // capturing the GPU page cache. The previous outer deferred callback
        // could run after snapshot and append all three stats tiles at the
        // bottom of the page during a fast GPU -> other -> GPU return.
        // Native feature tiles still materialize on Low-priority dispatcher.
        ContentStack.Children.Add(gpuStatsHost);
        ScheduleGpuStatsShell(gpuStatsHost, token, gpuGeneration);
        long statsMs = gpuPhase.ElapsedMilliseconds - shellStartMs - actionsMs - heroMs;

        // Reserve the Driver status slot synchronously, but defer its cold
        // native surface construction. A release smoke recorded 44ms inside
        // BuildSectionSurface here, on the 55ms GPU first-paint hot path.
        // This stable slot also belongs to any subsequent GPU cache snapshot.
        _gpuDriverStatusHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        _gpuDriverStatusBody = root;
        ContentStack.Children.Add(_gpuDriverStatusHost);
        ScheduleGpuDriverStatusSurface(_gpuDriverStatusHost, root, token, gpuGeneration);
        long statusMs = gpuPhase.ElapsedMilliseconds - shellStartMs - actionsMs - heroMs - statsMs;

        long baseUiMs = gpuPhase.ElapsedMilliseconds;
        if (baseUiMs - detectMs >= 8)
        {
            LogService.Info("performance", $"GPU_BASE_UI_PHASE actionsMs={actionsMs} heroMs={heroMs} statsMs={statsMs} statusMs={statusMs} totalMs={baseUiMs - detectMs}");
            LogService.Info("performance", $"GPU_BASE_UI_DETAIL actionQueueMs={actionQueueMs} heroCreateMs={heroCreateMs} unaccountedMs={Math.Max(0, actionsMs - actionQueueMs - heroCreateMs)} reused={_gpuInfo != null && _gpuStaticHero != null}");
        }
        _gpuDriverCardsHost = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(_gpuDriverCardsHost);
        ScheduleGpuDriverCards(token);
        long cardsMs = gpuPhase.ElapsedMilliseconds;
        long totalMs = cardsMs;
        if (detectMs > 0 || totalMs >= 20)
            LogService.Info("performance", $"GPU_UI_PHASE detectMs={detectMs} baseUiMs={baseUiMs - detectMs} cardsMs={cardsMs - baseUiMs} totalMs={totalMs} detected={_gpuInfo != null}");

        // A superseded navigation should not initiate a fresh GPU probe.
        // The next active GPU visit starts it normally.
        if (_gpuInfo == null && !token.IsCancellationRequested)
            deferredDetection = _gpuDeferredDetectionTask ??= ResolveGpuInfoDeferredAsync();
        if (!token.IsCancellationRequested && _gpuInfo == null && deferredDetection != null)
        {
            int navigationGeneration = _navigationGeneration;
            SafeTaskService.Forget(HydrateGpuDriverCenterAsync(deferredDetection, detectionGeneration, token, navigationGeneration), "gpu", "Deferred GPU detection");
        }
        return;
    }

    private void ScheduleGpuDriverStatusSurface(StackPanel host, StackPanel body, CancellationToken token, int generation)
    {
        if (ReferenceEquals(_gpuDriverStatusQueuedHost, host) || host.Children.Count > 0) return;
        _gpuDriverStatusQueuedHost = host;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(_gpuDriverStatusQueuedHost, host))
                _gpuDriverStatusQueuedHost = null;
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "gpu" || !ReferenceEquals(_gpuDriverStatusHost, host) ||
                !ReferenceEquals(_gpuDriverStatusBody, body) || host.Parent == null ||
                host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            host.Children.Add(BuildSectionSurface(
                "gpu", "Driver status", "Kontrola, stažení a instalace grafických ovladačů", body));
            LogService.Info("performance", $"GPU_STATUS_SURFACE_DEFERRED ms={sw.ElapsedMilliseconds}");
        }))
        {
            if (ReferenceEquals(_gpuDriverStatusQueuedHost, host))
                _gpuDriverStatusQueuedHost = null;
        }
    }

    private void ScheduleGpuStatsShell(StackPanel host, CancellationToken token, int generation)
    {
        if (ReferenceEquals(_gpuStatsQueuedHost, host) || host.Children.Count > 0) return;
        _gpuStatsQueuedHost = host;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(_gpuStatsQueuedHost, host)) _gpuStatsQueuedHost = null;
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu" || !ReferenceEquals(_gpuStatsHost, host) || host.Parent == null || host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Never reparent a live WinUI visual from a previous GPU page. Under rapid
            // GPU -> other-section navigation CoreMessaging can still own composition
            // state for the detached tree, and native reparenting can fault before a
            // managed exception is observable. Keep the data cached, not the visual.
            var statsShell = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 14) };
            for (int i = 0; i < 3; i++) statsShell.ColumnDefinitions.Add(new ColumnDefinition());
            string name = _gpuInfo?.Name ?? "Detekuji GPU…";
            _gpuNameTile = BuildFeatureTile("gpu", "GPU", name, _gpuInfo?.Vendor ?? "Detekce probíhá");
            _gpuDriverTile = BuildFeatureTile("components", "ZDROJE OVLADAČŮ", "2", "NVIDIA + Exostenza");
            _gpuDriverCenterTile = BuildFeatureTile("system", "OVLADAČE GPU", "PŘIPRAVENO", "Oficiální ovladač + volitelná Exostenza");
            var gpuCards = new[] { _gpuNameTile, _gpuDriverTile, _gpuDriverCenterTile };
            for (int i = 0; i < gpuCards.Length; i++)
            {
                Grid.SetColumn(gpuCards[i], i);
                statsShell.Children.Add(gpuCards[i]);
            }

            _gpuStatsShell = statsShell;
            host.Children.Add(statsShell);
            LogService.Info("performance", $"GPU_STATS_DEFERRED ms={sw.ElapsedMilliseconds} freshVisual=true");
        }))
        {
            if (ReferenceEquals(_gpuStatsQueuedHost, host)) _gpuStatsQueuedHost = null;
        }
    }

    private void ScheduleGpuHeroActions(StackPanel host, CancellationToken token)
    {
        if (ReferenceEquals(_gpuHeroActionsQueuedHost, host) || host.Children.Count > 0) return;
        int generation = _navigationGeneration;
        _gpuHeroActionsQueuedHost = host;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(_gpuHeroActionsQueuedHost, host)) _gpuHeroActionsQueuedHost = null;
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu" || !ReferenceEquals(_gpuHeroActionsHost, host) || host.Parent == null || host.Children.Count > 0)
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
        }))
        {
            if (ReferenceEquals(_gpuHeroActionsQueuedHost, host)) _gpuHeroActionsQueuedHost = null;
        }
    }

    private static async Task<GpuDriverInfo?> ResolveWarmGpuOrDetectAsync(
        Task<GpuDriverInfo?>? persistedRead,
        Func<Task<GpuDriverInfo?>> fallbackDetect,
        CancellationToken cancellationToken,
        Action<Exception> reportCacheFailure)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (persistedRead != null)
        {
            try
            {
                var persisted = await persistedRead.ConfigureAwait(false);
                if (persisted != null) return persisted;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A failed optional disk cache must not prevent a live GPU
                // detection. Only the fallback failure is terminal.
                reportCacheFailure(ex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await fallbackDetect().ConfigureAwait(false);
    }

    private async Task<GpuDriverInfo?> ResolveGpuInfoDeferredAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await ResolveWarmGpuOrDetectAsync(
                _gpuPersistentWarmTask,
                () => GpuDriverService.DetectPrimaryGpuAsync(_windowLifetimeCts.Token),
                _windowLifetimeCts.Token,
                ex => LogService.Error("GPU persisted warm read failed, trying live detection", ex))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested)
        {
            return null;
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

    private async Task HydrateGpuDriverCenterAsync(Task<GpuDriverInfo?> detection, long detectionGeneration, CancellationToken navigationToken, int navigationGeneration)
    {
        GpuDriverInfo? info;
        try { info = await detection.ConfigureAwait(false); }
        catch (Exception ex)
        {
            // A fault must not pin the completed detection forever, preventing
            // retries when the GPU page is visited again.
            _ = System.Threading.Interlocked.CompareExchange(ref _gpuDeferredDetectionTask, null, detection);
            LogService.Error("GPU deferred hydration failed", ex);
            return;
        }
        if (info == null || detectionGeneration != GpuDriverService.DetectionCacheGeneration)
        {
            _ = System.Threading.Interlocked.CompareExchange(ref _gpuDeferredDetectionTask, null, detection);
            return;
        }

        // WinUI-owned GPU state must only be published on its dispatcher,
        // AFTER validating navigation and PnP generations. Previously a
        // cancelled page could overwrite _gpuInfo from a worker thread.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _ = System.Threading.Interlocked.CompareExchange(ref _gpuDeferredDetectionTask, null, detection);
            if (_windowLifetimeCts.IsCancellationRequested || navigationToken.IsCancellationRequested ||
                navigationGeneration != _navigationGeneration || _section != "gpu" ||
                detectionGeneration != GpuDriverService.DetectionCacheGeneration)
                return;
            _gpuInfo = info;
            ApplyGpuInfoToLiveUi(info);
        }))
            _ = System.Threading.Interlocked.CompareExchange(ref _gpuDeferredDetectionTask, null, detection);
    }

    // Test only: fake completed Task values. Never scans hardware, touches
    // the persisted GPU cache, installs drivers, or changes Windows settings.
    internal async Task RunGpuDeferredHydrationOwnershipSmokeAsync()
    {
        await ShowSectionAsync("gpu");
        var originalInfo = _gpuInfo;
        var originalTask = _gpuDeferredDetectionTask;
        long detectionGeneration = GpuDriverService.DetectionCacheGeneration;
        int activeNavigation = _navigationGeneration;
        var staleInfo = new GpuDriverInfo("STALE GPU SMOKE", "Test", "0", "0", null);
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stale = Task.FromResult<GpuDriverInfo?>(staleInfo);
            _gpuDeferredDetectionTask = stale;
            await HydrateGpuDriverCenterAsync(stale, detectionGeneration, cancelled.Token, activeNavigation);
            await Task.Delay(40);
            if (!ReferenceEquals(_gpuInfo, originalInfo) || _gpuDeferredDetectionTask != null)
                throw new InvalidOperationException("Cancelled GPU hydration mutated the current driver or retained its completed task.");

            var wrongNavigation = Task.FromResult<GpuDriverInfo?>(staleInfo);
            _gpuDeferredDetectionTask = wrongNavigation;
            await HydrateGpuDriverCenterAsync(wrongNavigation, detectionGeneration, CancellationToken.None, activeNavigation - 1);
            await Task.Delay(40);
            if (!ReferenceEquals(_gpuInfo, originalInfo) || _gpuDeferredDetectionTask != null)
                throw new InvalidOperationException("Stale-navigation GPU hydration published a driver or retained its task.");

            var missing = Task.FromResult<GpuDriverInfo?>(null);
            _gpuDeferredDetectionTask = missing;
            await HydrateGpuDriverCenterAsync(missing, detectionGeneration, CancellationToken.None, activeNavigation);
            if (!ReferenceEquals(_gpuInfo, originalInfo) || _gpuDeferredDetectionTask != null)
                throw new InvalidOperationException("Missing GPU result blocked the next detection retry.");

            int fallbackRuns = 0, readFailures = 0;
            Task<GpuDriverInfo?> Probe()
            {
                fallbackRuns++;
                return Task.FromResult<GpuDriverInfo?>(staleInfo);
            }
            var recovered = await ResolveWarmGpuOrDetectAsync(
                Task.FromException<GpuDriverInfo?>(new IOException("synthetic cache failure")),
                Probe, CancellationToken.None, _ => readFailures++);
            if (!ReferenceEquals(recovered, staleInfo) || fallbackRuns != 1 || readFailures != 1)
                throw new InvalidOperationException("Faulted persisted GPU cache blocked live fallback.");

            var cached = await ResolveWarmGpuOrDetectAsync(
                Task.FromResult<GpuDriverInfo?>(staleInfo),
                Probe, CancellationToken.None, _ => readFailures++);
            if (!ReferenceEquals(cached, staleInfo) || fallbackRuns != 1 || readFailures != 1)
                throw new InvalidOperationException("Valid persisted GPU cache unnecessarily triggered fallback.");

            using var cancelledProbe = new CancellationTokenSource();
            cancelledProbe.Cancel();
            try
            {
                _ = await ResolveWarmGpuOrDetectAsync(
                    Task.FromResult<GpuDriverInfo?>(null), Probe,
                    cancelledProbe.Token, _ => readFailures++);
                throw new InvalidOperationException("Cancelled GPU cache resolver unexpectedly continued.");
            }
            catch (OperationCanceledException) when (cancelledProbe.IsCancellationRequested) { }
            if (fallbackRuns != 1)
                throw new InvalidOperationException("Cancelled GPU cache resolver executed hardware fallback.");
            LogService.Info(RunGpuWindowWarmFlightPolicySmoke());
            LogService.Info("GPU_DEFERRED_HYDRATION_OWNERSHIP_SMOKE_OK cancelledIgnored=true navigationIgnored=true retryAfterMiss=true cacheFaultFallback=true cacheHitNoProbe=true canceledProbeIgnored=true driverUnchanged=true");
        }
        finally
        {
            _gpuInfo = originalInfo;
            _gpuDeferredDetectionTask = originalTask;
        }
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
