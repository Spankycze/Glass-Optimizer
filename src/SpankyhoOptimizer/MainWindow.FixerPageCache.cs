using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _fixerStaticCache;
    private List<UIElement>? _fixerPageCache;
    private StackPanel? _fixerAppHealthHost;
    private StackPanel? _fixerStaticActionsHost;
    private StackPanel? _fixerHeroHost;
    private bool _fixerHeroQueued;
    private bool _fixerStaticActionsQueued;
    private DateTime _fixerDynamicAt;

    private bool TryAttachFixerPageCache(CancellationToken token)
    {
        if (_fixerPageCache == null) return false;
        if (_fixerAppHealthHost == null)
        {
            LogService.Info("fixer", "Fixer cached page missing dynamic host; rebuilding page instead of leaving loading placeholder.");
            InvalidateFixerPageCache();
            return false;
        }
        ContentStack.Children.Clear();
        foreach (var child in _fixerPageCache) AttachCachedElement(child);
        // Any queued batch belongs to the previous navigation token.
        // Re-arm work for this visit so rapid section switching cannot strand an empty host.
        _fixerStaticActionsQueued = false;
        _fixerHeroQueued = false;
        ScheduleDeferredFixerHero(token);
        if (_fixerStaticActionsHost != null && _fixerStaticActionsHost.Children.Count < 7)
            ScheduleDeferredFixerStaticActions(token);
        // Cached buttons capture the previous navigation token. Always rebind
        // diagnostics/actions to the current navigation, even within 15 seconds.
        // ScanCachedAsync still reuses its service cache when it is fresh.
        if (_fixerAppHealthHost != null)
        {
            int generation = _navigationGeneration;
            var appHealthHost = _fixerAppHealthHost;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "fixer" ||
                    !ReferenceEquals(_fixerAppHealthHost, appHealthHost) ||
                    !ReferenceEquals(_fixerAppHealthHost, appHealthHost))
                    return;
                LogService.Info("fixer", $"Fixer cached diagnostics refresh accepted: generation={generation}.");
                _fixerDynamicAt = DateTime.UtcNow;
                SafeTaskService.Forget(LoadAppHealthAsync(appHealthHost, token), "fixer", "Cached App Health");
            }))
                LogService.Info("fixer", "Fixer cached refresh UI dispatch rejected.");
        }
        return true;
    }

    private void CaptureFixerPageCache()
    {
        var cache = new List<UIElement>(ContentStack.Children.Count);
        foreach (UIElement child in ContentStack.Children)
            cache.Add(child);
        _fixerPageCache = cache;
        _fixerDynamicAt = DateTime.UtcNow;
    }

    private void InvalidateFixerDynamicCache()
    {
        _fixerDynamicAt = default;
    }

    private void InvalidateFixerPageCache()
    {
        _fixerPageCache = null;
        _fixerStaticCache = null;
        _fixerAppHealthHost = null;
        _fixerStaticActionsHost = null;
        _fixerHeroHost = null;
        _fixerHeroQueued = false;
        _fixerStaticActionsQueued = false;
        _fixerDynamicAt = default;
    }

    private void InvalidateFixerStaticCache() => InvalidateFixerPageCache();

    private IReadOnlyList<UIElement> GetFixerStaticElements()
    {
        if (_fixerStaticCache != null) return _fixerStaticCache;
        var items = new List<UIElement>();
        // Build the expensive WinUI hero on the existing low-priority UI
        // queue, not during the foreground navigation. The cached placeholder
        // is stable and re-armable after rapid navigation cancellation.
        _fixerHeroHost = new StackPanel();
        items.Add(_fixerHeroHost);
        _fixerStaticActionsHost = new StackPanel { Spacing = 10 };
        items.Add(BuildDashboardExpander(
            "Repair nástroje",
            "Store • síť • GPU • Game Bar • SFC • DISM • Explorer",
            _fixerStaticActionsHost));
        _fixerStaticCache = items;
        return items;
    }

    internal async System.Threading.Tasks.Task RunFixerDeferredHeroSmokeAsync()
    {
        await ShowSectionAsync("fixer");
        var first = _fixerHeroHost;
        if (first == null || _fixerPageCache == null)
            throw new InvalidOperationException("Fixer foreground placeholder or cache missing.");
        for (int i = 0; i < 80 && first.Children.Count == 0; i++)
            await System.Threading.Tasks.Task.Delay(20);
        if (first.Children.Count != 1)
            throw new InvalidOperationException("Fixer deferred hero failed to materialize.");

        await ShowSectionAsync("overview");
        await ShowSectionAsync("fixer");
        if (!ReferenceEquals(first, _fixerHeroHost) || first.Children.Count != 1 ||
            _fixerStaticActionsHost == null)
            throw new InvalidOperationException("Fixer cached hero was lost on navigation.");
        for (int i = 0; i < 80 && _fixerStaticActionsHost.Children.Count < 7; i++)
            await System.Threading.Tasks.Task.Delay(20);
        if (_fixerStaticActionsHost.Children.Count != 7)
            throw new InvalidOperationException("Fixer deferred repair actions were stranded.");

        // A cancelled low-priority batch can leave 1..6 native action cards
        // attached to the cached host. Reproduce that exact partial state,
        // navigate away, then demand completion with no missing/duplicate cards.
        var actionsHost = _fixerStaticActionsHost;
        for (int i = 0; i < 3; i++)
            actionsHost.Children.RemoveAt(actionsHost.Children.Count - 1);
        await ShowSectionAsync("overview");
        await ShowSectionAsync("fixer");
        if (!ReferenceEquals(first, _fixerHeroHost) ||
            !ReferenceEquals(actionsHost, _fixerStaticActionsHost))
            throw new InvalidOperationException("Fixer resumed partial cache lost stable host identity.");
        for (int i = 0; i < 80 && actionsHost.Children.Count < 7; i++)
            await System.Threading.Tasks.Task.Delay(20);
        if (actionsHost.Children.Count != 7)
            throw new InvalidOperationException("Fixer partial static action batch failed to resume.");

        await ShowSectionAsync("overview");
        LogService.Info("performance", "FIXER_HERO_LIFECYCLE_SMOKE_OK hero=1 actions=7 cached=true partialResume=true");
    }

    private void ScheduleDeferredFixerHero(CancellationToken token)
    {
        var host = _fixerHeroHost;
        if (host == null || host.Children.Count > 0 || _fixerHeroQueued) return;
        int generation = _navigationGeneration;
        _fixerHeroQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation == _navigationGeneration && ReferenceEquals(_fixerHeroHost, host))
                _fixerHeroQueued = false;
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "fixer" || !ReferenceEquals(_fixerHeroHost, host) ||
                host.Children.Count != 0)
                return;

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            host.Children.Add(BuildModernSectionHero(
                "fixer",
                "Fixer",
                "Kontrola, cílené opravy a ověření výsledku bez slepých zásahů.",
                "SYSTEM HEALTH",
                "Explorer • síť • Store • Update • GPU",
                actions));
        }))
            _fixerHeroQueued = false;
    }

    private void ScheduleDeferredFixerStaticActions(CancellationToken token)
    {
        if (_fixerStaticActionsQueued || _fixerStaticActionsHost == null || _fixerStaticActionsHost.Children.Count >= 7) return;
        _fixerStaticActionsQueued = true;
        // A rapid navigation can interrupt after any of the seven cards.
        // Resume from the existing card count instead of duplicating the first
        // card or leaving the partially populated host permanently incomplete.
        QueueFixerStaticActionBatch(token, _navigationGeneration,
            _fixerStaticActionsHost, _fixerStaticActionsHost.Children.Count);
    }

    private void QueueFixerStaticActionBatch(CancellationToken token, int generation, StackPanel host, int batch)
    {
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "fixer" ||
                !ReferenceEquals(_fixerStaticActionsHost, host))
            {
                // A stale callback must not clear the flag for a newer navigation.
                if (generation == _navigationGeneration && ReferenceEquals(_fixerStaticActionsHost, host))
                    _fixerStaticActionsQueued = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildFixerStaticActionBatch(host, batch);
            LogService.Info("performance", $"FIXER_STATIC_ACTIONS_BATCH batch={batch + 1} ms={sw.ElapsedMilliseconds} count={host.Children.Count}");

            if (batch < 6)
                QueueFixerStaticActionBatch(token, generation, host, batch + 1);
            else
                _fixerStaticActionsQueued = false;
        })) _fixerStaticActionsQueued = false;
    }

    private void BuildFixerStaticActionBatch(StackPanel host, int batch)
    {
        switch (batch)
        {
            case 0:
                host.Children.Add(ActionCard("Microsoft Store", "Vyčistí Store cache přes oficiální WSReset bez odinstalace aplikací.", "Reset Store", ResetStoreCacheAsync));
                break;
            case 1:
                host.Children.Add(ActionCard("Network repair", "Vyčistí DNS cache a resetuje Winsock. Použij při problémech se sítí.", "Opravit síť", RepairNetworkAsync));
                break;
            case 2:
                host.Children.Add(ActionCard("GPU / NVIDIA repair", "Přejde do GPU centra se správou grafického ovladače.", "Otevřít GPU", OpenGpuRepairCenterAsync));
                break;
            case 3:
                host.Children.Add(ActionCard("Game Bar / Capture", "Otevře systémové nastavení Game Baru a záznamu bez slepého přepisování policy.", "Otevřít", OpenGameBarSettingsAsync));
                break;
            case 4:
                host.Children.Add(ActionCard("System File Checker", "Spustí SFC /scannow skrytě. Použij při podezření na poškozené systémové soubory.", "Spustit SFC", async () =>
                {
                    await WindowsActions.RunSfcAsync();
                    InvalidateFixerDynamicCache();
                    await ShowMessageAsync("SFC", "Kontrola systémových souborů byla dokončena.");
                }));
                break;
            case 5:
                host.Children.Add(ActionCard("DISM RestoreHealth", "Opraví Windows component store. Použij při poškození komponent.", "Spustit DISM", async () =>
                {
                    await WindowsActions.RunDismRepairAsync();
                    InvalidateFixerDynamicCache();
                    await ShowMessageAsync("DISM", "RestoreHealth dokončen.");
                }));
                break;
            case 6:
                host.Children.Add(ActionCard("Restart Exploreru", "Rychlá oprava Explorer/UI bez restartu PC.", "Restart Explorer", async () =>
                {
                    await WindowsActions.RestartExplorerAsync();
                    InvalidateFixerDynamicCache();
                }));
                break;
        }
    }

    private async Task ResetStoreCacheAsync()
    {
        ProcessRunner.Open("wsreset.exe");
        ShowActionFeedback("Microsoft Store", ActionFeedbackKind.Info, "WSReset byl spuštěn oficiální cestou Windows. Výsledek ověř Windows Store po dokončení.");
        InvalidateFixerDynamicCache();
        await ShowStyledInfoAsync("Microsoft Store", "Spustil jsem oficiální WSReset. Po dokončení se Store může automaticky otevřít.");
    }

    private async Task RepairNetworkAsync()
    {
        if (!await ShowStyledConfirmAsync("Oprava sítě", "Vyčistí DNS cache a resetuje Winsock. Po resetu Winsock může být potřeba restart Windows.", "Opravit síť", "Zrušit", "Fixer")) return;
        bool completed = await RunCancellableWithProgressAsync(
            "Oprava sítě", "Resetuji DNS/Winsock…",
            token => SystemRepairDiagnosticsService.FlushNetworkAsync(token));
        if (!completed)
        {
            LogService.Info("fixer", "Network repair cancelled by user.");
            return;
        }
        var verify = await SystemRepairDiagnosticsService.VerifyAreaAsync("Network");
        InvalidateFixerDynamicCache();
        if (verify?.Healthy == true)
        {
            ShowActionFeedback("Network repair", ActionFeedbackKind.RestartRequired,
                "DNS/Winsock reset proběhl a síť po opravě odpovídá. Restart dokončí aplikaci Winsock změn.");
            await ShowStyledInfoAsync("Oprava sítě",
                "DNS cache byla vyčištěna a Winsock resetován. Aktuální konektivita byla po opravě ověřena.\n\n" +
                verify.Detail + "\n\nPro plné dokončení restartuj Windows.");
        }
        else
        {
            string detail = verify?.Detail ?? "Post-repair kontrola sítě nebyla dostupná.";
            ShowActionFeedback("Network repair", ActionFeedbackKind.Failed, detail);
            await ShowStyledInfoAsync("Oprava sítě — kontrola",
                "Reset DNS/Winsock proběhl, ale čerstvá kontrola konektivity není zelená.\n\n" + detail +
                "\n\nRestartuj Windows a potom spusť kontrolu znovu.");
        }
    }

    private Task OpenGpuRepairCenterAsync() => ShowSectionAsync("gpu");

    private Task OpenGameBarSettingsAsync()
    {
        ProcessRunner.Open("ms-settings:gaming-gamebar");
        return Task.CompletedTask;
    }
}

