using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private TweakVirtualRowViewModel[]? _virtualTweakRows;
    private Dictionary<TweakDefinition, TweakVirtualRowViewModel>? _virtualTweakRowMapStore;
    private Dictionary<string, DeferredVirtualState>? _deferredVirtualStatesStore;
    private Dictionary<string, VirtualDashboardMetricRefs>? _virtualDashboardMetricRefsStore;

    private sealed record VirtualDashboardMetricRefs(
        TextBlock ReadBackValue,
        TextBlock ReadBackDetail,
        TextBlock ActiveValue,
        TextBlock ActiveDetail);

    private Dictionary<string, VirtualDashboardMetricRefs> VirtualDashboardMetricRefsBySection =>
        _virtualDashboardMetricRefsStore ??= new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<TweakDefinition, TweakVirtualRowViewModel> _virtualTweakRowMap
    {
        get => _virtualTweakRowMapStore ??= new();
        set => _virtualTweakRowMapStore = value;
    }

    private Dictionary<string, DeferredVirtualState> _deferredVirtualStates =>
        _deferredVirtualStatesStore ??= new(StringComparer.OrdinalIgnoreCase)
        {
            ["system"] = new DeferredVirtualState(),
            ["privacy"] = new DeferredVirtualState(),
            ["ui"] = new DeferredVirtualState(),
            ["update"] = new DeferredVirtualState()
        };

    private sealed record DeferredVirtualGroup(Panel Target, string Title, List<TweakVirtualRowViewModel> Rows);
    private sealed class DeferredVirtualState
    {
        public Queue<DeferredVirtualGroup> Groups { get; } = new();
        public DataTemplate? RowTemplate { get; set; }
        public int Generation { get; set; }
        public bool BatchQueued { get; set; }
    }

    private bool UseVirtualTweakRows() => _section is "system" or "privacy" or "update" || _tweaks.Count >= 24;

    private void ClearVirtualTweakRows()
    {
        bool hasRows = _virtualTweakRows is { Length: > 0 } || (_virtualTweakRowMapStore?.Count ?? 0) > 0;
        bool hasDeferred = false;
        if (_deferredVirtualStatesStore != null)
        {
            foreach (var state in _deferredVirtualStatesStore.Values)
            {
                if (state.BatchQueued || state.RowTemplate != null || state.Groups.Count > 0)
                {
                    hasDeferred = true;
                    break;
                }
            }
        }
        if (!hasRows && !hasDeferred) return;

        _virtualTweakRows = null;
        // Do not clear the map in-place: page caches (Privacy/System/etc.) can own the
        // same dictionary instance and must retain their tweak->row mapping across
        // navigation. Detach this page instead; the next virtual page gets its own map.
        _virtualTweakRowMap = new Dictionary<TweakDefinition, TweakVirtualRowViewModel>();

        // Deferred groups retain their target Panel and row VMs. When navigation leaves
        // a virtualized section, release those references immediately instead of waiting
        // for queued dispatcher callbacks that will no-op on the new section.
        if (_deferredVirtualStatesStore != null)
        {
            foreach (var state in _deferredVirtualStatesStore.Values)
            {
                state.Generation++;
                state.BatchQueued = false;
                state.Groups.Clear();
                state.RowTemplate = null;
            }
        }
    }

    private static int ImmediateVirtualGroupCount(string section) => section switch
    {
        // Keep the first two System groups visible as soon as the deferred
        // page root appears. The remaining groups still build in short idle
        // slices; cold foreground navigation and its 35ms gate stay intact.
        "system" => 2,
        "privacy" => 0,
        "ui" => 0,
        "update" => 0,
        _ => int.MaxValue
    };

    private void BuildVirtualTweakCards(Panel? rootHost = null)
    {
        var perf = Stopwatch.StartNew();
        ClearVirtualTweakRows();
        long clearRowsMs = perf.ElapsedMilliseconds;
        string section = _section;
        _deferredVirtualStates.TryGetValue(section, out var deferredState);
        if (deferredState != null) ClearDeferredVirtualGroups(section);
        long deferredClearMs = perf.ElapsedMilliseconds - clearRowsMs;

        var rowTemplate = UiResourceService.Get<DataTemplate>("TweakVirtualRowTemplate");
        long resourceMs = perf.ElapsedMilliseconds - clearRowsMs - deferredClearMs;
        long iconStartMs = perf.ElapsedMilliseconds;
        ImageSource sectionIconSource = SectionIconSource(section);
        long iconMs = perf.ElapsedMilliseconds - iconStartMs;
        IEnumerable<TweakDefinition> groupSource = section == "system" && _darkTheme
            ? _tweaks.OrderBy(GlassSystemOrder)
            : _tweaks;
        var groups = new List<(string Key, List<TweakDefinition> Tweaks)>();
        var groupIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (TweakDefinition tweak in groupSource)
        {
            string key = section == "system" && _darkTheme ? GlassSystemGroup(tweak) : tweak.Group;
            if (!groupIndexes.TryGetValue(key, out int index))
            {
                index = groups.Count;
                groupIndexes[key] = index;
                groups.Add((key, new List<TweakDefinition>()));
            }
            groups[index].Tweaks.Add(tweak);
        }
        long groupMs = perf.ElapsedMilliseconds - clearRowsMs - deferredClearMs - resourceMs - iconMs;
        long setupMs = perf.ElapsedMilliseconds;
        long headerMs = 0, vmMs = 0, repeaterMs = 0, expanderMs = 0, attachMs = 0;
        long dashboardMs = 0, advisorAttachMs = 0, virtualRootAttachMs = 0, finalizeMs = 0, scheduleMs = 0;
        var allRows = new List<TweakVirtualRowViewModel>(_tweaks.Count);
        int groupIndex = 0;
        int immediateGroups = ImmediateVirtualGroupCount(section);
        Panel rootAttachTarget = rootHost ?? ContentStack;
        Panel virtualAttachTarget = rootAttachTarget;
        if (section is "privacy" or "system")
        {
            var virtualRoot = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };

            long phase = perf.ElapsedMilliseconds;
            virtualRoot.Children.Add(BuildVirtualSectionDashboard(section));
            dashboardMs = perf.ElapsedMilliseconds - phase;

            phase = perf.ElapsedMilliseconds;
            rootAttachTarget.Children.Add(virtualRoot);
            virtualRootAttachMs = perf.ElapsedMilliseconds - phase;
            virtualAttachTarget = virtualRoot;
            if (_darkTheme)
            {
                var glassGroups = CreateGlassGroupGrid();
                virtualRoot.Children.Add(glassGroups);
                virtualAttachTarget = glassGroups;
            }
        }
        foreach (var group in groups)
        {
            string groupTitle = group.Key;
            bool deferred = deferredState != null && groupIndex >= immediateGroups;

            long phase = perf.ElapsedMilliseconds;
            var rows = new List<TweakVirtualRowViewModel>(group.Tweaks.Count);
            foreach (var tweak in group.Tweaks)
            {
                tweak.PropertyChanged -= Tweak_PropertyChanged;
                tweak.PropertyChanged += Tweak_PropertyChanged;
                var vm = new TweakVirtualRowViewModel(tweak, _tweakViewMode, _ => QueueDirtyStateRefresh(), sectionIconSource, deferInitialRefresh: deferred);
                rows.Add(vm); allRows.Add(vm); _virtualTweakRowMap[tweak] = vm;
            }
            vmMs += perf.ElapsedMilliseconds - phase;

            if (deferred && deferredState != null)
            {
                deferredState.Groups.Enqueue(new DeferredVirtualGroup(virtualAttachTarget, groupTitle, rows));
                deferredState.RowTemplate = rowTemplate;
                groupIndex++;
                continue;
            }

            phase = perf.ElapsedMilliseconds;
            var header = CreateVirtualGroupHeader(groupTitle);
            headerMs += perf.ElapsedMilliseconds - phase;

            phase = perf.ElapsedMilliseconds;
            var groupContent = CreateVirtualGroupRepeater(rows, rowTemplate);
            repeaterMs += perf.ElapsedMilliseconds - phase;

            phase = perf.ElapsedMilliseconds;
            var expander = CreateVirtualGroupExpander(header, groupContent, expanded: true);
            expanderMs += perf.ElapsedMilliseconds - phase;

            phase = perf.ElapsedMilliseconds;
            AttachVirtualGroup(virtualAttachTarget, expander);
            attachMs += perf.ElapsedMilliseconds - phase;
            groupIndex++;
        }
        long finalizeStartMs = perf.ElapsedMilliseconds;
        _virtualTweakRows = allRows.ToArray();
        int deferredCount = deferredState?.Groups.Count ?? 0;
        finalizeMs = perf.ElapsedMilliseconds - finalizeStartMs;

        long scheduleStartMs = perf.ElapsedMilliseconds;
        if (deferredCount > 0) ScheduleDeferredVirtualGroups(section);
        scheduleMs = perf.ElapsedMilliseconds - scheduleStartMs;

        LogService.Info("performance", $"VIRTUAL_UI_PHASE section={section} groups={groups.Count} rows={allRows.Count} deferred={deferredCount} setupMs={setupMs} clearRowsMs={clearRowsMs} deferredClearMs={deferredClearMs} resourceMs={resourceMs} iconMs={iconMs} groupMs={groupMs} dashboardMs={dashboardMs} advisorMs={advisorAttachMs} rootAttachMs={virtualRootAttachMs} headerMs={headerMs} vmMs={vmMs} repeaterMs={repeaterMs} expanderMs={expanderMs} attachMs={attachMs} finalizeMs={finalizeMs} scheduleMs={scheduleMs} totalMs={perf.ElapsedMilliseconds}");
    }

    private UIElement BuildVirtualSectionDashboard(string section)
    {
        if (UseGlassChrome)
        {
            VirtualDashboardMetricRefsBySection.Remove(section);
            // All System groups are deliberately drawn after the first frame.
            // Show the full catalog size immediately so it never looks like
            // the rework removed toggles while deferred groups are queued.
            string purpose = section == "system"
                ? $"{_tweaks.Count} skutečných tweaků • " +
                  $"{_tweaks.Select(GlassSystemGroup).Distinct(StringComparer.OrdinalIgnoreCase).Count()} skupin • " +
                  "všechny skupiny se zobrazují postupně níže"
                : $"{_tweaks.Count} nastavení • soukromí a ochrana dat";
            return BuildNeutralSectionIntro(
                section == "system" ? "Systémové tweaky" : "Soukromí", purpose);
        }
        int total = _tweaks.Count;
        int recommended = 0;
        int loaded = 0;
        int active = 0;
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TweakDefinition tweak in _tweaks)
        {
            if (tweak.Recommended) recommended++;
            if (tweak.IsLoaded)
            {
                loaded++;
                if (tweak.Current) active++;
            }
            groupNames.Add(tweak.Group);
        }
        int groups = groupNames.Count;

        string title = section == "privacy" ? "Privacy & Security" : "System Tweaks";
        string subtitle = section == "privacy"
            ? "Recommended privacy nastavení seskupené podle oblasti. Všechny skupiny jsou rovnou dostupné."
            : "Systémové tweaky po kategoriích. Všechny skupiny jsou přehledně rozbalené.";

        var root = new StackPanel { Spacing = 9, Margin = new Thickness(0, 0, 0, 4) };
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        root.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        var readBackMetric = BuildDashboardMetric(
            "Read-back",
            $"{loaded}/{total}",
            loaded < total ? "Načítám skutečný stav…" : "Načtený skutečný stav");
        var activeMetric = BuildDashboardMetric(
            "Aktivní",
            loaded == 0 && total > 0 ? "…" : active.ToString(),
            loaded < total ? "Načítám skutečný stav…" : "Potvrzené hodnoty");

        root.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Tweaky", total.ToString(), $"{groups} kategorií"),
            BuildDashboardMetric("Recommended", recommended.ToString(), "Doporučený baseline", "SpankySuccessBrush"),
            readBackMetric,
            activeMetric));

        if (TryGetDashboardMetricText(readBackMetric, 1, out var readBackValue) &&
            TryGetDashboardMetricText(readBackMetric, 2, out var readBackDetail) &&
            TryGetDashboardMetricText(activeMetric, 1, out var activeValue) &&
            TryGetDashboardMetricText(activeMetric, 2, out var activeDetail))
        {
            VirtualDashboardMetricRefsBySection[section] =
                new VirtualDashboardMetricRefs(readBackValue, readBackDetail, activeValue, activeDetail);
        }

        return BuildDashboardCard(root, new Thickness(14, 12, 14, 12));
    }

    private static bool TryGetDashboardMetricText(FrameworkElement metric, int childIndex, out TextBlock text)
    {
        if (metric is Border { Child: StackPanel stack } &&
            childIndex >= 0 &&
            childIndex < stack.Children.Count &&
            stack.Children[childIndex] is TextBlock found)
        {
            text = found;
            return true;
        }

        text = null!;
        return false;
    }

    private void RefreshVirtualSectionDashboardMetrics(string? section)
    {
        if (string.IsNullOrWhiteSpace(section) ||
            !VirtualDashboardMetricRefsBySection.TryGetValue(section, out var refs))
            return;

        int total = _tweaks.Count;
        int loaded = _tweaks.Count(x => x.IsLoaded);
        int active = _tweaks.Count(x => x.IsLoaded && x.Current);

        // WinUI Text setters invalidate layout even when the dashboard's
        // counts did not change. Most tweak events only update a single row.
        // Avoid four unnecessary dependency-property writes per refresh.
        string readBack = $"{loaded}/{total}";
        string readDetail = loaded < total
            ? "Načítám skutečný stav…"
            : "Načtený skutečný stav";
        string activeValue = loaded == 0 && total > 0 ? "…" : active.ToString();
        string activeDetail = loaded < total
            ? "Načítám skutečný stav…"
            : "Potvrzené hodnoty";
        if (!string.Equals(refs.ReadBackValue.Text, readBack, StringComparison.Ordinal))
            refs.ReadBackValue.Text = readBack;
        if (!string.Equals(refs.ReadBackDetail.Text, readDetail, StringComparison.Ordinal))
            refs.ReadBackDetail.Text = readDetail;
        if (!string.Equals(refs.ActiveValue.Text, activeValue, StringComparison.Ordinal))
            refs.ActiveValue.Text = activeValue;
        if (!string.Equals(refs.ActiveDetail.Text, activeDetail, StringComparison.Ordinal))
            refs.ActiveDetail.Text = activeDetail;
    }

    private static UIElement CreateVirtualGroupHeader(string titleText)
    {
        // Virtualized System/Privacy/Update groups share the same quiet,
        // scan-friendly hierarchy as classic tweak groups. No colored rail.
        var title = new TextBlock
        {
            Text = titleText,
            Tag = "UiGroupTitleSize", FontSize = UiFontSize("UiGroupTitleSize"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            Tag = "virtual-group-heading",
            Margin = new Thickness(0, 6, 0, 5),
            Padding = new Thickness(4, 6, 4, 7),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SectionBorderBrush(),
            Background = null,
            Child = title
        };
    }

    private static ItemsRepeater CreateVirtualGroupRepeater(List<TweakVirtualRowViewModel> rows, DataTemplate rowTemplate) =>
        new()
        {
            // Configure the layout and template before binding the source.
            // A WinUI ItemsRepeater can otherwise invalidate its initial
            // realization repeatedly while the native control is cold.
            // The final visual tree, styling and source identity are unchanged.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Layout = new StackLayout { Spacing = UseGlassChrome ? 0 : 6 },
            ItemTemplate = rowTemplate,
            ItemsSource = rows
        };

    private static UIElement CreateVirtualGroupExpander(UIElement header, UIElement content, bool expanded = true)
    {
        if (UseGlassChrome && (_themeSection is "system" or "privacy"))
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(header);
            body.Children.Add(content);
            return new Border
            {
                Background = GlassPanelBrush(), BorderBrush = GlassEdgeBrush(),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(26),
                Padding = new Thickness(20,8,20,8), VerticalAlignment = VerticalAlignment.Top,
                Child = body
            };
        }
        return new Expander
        {
            Header = header, IsExpanded = expanded, Content = content,
            Margin = new Thickness(0,0,0,4), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private void ScheduleDeferredVirtualGroups(string section)
    {
        if (!_deferredVirtualStates.TryGetValue(section, out var state) || state.BatchQueued || state.Groups.Count == 0 || state.RowTemplate == null) return;
        int generation = state.Generation;
        state.BatchQueued = true;
        // User-visible rows must not be starved behind startup shell work:
        // at Low priority the first deferred batch waited ~130 ms and all
        // groups ~370 ms after the deferred root (Debug, 3 runs). Normal keeps
        // every batch at <=4 groups / ~3 ms and fills the page in ~30 ms; the
        // remaining titlebar/sidebar cosmetics shift by only that work.
        bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            // A callback from a previous navigation generation must not clear
            // the queued marker for a newly scheduled batch.
            if (generation != state.Generation) return;
            state.BatchQueued = false;
            if (_section != section || state.RowTemplate == null) return;
            var phase = Stopwatch.StartNew();
            int completed = 0;
            // Detailed stage timing is only needed for opted-in diagnostics
            // and smoke tests, never for a normal navigation hot path.
            bool profileGroups = LogService.PerformanceDiagnosticsEnabled;
            while (completed < 4 && state.Groups.Count > 0)
            {
                var item = state.Groups.Dequeue();
                long beforeGroupMs = profileGroups ? phase.ElapsedMilliseconds : 0;
                // The group is now about to become visible. Evaluate row policy
                // here, not synchronously during the cold navigation's root
                // build; the 26 deferred System groups need no eager refresh.
                foreach (var row in item.Rows) row.Refresh(_tweakViewMode);
                long afterRefreshMs = profileGroups ? phase.ElapsedMilliseconds : 0;
                var header = CreateVirtualGroupHeader(item.Title);
                long afterHeaderMs = profileGroups ? phase.ElapsedMilliseconds : 0;
                var content = CreateVirtualGroupRepeater(item.Rows, state.RowTemplate);
                long afterRepeaterMs = profileGroups ? phase.ElapsedMilliseconds : 0;
                AttachVirtualGroup(item.Target, CreateVirtualGroupExpander(header, content, expanded: true));
                long afterAttachMs = profileGroups ? phase.ElapsedMilliseconds : 0;
                // Only an actual >8ms UI hitch deserves a diagnostic line.
                // Distinguish policy refresh from native template/attach costs.
                if (profileGroups && afterAttachMs - beforeGroupMs >= 8)
                    LogService.Info("performance",
                        $"VIRTUAL_DEFERRED_SLOW_GROUP section={section} title={item.Title} rows={item.Rows.Count} refreshMs={afterRefreshMs-beforeGroupMs} headerMs={afterHeaderMs-afterRefreshMs} repeaterMs={afterRepeaterMs-afterHeaderMs} attachMs={afterAttachMs-afterRepeaterMs} totalMs={afterAttachMs-beforeGroupMs}");
                completed++;
                if (phase.ElapsedMilliseconds >= 3) break;
            }
            LogService.Info("performance", $"VIRTUAL_DEFERRED section={section} batch={completed} remaining={state.Groups.Count} ms={phase.ElapsedMilliseconds}");
            if (state.Groups.Count > 0) ScheduleDeferredVirtualGroups(section);
        });
        // If the dispatcher rejects work during navigation/shutdown, allow a
        // future visit to queue the remaining groups rather than staying stuck.
        if (!queued && generation == state.Generation)
            state.BatchQueued = false;
    }

    private void ClearDeferredVirtualGroups(string section)
    {
        if (!_deferredVirtualStates.TryGetValue(section, out var state)) return;
        state.Generation++;
        state.BatchQueued = false;
        state.Groups.Clear();
        state.RowTemplate = null;
    }

    private int GetDeferredVirtualGroupCount(string section) =>
        _deferredVirtualStates.TryGetValue(section, out var state) ? state.Groups.Count : 0;

    // Smoke/QA only: navigation returns after the page root, while virtual
    // groups are attached in bounded dispatcher batches. Wait for the real
    // queue to drain instead of relying on a fixed delay, so layout checks
    // never inspect a half-populated System page.
    internal async Task WaitForDeferredVirtualGroupsAsync(string section, int timeoutMs = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (!_deferredVirtualStates.TryGetValue(section, out var state) ||
                (!state.BatchQueued && state.Groups.Count == 0))
                return;
            await Task.Delay(15);
        }
    }

    // Navigation-only read-only performance regression: the GPU hardware
    // and card shell should not be discarded when tweak detection becomes stale.
    internal async Task RunGpuStaleCacheNavigationSmokeAsync()
    {
        await ShowSectionAsync("gpu");
        for (int i = 0; i < 120 && _gpuPageCache == null; i++)
            await Task.Delay(25);
        if (_gpuPageCache == null || _gpuTweakHost == null)
            throw new InvalidOperationException("GPU stale-cache smoke could not warm complete page.");
        var oldHost = _gpuTweakHost;
        DateTime oldDetectedAt = _tweakCacheTime.TryGetValue("gpu|", out var stamp) ? stamp : default;
        long elapsedMs;
        bool reused;
        try
        {
            _tweakCacheTime["gpu|"] = DateTime.UtcNow.AddSeconds(-95);
            await ShowSectionAsync("settings");
            var stopwatch = Stopwatch.StartNew();
            await ShowSectionAsync("gpu");
            stopwatch.Stop();
            elapsedMs = stopwatch.ElapsedMilliseconds;
            reused = ReferenceEquals(oldHost, _gpuTweakHost);
            if (!reused)
                throw new InvalidOperationException("GPU stale tweak refresh destroyed the cached UI and row viewmodels.");
            for (int i = 0; i < 100 && (!_tweakCacheTime.TryGetValue("gpu|", out var updated) ||
                                              DateTime.UtcNow - updated > TimeSpan.FromSeconds(10)); i++)
                await Task.Delay(25);
            if (!_tweakCacheTime.TryGetValue("gpu|", out var refreshedAt) ||
                DateTime.UtcNow - refreshedAt > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("GPU stale-cache refresh did not update tweak detection.");
            LogService.Info("performance",
                $"GPU_STALE_CACHE_NAV_SMOKE_OK wallMs={elapsedMs} reusedHost={reused} cards={CountClassicTweakCards(_gpuTweakHost)} backgroundRefresh=true");
        }
        finally
        {
            if (oldDetectedAt != default && _tweakCacheTime.TryGetValue("gpu|", out var now) &&
                now < oldDetectedAt) _tweakCacheTime["gpu|"] = oldDetectedAt;
        }
    }

    internal async Task RunGpuDeferredTweakSmokeAsync()
    {
        InvalidateGpuPageCache(includeStaticShell: true);
        var sw = Stopwatch.StartNew();
        await ShowSectionAsync("gpu");
        long renderMs = sw.ElapsedMilliseconds;
        if (renderMs > 55)
            throw new InvalidOperationException($"GPU foreground render too slow: {renderMs} ms.");

        int expected = _tweaks.Count;
        for (int i = 0; i < 40 && CountClassicTweakCards(_gpuTweakHost) < expected; i++)
            await Task.Delay(25);
        int actual = CountClassicTweakCards(_gpuTweakHost);
        if (expected != 4 || actual != expected)
            throw new InvalidOperationException($"GPU deferred tweak cards incomplete: expected={expected} actual={actual}.");

        for (int i = 0; i < 120 && _gpuPageCache == null; i++)
            await Task.Delay(25);
        if (_gpuPageCache == null)
            throw new InvalidOperationException("GPU page cache was not captured.");

        var host = _gpuTweakHost;
        await ShowSectionAsync("settings");
        await ShowSectionAsync("gpu");
        if (!ReferenceEquals(_gpuTweakHost, host))
            throw new InvalidOperationException("GPU deferred tweak host was rebuilt instead of reattached.");
        if (CountClassicTweakCards(_gpuTweakHost) != expected)
            throw new InvalidOperationException("GPU cached tweak cards were not preserved after reattach.");

        // Also exercise cancellation, navigation ownership and the retry
        // path of the deferred GPU driver status with in-memory fake Tasks.
        await RunGpuDeferredHydrationOwnershipSmokeAsync();
        LogService.Info("performance", $"GPU_DEFERRED_TWEAK_SMOKE_OK renderMs={renderMs} cards={actual} reattached=true hydrationOwnership=true");
    }

    internal async Task RunToggleStateConsistencySmokeAsync()
    {
        var source = CatalogService.GetTweaks("ui").First(x => x.Id == "ui.shortcutarrow");
        await _engine.DetectAsync(new[] { source });
        bool originalPending = source.Pending;

        var vm = new TweakVirtualRowViewModel(source, _tweakViewMode, _ => { }, null!);
        var card = CreateTweakCard(source);
        bool smokeCardAttached = false;
        try
        {
            source.Pending = !originalPending;
            vm.Refresh(_tweakViewMode, force: true);
            if (vm.Pending != source.Pending)
                throw new InvalidOperationException("Virtual toggle neodpovídá source.Pending po force-refreshi.");

            var grid = card.Child as Grid ?? throw new InvalidOperationException("Classic tweak card nemá Grid.");
            var cardRefs = grid.Tag as ClassicTweakCardRefs
                ?? throw new InvalidOperationException("Classic tweak card nemá přímé odkazy.");
            var toggle = cardRefs.Toggle;
            int writebacksBeforeSync = cardRefs.UserWritebacks;
            RefreshClassicTweakCard(card);
            // Explicitly simulate unrelated queued dirty-state work. This
            // previously caused a false failure when the smoke reset/read the
            // shared flag even though the tested card never wrote back.
            QueueDirtyStateRefresh();
            await Task.Yield();
            // The shared _dirtyRefreshQueued can be set by any concurrent
            // background tweak detection. Only this card's writeback count
            // proves whether the programmatic toggle refresh changed a model.
            if (cardRefs.UserWritebacks != writebacksBeforeSync)
                throw new InvalidOperationException("Programová synchronizace classic toggle nečekaně změnila Pending.");
            if (toggle.IsOn != source.Pending)
                throw new InvalidOperationException("Classic toggle neodpovídá source.Pending po force-refreshi.");

            card.Opacity = 0;
            ContentStack.Children.Add(card);
            smokeCardAttached = true;
            card.UpdateLayout();
            toggle.ApplyTemplate();
            await Task.Yield();

            // Test the actual ToggleSwitch -> Toggled -> tweak Pending path, not just visual states.
            bool start = toggle.IsOn;
            int writesBeforeUserToggle = cardRefs.UserWritebacks;
            toggle.IsOn = !start;
            await Task.Yield();
            if (source.Pending != toggle.IsOn ||
                cardRefs.UserWritebacks != writesBeforeUserToggle + 1)
                throw new InvalidOperationException("ToggleSwitch Toggled handler nepropagoval přesně jednu změnu Pending.");

            toggle.IsOn = start;
            await Task.Yield();
            if (source.Pending != start ||
                cardRefs.UserWritebacks != writesBeforeUserToggle + 2)
                throw new InvalidOperationException("ToggleSwitch návrat stavu nepropagoval přesně jednu změnu Pending.");

            if (!toggle.IsEnabled)
                throw new InvalidOperationException("Editovatelný tweak má neaktivní ToggleSwitch.");
        }
        finally
        {
            if (smokeCardAttached)
                ContentStack.Children.Remove(card);
            source.Pending = originalPending;
            vm.Refresh(_tweakViewMode, force: true);
            RefreshClassicTweakCard(card);
        }

        // Global Apply's final readback must preserve a newly staged choice,
        // yet reconcile Pending when Windows really reverted the confirmed
        // value. Exercise the same native-detection path in memory only.
        bool simulatedActual = true;
        var applyReadback = new TweakDefinition
        {
            Id = "smoke.ui.apply-final-readback",
            Section = "smoke",
            Group = "Apply readback",
            Name = "Apply readback",
            Description = "In-memory post-Apply state check",
            RequiresAdmin = false,
            DetectAsync = _ => Task.FromResult(simulatedActual)
        };
        applyReadback.IsLoaded = true;
        applyReadback.Current = true;
        applyReadback.Pending = false; // newer choice after confirmed Apply(true)
        await _engine.DetectAsync(new[] { applyReadback });
        if (!applyReadback.Current || applyReadback.Pending || !applyReadback.IsDirty)
            throw new InvalidOperationException(
                "Post-Apply readback lost the user's newer Pending selection.");

        // No newer edit: real external drift must update BOTH fields so the
        // confirmed target comparison can report failure instead of success.
        applyReadback.Pending = true;
        simulatedActual = false;
        await _engine.DetectAsync(new[] { applyReadback });
        if (applyReadback.Current || applyReadback.Pending || !applyReadback.IsLoaded)
            throw new InvalidOperationException(
                "Post-Apply drift readback did not reconcile the Windows state.");

        LogService.Info("ui", "TOGGLE_STATE_CONSISTENCY_SMOKE_OK virtual=true classic=true visual=true applyPendingPreserved=true driftReconciled=true");
    }

    private static T? FindNamedVisualDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal)) return typed;
            var nested = FindNamedVisualDescendant<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private void RefreshVirtualTweakRows(bool force = false)
    {
        if (_virtualTweakRows == null) return;
        foreach (var row in _virtualTweakRows) row.Refresh(_tweakViewMode, force);
    }
    private sealed class TweakVirtualRowViewModel : INotifyPropertyChanged
    {
        private readonly TweakDefinition? _source;
        private readonly Action<TweakDefinition>? _changed;
        private TweakViewMode _mode;
        private bool _pending;
        private readonly bool _glassFeatured;
        private readonly bool _invertDisplay;
        private bool _suppress;
        private string _verdict = string.Empty;
        private string _meta = string.Empty;
        private string _policyDetail = string.Empty;
        private bool _canEdit;
        // Native WinUI brush resource lookups should not run for every
        // deferred tweak row during the cold page-root build. Materialize
        // defaults only when a visible template actually binds them.
        private Brush? _badgeBrush;
        private string _statusText = string.Empty;
        private string _statusBrushResource = string.Empty;
        private Brush? _statusBrush;
        private int _lastSourceRevision = -1;
        private int _lastRevalidationRevision = -1;
        private TweakViewMode _lastRefreshMode;
        private bool _refreshInitialized;

        private TweakVirtualRowViewModel(string header)
        {
            HeaderText = header.ToUpperInvariant();
            HeaderVisibility = Visibility.Visible;
            CardVisibility = Visibility.Collapsed;
        }

        public TweakVirtualRowViewModel(TweakDefinition source, TweakViewMode mode, Action<TweakDefinition> changed, ImageSource iconSource, bool deferInitialRefresh = false)
        {
            _source = source; _changed = changed; _mode = mode;
            _glassFeatured = _themeSection == "system" && GlassSystemOrder(source) < 8;
            _invertDisplay = _glassFeatured && (source.Id is "ui.taskanim" or "ui.transparency" or "sys.gamedvr");
            HeaderVisibility = Visibility.Collapsed;
            CardVisibility = Visibility.Visible;
            Name = source.Name; Description = source.Description;
            RestartVisibility = source.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
            IconSource = iconSource;
            if (!deferInitialRefresh) Refresh(mode);
        }

        public static TweakVirtualRowViewModel Header(string text) => new(text);
        public TweakDefinition? Source => _source;
        public string HeaderText { get; } = string.Empty;
        public Visibility HeaderVisibility { get; }
        public Visibility CardVisibility { get; }
        public string Name { get; } = string.Empty;
        public string Description { get; } = string.Empty;
        public string DisplayName => !_glassFeatured ? Name : _source?.Id switch
        {
            "ui.taskanim" => "Animace taskbaru", "ui.transparency" => "Průhlednost",
            "sys.gamemode" => "Herní režim", "sys.gamedvr" => "Záznam na pozadí",
            "privacy.telemetry" => "Omezit telemetrii", "privacy.adid" => "Vypnout reklamní ID",
            "privacy.tailored" => "Vypnout přizpůsobený obsah", "privacy.activity" => "Vypnout historii aktivit", _ => Name
        };
        public string DisplayDescription => !_glassFeatured ? Description : _source?.Id switch
        {
            "ui.taskanim" => "Zapne nebo vypne animace hlavního panelu Windows.",
            "ui.transparency" => "Povolí průhledné efekty v prostředí Windows.",
            "sys.gamemode" => "Optimalizuje systém pro lepší výkon ve hrách.",
            "sys.gamedvr" => "Povolí nebo zakáže záznam herních klipů na pozadí.",
            "privacy.telemetry" => "Omezí odesílání diagnostických dat společnosti Microsoft.",
            "privacy.adid" => "Zakáže používání reklamního identifikátoru pro personalizaci reklam.",
            "privacy.tailored" => "Vypne přizpůsobený obsah založený na diagnostických datech.",
            "privacy.activity" => "Zabrání odesílání historie vaší aktivity do cloudu.", _ => Description
        };
        public bool DisplayPending { get => _invertDisplay ? !Pending : Pending; set => Pending = _invertDisplay ? !value : value; }
        public Visibility RestartVisibility { get; }
        public ImageSource? IconSource { get; }
        public Brush CardBackground => _darkTheme && (_glassFeatured || _source?.Section is "system" or "privacy") ? ThemeColor(0,0,0,0) : SectionCardBackgroundBrush();
        public Brush CardBorder => SectionBorderBrush();
        public Brush TextPrimary => TextPrimaryBrush();
        public Brush TextSecondary => TextSecondaryBrush();
        public Brush Accent => AccentBrush();
        public Brush AccentSoft => AccentSoftBrush();
        public string Verdict => _verdict;
        public string Meta => _meta;
        public string PolicyDetail => _policyDetail;
        public Brush BadgeBrush => _badgeBrush ??= AccentBrush();
        public string StatusText => _statusText;
        public Brush StatusBrush => _statusBrush ??= AccentBrush();
        public bool CanEdit => _canEdit;
        public Visibility DetailVisibility => _darkTheme && (_glassFeatured || _source?.Section is "system" or "privacy") ? Visibility.Collapsed : Visibility.Visible;
        public string RowDetail => $"{StatusText}\n{Meta}\n{PolicyDetail}";
        public string ToggleStateText => DisplayPending ? "ON" : "OFF";

        public bool Pending
        {
            get => _pending;
            set
            {
                if (_pending == value) return;
                _pending = value; OnChanged(); OnChanged(nameof(ToggleStateText)); OnChanged(nameof(DisplayPending));
                if (_suppress || _source == null) return;
                var live = TweakPolicyService.Evaluate(_source, _mode);
                if (!live.CanEdit || !_source.IsLoaded || _source.IsBusy)
                {
                    // An async policy/working-state update can invalidate a
                    // click after WinUI toggles visually. Roll back every
                    // bound representation, including the ON/OFF label.
                    _pending = _source.Pending;
                    OnChanged();
                    OnChanged(nameof(ToggleStateText));
                    OnChanged(nameof(DisplayPending));
                    return;
                }
                _source.Pending = value;
                _changed?.Invoke(_source);
            }
        }
        public void Refresh(TweakViewMode mode, bool force = false)
        {
            if (_source == null) return;
            _mode = mode;
            int sourceRevision = _source.Revision;
            int revalidationRevision = WindowsBuildRevalidationService.Revision;
            if (!force && _refreshInitialized && _lastRefreshMode == mode && _lastSourceRevision == sourceRevision && _lastRevalidationRevision == revalidationRevision)
                return;

            _refreshInitialized = true;
            _lastRefreshMode = mode;
            _lastSourceRevision = sourceRevision;
            _lastRevalidationRevision = revalidationRevision;

            var policy = TweakPolicyService.Evaluate(_source, mode);
            string newVerdict = policy.Verdict;
            string newPolicyDetail = policy.Detail;
            bool newCanEdit = policy.CanEdit && !_source.IsBusy;
            string newMeta = mode == TweakViewMode.Diagnostics || !_source.IsLoaded
                ? policy.Detail
                : TweakVerificationMeta(_source);

            bool verdictChanged = !string.Equals(_verdict, newVerdict, StringComparison.Ordinal);
            bool detailChanged = !string.Equals(_policyDetail, newPolicyDetail, StringComparison.Ordinal);
            bool canEditChanged = _canEdit != newCanEdit;
            bool metaChanged = !string.Equals(_meta, newMeta, StringComparison.Ordinal);

            if (verdictChanged || force)
            {
                _verdict = newVerdict;
                // A forced row refresh may follow a theme change even when
                // the verdict text is identical. Rebind its current brush.
                _badgeBrush = UiBrush(newVerdict == "Recommended"
                    ? "SpankySuccessBrush"
                    : newVerdict is "Advanced" or "Needs attention" or "Requires elevation" or "Requires restart"
                        ? "SpankyWarningBrush" : "SpankyAccentBrush");
            }
            if (detailChanged) _policyDetail = newPolicyDetail;
            if (canEditChanged) _canEdit = newCanEdit;
            if (metaChanged) _meta = newMeta;

            var uiState = TweakUiState(_source);
            string newStatusText = uiState.Text;
            bool statusChanged = !string.Equals(_statusText, newStatusText, StringComparison.Ordinal);
            bool statusBrushChanged = !string.Equals(_statusBrushResource, uiState.Resource,
                StringComparison.Ordinal);
            if (statusChanged) _statusText = newStatusText;
            if (statusBrushChanged || force || _statusBrush == null)
            {
                // A policy can change semantic color WITHOUT changing the
                // Czech status string. Also refresh theme resources on a
                // forced row repaint without performing a brush lookup on
                // every otherwise unchanged row.
                _statusBrushResource = uiState.Resource;
                _statusBrush = UiBrush(uiState.Resource);
            }

            bool pendingChanged = _pending != _source.Pending;
            if (pendingChanged)
            {
                _suppress = true;
                _pending = _source.Pending;
                _suppress = false;
            }

            if (verdictChanged) OnChanged(nameof(Verdict));
            if (verdictChanged || force) OnChanged(nameof(BadgeBrush));
            if (detailChanged) OnChanged(nameof(PolicyDetail));
            if (detailChanged || metaChanged || statusChanged) OnChanged(nameof(RowDetail));
            if (canEditChanged) OnChanged(nameof(CanEdit));
            if (metaChanged) OnChanged(nameof(Meta));
            if (statusChanged) OnChanged(nameof(StatusText));
            if (statusChanged || statusBrushChanged || force) OnChanged(nameof(StatusBrush));
            if (pendingChanged) { OnChanged(nameof(Pending)); OnChanged(nameof(ToggleStateText)); OnChanged(nameof(DisplayPending)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null)
        {
            var handlers = PropertyChanged;
            if (handlers == null) return;

            var args = new PropertyChangedEventArgs(name);
            foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // WinUI virtualization can leave a native binding delegate attached
                    // briefly after its element has been recycled. Drop only that stale
                    // subscriber instead of aborting the whole row refresh.
                    PropertyChanged -= handler;
                }
                catch (ObjectDisposedException)
                {
                    PropertyChanged -= handler;
                }
            }
        }
    }

}