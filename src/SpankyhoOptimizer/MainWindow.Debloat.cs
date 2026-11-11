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

    private DebloatRowViewModel[] GetDebloatRows(IReadOnlyList<InstalledAppInfo> apps)
    {
        if (ReferenceEquals(_debloatRowsSnapshot, apps) && _debloatRowsCache != null) return _debloatRowsCache;
        var rows = apps.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(app => new DebloatRowViewModel(app,
                _debloatKeep.TryGetValue(app.PackageFullName, out var saved) ? saved : app.Protected || !app.Recommended,
                (vm, keep) => _debloatKeep[vm.Source.PackageFullName] = keep)).ToArray();
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
        int keepCount = 0;
        foreach (var vm in source)
        {
            _debloatKeep[vm.Source.PackageFullName] = vm.Keep;
            if (vm.Keep) keepCount++;
        }
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
            MaxHeight = 680,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = repeater
        };

        long listMs = perf.ElapsedMilliseconds - snapshotMs - rowsMs;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Doporučené odebrání", () => OpenDebloatKeepManagerAsync(_debloatActionToken)));
        actions.Children.Add(QuickButton("Ruční výběr", () => OpenDebloatManagerAsync(_debloatActionToken)));
        actions.Children.Add(QuickButton("Obnovit", () =>
        {
            InstalledAppService.InvalidateCache();
            InvalidateDebloatPageCache();
            return ShowSectionAsync("debloat");
        }));

        int recommended = 0;
        int protectedCount = 0;
        foreach (var app in apps)
        {
            if (app.Protected) protectedCount++;
            else if (app.Recommended) recommended++;
        }
        root.Children.Add(BuildModernSectionHero(
            "debloat",
            "Debloater",
            "Aplikace, seznam ponechaných položek a doporučené čištění s ochranou systémových balíků.",
            "SPRÁVCE APLIKACÍ",
            $"{apps.Count} aplikací • {recommended} doporučených • {protectedCount} chráněných",
            actions));
        root.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Aplikace", apps.Count.ToString(), "Nainstalované balíky"),
            BuildDashboardMetric("Doporučené", recommended.ToString(), "Bezpečné čištění", "SpankySuccessBrush"),
            BuildDashboardMetric("Chráněné", protectedCount.ToString(), "Nelze odstranit", "SpankyWarningBrush"),
            BuildDashboardMetric("Ponechat", keepCount.ToString(), "Chráněno před odebráním")));
        root.Children.Add(BuildSectionSurface("debloat", "Nainstalované aplikace", "Jednoduchý přehled bez zbytečných filtrů", list));
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

