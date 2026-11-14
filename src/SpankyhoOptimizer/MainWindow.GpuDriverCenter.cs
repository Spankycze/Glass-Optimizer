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
}
