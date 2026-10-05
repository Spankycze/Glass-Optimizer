using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
            ["update"] = new DeferredVirtualState(),
            ["browsers"] = new DeferredVirtualState()
        };

    private sealed record DeferredVirtualGroup(Panel Target, string Title, List<TweakVirtualRowViewModel> Rows);
    private sealed class DeferredVirtualState
    {
        public Queue<DeferredVirtualGroup> Groups { get; } = new();
        public DataTemplate? RowTemplate { get; set; }
        public int Generation { get; set; }
        public bool BatchQueued { get; set; }
    }

    private bool UseVirtualTweakRows() => _section is "system" or "privacy" or "update" or "browsers" || _tweaks.Count >= 24;

    private void ClearVirtualTweakRows()
    {
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
        "system" => 0,
        "privacy" => 0,
        "ui" => 0,
        "update" => 0,
        "browsers" => 0,
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
        var groups = _tweaks.GroupBy(t => t.Group).ToArray();
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

            if (section == "system")
            {
                phase = perf.ElapsedMilliseconds;
                ScheduleSystemOptimizationAdvisors(virtualRoot);
                advisorAttachMs = perf.ElapsedMilliseconds - phase;
            }

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
        else if (section == "browsers")
        {
            // Keep browser tweaks flat: the page already has its own browser dashboard,
            // so adding another expanded wrapper creates unnecessary nested cards.
            virtualAttachTarget = rootAttachTarget;
        }

        foreach (var group in groups)
        {
            var groupTweaks = group.ToArray();
            string groupTitle = $"{group.Key}";

            long phase = perf.ElapsedMilliseconds;
            var rows = new List<TweakVirtualRowViewModel>(groupTweaks.Length);
            foreach (var tweak in groupTweaks)
            {
                tweak.PropertyChanged -= Tweak_PropertyChanged;
                tweak.PropertyChanged += Tweak_PropertyChanged;
                var vm = new TweakVirtualRowViewModel(tweak, _tweakViewMode, _ => QueueDirtyStateRefresh(), sectionIconSource);
                rows.Add(vm); allRows.Add(vm); _virtualTweakRowMap[tweak] = vm;
            }
            vmMs += perf.ElapsedMilliseconds - phase;

            bool deferred = deferredState != null && groupIndex >= immediateGroups;
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

        LogService.Info("performance", $"VIRTUAL_UI_PHASE section={section} groups={groups.Length} rows={allRows.Count} deferred={deferredCount} setupMs={setupMs} clearRowsMs={clearRowsMs} deferredClearMs={deferredClearMs} resourceMs={resourceMs} iconMs={iconMs} groupMs={groupMs} dashboardMs={dashboardMs} advisorMs={advisorAttachMs} rootAttachMs={virtualRootAttachMs} headerMs={headerMs} vmMs={vmMs} repeaterMs={repeaterMs} expanderMs={expanderMs} attachMs={attachMs} finalizeMs={finalizeMs} scheduleMs={scheduleMs} totalMs={perf.ElapsedMilliseconds}");
    }

    private UIElement BuildVirtualSectionDashboard(string section)
    {
        if (UseGlassChrome)
        {
            VirtualDashboardMetricRefsBySection.Remove(section);
            return BuildNeutralSectionIntro(section == "system" ? "System Tweaks" : "Soukromí",
                "Nastavení Windows podle oblastí. Vybrané změny potvrdíte dole.");
        }
        int total = _tweaks.Count;
        int recommended = _tweaks.Count(x => x.Recommended);
        int loaded = _tweaks.Count(x => x.IsLoaded);
        int active = _tweaks.Count(x => x.IsLoaded && x.Current);
        int groups = _tweaks.Select(x => x.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();

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

        refs.ReadBackValue.Text = $"{loaded}/{total}";
        refs.ReadBackDetail.Text = loaded < total
            ? "Načítám skutečný stav…"
            : "Načtený skutečný stav";
        refs.ActiveValue.Text = loaded == 0 && total > 0 ? "…" : active.ToString();
        refs.ActiveDetail.Text = loaded < total
            ? "Načítám skutečný stav…"
            : "Potvrzené hodnoty";
    }

    private static UIElement CreateVirtualGroupHeader(string titleText)
    {
        // Lightweight card header: stronger hierarchy without Grid/ColumnDefinition cold cost.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new Border
        {
            Width = UseGlassChrome ? 0 : 4,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            Background = UseNeutralSectionChrome ? ThemeColor(128,128,128) : ButtonVioletBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = titleText,
            FontSize = UseGlassChrome ? 18 : 13.5,
            CharacterSpacing = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            Margin = new Thickness(0, 10, 0, 6),
            Padding = UseNeutralSectionChrome ? new Thickness(4,10,4,8) : new Thickness(11, 8, 11, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(UseNeutralSectionChrome ? 0 : (_darkTheme ? 1.2 : 1)),
            BorderBrush = SectionBorderBrush(),
            Background = UseGlassChrome ? ThemeColor(0,0,0,0) : SectionHeaderBackgroundBrush(),
            Child = row
        };
    }

    private static ItemsRepeater CreateVirtualGroupRepeater(List<TweakVirtualRowViewModel> rows, DataTemplate rowTemplate) =>
        new()
        {
            ItemsSource = rows,
            ItemTemplate = rowTemplate,
            Layout = new StackLayout { Spacing = 6 },
            HorizontalAlignment = HorizontalAlignment.Stretch
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
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22),
                Padding = new Thickness(18,10,18,10), VerticalAlignment = VerticalAlignment.Top,
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
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            state.BatchQueued = false;
            if (generation != state.Generation || _section != section || state.RowTemplate == null) return;
            var phase = Stopwatch.StartNew();
            int completed = 0;
            while (completed < 4 && state.Groups.Count > 0)
            {
                var item = state.Groups.Dequeue();
                var header = CreateVirtualGroupHeader(item.Title);
                var content = CreateVirtualGroupRepeater(item.Rows, state.RowTemplate);
                AttachVirtualGroup(item.Target, CreateVirtualGroupExpander(header, content, expanded: true));
                completed++;
                if (phase.ElapsedMilliseconds >= 3) break;
            }
            LogService.Info("performance", $"VIRTUAL_DEFERRED section={section} batch={completed} remaining={state.Groups.Count} ms={phase.ElapsedMilliseconds}");
            if (state.Groups.Count > 0) ScheduleDeferredVirtualGroups(section);
        });
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

        LogService.Info("performance", $"GPU_DEFERRED_TWEAK_SMOKE_OK renderMs={renderMs} cards={actual} reattached=true");
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

            RefreshClassicTweakCard(card);
            var grid = card.Child as Grid ?? throw new InvalidOperationException("Classic tweak card nemá Grid.");
            var toggle = grid.Children.OfType<ToggleSwitch>().FirstOrDefault()
                ?? throw new InvalidOperationException("Classic tweak card nemá ToggleSwitch.");
            if (toggle.IsOn != source.Pending)
                throw new InvalidOperationException("Classic toggle neodpovídá source.Pending po force-refreshi.");

            card.Opacity = 0;
            card.IsHitTestVisible = false;
            ContentStack.Children.Add(card);
            smokeCardAttached = true;
            card.UpdateLayout();
            toggle.ApplyTemplate();
            await Task.Yield();
            card.UpdateLayout();

            var track = FindNamedVisualDescendant<Border>(toggle, "SpankyTrack")
                ?? throw new InvalidOperationException("Global ToggleSwitch template nemá SpankyTrack.");
            var thumb = track.Child as Border
                ?? throw new InvalidOperationException("Global ToggleSwitch template nemá thumb Border.");
            var transform = thumb.RenderTransform as TranslateTransform
                ?? throw new InvalidOperationException("Global ToggleSwitch template nemá TranslateTransform palce.");

            if (!VisualStateManager.GoToState(toggle, "Off", false) || Math.Abs(transform.X) > 0.01)
                throw new InvalidOperationException("ToggleSwitch OFF vizuální stav neposunul palec doleva.");
            var offBrush = track.Background as SolidColorBrush
                ?? throw new InvalidOperationException("ToggleSwitch OFF track nemá SolidColorBrush.");
            string offColor = offBrush.Color.ToString();

            if (!VisualStateManager.GoToState(toggle, "On", false) || Math.Abs(transform.X - 20) > 0.01)
                throw new InvalidOperationException("ToggleSwitch ON vizuální stav neposunul palec doprava.");
            var onBrush = track.Background as SolidColorBrush
                ?? throw new InvalidOperationException("ToggleSwitch ON track nemá SolidColorBrush.");
            string onColor = onBrush.Color.ToString();
            if (string.Equals(offColor, onColor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ToggleSwitch ON/OFF vizuální stav nemění barvu tracku.");
        }
        finally
        {
            if (smokeCardAttached)
                ContentStack.Children.Remove(card);
            source.Pending = originalPending;
            vm.Refresh(_tweakViewMode, force: true);
            RefreshClassicTweakCard(card);
        }

        LogService.Info("ui", "TOGGLE_STATE_CONSISTENCY_SMOKE_OK virtual=true classic=true visual=true");
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
        private bool _suppress;
        private string _verdict = string.Empty;
        private string _meta = string.Empty;
        private string _policyDetail = string.Empty;
        private bool _canEdit;
        private Brush _badgeBrush = AccentBrush();
        private string _statusText = string.Empty;
        private Brush _statusBrush = AccentBrush();
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

        public TweakVirtualRowViewModel(TweakDefinition source, TweakViewMode mode, Action<TweakDefinition> changed, ImageSource iconSource)
        {
            _source = source; _changed = changed; _mode = mode;
            HeaderVisibility = Visibility.Collapsed;
            CardVisibility = Visibility.Visible;
            Name = source.Name; Description = source.Description;
            RestartVisibility = source.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
            IconSource = iconSource;
            Refresh(mode);
        }

        public static TweakVirtualRowViewModel Header(string text) => new(text);
        public TweakDefinition? Source => _source;
        public string HeaderText { get; } = string.Empty;
        public Visibility HeaderVisibility { get; }
        public Visibility CardVisibility { get; }
        public string Name { get; } = string.Empty;
        public string Description { get; } = string.Empty;
        public Visibility RestartVisibility { get; }
        public ImageSource? IconSource { get; }
        public Brush CardBackground => _darkTheme && (_source?.Section is "system" or "privacy") ? ThemeColor(0,0,0,0) : SectionCardBackgroundBrush();
        public Brush CardBorder => SectionBorderBrush();
        public Brush TextPrimary => TextPrimaryBrush();
        public Brush TextSecondary => TextSecondaryBrush();
        public Brush Accent => AccentBrush();
        public Brush AccentSoft => AccentSoftBrush();
        public string Verdict => _verdict;
        public string Meta => _meta;
        public string PolicyDetail => _policyDetail;
        public Brush BadgeBrush => _badgeBrush;
        public string StatusText => _statusText;
        public Brush StatusBrush => _statusBrush;
        public bool CanEdit => _canEdit;
        public string ToggleStateText => _pending ? "ON" : "OFF";

        public bool Pending
        {
            get => _pending;
            set
            {
                if (_pending == value) return;
                _pending = value; OnChanged(); OnChanged(nameof(ToggleStateText));
                if (_suppress || _source == null) return;
                var live = TweakPolicyService.Evaluate(_source, _mode);
                if (!live.CanEdit || !_source.IsLoaded)
                {
                    _pending = _source.Pending; OnChanged(); return;
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

            if (verdictChanged)
            {
                _verdict = newVerdict;
                _badgeBrush = UiBrush(newVerdict == "Recommended"
                    ? "SpankySuccessBrush"
                    : newVerdict is "Advanced" or "Needs attention"
                        ? "SpankyWarningBrush" : "SpankyAccentBrush");
            }
            if (detailChanged) _policyDetail = newPolicyDetail;
            if (canEditChanged) _canEdit = newCanEdit;
            if (metaChanged) _meta = newMeta;

            var uiState = TweakUiState(_source);
            string newStatusText = uiState.Text;
            Brush newStatusBrush = UiBrush(uiState.Resource);
            bool statusChanged = !string.Equals(_statusText, newStatusText, StringComparison.Ordinal);
            if (statusChanged)
            {
                _statusText = newStatusText;
                _statusBrush = newStatusBrush!;
            }

            bool pendingChanged = _pending != _source.Pending;
            if (pendingChanged)
            {
                _suppress = true;
                _pending = _source.Pending;
                _suppress = false;
            }

            if (verdictChanged) { OnChanged(nameof(Verdict)); OnChanged(nameof(BadgeBrush)); }
            if (detailChanged) OnChanged(nameof(PolicyDetail));
            if (canEditChanged) OnChanged(nameof(CanEdit));
            if (metaChanged) OnChanged(nameof(Meta));
            if (statusChanged) { OnChanged(nameof(StatusText)); OnChanged(nameof(StatusBrush)); }
            if (pendingChanged) { OnChanged(nameof(Pending)); OnChanged(nameof(ToggleStateText)); }
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