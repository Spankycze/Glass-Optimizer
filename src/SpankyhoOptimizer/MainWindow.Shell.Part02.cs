using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async System.Threading.Tasks.Task ScheduleDeferredSidebarAfterFirstRenderAsync()
    {
        // Each WinUI button and SVG costs native initialization. Building all
        // 21 within ONE UI callback blocked the dispatcher for 35-57 ms.
        // Prepare time-budgeted offscreen batches, then attach the complete
        // menu atomically. Normal-priority small turns avoid the 200ms delay
        // caused by starving low-priority batches behind background UI tasks;
        // no partially built navigation controls become visible.
        await System.Threading.Tasks.Task.Yield();
        if (_windowLifetimeCts.IsCancellationRequested || _mainTabsCompleted) return;

        var root = RootGrid;
        var items = GetAvailableMainSectionItems();
        var prepared = new StackPanel { Spacing = CategoryBar.Spacing };
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Yield above has already placed us on a post-first-render UI turn.
        // Preparing the first TextBlock here (12-16ms native cold start)
        // keeps it separate from the button batch without paying for an
        // additional DispatcherQueue turn. The complete menu still attaches
        // atomically only after all buttons have been prepared.
        var labelWarm = System.Diagnostics.Stopwatch.StartNew();
        var firstLabel = CreateMainSidebarLabel(items[0].Title);
        long labelMs = labelWarm.ElapsedMilliseconds;
        if (labelMs >= DeferredSidebarBatchBudgetMs)
            LogService.Info("performance", $"SIDEBAR_FIRST_LABEL_PREPARED ms={labelMs}");
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => BuildDeferredSidebarBatch(root, prepared, items, 0, phase, tcs, firstLabel)))
            return;

        _ = await tcs.Task.ConfigureAwait(false);
    }

    private void BuildDeferredSidebarBatch(
        Grid root, StackPanel prepared, (string Title, string Section)[] items, int start,
        System.Diagnostics.Stopwatch phase, TaskCompletionSource<bool> done,
        TextBlock? firstPreparedLabel = null)
    {
        try
        {
            // Theme reconstruction may replace RootGrid before a queued batch runs.
            // Never attach old-theme buttons or mutate the new shell.
            if (_windowLifetimeCts.IsCancellationRequested ||
                !ReferenceEquals(RootGrid, root) || _mainTabsCompleted)
            {
                done.TrySetResult(false);
                return;
            }

            var batch = System.Diagnostics.Stopwatch.StartNew();
            // Native WinUI SVG control creation has a cold-start spike on
            // the first item. Never put more than three controls or more than
            // the 8ms soft budget into the same dispatcher callback.
            int end = start;
            while (end < items.Length && end - start < DeferredSidebarBatchSize)
            {
                var item = items[end];
                AddMainSection(prepared, item.Title, item.Section,
                    end == 0 ? firstPreparedLabel : null);
                end++;
                if (batch.ElapsedMilliseconds >= DeferredSidebarBatchBudgetMs)
                    break;
            }
            long batchMs = batch.ElapsedMilliseconds;
            _deferredSidebarBatchCount++;
            _deferredSidebarLongestBatchMs = Math.Max(_deferredSidebarLongestBatchMs, batchMs);
            // Log only an actual dispatcher hitch; the final aggregate
            // already records the full sidebar build and slowest batch.
            if (batchMs >= DeferredSidebarBatchBudgetMs)
                LogService.Info("performance",
                    $"SIDEBAR_DEFERRED_BATCH start={start} added={end - start} ms={batchMs}");

            if (end < items.Length)
            {
                if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                    () => BuildDeferredSidebarBatch(root, prepared, items, end, phase, done)))
                    done.TrySetResult(false);
                return;
            }

            // Navigation can change while offscreen batches are being built.
            // Correct only stale selected visuals rather than repainting all
            // 21 buttons and triggering native animations unnecessarily.
            foreach (var entry in _categoryButtons)
            {
                bool shouldBeActive = string.Equals(entry.Key, _section, StringComparison.OrdinalIgnoreCase);
                if ((entry.Value.BorderThickness.Left > 0) != shouldBeActive)
                    SetCategoryButtonVisualState(entry.Value, shouldBeActive);
            }

            AttachCompletedMainTabs(prepared, phase, phase.ElapsedMilliseconds);
            done.TrySetResult(true);
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    private void CompleteDeferredMainTabs()
    {
        // Theme replacement must still complete synchronously: its caller
        // immediately applies theme resources and restores navigation.
        if (_mainTabsCompleted)
        {
            EnsureSidebarScrollHost();
            return;
        }

        var phase = System.Diagnostics.Stopwatch.StartNew();
        var prepared = new StackPanel { Spacing = CategoryBar.Spacing };
        foreach (var item in GetAvailableMainSectionItems())
            AddMainSection(prepared, item.Title, item.Section);
        AttachCompletedMainTabs(prepared, phase, phase.ElapsedMilliseconds);
    }

    private void AttachCompletedMainTabs(
        StackPanel prepared, System.Diagnostics.Stopwatch phase, long prepareMs)
    {
        CategoryBar = prepared;
        EnsureSidebarScrollHost();
        long attachMs = phase.ElapsedMilliseconds - prepareMs;
        _mainTabsCompleted = true;
        _activeCategory = _section;
        LogService.Info("performance",
            $"SIDEBAR_DEFERRED items={CategoryBar.Children.Count} ms={phase.ElapsedMilliseconds} prepareMs={prepareMs} attachMs={attachMs} atomic=true batches={_deferredSidebarBatchCount} maxBatchMs={_deferredSidebarLongestBatchMs}");
    }

    internal async System.Threading.Tasks.Task RunSidebarDeferredSmokeAsync()
    {
        for (int i = 0; i < 40 && !_mainTabsCompleted; i++)
            await System.Threading.Tasks.Task.Delay(25);
        // The startup Overview metadata must never silently diverge from the
        // canonical catalog; assert record value equality after the first
        // frame when the catalog is allowed to initialize during smoke.
        if (StartupOverviewSection != CatalogService.GetSection("overview"))
            throw new InvalidOperationException("Startup Overview metadata drifted from the catalog.");
        if (StartupSystemSection != CatalogService.GetSection("system"))
            throw new InvalidOperationException("Fast System metadata drifted from the catalog.");
        var items = GetAvailableMainSectionItems();
        int expectedSidebarItems = items.Length;
        // A cold asset decode may end a batch early, so the number of turns
        // is intentionally adaptive while each turn remains small.
        if (_deferredSidebarBatchCount != 0 &&
            (_deferredSidebarBatchCount < (expectedSidebarItems + DeferredSidebarBatchSize - 1) / DeferredSidebarBatchSize ||
             _deferredSidebarBatchCount > expectedSidebarItems ||
             _deferredSidebarLongestBatchMs <= 0))
            throw new InvalidOperationException(
                $"Deferred sidebar budgeted batching failed: batches={_deferredSidebarBatchCount} maxMs={_deferredSidebarLongestBatchMs}.");
        if (!_mainTabsCompleted || CategoryBar.Children.Count != expectedSidebarItems || _categoryButtons.Count != expectedSidebarItems)
            throw new InvalidOperationException($"Deferred sidebar incomplete: completed={_mainTabsCompleted} children={CategoryBar.Children.Count} buttons={_categoryButtons.Count} expected={expectedSidebarItems}.");

        if (_mainTabsShell?.Child is not ScrollViewer { VerticalScrollMode: ScrollMode.Enabled, VerticalScrollBarVisibility: ScrollBarVisibility.Hidden } scrollHost ||
            !ReferenceEquals(scrollHost.Content, CategoryBar))
            throw new InvalidOperationException("Sidebar scrolling host/atomic attachment invariant broken.");

        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Button button in _categoryButtons.Values)
        {
            if (button.Tag is not string section || button.Content is not StackPanel row || row.Children.Count == 0 || row.Children[0] is not Grid)
                throw new InvalidOperationException("Sidebar icon smoke expected custom SVG host as the first menu element.");
            if (!button.IsEnabled || !button.IsHitTestVisible || row.IsHitTestVisible)
                throw new InvalidOperationException($"Sidebar hit-test invariant broken for {section}: enabled={button.IsEnabled} buttonHit={button.IsHitTestVisible} childHit={row.IsHitTestVisible}.");
            if (button.ClickMode != ClickMode.Release)
                throw new InvalidOperationException($"Sidebar click mode invariant broken for {section}: {button.ClickMode}.");
            // New sidebar buttons must be born at the final geometry. A
            // post-construction resize costs native layout work for all 21
            // elements and can cause a visibly inconsistent first frame.
            if (row.Children.Count < 2 || row.Children[0] is not Grid iconHost ||
                row.Children[1] is not TextBlock navLabel ||
                iconHost.Children.Count < 2 || iconHost.Children[1] is not Image svgIcon ||
                iconHost.Width != 30 || iconHost.Height != 30 ||
                svgIcon.Width != 28 || svgIcon.Height != 28 ||
                button.MinHeight != (_darkTheme ? 48 : 36) ||
                button.CornerRadius.TopLeft != (_darkTheme ? 20 : 16) ||
                button.Padding.Top != (_darkTheme ? 6 : 3) ||
                row.Spacing != (_darkTheme ? 11 : 9) ||
                navLabel.Tag is not string { Length: > 0 } fontTag ||
                fontTag != "UiNavigationSize" ||
                navLabel.FontSize != UiFontSize("UiNavigationSize") ||
                navLabel.MaxWidth != (_darkTheme ? 122 : 112))
                throw new InvalidOperationException($"Sidebar final-layout invariant broken for {section}.");
            string expectedTitle = items.First(x => x.Section == section).Title;
            if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) != expectedTitle ||
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) != "sidebar-" + section)
                throw new InvalidOperationException($"Sidebar UIAutomation identity missing or incorrect for {section}.");
            bool selected = string.Equals(section, _section, StringComparison.OrdinalIgnoreCase);
            if ((button.BorderThickness.Left > 0) != selected)
                throw new InvalidOperationException($"Sidebar initial selection border mismatch for {section}.");
            if (!selected && (button.Shadow != null || button.Translation.Z != 0))
                throw new InvalidOperationException($"Sidebar inactive button retained shadow/translation: {section}.");
            assets.Add(SidebarIconName(section));
        }
        int expectedUniqueAssets = items.Select(x => SidebarIconName(x.Section)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (assets.Count != expectedUniqueAssets)
            throw new InvalidOperationException($"Sidebar icon coverage mismatch: {assets.Count}/{expectedUniqueAssets} unique assets for {expectedSidebarItems} sections.");
        if (!ReferenceEquals(SidebarIconSource("gpu"), SidebarIconSource("gpu")) ||
            !ReferenceEquals(SidebarIconSource("network"), SidebarIconSource("network")) ||
            !ReferenceEquals(SidebarIconSource("gpu"), SectionIconSource("gpu")) ||
            !ReferenceEquals(SidebarIconSource("overview"), SectionIconSource("overview")))
            throw new InvalidOperationException("Shared SVG source cache did not reuse image sources across sidebar/section surfaces.");

        foreach (string asset in HardwareIconMap.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SidebarIcons", asset + ".svg");
            if (!System.IO.File.Exists(path))
                throw new InvalidOperationException("Mapped icon asset is missing: " + asset + ".svg");
        }
        if (!ReferenceEquals(SectionIconSource("activity"), SectionIconSource("overview")) ||
            !ReferenceEquals(SectionIconSource("restore"), SectionIconSource("update")))
            throw new InvalidOperationException("Utility icon alias/source cache mismatch.");

        LogService.Info($"SIDEBAR_DEFERRED_SMOKE_OK uniqueAssets={assets.Count} sections={expectedSidebarItems} style=CustomSvg sourceCache=true utilityAssetsVerified=true hitTestOwner=button clickMode=release");
    }

    internal async System.Threading.Tasks.Task RunHeroActionDeferredSmokeAsync()
    {
        for (int i = 0; i < 40 && !_heroActionButtonReady; i++)
            await System.Threading.Tasks.Task.Delay(25);
        if (!_heroActionButtonReady)
            throw new InvalidOperationException("Deferred hero action Button did not replace its startup placeholder.");
        LogService.Info("HERO_ACTION_DEFERRED_SMOKE_OK");
    }

    internal async System.Threading.Tasks.Task RunLazyActionBarSmokeAsync()
    {
        await System.Threading.Tasks.Task.Delay(350);
        if (_actionBarInitialized)
            throw new InvalidOperationException("Action bar was eagerly initialized during startup.");

        UpdateDirtyState();
        if (_actionBarInitialized)
            throw new InvalidOperationException("Action bar initialized even though there are no dirty tweaks.");
        if (ActionBar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Action bar placeholder should stay collapsed when there are no dirty tweaks.");

        var attach = System.Diagnostics.Stopwatch.StartNew();
        EnsureActionBarInitialized();
        long attachMs = attach.ElapsedMilliseconds;
        if (!_actionBarInitialized || ActionBar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("On-demand action bar factory failed to initialize a collapsed bar.");
        if (attachMs > 25)
            throw new InvalidOperationException($"On-demand action bar initialization is too slow: {attachMs} ms.");

        ActionBar.Visibility = Visibility.Visible;
        try
        {
            RootGrid.UpdateLayout();
            if (ActionBar.ActualHeight <= 0 || ActionBar.ActualHeight > 58)
                throw new InvalidOperationException($"Action bar must remain compact: height={ActionBar.ActualHeight}.");
        }
        finally { ActionBar.Visibility = Visibility.Collapsed; }

        LogService.Info("ACTIONBAR_LAZY_SMOKE_OK cleanStateSkipped=true onDemand=true factoryVerified=true attachMs=" + attachMs);
    }

    private TextBlock CreateMainSidebarLabel(string title) => new()
    {
        Text = title, Tag = "UiNavigationSize", FontSize = UiFontSize("UiNavigationSize"),
        FontWeight = FontWeights.SemiBold,
        Foreground = _darkTheme ? ThemeColor(232,232,232) : TextPrimaryBrush(),
        TextWrapping = TextWrapping.Wrap, MaxWidth = _darkTheme ? 122 : 112,
        MaxLines = 2, VerticalAlignment = VerticalAlignment.Center
    };

    private void AddMainSection(StackPanel host, string title, string section, TextBlock? preparedLabel = null)
    {
        // First sidebar entry frequently takes 23-39 ms during cold native
        // control creation. Resolve the expensive phase only during smoke
        // or explicit performance diagnostics, not in normal navigation.
        var firstIconProfile = LogService.PerformanceDiagnosticsEnabled &&
            string.Equals(section, "overview", StringComparison.OrdinalIgnoreCase)
            ? System.Diagnostics.Stopwatch.StartNew() : null;
        // PERF/UI INVARIANT: the Button must own all pointer input. Child visuals are display-only,
        // otherwise future icon/hover work can silently break sidebar navigation hit-testing.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = _darkTheme ? 11 : 9,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        long rowMs = firstIconProfile?.ElapsedMilliseconds ?? 0;
        var menuIcon = SidebarIcon(section, 22, navigation: true);
        long iconMs = firstIconProfile?.ElapsedMilliseconds ?? 0;
        menuIcon.Margin = new Thickness(0, 0, 1, 0);
        row.Children.Add(menuIcon);
        long iconAttachMs = firstIconProfile?.ElapsedMilliseconds ?? 0;
        var navLabel = preparedLabel ?? CreateMainSidebarLabel(title);
        long labelCreateMs = firstIconProfile?.ElapsedMilliseconds ?? 0;
        row.Children.Add(navLabel);
        long rowContentMs = firstIconProfile?.ElapsedMilliseconds ?? 0;
        var button = new Button
        {
            Tag = section,
            Content = row,
            MinHeight = _darkTheme ? 48 : 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = _darkTheme ? new Thickness(8, 6, 8, 6) : new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(_darkTheme ? 20 : 16),
            Background = SidebarButtonBackground(active: false),
            BorderBrush = SidebarButtonBorder(active: false),
            BorderThickness = new Thickness(0),
            Foreground = _darkTheme ? ThemeColor(192,192,192) : TextSecondaryBrush(),
            IsHitTestVisible = true,
            IsEnabled = true
        };
        long buttonMs = firstIconProfile?.ElapsedMilliseconds ?? 0;
        // Stable UI Automation identity for sidebar navigation regardless of visual layout.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "sidebar-" + section);
        // Share two instance handlers across the entire sidebar instead of capturing
        // 21 different buttons/section strings in 42 long-lived closures.
        button.PointerEntered += OnMainSidebarPointerEntered;
        button.PointerExited += OnMainSidebarPointerExited;
        button.ClickMode = ClickMode.Release;
        button.Click += OnMainSidebarButtonClick;
        // Inactive visuals were already initialized in the Button initializer.
        // Do not reset properties/shadows/transforms again on every cold sidebar build.
        if (string.Equals(section, _section, StringComparison.OrdinalIgnoreCase))
            SetCategoryButtonVisualState(button, active: true, resetMotion: false);
        // All navigation geometry is set at construction. Theme changes still
        // call ApplyGlassNavigationGeometry for existing buttons.
        button.Resources["ButtonBackgroundPointerOver"] = SidebarHoverBackground();
        button.Resources["ButtonBackgroundPressed"] = _darkTheme ? ThemeColor(100, 24, 55, 79) : SurfaceBrush();
        button.Resources["ButtonBorderBrushPointerOver"] = _darkTheme ? GlassEdgeBrush() : BorderThemeBrush();
        // Keep persistent sidebar buttons transform-free; pointer feedback is already handled
        // by their background/border states and must never make menu content disappear.
        _categoryButtons[section] = button;
        host.Children.Add(button);
        if (firstIconProfile is { } slowWatch && slowWatch.ElapsedMilliseconds >= 8)
            LogService.Info("performance",
                $"SIDEBAR_FIRST_BUTTON_PHASE rowMs={rowMs} svgMs={iconMs-rowMs} iconAttachMs={iconAttachMs-iconMs} labelCreateMs={labelCreateMs-iconAttachMs} labelAttachMs={rowContentMs-labelCreateMs} buttonMs={buttonMs-rowContentMs} wiringMs={slowWatch.ElapsedMilliseconds-buttonMs} totalMs={slowWatch.ElapsedMilliseconds}");
    }

    private void OnMainSidebarPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section } button ||
            string.Equals(_activeCategory, section, StringComparison.OrdinalIgnoreCase)) return;
        button.Background = SidebarHoverBackground();
        button.BorderBrush = _darkTheme ? ThemeColor(82,126,184,224) : BorderThemeBrush();
    }

    private void OnMainSidebarPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } button &&
            !string.Equals(_activeCategory, section, StringComparison.OrdinalIgnoreCase))
            SetCategoryButtonVisualState(button, active: false, resetMotion: false);
    }

    private void OnMainSidebarButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;
        if (string.Equals(section, _section, StringComparison.OrdinalIgnoreCase)) return;
        SafeTaskService.Forget(
            NavigateMainSectionFromUiAsync(section),
            "navigation",
            "Sidebar click " + section);
    }

    private Border BuildSubNavigation()
    {
        var shell = new Border { Background = UseGlassChrome ? GlassPanelBrush() : SurfaceBrush(), BorderBrush = UseGlassChrome ? ThemeColor(0,0,0,0) : BorderThemeBrush(), BorderThickness = UseGlassChrome ? new Thickness(0) : new Thickness(0, 0, 0, 1), Padding = new Thickness(28, 8, 28, 8), Visibility = Visibility.Collapsed };
        SideMenu = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        SubSelectorHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        shell.Child = SubSelectorHost; SubNavShell = shell; return shell;
    }

    private void UpdateCategorySelection(string key)
    {
        if (string.Equals(_activeCategory, key, StringComparison.OrdinalIgnoreCase))
            return;

        Button? previous = null;
        if (_categoryButtons.TryGetValue(_activeCategory, out previous))
            SetCategoryButtonVisualState(previous, active: false, resetMotion: false);

        _categoryButtons.TryGetValue(key, out Button? current);
        if (current != null)
            SetCategoryButtonVisualState(current, active: true, resetMotion: false);

        _activeCategory = key;
        // The sidebar uses only built-in Button hover state, no persistent
        // compositor animations. Native StopAnimation/Scale/Opacity calls are
        // occasionally 60+ ms on a busy compositor and stalled GPU navigation
        // despite its own page construction taking <10 ms. Update colors and
        // selection synchronously; repair any inherited motion off the hot path.
        int generation = _navigationGeneration;
        if (previous != null || current != null)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation != _navigationGeneration) return;
                if (previous is { IsLoaded: true }) ResetMotionVisual(previous);
                if (current is { IsLoaded: true }) ResetMotionVisual(current);
            });
    }

    private static void SetCategoryButtonVisualState(
        Button button, bool active, bool resetMotion = true)
    {
        if (resetMotion) ResetMotionVisual(button);
        button.Foreground = active ? ThemeColor(255, 255, 255) : (_darkTheme ? ThemeColor(192,192,192) : TextSecondaryBrush());
        button.Background = SidebarButtonBackground(active);
        button.BorderBrush = SidebarButtonBorder(active);
        button.BorderThickness = new Thickness(active ? 1 : 0);
        button.Shadow = _darkTheme && active ? (_glassNavigationShadow ??= new ThemeShadow()) : null;
        button.Translation = new System.Numerics.Vector3(0, 0, _darkTheme && active ? 10 : 0);
    }

    private static ThemeShadow? _glassNavigationShadow;
    private static Brush? _sidebarActiveBackground;
    private static Brush? _sidebarInactiveBackground;
    private static Brush? _sidebarHoverBackground;
    private static Brush? _sidebarActiveBorder;
    private static Brush? _sidebarInactiveBorder;

    private static Brush SidebarButtonBackground(bool active)
    {
        if (!_darkTheme) return active ? AccentSoftBrush() : SurfaceElevatedBrush();
        return active
            ? (_sidebarActiveBackground ??= GlassNavigationBrush())
            : (_sidebarInactiveBackground ??= ThemeColor(0, 0, 0, 0));
    }

    private static Brush SidebarHoverBackground()
        => !_darkTheme ? SurfaceElevatedBrush() : (_sidebarHoverBackground ??= ThemeColor(52,24,64,94));

    private static Brush SidebarButtonBorder(bool active)
        => !_darkTheme ? BorderThemeBrush() : active
            ? (_sidebarActiveBorder ??= GlassBlueEdgeBrush())
            : (_sidebarInactiveBorder ??= ThemeColor(0,0,0,0));

    private void EnsureActionBarInitialized()
    {
        if (_actionBarInitialized) return;
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var old = ActionBar;
        var bar = BuildActionBar();
        bar.Margin = new Thickness(ShellSidebarWidth + 20, 0, 20, 20);
        bar.VerticalAlignment = VerticalAlignment.Bottom;
        bar.SizeChanged += (_, _) => UpdateRowlessShellGeometry();
        RootGrid.Children.Remove(old);
        RootGrid.Children.Add(bar);
        ActionBar = bar;
        _actionBarInitialized = true;
        UpdateRowlessShellGeometry();
        LogService.Info("performance", $"ACTIONBAR_DEFERRED_STAGE ms={phase.ElapsedMilliseconds}");
    }

    private Border BuildActionBar()
    {
        var bar = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(28, 0, 28, 16),
            Padding = new Thickness(14,8,8,8),
            CornerRadius = new CornerRadius(16),
            Background = _darkTheme ? GlassPanelBrush() : SurfaceElevatedBrush(),
            BorderBrush = _darkTheme ? GlassEdgeBrush() : BorderThemeBrush(),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DirtyText = new TextBlock { FontSize = 14, Text = "Nevyřízené změny", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(DirtyText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var discard = new Button { Content = "Zrušit" };
        ApplyPillButtonAppearance(discard);
        ApplyMaterialButtonAppearance(discard);
        discard.MinWidth = 100;
        AttachHoverMotion(discard, 1.008f);
        discard.Click += Discard_Click;
        var apply = new Button { Content = "Použít změny" };
        ApplyPillButtonAppearance(apply);
        ApplyMaterialButtonAppearance(apply, primary: true);
        apply.BorderBrush = _darkTheme ? GlassBlueEdgeBrush() : ButtonVioletBorderBrush();
        apply.Foreground = _darkTheme ? ThemeColor(246,250,255) : TextPrimaryBrush();
        apply.MinWidth = 140;
        AttachHoverMotion(apply, 1.012f);
        apply.Click += Apply_Click;
        actions.Children.Add(discard); actions.Children.Add(apply);
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        bar.Child = grid;
        return bar;
    }

    private void UpdateVisualSelection(string key, string glyph)
    {
        string topKey = key switch
        {
            "logs" or "restore" => "settings",
            "cursor" or "powershell" => "themes",
            _ => key
        };
        UpdateCategorySelection(topKey);
        if (SubNavShell != null)
            SubNavShell.Visibility = _subOptions.ContainsKey(key) ? Visibility.Visible : Visibility.Collapsed;
        // ShowSectionCoreAsync applies geometry once after its target hero height is known.
    }
}
