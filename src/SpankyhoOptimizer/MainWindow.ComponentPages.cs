using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _driverPageLoadGeneration;
    private TweakVirtualRowViewModel[]? _driverRows;
    private ListView? _driverVirtualList;

    private TweakVirtualRowViewModel[] GetOrCreateDriverRows()
    {
        if (_driverRows != null && DriverRowsMatch(_driverRows, _tweaks))
        {
            for (int i = 0; i < _driverRows.Length; i++)
                _driverRows[i].Refresh(_tweakViewMode);
            return _driverRows;
        }

        return _driverRows = CreateDriverRows(_tweaks);
    }

    private bool DriverRowsMatch(
        TweakVirtualRowViewModel[] rows,
        System.Collections.Generic.IReadOnlyList<TweakDefinition> tweaks)
    {
        if (rows.Length != tweaks.Count) return false;
        for (int i = 0; i < rows.Length; i++)
            if (!ReferenceEquals(rows[i].Source, tweaks[i])) return false;
        return true;
    }

    private TweakVirtualRowViewModel[] CreateDriverRows(
        System.Collections.Generic.IReadOnlyList<TweakDefinition> tweaks)
    {
        var rows = new TweakVirtualRowViewModel[tweaks.Count];
        var driverIcon = SectionIconSource("drivers");
        for (int i = 0; i < tweaks.Count; i++)
        {
            TweakDefinition tweak = tweaks[i];
            tweak.PropertyChanged -= Tweak_PropertyChanged;
            tweak.PropertyChanged += Tweak_PropertyChanged;
            rows[i] = new TweakVirtualRowViewModel(
                tweak,
                _tweakViewMode,
                _ => QueueDirtyStateRefresh(),
                driverIcon);
        }
        return rows;
    }

    private void PrewarmDriverRowsCache()
    {
        var driverTweaks = CatalogService.GetTweaks("components", "Ovladače");
        if (_driverRows != null && DriverRowsMatch(_driverRows, driverTweaks)) return;
        _driverRows = CreateDriverRows(driverTweaks);
    }

    private Task BuildDriverManagerPageAsync(CancellationToken token)
    {
        int pageGeneration = unchecked(++_driverPageLoadGeneration);
        var perf = System.Diagnostics.Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();

        long heroStartMs = perf.ElapsedMilliseconds;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Vybrat co vypnout", () => ShowComponentDisableSelectorAsync("Ovladače")));
        ContentStack.Children.Add(BuildSectionHero("components", "OVLADAČE", "Ovladače", "Volitelné a starší systémové ovladače", actions));
        long heroMs = perf.ElapsedMilliseconds - heroStartMs;

        long advisorStartMs = perf.ElapsedMilliseconds;
        BuildComponentAdvisor();
        long advisorMs = perf.ElapsedMilliseconds - advisorStartMs;

        long countStartMs = perf.ElapsedMilliseconds;
        int loaded = _tweaks.Count(x => x.IsLoaded);
        int recommended = _tweaks.Count(x => x.Recommended && x.Risk != TweakRisk.Advanced);
        int unsafeCount = _tweaks.Count(x => x.Risk == TweakRisk.Advanced);
        int optimized = _tweaks.Count(x => x.IsLoaded && x.Current && x.Risk != TweakRisk.Advanced);
        int protectedCount = _tweaks.Count(x => x.Risk == TweakRisk.Advanced);
        long countsMs = perf.ElapsedMilliseconds - countStartMs;

        long summaryStartMs = perf.ElapsedMilliseconds;
        var summaryHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(summaryHost);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                pageGeneration != _driverPageLoadGeneration ||
                _section != "components" ||
                _sub != "Ovladače" ||
                summaryHost.Children.Count > 0)
                return;

            var summaryPerf = System.Diagnostics.Stopwatch.StartNew();
            summaryHost.Children.Add(BuildPageSummaryStrip(
                ("Doporučené", recommended.ToString(), recommended > 0 ? "SpankyInfoBrush" : "SpankySuccessBrush"),
                ("Načteno", $"{loaded}/{_tweaks.Count}", loaded == _tweaks.Count ? "SpankySuccessBrush" : "SpankyInfoBrush"),
                ("Optimalizováno", optimized.ToString(), "SpankySuccessBrush"),
                ("Chráněné", protectedCount.ToString(), "SpankyWarningBrush")));
            LogService.Info("performance", $"COMPONENT_DRIVER_SUMMARY_DEFERRED ms={summaryPerf.ElapsedMilliseconds}");
        });
        long summaryMs = perf.ElapsedMilliseconds - summaryStartMs;

        long shellMs = perf.ElapsedMilliseconds;

        var legacy = new StackPanel { Spacing = 7 };
        legacy.Children.Add(new TextBlock
        {
            Text = $"DOPORUČENÉ {recommended}   •   RIZIKOVÉ {unsafeCount}   •   HOTOVO {optimized}   •   CHRÁNĚNÉ {protectedCount}   •   STAV {loaded}/{_tweaks.Count}",
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var driverRowsHost = new StackPanel();
        driverRowsHost.Children.Add(new TextBlock
        {
            Text = "Načítám volitelné ovladače…",
            Foreground = TextSecondaryBrush()
        });
        legacy.Children.Add(driverRowsHost);

        ContentStack.Children.Add(BuildSectionSurface(
            "components",
            "Volitelné starší ovladače",
            "Všechny možnosti jsou dostupné hned; vizuální řádky se vytváří jen podle potřeby.",
            legacy));

        long attachMs = perf.ElapsedMilliseconds - shellMs;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                pageGeneration != _driverPageLoadGeneration ||
                _section != "components" ||
                _sub != "Ovladače")
                return;

            var deferredPerf = System.Diagnostics.Stopwatch.StartNew();
            long rowVmStartMs = deferredPerf.ElapsedMilliseconds;
            var rows = GetOrCreateDriverRows();
            long rowVmMs = deferredPerf.ElapsedMilliseconds - rowVmStartMs;

            long resourceStartMs = deferredPerf.ElapsedMilliseconds;
            var rowTemplate = UiResourceService.Get<DataTemplate>("TweakVirtualRowTemplate");
            var itemsPanel = UiResourceService.Get<ItemsPanelTemplate>("SpankyVirtualizingItemsPanel");
            long resourceMs = deferredPerf.ElapsedMilliseconds - resourceStartMs;

            long listStartMs = deferredPerf.ElapsedMilliseconds;
            var list = new ListView
            {
                MaxHeight = 700,
                SelectionMode = ListViewSelectionMode.None,
                ItemsSource = rows,
                ItemTemplate = rowTemplate,
                ItemsPanel = itemsPanel
            };
            _driverVirtualList = list;
            long listMs = deferredPerf.ElapsedMilliseconds - listStartMs;

            driverRowsHost.Children.Clear();
            driverRowsHost.Children.Add(list);
            SafeTaskService.Forget(ScheduleDriverTweakStateLoadAsync(token, pageGeneration), "drivers", "Driver tweak state debounce");
            LogService.Info("performance",
                $"COMPONENT_DRIVER_ROWS_DEFERRED rowVmMs={rowVmMs} resourceMs={resourceMs} listMs={listMs} totalMs={deferredPerf.ElapsedMilliseconds}");
        });

        LogService.Info("performance",
            $"COMPONENT_DRIVER_UI_PHASE tweaks={_tweaks.Count} shellMs={shellMs} rowsMs=0 attachMs={attachMs} totalMs={perf.ElapsedMilliseconds} virtualized=true deferredRows=true");
        return Task.CompletedTask;
    }

    private async Task ScheduleDriverTweakStateLoadAsync(CancellationToken token, int pageGeneration)
    {
        try
        {
            await Task.Delay(180, token).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || pageGeneration != _driverPageLoadGeneration || _section != "components" || _sub != "Ovladače") return;
                SafeTaskService.Forget(LoadDriverTweakStatesSafeAsync(token, pageGeneration), "drivers", "Driver tweak state auto-detect");
            });
        }
        catch (OperationCanceledException) { }
    }

    private long CurrentDriverRevisionStamp()
    {
        long stamp = _tweaks.Count;
        foreach (TweakDefinition tweak in _tweaks)
            stamp = unchecked((stamp * 397) ^ tweak.Revision);
        return stamp;
    }

    private async Task<int> RefreshDriverTweakRowsAsync(CancellationToken token)
    {
        var rows = _driverRows;
        if (rows == null) return 0;

        var perf = System.Diagnostics.Stopwatch.StartNew();
        int refreshed = 0;
        int batch = 0;

        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            row.Refresh(_tweakViewMode);
            refreshed++;
            batch++;
            if (batch >= 8)
            {
                batch = 0;
                await Task.Yield();
            }
        }

        LogService.Info("performance",
            $"DRIVER_ROWS_REFRESH rows={rows.Length} refreshed={refreshed} totalMs={perf.ElapsedMilliseconds}");
        return refreshed;
    }

    private async Task LoadDriverTweakStatesSafeAsync(CancellationToken token, int pageGeneration)
    {
        try
        {
            await _engine.DetectAsync(_tweaks, token);
            if (!token.IsCancellationRequested && pageGeneration == _driverPageLoadGeneration && _section == "components" && _sub == "Ovladače")
            {
                int refreshed = await RefreshDriverTweakRowsAsync(token);
                long stamp = CurrentDriverRevisionStamp();
                if (_componentsPageCache.ContainsKey("Ovladače"))
                    _componentsPageRevisionStamp["Ovladače"] = stamp;
                QueueDirtyStateRefresh();
                LogService.Info("drivers",
                    $"DRIVER_VIRTUAL_REFRESH_OK rows={_driverRows?.Length ?? 0} refreshed={refreshed} stamp={stamp}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Driver tweak state load failed", ex);
            if (token.IsCancellationRequested || pageGeneration != _driverPageLoadGeneration || _section != "components" || _sub != "Ovladače")
                return;

            bool retry = await ShowDriverLoadErrorAsync(ex, token, pageGeneration);
            if (retry && !token.IsCancellationRequested && pageGeneration == _driverPageLoadGeneration && _section == "components" && _sub == "Ovladače")
            {
                await Task.Delay(120, token);
                await LoadDriverTweakStatesSafeAsync(token, pageGeneration);
            }
        }
    }
}
