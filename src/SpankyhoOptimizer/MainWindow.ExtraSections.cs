using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private System.Collections.Generic.List<UIElement>? _appsPageCache;
    private TextBlock? _appsWingetStatus;
    private StackPanel? _appsDetectedHost;
    private StackPanel? _appsHeroHost;
    private StackPanel? _appsWingetSurfaceHost;
    private StackPanel? _appsDetectedSurfaceHost;
    private StackPanel? _appsDeferredBody;
    private StackPanel? _appsLauncherCacheHost;
    private bool _appsShellQueued;
    private bool _appsDeferredQueued;
    private bool _appsLauncherCacheQueued;
    private int _appsLauncherQueuedGeneration = -1;
    private DateTime _appsUiDataAt;

    private void BuildPrivacyActions()
    {
        // No user-facing diagnostics panel in the production UI.
    }

    private Task BuildAppsPageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_appsPageCache != null)
        {
            foreach (var element in _appsPageCache) AttachCachedElement(element);
            if ((_appsHeroHost?.Children.Count ?? 0) == 0 ||
                (_appsWingetSurfaceHost?.Children.Count ?? 0) == 0 ||
                (_appsDetectedSurfaceHost?.Children.Count ?? 0) == 0)
                ScheduleAppsShell(token);
            if (_appsDeferredBody is { Children.Count: 0 })
                ScheduleAppsDeferredBody(token);
            if (DateTime.UtcNow - _appsUiDataAt > TimeSpan.FromMinutes(5))
            {
                ScheduleAppsBackgroundRefresh(_appsWingetStatus, _appsDetectedHost, token);
                _appsUiDataAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        _appsHeroHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        _appsWingetSurfaceHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        _appsDetectedSurfaceHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

        var wingetStatus = new TextBlock
        {
            Text = "Načítám stav Winget…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        _appsWingetStatus = wingetStatus;

        var detectedHost = new StackPanel { Spacing = 7 };
        _appsDetectedHost = detectedHost;
        detectedHost.Children.Add(new TextBlock
        {
            Text = "Načítám nainstalované aplikace…",
            Foreground = TextSecondaryBrush()
        });

        ContentStack.Children.Add(_appsHeroHost);
        ContentStack.Children.Add(_appsWingetSurfaceHost);
        ContentStack.Children.Add(_appsDetectedSurfaceHost);

        _appsDeferredBody = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(_appsDeferredBody);

        _appsPageCache = SnapshotChildren(ContentStack);
        _appsUiDataAt = DateTime.UtcNow;
        ScheduleAppsShell(token);
        ScheduleAppsDeferredBody(token);
        ScheduleAppsBackgroundRefresh(wingetStatus, detectedHost, token);
        return Task.CompletedTask;
    }

    private void ScheduleAppsShell(CancellationToken token)
    {
        if (_appsShellQueued || _appsHeroHost == null || _appsWingetSurfaceHost == null || _appsDetectedSurfaceHost == null)
            return;

        if (_appsHeroHost.Children.Count > 0 &&
            _appsWingetSurfaceHost.Children.Count > 0 &&
            _appsDetectedSurfaceHost.Children.Count > 0)
            return;

        _appsShellQueued = true;
        var heroHost = _appsHeroHost;
        var wingetSurfaceHost = _appsWingetSurfaceHost;
        var detectedSurfaceHost = _appsDetectedSurfaceHost;
        var wingetStatus = _appsWingetStatus;
        var detectedHost = _appsDetectedHost;
        int appsGeneration = _navigationGeneration;

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _appsShellQueued = false;
            if (token.IsCancellationRequested || appsGeneration != _navigationGeneration ||
                _section != "apps" || !ReferenceEquals(_appsHeroHost, heroHost) ||
                !ReferenceEquals(_appsWingetSurfaceHost, wingetSurfaceHost) ||
                !ReferenceEquals(_appsDetectedSurfaceHost, detectedSurfaceHost))
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (heroHost.Children.Count == 0)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                actions.Children.Add(PrimaryQuickButton("Software Updater", () => ShowSoftwareManagerAsync(_windowLifetimeCts.Token)));
                actions.Children.Add(QuickButton("Autostart launcherů", ShowLauncherStartupDialogAsync));
                heroHost.Children.Add(BuildModernSectionHero(
                    "apps",
                    "Programy / Launchery",
                    "Software Updater, detekované aplikace a launcher cache na jednom místě.",
                    "APPS",
                    $"{LauncherCacheService.Catalog.Count} podporovaných cache profilů",
                    actions));
            }

            if (wingetSurfaceHost.Children.Count == 0 && wingetStatus != null)
                wingetSurfaceHost.Children.Add(BuildSectionSurface(
                    "apps",
                    "Software Updater",
                    "Winget stav • dostupné aktualizace • per-app / Update all",
                    wingetStatus));

            if (detectedSurfaceHost.Children.Count == 0 && detectedHost != null)
                detectedSurfaceHost.Children.Add(BuildSectionSurface(
                    "apps",
                    "Detekované aplikace",
                    "Zobrazuji jen software skutečně nalezený v systému",
                    detectedHost));

            LogService.Info("performance",
                $"APPS_SHELL_DEFERRED ms={sw.ElapsedMilliseconds} hero={heroHost.Children.Count} winget={wingetSurfaceHost.Children.Count} detected={detectedSurfaceHost.Children.Count}");
        })) _appsShellQueued = false;
    }

    private void ScheduleAppsBackgroundRefresh(TextBlock? winget, StackPanel? detected, CancellationToken token)
    {
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "apps") return;
            if (winget != null) SafeTaskService.Forget(UpdateAppsWingetStatusAsync(winget, token), "apps", "Winget status");
            if (detected != null) SafeTaskService.Forget(FillDetectedAppsPanelAsync(detected, token), "apps", "Detected apps inventory");
        });
    }

    private void ScheduleAppsDeferredBody(CancellationToken token)
    {
        if (_appsDeferredQueued || _appsDeferredBody == null || _appsDeferredBody.Children.Count > 0) return;
        int generation = _navigationGeneration;
        var body = _appsDeferredBody;
        _appsDeferredQueued = true;
        bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _appsDeferredQueued = false;
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "apps" || !ReferenceEquals(_appsDeferredBody, body) || body == null || body.Parent == null || body.Children.Count > 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var launcherHost = new StackPanel { Spacing = 8 };
            _appsLauncherCacheHost = launcherHost;
            _appsDeferredBody.Children.Add(BuildSectionSurface("apps", "Cache launcherů", "Steam, Battle.net, Epic, EA, Ubisoft, Riot, GOG a Discord", launcherHost));
            ScheduleLauncherCacheCards(launcherHost, token);
            LogService.Info("performance", $"APPS_DEFERRED_BODY ms={sw.ElapsedMilliseconds} cards={_appsDeferredBody.Children.Count} launcherShellOnly=true");
        });
        // A rejected low-priority enqueue must release the pending bit, or the
        // Apps deferred body can never be scheduled again in this session.
        if (!queued) _appsDeferredQueued = false;
    }

    private void ScheduleLauncherCacheCards(StackPanel host, CancellationToken token)
    {
        if (_appsLauncherCacheQueued || host.Children.Count > 0) return;
        int generation = _navigationGeneration;
        _appsLauncherCacheQueued = true;
        _appsLauncherQueuedGeneration = generation;
        var catalog = LauncherCacheService.Catalog.ToArray();

        void QueueBatch(int start)
        {
            // Eight single-card callbacks caused eight extra dispatcher hops
            // and intermediate XAML layout passes. Four light cards per
            // callback remain well below a frame on the measured warm path,
            // while keeping the entire launcher panel off the first frame.
            bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "apps" || !ReferenceEquals(_appsLauncherCacheHost, host) || host.Parent == null)
                {
                    // An obsolete dispatcher callback must not clear the
                    // pending bit of a newer Apps page navigation.
                    if (_appsLauncherQueuedGeneration == generation)
                        _appsLauncherCacheQueued = false;
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int end = Math.Min(start + 4, catalog.Length);
                for (int i = start; i < end; i++)
                {
                    var pair = catalog[i];
                    var name = pair.Key;
                    var paths = pair.Value;
                    host.Children.Add(ActionCard(name, "Vyčistí pouze webovou a grafickou cache. Herní soubory, účty a shader cache Steamu zůstanou.", "Vyčistit", async () =>
                    {
                        var size = await LauncherCacheService.GetSizeAsync(paths);
                        if (!await ShowStyledConfirmAsync($"Vyčistit cache — {name}?", $"Nalezeno přibližně {size / 1048576.0:0.0} MB. Herní soubory a účty zůstanou.", "Vyčistit", "Zrušit", "Cache launcherů")) return;
                        var result = await LauncherCacheService.ClearVerifiedAsync(paths);
                        string detail = $"Uvolněno {result.FreedBytes / 1048576.0:0.0} MB • zbývá {result.AfterBytes / 1048576.0:0.0} MB";
                        if (result.FailedFiles > 0) detail += $" • zamčené/nepřístupné soubory: {result.FailedFiles}";
                        ShowActionFeedback(
                            name,
                            result.FailedFiles > 0 ? ActionFeedbackKind.Info : ActionFeedbackKind.Verified,
                            detail);
                        await ShowMessageAsync(name, detail);
                    }));
                }

                LogService.Info("performance", $"APPS_LAUNCHER_BATCH start={start} added={end - start} ms={sw.ElapsedMilliseconds} total={host.Children.Count}");
                if (end < catalog.Length)
                    QueueBatch(end);
                else
                    _appsLauncherCacheQueued = false;
            });
            if (!queued && _appsLauncherQueuedGeneration == generation &&
                ReferenceEquals(_appsLauncherCacheHost, host))
                _appsLauncherCacheQueued = false;
        }

        if (catalog.Length == 0)
        {
            _appsLauncherCacheQueued = false;
            return;
        }

        QueueBatch(0);
    }

    internal async Task RunAppsDeferredBodySmokeAsync()
    {
        InvalidateAppsPageCache();
        await ShowSectionAsync("apps");
        for (int i = 0; i < 40 &&
             (((_appsHeroHost?.Children.Count ?? 0) == 0) ||
              ((_appsWingetSurfaceHost?.Children.Count ?? 0) == 0) ||
              ((_appsDetectedSurfaceHost?.Children.Count ?? 0) == 0) ||
              ((_appsDeferredBody?.Children.Count ?? 0) < 1)); i++)
            await Task.Delay(25);

        if ((_appsHeroHost?.Children.Count ?? 0) == 0 ||
            (_appsWingetSurfaceHost?.Children.Count ?? 0) == 0 ||
            (_appsDetectedSurfaceHost?.Children.Count ?? 0) == 0)
            throw new InvalidOperationException("Apps deferred shell incomplete.");

        int actual = _appsDeferredBody?.Children.Count ?? 0;
        if (actual < 1)
            throw new InvalidOperationException($"Apps deferred body incomplete: expected>=1 actual={actual}.");

        int expectedLaunchers = LauncherCacheService.Catalog.Count;
        for (int i = 0; i < 80 && (_appsLauncherCacheHost?.Children.Count ?? 0) < expectedLaunchers; i++)
            await Task.Delay(25);
        int launcherActual = _appsLauncherCacheHost?.Children.Count ?? 0;
        if (launcherActual != expectedLaunchers)
            throw new InvalidOperationException($"Apps launcher cache render incomplete: expected={expectedLaunchers} actual={launcherActual}.");

        LogService.Info("performance", $"APPS_DEFERRED_BODY_SMOKE_OK children={actual} launchers={launcherActual}");
    }

    private void InvalidateAppsPageCache()
    {
        _appsPageCache = null;
        _appsWingetStatus = null;
        _appsDetectedHost = null;
        _appsHeroHost = null;
        _appsWingetSurfaceHost = null;
        _appsDetectedSurfaceHost = null;
        _appsDeferredBody = null;
        _appsLauncherCacheHost = null;
        _appsShellQueued = false;
        _appsDeferredQueued = false;
        _appsLauncherCacheQueued = false;
        _appsLauncherQueuedGeneration = -1;
        _appsUiDataAt = default;
    }

    private async Task UpdateAppsWingetStatusAsync(TextBlock target, CancellationToken token)
    {
        try
        {
            using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            statusCts.CancelAfter(TimeSpan.FromSeconds(15));
            var winget = await WingetUpdateService.GetStatusAsync(statusCts.Token);
            if (token.IsCancellationRequested || _section != "apps") return;
            target.Text = $"Winget {winget.Version} • {winget.Message}";
            target.Foreground = winget.Available ? SuccessBrush() : TextSecondaryBrush();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && _section == "apps")
            {
                target.Text = "Winget stav se nepodařilo načíst: " + ex.Message;
                target.Foreground = WarningBrush();
                ShowActionFeedback("Software Updater", ActionFeedbackKind.Failed, ex.Message);
            }
            LogService.Error("apps", "Winget status failed", ex);
        }
    }


}
