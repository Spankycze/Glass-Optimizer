using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;
using SpankyhoOptimizer.ViewModels;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static DataTemplate DebloatRowTemplate() => UiResourceService.Get<DataTemplate>("DebloatRowLogoTemplateV151");
    private IReadOnlyList<InstalledAppInfo>? _debloatRowsSnapshot;
    private DebloatRowViewModel[]? _debloatRowsCache;
    private CancellationToken _debloatActionToken;
    private Action? _debloatSelectionChanged;

    private DebloatRowViewModel[] GetDebloatRows(IReadOnlyList<InstalledAppInfo> apps)
    {
        if (ReferenceEquals(_debloatRowsSnapshot, apps) && _debloatRowsCache != null) return _debloatRowsCache;
        var rows = apps.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(app => new DebloatRowViewModel(app,
                _debloatKeep.TryGetValue(app.PackageFullName, out var saved) ? saved : app.Protected || !app.Recommended,
                (vm, keep) =>
                {
                    _debloatKeep[vm.Source.PackageFullName] = keep;
                    _debloatSelectionChanged?.Invoke();
                })).ToArray();
        _debloatRowsSnapshot = apps;
        return _debloatRowsCache = rows;
    }

    private static bool AreDebloatSummariesEquivalent(
        IReadOnlyList<InstalledAppInfo> left, IReadOnlyList<InstalledAppInfo> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    internal static void RunDebloatSummaryEqualitySmoke()
    {
        var sample = new InstalledAppInfo(
            "Smoke.A", "Smoke.A_1_x64__test", "Smoke.A_test",
            "App A", "Test publisher", null, false,
            DebloatCategory.ThirdParty, DebloatRisk.Optional, false, "Optional");
        InstalledAppInfo[] first = [sample];
        InstalledAppInfo[] equal = [sample with { }];
        if (!AreDebloatSummariesEquivalent(first, equal) ||
            !AreDebloatSummariesEquivalent(first, first) ||
            AreDebloatSummariesEquivalent(first, Array.Empty<InstalledAppInfo>()) ||
            AreDebloatSummariesEquivalent(first, [sample with { Publisher = "Changed publisher" }]) ||
            AreDebloatSummariesEquivalent(first, [sample with { Recommended = true }]) ||
            AreDebloatSummariesEquivalent(first, [sample with { Protected = true }]))
            throw new InvalidOperationException("Debloat summary equality missed metadata/selection drift.");
        var row = new DebloatRowViewModel(sample, keep: true, (_, _) => { });
        if (row.Remove || row.StateText != "PONECHAT")
            throw new InvalidOperationException("Debloat remove checkbox started inverted.");
        row.Remove = true;
        if (row.Keep || row.StateText != "ODEBRAT")
            throw new InvalidOperationException("Debloat remove checkbox did not invert the keep state.");
        LogService.Info("debloat",
            "DEBLOAT_SUMMARY_EQUALITY_SMOKE_OK unchanged=true publisherChange=true recommendedChange=true protectedChange=true countChange=true");
    }

    private void QueueDebloatRerender(CancellationToken token, int generation, string operation)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "debloat") return;
            InvalidateDebloatPageCache();
            SafeTaskService.Forget(ShowSectionAsync("debloat"), "debloat", operation);
        });
    }

    private async Task LoadDebloatAfterFirstFrameAsync(CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "debloat")
                return;
            await LoadDebloatAndRefreshAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadDebloatAndRefreshAsync(CancellationToken token, bool forceRefresh = false)
    {
        int generation = _navigationGeneration;
        try
        {
            if (!forceRefresh)
            {
                bool hydrated = await InstalledAppService.TryWarmDisplaySnapshotAsync(token).ConfigureAwait(false);
                if (hydrated)
                {
                    // The hydrated snapshot is now in memory. Render it once:
                    // BuildDebloatPageAsync schedules one deferred forced scan
                    // when it is stale. Starting another scan here caused two
                    // overlapping inventory requests and duplicate UI rebuilds.
                    if (!token.IsCancellationRequested && _section == "debloat")
                        QueueDebloatRerender(token, generation, "Debloat display-cache rerender");
                    return;
                }
            }

            var cached = forceRefresh ? null : InstalledAppService.TryGetMemorySnapshot(includeLogos: false);
            if (cached == null || InstalledAppService.SummaryNeedsBackgroundRefresh)
                await InstalledAppService.GetInstalledAsync(token, forceRefresh: true, includeLogos: false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_section == "debloat")
                QueueDebloatRerender(token, generation, "Debloat inventory rerender");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("Debloat inventory failed", ex); }
    }

    private async Task EnrichDebloatLogosAsync(DebloatRowViewModel[] rows, CancellationToken token)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var detailed = await Task.Run(
                () => InstalledAppService.TryGetFreshSnapshot(includeLogos: true),
                token).ConfigureAwait(false);
            bool persistentHit = detailed != null;
            detailed ??= await InstalledAppService.GetInstalledAsync(token, includeLogos: true).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            int applied = ApplyDebloatLogos(rows, detailed);
            LogService.Info("performance",
                $"DEBLOAT_LOGO_ENRICH persistentHit={persistentHit} count={detailed.Count} applied={applied} ms={sw.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("debloat", "Deferred logo enrichment failed", ex); }
    }

    private async Task RefreshDebloatDetailsAfterIdleAsync(
        DebloatRowViewModel[] rows,
        IReadOnlyList<InstalledAppInfo> displayedSnapshot,
        CancellationToken token,
        int generation)
    {
        try
        {
            await Task.Delay(650, token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "debloat") return;
            await RefreshDebloatDetailsAndUiAsync(rows, displayedSnapshot, token, generation);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshDebloatDetailsAndUiAsync(
        DebloatRowViewModel[] rows,
        IReadOnlyList<InstalledAppInfo> displayedSnapshot,
        CancellationToken token,
        int generation)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var freshSummary = await InstalledAppService.GetInstalledAsync(
                token, forceRefresh: true, includeLogos: false);
            token.ThrowIfCancellationRequested();

            // Package-ID equality alone misses renamed apps, publisher/risk
            // changes and recommended/protected status changes. The scanner
            // emits stable display-name order, so compare complete records.
            // A truly unchanged inventory should not tear down the entire
            // ItemsRepeater and recreate every Debloat row.
            bool inventoryChanged = !AreDebloatSummariesEquivalent(displayedSnapshot, freshSummary);

            if (inventoryChanged)
            {
                LogService.Info("debloat",
                    $"Deferred summary refresh changed inventory old={displayedSnapshot.Count} new={freshSummary.Count} ms={sw.ElapsedMilliseconds}");
                if (generation == _navigationGeneration && _section == "debloat")
                    QueueDebloatRerender(token, generation, "Deferred Debloat inventory rerender");
                return;
            }

            long summaryMs = sw.ElapsedMilliseconds;
            await EnrichDebloatLogosAsync(rows, token);
            LogService.Info("debloat",
                $"Deferred summary refresh unchanged count={freshSummary.Count} summaryMs={summaryMs} totalMs={sw.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("debloat", "Deferred one-pass detail refresh failed", ex); }
    }

    private static int ApplyDebloatLogos(
        DebloatRowViewModel[] rows,
        IReadOnlyList<InstalledAppInfo> detailed)
    {
        var logos = detailed
            .Where(x => !string.IsNullOrWhiteSpace(x.LogoPath))
            .GroupBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().LogoPath!, StringComparer.OrdinalIgnoreCase);

        int applied = 0;
        foreach (var row in rows)
            if (logos.TryGetValue(row.Source.PackageName, out var logo) && row.SetIconPath(logo))
                applied++;

        LogService.Info("debloat", $"Deferred logo enrichment applied={applied}/{rows.Length} resolved={logos.Count}");
        return applied;
    }

    private async Task BuildDebloatPageAsync(CancellationToken token)
    {
        _debloatActionToken = token;
        var perf = System.Diagnostics.Stopwatch.StartNew();
        var root = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Prefer the lightweight summary snapshot for first paint. Persistent logo-map
        // hydration performs disk I/O and File.Exists checks and belongs in the deferred enrichment path.
        var apps = InstalledAppService.TryGetMemorySnapshot(includeLogos: false);
        long snapshotMs = perf.ElapsedMilliseconds;
        if (apps == null)
        {
            Microsoft.UI.Xaml.FrameworkElement loadingHero;
            if (_prewarmedDebloatLoadingHero != null &&
                _prewarmedDebloatThemePreference == _themePreference)
            {
                loadingHero = _prewarmedDebloatLoadingHero;
                _prewarmedDebloatLoadingHero = null;
                _prewarmedDebloatThemePreference = null;
            }
            else
            {
                loadingHero = BuildModernSectionHero(
                    "debloat",
                    "Debloater",
                    "Bezpečný správce aplikací s doporučeným výběrem, seznamem ponechaných aplikací a chráněnými balíky.",
                    "ČIŠTĚNÍ APLIKACÍ",
                    "Načítám nainstalované aplikace…");
            }

            root.Children.Add(loadingHero);
            ContentStack.Children.Add(root);
            int coldGeneration = _navigationGeneration;
            SafeTaskService.Forget(LoadDebloatAfterFirstFrameAsync(token, coldGeneration), "debloat", "Load installed apps");
            return;
        }

        token.ThrowIfCancellationRequested();
        var source = GetDebloatRows(apps);
        foreach (var vm in source)
            _debloatKeep[vm.Source.PackageFullName] = vm.Keep;
        long rowsMs = perf.ElapsedMilliseconds - snapshotMs;

        var repeater = new ItemsRepeater
        {
            ItemsSource = source,
            ItemTemplate = DebloatRowTemplate(),
            Layout = new StackLayout { Spacing = 0 },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var list = new ScrollViewer
        {
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = repeater
        };

        int filterMode = 0;
        string query = string.Empty;
        var filterButtons = new Button[3];
        var filterStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var countText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var search = new TextBox
        {
            PlaceholderText = "Hledat aplikaci…",
            MinWidth = 210,
            MaxWidth = 260,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 4, 12, 4),
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush()
        };
        search.HorizontalAlignment = HorizontalAlignment.Right;
        var removeButton = PrimaryQuickButton("Odebrat vybrané", () => RemoveSelectedDebloatAsync(source, token));
        removeButton.MinWidth = 170;

        bool Matches(DebloatRowViewModel row)
        {
            if (filterMode == 0 && !row.Source.Recommended) return false;
            if (filterMode == 2 && !row.Remove) return false;
            if (query.Length == 0) return true;
            return row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   row.Source.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   row.Source.Reason.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        void UpdateFilterButtons()
        {
            int selected = source.Count(x => x.Remove && IsDebloatSelectable(x.Source));
            filterButtons[0].Content = $"Doporučené  {source.Count(x => x.Source.Recommended)}";
            filterButtons[1].Content = $"Vše  {source.Length}";
            filterButtons[2].Content = $"Vybrané  {selected}";
            for (int i = 0; i < filterButtons.Length; i++)
            {
                bool on = i == filterMode;
                filterButtons[i].Background = on ? AccentSoftBrush() : SectionCardBackgroundBrush();
                filterButtons[i].BorderBrush = on ? AccentBrush() : SectionBorderBrush();
                filterButtons[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                filterButtons[i].Opacity = on ? 1 : .82;
            }
        }

        void UpdateCount()
        {
            int visible = source.Count(Matches);
            int selected = source.Count(x => x.Remove && IsDebloatSelectable(x.Source));
            countText.Text = $"Zobrazeno {visible} z {source.Length} • vybráno {selected}";
            removeButton.IsEnabled = selected > 0;
            UpdateFilterButtons();
        }

        void RefreshList()
        {
            repeater.ItemsSource = source.Where(Matches).ToArray();
            UpdateCount();
        }

        Button MakeFilterButton(int mode)
        {
            var button = new Button { MinHeight = 34 };
            ApplyPillButtonAppearance(button);
            button.Padding = new Thickness(12, 5, 12, 5);
            button.Click += (_, _) =>
            {
                filterMode = mode;
                RefreshList();
            };
            filterButtons[mode] = button;
            return button;
        }

        filterStrip.Children.Add(MakeFilterButton(0));
        filterStrip.Children.Add(MakeFilterButton(1));
        filterStrip.Children.Add(MakeFilterButton(2));
        search.TextChanged += (_, _) =>
        {
            query = search.Text?.Trim() ?? string.Empty;
            RefreshList();
        };

        var filterBar = new Grid { ColumnSpacing = 10 };
        filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterBar.Children.Add(filterStrip);
        Grid.SetColumn(search, 1);
        filterBar.Children.Add(search);

        var summary = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        };
        var summaryHint = new TextBlock
        {
            Text = "Před odebráním se vytvoří bod obnovení.",
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var summaryStack = new StackPanel { Spacing = 2 };
        summaryStack.Children.Add(summary);
        summaryStack.Children.Add(summaryHint);
        var footerGrid = new Grid { ColumnSpacing = 12 };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(summaryStack);
        Grid.SetColumn(removeButton, 1);
        removeButton.VerticalAlignment = VerticalAlignment.Center;
        footerGrid.Children.Add(removeButton);
        var footer = new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Child = footerGrid
        };

        _debloatSelectionChanged = () =>
        {
            int selected = source.Count(x => x.Remove && IsDebloatSelectable(x.Source));
            summary.Text = selected == 0 ? "Zatím nic nevybráno" : $"Vybráno {selected} aplikací";
            UpdateCount();
        };
        int recommended = apps.Count(x => x.Recommended);
        int protectedCount = apps.Count(x => x.Protected);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Obnovit", () =>
        {
            InstalledAppService.InvalidateCache();
            InvalidateDebloatPageCache();
            return ShowSectionAsync("debloat");
        }));
        root.Children.Add(BuildModernSectionHero(
            "debloat",
            "Debloater",
            "Zaškrtni, co chceš odebrat. Doporučené položky jsou předvybrané a nic se neodebere bez potvrzení.",
            "APLIKACE K ODEBRÁNÍ",
            $"{apps.Count} aplikací • {recommended} doporučených • {protectedCount} chráněných",
            actions));

        var body = new StackPanel { Spacing = 9 };
        body.Children.Add(filterBar);
        body.Children.Add(countText);
        body.Children.Add(list);
        body.Children.Add(footer);
        root.Children.Add(BuildSectionSurface(
            "debloat",
            "Aplikace k odebrání",
            "Zaškrtnuté položky se odeberou až po potvrzení a vytvoření bodu obnovení.",
            body));
        _debloatSelectionChanged();
        RefreshList();
        long listMs = perf.ElapsedMilliseconds - snapshotMs - rowsMs;
        ContentStack.Children.Add(root);
        long totalMs = perf.ElapsedMilliseconds;
        long composeMs = totalMs - snapshotMs - rowsMs - listMs;
        if (totalMs >= 15)
            LogService.Info("performance", $"DEBLOAT_UI_PHASE snapshotMs={snapshotMs} rowsMs={rowsMs} listMs={listMs} composeMs={composeMs} totalMs={totalMs} apps={apps.Count}");

        if (InstalledAppService.SummaryNeedsBackgroundRefresh)
        {
            SafeTaskService.Forget(
                RefreshDebloatDetailsAfterIdleAsync(source, apps, token, _navigationGeneration),
                "debloat",
                "Refresh persisted installed-app snapshot + logos");
        }
        else
        {
            SafeTaskService.Forget(
                EnrichDebloatLogosAsync(source, token),
                "debloat",
                "Deferred installed-app logo enrichment");
        }
    }
}

