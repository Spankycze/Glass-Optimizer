using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow : Window
{
    private async Task ShowSectionCoreAsync(string key, int generation, CancellationToken token)
    {
        var navPhase = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration) throw new OperationCanceledException(token);
        string previousSection = _section;
        CleanupForNavigation(key);
        long cleanupMs = navPhase.ElapsedMilliseconds;
        _section = key;
        if (key == "overview" && _windowTelemetryActive)
            SignalLiveMetricsWake();
        _themeSection = key;
        UpdateGlassShell();
        bool overviewNoScroll = string.Equals(key, "overview", StringComparison.OrdinalIgnoreCase) &&
            (RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth) >= 1200 &&
            (RootGrid.ActualHeight > 1 ? RootGrid.ActualHeight : 860) >= 790;
        if (!overviewNoScroll && _pageScroll.VerticalScrollMode != ScrollMode.Enabled)
            _pageScroll.VerticalScrollMode = ScrollMode.Enabled;
        if (overviewNoScroll)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation != _navigationGeneration || _section != "overview") return;
                if (Math.Abs(_pageScroll.VerticalOffset) > 0.1)
                    _pageScroll.ChangeView(null, 0, null, true);
                if (_pageScroll.VerticalScrollMode != ScrollMode.Disabled)
                    _pageScroll.VerticalScrollMode = ScrollMode.Disabled;
            });
        }
        if (ActionBar.Visibility != Visibility.Collapsed)
            ActionBar.Visibility = Visibility.Collapsed;
        _powerPageCacheAttached = false;
        long stateMs = navPhase.ElapsedMilliseconds;
        ClearNavigationHostsFast(previousSection, key);
        long clearMs = navPhase.ElapsedMilliseconds;
        SectionDefinition sectionDefinition = key == "overview"
            ? StartupOverviewSection
            : key == "system"
                ? StartupSystemSection
            : key == "components"
                ? new SectionDefinition(
                    "components",
                    _sub ?? "Služby",
                    _sub switch
                    {
                        "Ovladače" => "Správa ovladačů a kompatibility zařízení",
                        "Task Scheduler" => "Plánované úlohy a doporučené nastavení",
                        _ => "Bezpečné doporučené nastavení služeb"
                    },
                    "\uE713")
                : CatalogService.GetSection(key);
        long catalogMs = navPhase.ElapsedMilliseconds;
        UpdateVisualSelection(key, sectionDefinition.IconGlyph);
        long selectionMs = navPhase.ElapsedMilliseconds;
        Visibility targetHeroVisibility = key == "overview" ? Visibility.Visible : Visibility.Collapsed;
        if (_heroDeferredCompleted && HeroTextPanel.Visibility != targetHeroVisibility)
            HeroTextPanel.Visibility = targetHeroVisibility;
        long heroMs = navPhase.ElapsedMilliseconds;
        double targetHeaderHeight = CalculateSectionHeroHeight(
            key,
            RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth);
        // The navigation selection changed SubNav visibility. Apply the final layout once,
        // including the target hero height; the previous flow could do two full passes.
        UpdateRowlessShellGeometry(heroHeightOverride: targetHeaderHeight);
        long headerSizeMs = navPhase.ElapsedMilliseconds;
        string heroTitle = key == "overview" ? "P\u0159ehled" : sectionDefinition.Title;
        string heroSubtitle = key == "overview"
            ? "Hardware, \u017eiv\u00e9 vyu\u017eit\u00ed a stav syst\u00e9mu"
            : sectionDefinition.Subtitle;
        if (_heroDeferredCompleted)
        {
            if (!string.Equals(PageTitle.Text, heroTitle, StringComparison.Ordinal))
                PageTitle.Text = heroTitle;
            if (!string.Equals(PageSubtitle.Text, heroSubtitle, StringComparison.Ordinal))
                PageSubtitle.Text = heroSubtitle;
        }
        long shellMs = navPhase.ElapsedMilliseconds;
        long shellOnlyMs = shellMs - cleanupMs;
        // Keep detailed shell telemetry for genuinely slow navigation only.
        // System used to log every navigation (including 0 ms hot-path soaks), creating
        // avoidable queue/string/disk pressure exactly while measuring navigation.
        if (cleanupMs >= 15 || shellOnlyMs >= 15)
        {
            LogService.Info("performance", $"NAV_PHASE section={key} cleanupMs={cleanupMs} shellMs={shellOnlyMs}");
            LogService.Info("performance",
                $"NAV_SHELL_PHASE section={key} stateMs={stateMs - cleanupMs} clearMs={clearMs - stateMs} catalogMs={catalogMs - clearMs} selectionMs={selectionMs - catalogMs} heroMs={heroMs - selectionMs} headerSizeMs={headerSizeMs - heroMs} textMs={shellMs - headerSizeMs}");
        }
        if (CrashLoopGuardService.SafeMode && !IsSafeModeAllowedSection(key))
        {
            BuildCrashSafeModePage();
            return;
        }
        long subSelectorStartMs = navPhase.ElapsedMilliseconds;
        if (_subOptions.TryGetValue(key, out string[] value))
        {
            if (_sub == null)
            {
                _sub = value[0];
            }
            if (Array.IndexOf(value, _sub) < 0)
            {
                _sub = value[0];
            }
            bool deferComponentsServiceSelector =
                key == "components" && string.Equals(_sub, "Služby", StringComparison.Ordinal);
            if (!deferComponentsServiceSelector)
            {
                BuildSubSelector(key, value);
            }
            else
            {
                int selectorGeneration = generation;
                string[] selectorOptions = value;
                SafeTaskService.Forget(
                    CompleteDeferredComponentsSelectorAsync(selectorOptions, selectorGeneration, token),
                    "components",
                    "Deferred Components selector");
            }
        }
        else
        {
            _sub = null;
        }
        long subSelectorMs = navPhase.ElapsedMilliseconds - subSelectorStartMs;
        if (subSelectorMs >= 5)
            LogService.Info("performance", $"NAV_SUBSELECTOR_PHASE section={key} sub={_sub ?? "-"} ms={subSelectorMs}");
        if (key is "overview" or "components" or "gpu" or "network" or "storage" or "startup" or "defender")
        {
            long preludeTotalMs = navPhase.ElapsedMilliseconds;
            if (preludeTotalMs >= 5)
                LogService.Info("performance",
                    $"NAV_PRELUDE_PHASE section={key} cleanupMs={cleanupMs} stateMs={stateMs - cleanupMs} clearMs={clearMs - stateMs} catalogMs={catalogMs - clearMs} selectionMs={selectionMs - catalogMs} heroMs={heroMs - selectionMs} headerMs={headerSizeMs - heroMs} textMs={shellMs - headerSizeMs} subMs={subSelectorMs} totalMs={preludeTotalMs}");
        }
        if (key == "overview")
        {
            long beforeOverviewMs = navPhase.ElapsedMilliseconds;
            await BuildOverviewDashboardAsync(token);
            long overviewMs = navPhase.ElapsedMilliseconds - beforeOverviewMs;
            if (LogService.PerformanceDiagnosticsEnabled)
                LogService.Info("performance",
                    $"NAV_OVERVIEW_BUILD preludeMs={beforeOverviewMs} overviewMs={overviewMs} coreMs={navPhase.ElapsedMilliseconds} cache={_overviewPageCache?.Count ?? 0}");
            return;
        }
        if (key == "power")
        {
            await BuildPowerPlanAsync(token);
            return;
        }
        if (key == "debloat")
        {
            _debloatActionToken = token;
            if (TryAttachDebloatPageCache()) return;
            await BuildDebloatPageAsync(token);
            ThrowIfNavigationStale(generation, token);
            CaptureDebloatPageCache();
            return;
        }
        if (key == "apps")
        {
            var appsForeground = Stopwatch.StartNew();
            await BuildAppsPageAsync(token);
            LogService.Info("performance", $"APPS_NAV_PHASE totalMs={appsForeground.ElapsedMilliseconds}");
            return;
        }

        if (key == "fixer")
        {
            await BuildFixerPageAsync(token);
            return;
        }
        if (key == "restore")
        {
            await BuildRestorePageAsync(token);
            return;
        }
        if (key == "logs")
        {
            BuildLogsPageAsync(token);
            return;
        }
        if (key == "extensions")
        {
            await BuildExtensionsPageAsync(token);
            return;
        }
        if (key == "spicetify")
        {
            await BuildSpicetifyPageAsync(token);
            return;
        }
        if (key == "themes")
        {
            if (!TryAttachThemesPageCache())
            {
                BuildThemesPage();
                CaptureThemesPageCache();
            }
            return;
        }
        if (key == "powershell")
        {
            await BuildPowerShellPageAsync(token);
            return;
        }
        if (key == "cursor")
        {
            BuildCursorPage();
            return;
        }
        if (key == "settings")
        {
            BuildSettingsPage();
            return;
        }
        if (key == "startup")
        {
            var startupNavPerf = Stopwatch.StartNew();
            if (TryAttachStartupPageCache())
            {
                if (startupNavPerf.ElapsedMilliseconds >= 8)
                    LogService.Info("performance", $"STARTUP_NAV_PHASE cacheHit=true totalMs={startupNavPerf.ElapsedMilliseconds}");
                return;
            }
            long cacheMs = startupNavPerf.ElapsedMilliseconds;
            await BuildStartupManagerAsync(token);
            ThrowIfNavigationStale(generation, token);
            long managerMs = startupNavPerf.ElapsedMilliseconds - cacheMs;
            int startupGeneration = generation;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || startupGeneration != _navigationGeneration || _section != "startup") return;
                BuildWindowsModernizerStartupPanel(token);
                CaptureStartupPageCache();
            });
            long modernizerMs = startupNavPerf.ElapsedMilliseconds - cacheMs - managerMs;
            CaptureStartupPageCache();
            long captureMs = startupNavPerf.ElapsedMilliseconds - cacheMs - managerMs - modernizerMs;
            if (startupNavPerf.ElapsedMilliseconds >= 10)
                LogService.Info("performance", $"STARTUP_NAV_PHASE cacheHit=false cacheMs={cacheMs} managerMs={managerMs} modernizerMs={modernizerMs} captureMs={captureMs} totalMs={startupNavPerf.ElapsedMilliseconds}");
            return;
        }
        if (key == "usb")
        {
            await BuildUsbToolsAsync(token);
            return;
        }
        if (key == "lianli")
        {
            await BuildLianLiPageAsync(token);
            return;
        }
        if (key == "activity")
        {
            BuildActivityPage();
            return;
        }
        if (key == "components")
        {
            string componentSub = _sub ?? "Služby";
            if (string.Equals(componentSub, "Služby", StringComparison.OrdinalIgnoreCase))
            {
                _tweaks = EmptyComponentTweaks;
                if (_componentsPageCacheStore != null && TryAttachComponentsPageCache("Služby", token))
                    return;

                await BuildServiceManagerPageAsync(token);
                ThrowIfNavigationStale(generation, token);
                SafeTaskService.Forget(
                    CompleteDeferredServiceChromeAsync(generation, token),
                    "components",
                    "Deferred service chrome/cache");
                return;
            }

            var componentPerf = Stopwatch.StartNew();
            string componentCacheKey = "components|" + componentSub;
            if (!_tweakCache.TryGetValue(componentCacheKey, out _tweaks))
                _tweakCache[componentCacheKey] = _tweaks = CatalogService.GetTweaks("components", componentSub);
            long catalogPhaseMs = componentPerf.ElapsedMilliseconds;

            if (TryAttachComponentsPageCache(componentSub, token))
            {
                if (componentPerf.ElapsedMilliseconds >= 8)
                    LogService.Info("performance", $"COMPONENT_NAV_PHASE sub={componentSub} cacheHit=true catalogMs={catalogPhaseMs} totalMs={componentPerf.ElapsedMilliseconds}");
                return;
            }
            long cachePhaseMs = componentPerf.ElapsedMilliseconds - catalogPhaseMs;
            BuildComponentsContextActions();
            long contextPhaseMs = componentPerf.ElapsedMilliseconds - catalogPhaseMs - cachePhaseMs;
            long buildStart = componentPerf.ElapsedMilliseconds;
            if (componentSub == "Ovladače")
                await BuildDriverManagerPageAsync(token);
            else
                await BuildTaskManagerPageAsync(token);
            ThrowIfNavigationStale(generation, token);
            long buildPhaseMs = componentPerf.ElapsedMilliseconds - buildStart;

            long captureStart = componentPerf.ElapsedMilliseconds;
            CaptureComponentsPageCache(componentSub);
            long capturePhaseMs = componentPerf.ElapsedMilliseconds - captureStart;
            if (componentPerf.ElapsedMilliseconds >= 10)
                LogService.Info("performance",
                    $"COMPONENT_NAV_PHASE sub={componentSub} cacheHit=false catalogMs={catalogPhaseMs} cacheMs={cachePhaseMs} contextMs={contextPhaseMs} buildMs={buildPhaseMs} captureMs={capturePhaseMs} totalMs={componentPerf.ElapsedMilliseconds}");
            return;
        }
        if (key == "ai") { await BuildAiPageAsync(token); return; }
        if (key == "ai-runtime")
        {
            if (TryAttachAiRuntimePageCache()) return;
            await BuildAiSystemRuntimePageAsync(token);
            CaptureAiRuntimePageCache();
            return;
        }
        if (key == "defender") { await BuildDefenderPageAsync(token); return; }
        if (key == "gpu")
        {
            long gpuStartMs = navPhase.ElapsedMilliseconds;
            const string gpuCacheKey = "gpu|";
            SynchronizeGpuDetectionGeneration();
            bool gpuStale = !_tweakCacheTime.TryGetValue(gpuCacheKey, out var gpuDetectedAt) ||
                DateTime.UtcNow - gpuDetectedAt > TimeSpan.FromSeconds(90);

            if (_tweakCache.TryGetValue(gpuCacheKey, out var cachedGpuTweaks))
                _tweaks = cachedGpuTweaks;
            else
                _tweaks = EmptyComponentTweaks;

            if (TryAttachGpuPageCache(token))
            {
                // Expired tweak detection is independent of the cached GPU page.
                // Keep its WinUI tree, and re-read states in the background.
                if (gpuStale)
                    SafeTaskService.Forget(
                        RunGpuDeferredStateAfterSettleAsync(gpuCacheKey, _tweaks.ToArray(), true, token, generation),
                        "gpu", "Refresh stale GPU tweaks without rebuilding page");
                return;
            }
            if (gpuStale) InvalidateGpuPageCache();
            long gpuPrepareMs = navPhase.ElapsedMilliseconds;

            BuildGpuDriverCenter(token);
            long gpuBuildMs = navPhase.ElapsedMilliseconds;
            ThrowIfNavigationStale(generation, token);
            ScheduleGpuActions(token);
            SafeTaskService.Forget(
                CompleteGpuTweaksAfterFirstFrameAsync(gpuCacheKey, gpuStale, token, generation),
                "gpu",
                "Deferred GPU catalog + tweak UI");
            long gpuEndMs = navPhase.ElapsedMilliseconds;
            if (gpuEndMs - gpuStartMs >= 15 || gpuEndMs >= 35)
                LogService.Info("performance", $"GPU_NAV_DIAG preludeMs={gpuStartMs} prepareMs={gpuPrepareMs - gpuStartMs} buildMs={gpuBuildMs - gpuPrepareMs} deferredMs={gpuEndMs - gpuBuildMs} totalMs={gpuEndMs} stale={gpuStale}");
            return;
        }
        Stopwatch? systemForeground = key == "system" ? Stopwatch.StartNew() : null;
        Stopwatch? storageCatalogPhase = key == "storage" ? Stopwatch.StartNew() : null;
        string tweakCacheKey = key + "|" + (_sub ?? "");
        // First System navigation needs only the empty virtual host. Construct
        // catalog definitions in the existing LOW-priority root callback after
        // foreground has returned, not synchronously on WinUI and not in a
        // racing worker whose scheduling can delay the 35ms first-paint gate.
        bool deferSystemCatalog = key == "system" && _systemPageCache == null &&
            !_tweakCache.ContainsKey("system|");
        if (deferSystemCatalog)
        {
            _tweaks = EmptyComponentTweaks;
        }
        else
        {
            if (!_tweakCache.TryGetValue(tweakCacheKey, out var cachedTweaks))
            {
                cachedTweaks = CatalogService.GetTweaks(key, _sub);
                _tweakCache[tweakCacheKey] = cachedTweaks;
            }
            _tweaks = key == "system" ? GlassSystemTweaks(cachedTweaks) : cachedTweaks;
        }
        long systemCatalogMs = systemForeground?.ElapsedMilliseconds ?? 0;
        long storageCatalogMs = storageCatalogPhase?.ElapsedMilliseconds ?? 0;
        if (key == "system" && TryAttachSystemPageCache(token))
        {
            bool systemStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var systemDetectedAt) ||
                DateTime.UtcNow - systemDetectedAt > TimeSpan.FromSeconds(90);
            if (systemStale)
                ScheduleRenderFirstBackground("system", tweakCacheKey, _tweaks.ToArray(), true, token);
            return;
        }
        if (key == "privacy" && TryAttachPrivacyPageCache())
        {
            bool privacyStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var privacyDetectedAt) ||
                DateTime.UtcNow - privacyDetectedAt > TimeSpan.FromSeconds(90);
            if (privacyStale)
                ScheduleRenderFirstBackground("privacy", tweakCacheKey, _tweaks.ToArray(), true, token);
            return;
        }
        if (key == "privacy") BuildPrivacyActions();
        if (key == "ui" && TryAttachUiPageCache())
        {
            bool uiStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var uiDetectedAt) ||
                DateTime.UtcNow - uiDetectedAt > TimeSpan.FromSeconds(90);
            if (uiStale)
                ScheduleRenderFirstBackground("ui", tweakCacheKey, _tweaks.ToArray(), true, token);
            return;
        }
        if (key == "memory")
        {
            BuildMemoryActions();
        }
        if (key == "storage")
        {
            long storagePreMs = navPhase.ElapsedMilliseconds;
            var storageForeground = Stopwatch.StartNew();
            bool storageStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var storageDetectedAt) || DateTime.UtcNow - storageDetectedAt > TimeSpan.FromSeconds(90);
            if (TryAttachStoragePageCache(token))
            {
                // Refresh expired tweak states without discarding a healthy
                // storage UI. The cache attach already refreshes stale disk
                // health/advanced panels and resumes deferred content.
                if (storageStale)
                    SafeTaskService.Forget(
                        RunStorageDeferredStateAfterSettleAsync(tweakCacheKey, _tweaks.ToArray(), true, token, generation),
                        "storage", "Refresh stale storage tweaks without rebuilding page");
                return;
            }
            if (storageStale) InvalidateStoragePageCache();

            AttachStorageStaticShell(token, out long storageHeroMs, out long storageMetricsMs);

            BuildStorageActions(token);
            ScheduleStorageOptimizationAdvisor(token);
            long storageActionsMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs;
            ScheduleStorageTools(token);
            long storageScheduleMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs - storageActionsMs;
            // Repeated cached navigation commonly takes 0ms. Retain actionable
            // slow-path diagnostics without flooding performance-smoke output.
            if (storageForeground.ElapsedMilliseconds >= 5)
                LogService.Info("performance",
                    $"STORAGE_FOREGROUND_PHASE preStorageMs={storagePreMs} catalogMs={storageCatalogMs} heroMs={storageHeroMs} metricsMs={storageMetricsMs} actionsMs={storageActionsMs} scheduleMs={storageScheduleMs} totalMs={storageForeground.ElapsedMilliseconds}");
        }
        if (key == "network")
        {
            if (TryAttachNetworkPageCache(token, out bool networkPanelsRefreshed))
            {
                bool networkStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var networkDetectedAt) || DateTime.UtcNow - networkDetectedAt > TimeSpan.FromSeconds(90);
                if (networkStale)
                {
                    int networkGeneration = generation;
                    var networkTweaks = _tweaks.ToArray();
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (token.IsCancellationRequested || networkGeneration != _navigationGeneration || _section != "network") return;
                        SafeTaskService.Forget(
                            DetectSectionTweaksAndRefreshAsync("network", tweakCacheKey, networkTweaks, token, networkGeneration),
                            "tweaks",
                            "Cached network detect");
                    });
                }
                return;
            }
            var networkBuild = Stopwatch.StartNew();
            BuildNetworkPerformancePanel(token);
            long networkPerfMs = networkBuild.ElapsedMilliseconds;
            await BuildNetworkTelemetryAsync(token);
            ThrowIfNavigationStale(generation, token);
            long networkShellMs = networkBuild.ElapsedMilliseconds - networkPerfMs;
            BuildNetworkDashboardBody(token);
            long networkDashboardMs = networkBuild.ElapsedMilliseconds - networkPerfMs - networkShellMs;
            await BuildDriverActionsAsync(token);
            ThrowIfNavigationStale(generation, token);
            long networkDriverMs = networkBuild.ElapsedMilliseconds - networkShellMs - networkPerfMs - networkDashboardMs;
            ScheduleNetworkTools(token);
            long networkToolsMs = networkBuild.ElapsedMilliseconds - networkShellMs - networkPerfMs - networkDashboardMs - networkDriverMs;
            CaptureNetworkPageCache();
            if (networkBuild.ElapsedMilliseconds >= 10)
                LogService.Info("performance", $"NETWORK_BUILD shellMs={networkShellMs} perfMs={networkPerfMs} dashboardMs={networkDashboardMs} driverMs={networkDriverMs} toolsMs={networkToolsMs} totalMs={networkBuild.ElapsedMilliseconds}");
        }
        if (key == "storage")
        {
            var storageBuild = Stopwatch.StartNew();
            int storageGeneration = generation;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || storageGeneration != _navigationGeneration || _section != "storage") return;
                SafeTaskService.Forget(BuildStorageHealthAsync(token), "storage", "Storage health");
                SafeTaskService.Forget(BuildStorageAdvancedAsync(token), "storage", "Storage advanced");
            });
            long kickoffMs = storageBuild.ElapsedMilliseconds;
            BuildStorageSnapshot(token);
            BuildWindowsModernizerStoragePanel(token);
            long snapshotMs = storageBuild.ElapsedMilliseconds - kickoffMs;
            if (storageBuild.ElapsedMilliseconds >= 5)
                LogService.Info("performance", $"STORAGE_BUILD kickoffMs={kickoffMs} snapshotMs={snapshotMs} toolsMs=0 totalMs={storageBuild.ElapsedMilliseconds}");
        }
        if (key == "update")
        {
            if (TryAttachUpdatePageCache(token))
                return;
            await BuildWindowsUpdateCenterAsync(token);
            ThrowIfNavigationStale(generation, token);
        }
        if (key == "ui")
        {
            var uiBuild = Stopwatch.StartNew();
            BuildContextMenuTweaker();
            if (uiBuild.ElapsedMilliseconds >= 10)
                LogService.Info("performance",
                    $"UI_BUILD_PHASE contextMenuMs={uiBuild.ElapsedMilliseconds} legacyExtras=false totalMs={uiBuild.ElapsedMilliseconds}");
        }
        var renderTailPerf = Stopwatch.StartNew();
        bool renderFirst = key is "system" or "privacy" or "network" or "memory" or "ui" or "storage" or "update";
        // All render-first sections defer tweak detection and its immediate/lazy
        // partition to the low-priority dispatcher. Avoid two useless LINQ
        // enumerations + arrays on the cold foreground navigation hot path.
        TweakDefinition[] lazyTweaks = renderFirst ? Array.Empty<TweakDefinition>() : _tweaks.Where(x => x.LazyDetect).ToArray();
        TweakDefinition[] immediateTweaks = renderFirst ? Array.Empty<TweakDefinition>() : _tweaks.Where(x => !x.LazyDetect).ToArray();
        long splitTweaksMs = renderTailPerf.ElapsedMilliseconds;
        bool staleDetection = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var detectedAt) || DateTime.UtcNow - detectedAt > TimeSpan.FromSeconds(90);
        if (renderFirst)
        {
            var phase = Stopwatch.StartNew();
            if (key == "network")
            {
                _networkTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_networkTweakHost);
                ScheduleNetworkTweakCards(token);
            }
            else if (key == "storage")
            {
                _storageTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_storageTweakHost);
                ScheduleStorageTweakCards(token);
            }
            else if (key == "system")
            {
                // Only an empty host exists before the first deferred System
                // render. Attach it in that same Low-priority callback so a
                // cold native visual-tree insertion cannot block foreground
                // navigation during a rapid multi-section smoke/user tour.
                _systemVirtualHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                ScheduleSystemVirtualCards(token);
            }
            else
            {
                BuildTweakCards();
            }
            long cardsMs = phase.ElapsedMilliseconds;
            if (key == "system" && systemForeground != null && systemForeground.ElapsedMilliseconds >= 12)
                LogService.Info("performance", $"SYSTEM_FOREGROUND_PHASE catalogMs={systemCatalogMs} preRenderMs={systemForeground.ElapsedMilliseconds - systemCatalogMs - cardsMs} cardsMs={cardsMs} totalMs={systemForeground.ElapsedMilliseconds}");
            if (key == "network") CaptureNetworkPageCache();
            if (key == "update") CaptureUpdatePageCache();
            if (key == "system" && _systemVirtualRowsCache != null) CaptureSystemPageCache();
            if (key == "privacy") CapturePrivacyPageCache();
            if (key == "ui") CaptureUiPageCache();
            // Zero-ms navigation must not queue two performance.log writes.
            // Slow paths still retain both diagnostics for investigation.
            long tailMs = renderTailPerf.ElapsedMilliseconds;
            if (cardsMs >= 5 || tailMs >= 12)
            {
                if (key is "network" or "storage" or "gpu")
                    LogService.Info("performance", $"RENDER_TAIL_PHASE section={key} splitMs={splitTweaksMs} cardsMs={cardsMs} totalMs={tailMs}");
                LogService.Info("performance", $"TWEAK_UI_PHASE section={key} count={_tweaks.Count} cardsMs={cardsMs}");
            }
            if (key == "storage")
            {
                int storageDeferredGeneration = generation;
                TweakDefinition[] storageDeferredTweaks = _tweaks.ToArray();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (token.IsCancellationRequested || storageDeferredGeneration != _navigationGeneration || _section != "storage") return;
                    SafeTaskService.Forget(RunStorageDeferredStateAfterSettleAsync(tweakCacheKey, storageDeferredTweaks, staleDetection, token, storageDeferredGeneration), "storage", "Deferred storage state");
                });
            }
            else if (!deferSystemCatalog)
            {
                // A deferred first-System catalog schedules its own state
                // detection after the root has its real 57 model definitions.
                ScheduleRenderFirstBackground(key, tweakCacheKey, _tweaks.ToArray(), staleDetection, token);
            }
            return;
        }
        if (staleDetection)
        {
            await _engine.DetectAsync(immediateTweaks, token);
            ThrowIfNavigationStale(generation, token);
            _tweakCacheTime[tweakCacheKey] = DateTime.UtcNow;
        }
        await BuildHardwareRecommendedPanelAsync(key, _tweaks, token);
        ThrowIfNavigationStale(generation, token);
        BuildTweakCards();
        if (lazyTweaks.Length > 0) SafeTaskService.Forget(DetectLazyTweaksAsync(key, lazyTweaks, token), "tweaks", "Lazy detect " + key);
    }
}
