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

        _appsPageCache = ContentStack.Children.Cast<UIElement>().ToList();
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

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
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
                actions.Children.Add(PrimaryQuickButton("Software Updater", () => ShowSoftwareManagerAsync(CancellationToken.None)));
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
        });
    }

    private void ScheduleAppsBackgroundRefresh(TextBlock? winget, StackPanel? detected, CancellationToken token)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "apps") return;
            if (winget != null) SafeTaskService.Forget(UpdateAppsWingetStatusAsync(winget, token), "apps", "Winget status");
            if (detected != null) SafeTaskService.Forget(FillDetectedAppsPanelAsync(detected, token), "apps", "Detected apps inventory");
        });
    }

    private void ScheduleAppsDeferredBody(CancellationToken token)
    {
        if (_appsDeferredQueued || _appsDeferredBody == null || _appsDeferredBody.Children.Count > 0) return;
        _appsDeferredQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _appsDeferredQueued = false;
            if (token.IsCancellationRequested || _section != "apps" || _appsDeferredBody == null || _appsDeferredBody.Children.Count > 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var launcherHost = new StackPanel { Spacing = 8 };
            _appsLauncherCacheHost = launcherHost;
            _appsDeferredBody.Children.Add(BuildSectionSurface("apps", "Cache launcherů", "Steam, Battle.net, Epic, EA, Ubisoft, Riot, GOG a Discord", launcherHost));
            _appsDeferredBody.Children.Add(ActionCard("Windows temp", "Vyčistí jen dočasné Windows soubory. Shader cache se nemaže, aby se hry zbytečně nevracely ke kompilaci shaderů.", "Vyčistit temp", async () =>
            {
                await WindowsActions.ClearTempAsync();
                ShowActionFeedback("Windows temp", ActionFeedbackKind.Verified, "Dočasné Windows soubory byly vyčištěny. Shader cache zůstala zachovaná.");
                await ShowMessageAsync("Programy / Launchery", "Dočasné Windows soubory byly vyčištěny. Shader cache zůstala zachovaná.");
            }));
            ScheduleLauncherCacheCards(launcherHost, token);
            LogService.Info("performance", $"APPS_DEFERRED_BODY ms={sw.ElapsedMilliseconds} cards={_appsDeferredBody.Children.Count} launcherShellOnly=true");
        });
    }

    private void ScheduleLauncherCacheCards(StackPanel host, CancellationToken token)
    {
        if (_appsLauncherCacheQueued || host.Children.Count > 0) return;
        _appsLauncherCacheQueued = true;
        var catalog = LauncherCacheService.Catalog.ToArray();

        void QueueBatch(int start)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "apps" || !ReferenceEquals(_appsLauncherCacheHost, host))
                {
                    _appsLauncherCacheQueued = false;
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int end = Math.Min(start + 1, catalog.Length);
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
              ((_appsDeferredBody?.Children.Count ?? 0) < 2)); i++)
            await Task.Delay(25);

        if ((_appsHeroHost?.Children.Count ?? 0) == 0 ||
            (_appsWingetSurfaceHost?.Children.Count ?? 0) == 0 ||
            (_appsDetectedSurfaceHost?.Children.Count ?? 0) == 0)
            throw new InvalidOperationException("Apps deferred shell incomplete.");

        int actual = _appsDeferredBody?.Children.Count ?? 0;
        if (actual < 2)
            throw new InvalidOperationException($"Apps deferred body incomplete: expected>=2 actual={actual}.");

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

    private async Task ShowLauncherCacheDiagnosticsAsync()
    {
        var body = new StackPanel { Spacing = 7 };
        long total = 0;
        foreach (var pair in LauncherCacheService.Catalog)
        {
            long size = 0;
            string value;
            try
            {
                size = await LauncherCacheService.GetSizeAsync(pair.Value);
                value = $"{size / 1048576d:0.0} MB";
            }
            catch (Exception ex)
            {
                value = "Nelze zjistit";
                LogService.Error("apps", $"Launcher cache size failed: {pair.Key}", ex);
            }
            total += size;
            body.Children.Add(DiagnosticLine(pair.Key, value));
        }
        body.Children.Insert(0, DiagnosticLine("Celkem cache", $"{total / 1048576d:0.0} MB"));
        await ShowStyledContentAsync("Launcher cache diagnostics", "Read-only analýza bez mazání dat.", new ScrollViewer { Content = body, MaxHeight = 520 }, "Zavřít");
    }

    private async Task ShowPrivacyDiagnosticsAsync()
    {
        var tweaks = CatalogService.GetTweaks("privacy");
        await _engine.DetectAsync(tweaks);
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(DiagnosticLine("Položky", tweaks.Count.ToString()));
        body.Children.Add(DiagnosticLine("Aktivní", tweaks.Count(x => x.IsLoaded && x.Current).ToString()));
        body.Children.Add(DiagnosticLine("Recommended", tweaks.Count(x => x.Recommended).ToString()));
        body.Children.Add(DiagnosticLine("Advanced", tweaks.Count(x => x.Risk == Models.TweakRisk.Advanced).ToString()));
        body.Children.Add(DiagnosticLine("Nelze zjistit", tweaks.Count(x => !x.IsLoaded).ToString()));
        await ShowStyledContentAsync("Privacy diagnostics", "Read-only stav telemetry, oprávnění a privacy policy.", body, "Zavřít");
    }

    private async Task ShowAiDiagnosticsAsync()
    {
        var tweaks = CatalogService.GetTweaks("ai");
        await _engine.DetectAsync(tweaks);
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(DiagnosticLine("AI tweak položky", tweaks.Count.ToString()));
        body.Children.Add(DiagnosticLine("Aktivní zásahy", tweaks.Count(x => x.IsLoaded && x.Current).ToString()));
        body.Children.Add(DiagnosticLine("Nezjištěno", tweaks.Count(x => !x.IsLoaded).ToString()));
        body.Children.Add(DiagnosticLine("Volitelné moduly", AiChoices.Length.ToString()));
        await ShowStyledContentAsync("AI / Recall diagnostics", "Read-only kontrola aktuálního stavu AI policy a modulů.", body, "Zavřít");
    }
    private async Task ShowDefenderDiagnosticsAsync()
    {
        var tweaks = CatalogService.GetDefenderTweaks();
        await _engine.DetectAsync(tweaks);
        bool tamper = false;
        try { tamper = WindowsActions.IsTamperProtectionOn(); }
        catch (Exception ex) { LogService.Error("defender", "Tamper Protection diagnostics failed", ex); }
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(DiagnosticLine("Tamper Protection", tamper ? "Zapnuto" : "Vypnuto / nezjištěno"));
        body.Children.Add(DiagnosticLine("Moduly", tweaks.Count.ToString()));
        body.Children.Add(DiagnosticLine("Aktivní zásahy", tweaks.Count(x => x.IsLoaded && x.Current).ToString()));
        body.Children.Add(DiagnosticLine("Nezjištěno", tweaks.Count(x => !x.IsLoaded).ToString()));
        await ShowStyledContentAsync("Defender diagnostics", "Read-only stav Defender modulů a Tamper Protection.", body, "Zavřít");
    }

}

