using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _fixerStaticCache;
    private List<UIElement>? _fixerPageCache;
    private StackPanel? _fixerAppHealthHost;
    private StackPanel? _fixerDiagnosticsHost;
    private StackPanel? _fixerStaticActionsHost;
    private bool _fixerStaticActionsQueued;
    private DateTime _fixerDynamicAt;

    private bool TryAttachFixerPageCache(CancellationToken token)
    {
        if (_fixerPageCache == null) return false;
        ContentStack.Children.Clear();
        foreach (var child in _fixerPageCache) AttachCachedElement(child);
        if (_fixerStaticActionsHost != null && _fixerStaticActionsHost.Children.Count == 0)
            ScheduleDeferredFixerStaticActions(token);
        if (_fixerAppHealthHost != null && _fixerDiagnosticsHost != null &&
            DateTime.UtcNow - _fixerDynamicAt > TimeSpan.FromSeconds(15))
        {
            _fixerDynamicAt = DateTime.UtcNow;
            var appHealthHost = _fixerAppHealthHost;
            var diagnosticsHost = _fixerDiagnosticsHost;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "fixer") return;
                SafeTaskService.Forget(LoadAppHealthAsync(appHealthHost, token), "fixer", "Cached App Health");
                SafeTaskService.Forget(LoadFixerDiagnosticsAsync(diagnosticsHost, token), "fixer", "Cached Fixer diagnostics");
            });
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
        SystemRepairDiagnosticsService.InvalidateCache();
    }

    private void InvalidateFixerPageCache()
    {
        _fixerPageCache = null;
        _fixerStaticCache = null;
        _fixerAppHealthHost = null;
        _fixerDiagnosticsHost = null;
        _fixerStaticActionsHost = null;
        _fixerStaticActionsQueued = false;
        _fixerDynamicAt = default;
    }

    private void InvalidateFixerStaticCache() => InvalidateFixerPageCache();

    private IReadOnlyList<UIElement> GetFixerStaticElements()
    {
        if (_fixerStaticCache != null) return _fixerStaticCache;
        var items = new List<UIElement>();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Zkontrolovat vše", ShowRepairCenterDiagnosticsAsync));
        items.Add(BuildModernSectionHero(
            "fixer",
            "Fixer",
            "Kontrola, cílené opravy a ověření výsledku bez slepých zásahů.",
            "SYSTEM HEALTH",
            "Explorer • síť • Store • Update • GPU",
            actions));
        _fixerStaticActionsHost = new StackPanel { Spacing = 10 };
        items.Add(BuildDashboardExpander(
            "Repair nástroje",
            "Store • síť • GPU • Game Bar • SFC • DISM • Explorer",
            _fixerStaticActionsHost));
        _fixerStaticCache = items;
        return items;
    }

    private void ScheduleDeferredFixerStaticActions(CancellationToken token)
    {
        if (_fixerStaticActionsQueued || _fixerStaticActionsHost == null || _fixerStaticActionsHost.Children.Count > 0) return;
        _fixerStaticActionsQueued = true;
        QueueFixerStaticActionBatch(token, 0);
    }

    private void QueueFixerStaticActionBatch(CancellationToken token, int batch)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "fixer" || _fixerStaticActionsHost == null)
            {
                _fixerStaticActionsQueued = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildFixerStaticActionBatch(_fixerStaticActionsHost, batch);
            LogService.Info("performance", $"FIXER_STATIC_ACTIONS_BATCH batch={batch + 1} ms={sw.ElapsedMilliseconds} count={_fixerStaticActionsHost.Children.Count}");

            if (batch < 6)
                QueueFixerStaticActionBatch(token, batch + 1);
            else
                _fixerStaticActionsQueued = false;
        });
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
}

