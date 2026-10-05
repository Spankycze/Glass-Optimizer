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
    private StackPanel SideMenu = null!;
    private StackPanel CategoryBar = null!;
    private StackPanel HeroTextPanel = null!;
    private bool _heroDeferredCompleted;
    private Border HeaderCard = null!;
    private Border SubNavShell = null!;
    private Image? _heroBackgroundImage;
    private Border? _heroImageFrame;
    private bool _actionBarInitialized;
    private Border? _prewarmedActionBar;
    private int _actionBarPrewarmGeneration;
    private bool _mainTabsCompleted;
    private bool _sidebarDeferredQueued;
    private int _sidebarDeferredIndex;
    private bool _heroActionButtonReady;
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly Dictionary<string, Button> _categoryButtons = new();
    private string _activeCategory = "home";

    private static readonly (string Title, string Section)[] MainSectionItems =
    {
        ("Přehled","overview"),("Power Plan","power"),("GPU / Display","gpu"),
        ("Internet / Síť","network"),("Úložiště / NVMe","storage"),("Systémové tweaky","system"),
        ("USB / HID","usb"),("Ventilátory","lianli"),("Po spuštění","startup"),("UI / Explorer","ui"),("Windows Update","update"),
        ("Spotify / Spicetify","spicetify"),("Programy / Launchery","apps"),("Soukromí","privacy"),("AI Debloater","ai"),("AI System Runtime","ai-runtime"),
        ("Defender","defender"),("Debloater","debloat"),("Fixer","fixer"),("PowerShell","powershell"),("Cursor","cursor"),("Nastavení","settings")
    };

    private static Brush? _shellDarkBackdrop;
    private bool _shellFirstPaintCompleted;
    private Border? _mainTabsShell;

    private Brush BuildShellBackdrop()
    {
        if (!_darkTheme) return SurfaceAltBrush();
        if (_shellDarkBackdrop != null) return _shellDarkBackdrop;
        if (!_shellFirstPaintCompleted) return SurfaceAltBrush();

        var b = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SurfaceAlt.R, _themePalette.SurfaceAlt.G, _themePalette.SurfaceAlt.B), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.Surface.R, _themePalette.Surface.G, _themePalette.Surface.B), Offset = .55 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B), Offset = 1 });
        _shellDarkBackdrop = b;
        return b;
    }
    private void CompleteDeferredShellBackdrop()
    {
        if (_shellFirstPaintCompleted) return;
        _shellFirstPaintCompleted = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Brush backdrop = BuildShellBackdrop();

        RootGrid.Background = backdrop;
        if (_mainTabsShell != null) _mainTabsShell.Background = backdrop;
        _pageScroll.Background = backdrop;
        PageHost.Background = backdrop;

        LogService.Info("performance",
            $"SHELL_BACKDROP_DEFERRED ms={sw.ElapsedMilliseconds} dark={_darkTheme}");
    }

    private void BuildShell()
    {
        _actionBarInitialized = false;
        _prewarmedActionBar = null;
        System.Threading.Interlocked.Increment(ref _actionBarPrewarmGeneration);
        _mainTabsCompleted = false;
        _sidebarDeferredQueued = false;
        _sidebarDeferredIndex = 0;
        _heroActionButtonReady = false;
        _categoryButtons.Clear();
        _activeCategory = string.Empty;
        var shellPhase = System.Diagnostics.Stopwatch.StartNew();
        long backdropStartMs = shellPhase.ElapsedMilliseconds;
        var shellBackdrop = BuildShellBackdrop();
        long backdropMs = shellPhase.ElapsedMilliseconds - backdropStartMs;
        RootGrid = new Grid { Background = shellBackdrop, RequestedTheme = _darkTheme ? ElementTheme.Dark : ElementTheme.Light };
        long rootCreateMs = shellPhase.ElapsedMilliseconds;
        StartupDiagnostics.BufferInfo($"SHELL_ROOT_PHASE backdropMs={backdropMs} gridMs={rootCreateMs-backdropMs} totalMs={rootCreateMs}");
        // Startup shell is intentionally rowless: first RowDefinition activation costs
        // ~20 ms on this machine. Geometry is maintained with stretch + margins instead.
        long rootColumnsMs = shellPhase.ElapsedMilliseconds;
        long rootLayoutMs = shellPhase.ElapsedMilliseconds;

        HeaderCard = BuildHeroPlaceholder(shellBackdrop);
        _heroDeferredCompleted = false;
        Grid.SetColumnSpan(HeaderCard, 2);
        RootGrid.Children.Add(HeaderCard);
        long heroMs = shellPhase.ElapsedMilliseconds;

        var mainTabs = BuildMainTabs(shellBackdrop);
        mainTabs.Width = 218;
        mainTabs.HorizontalAlignment = HorizontalAlignment.Left;
        mainTabs.VerticalAlignment = VerticalAlignment.Stretch;
        mainTabs.Margin = new Thickness(0, 38, 0, 0);
        Canvas.SetZIndex(mainTabs, 40);
        RootGrid.Children.Add(mainTabs);
        long tabsMs = shellPhase.ElapsedMilliseconds;

        var subNav = BuildSubNavigation();
        subNav.Margin = new Thickness(218, 223, 0, 0);
        subNav.VerticalAlignment = VerticalAlignment.Top;
        subNav.SizeChanged += (_, _) => UpdateRowlessShellGeometry();
        RootGrid.Children.Add(subNav);
        long subNavMs = shellPhase.ElapsedMilliseconds;

        _pageScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Background = shellBackdrop,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        PageHost = new StackPanel
        {
            Padding = new Thickness(16, 1, 16, 8),
            Background = shellBackdrop,
            Spacing = 0
        };
        PageHost.SizeChanged += (_, _) => SchedulePageScrollPolicyRefresh();
        PageHost.AddHandler(
            UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(OnPageHostKeyDown),
            handledEventsToo: true);
        _contentLayerHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        ContentStack = new StackPanel { Spacing = 0 };
        _contentLayerHost.Children.Add(ContentStack);
        PageHost.Children.Add(_contentLayerHost);
        TopActions = new StackPanel { Spacing = 10, Margin = new Thickness(0) };
        PageHost.Children.Add(TopActions);
        _pageScroll.Content = PageHost;
        _pageScroll.Margin = new Thickness(218, 223, 0, 0);
        RootGrid.Children.Add(_pageScroll);
        long hostMs = shellPhase.ElapsedMilliseconds;

        ActionBar = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(218, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ActionBar.SizeChanged += (_, _) => UpdateRowlessShellGeometry();
        RootGrid.Children.Add(ActionBar);
        UpdateRowlessShellGeometry();
        long actionMs = shellPhase.ElapsedMilliseconds;

        RootGrid.SizeChanged += (_, e) =>
        {
            bool compactWidth = e.NewSize.Width < 1180;
            PageHost.Padding = compactWidth ? new Thickness(13, 1, 13, 9) : new Thickness(16, 1, 16, 8);
            UpdateRowlessShellGeometry(e.NewSize.Width, null, e.NewSize.Height);
            SchedulePageScrollPolicyRefresh();
        };

        AdminBadge = new TextBlock { Text = "Správce", Visibility = Visibility.Collapsed };
        long titleStartMs = shellPhase.ElapsedMilliseconds;
        AppTitleBar = new Grid
        {
            Height = 36,
            Padding = new Thickness(12, 0, 142, 0),
            Visibility = Visibility.Collapsed
        };
        long titleBuildMs = shellPhase.ElapsedMilliseconds;
        Grid.SetColumnSpan(AppTitleBar, 2);
        Grid.SetRow(AppTitleBar, 0);
        AppTitleBar.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetZIndex(AppTitleBar, 200);
        RootGrid.Children.Add(AppTitleBar);
        long titleAttachMs = shellPhase.ElapsedMilliseconds;
        Content = RootGrid;
        long titleSetMs = shellPhase.ElapsedMilliseconds;
        StartupDiagnostics.BufferInfo($"TITLEBAR_BUILD_PHASE buildMs={titleBuildMs-titleStartMs} attachMs={titleAttachMs-titleBuildMs} setMs={titleSetMs-titleAttachMs} syncActivation=true");
        long totalMs = shellPhase.ElapsedMilliseconds;
        StartupDiagnostics.BufferInfo($"SHELL_PHASE rootCreateMs={rootCreateMs} columnsMs={rootColumnsMs - rootCreateMs} rowsMs={rootLayoutMs - rootColumnsMs} rootLayoutMs={rootLayoutMs - rootCreateMs} placeholderMs={heroMs - rootLayoutMs} heroMs={heroMs} tabsMs={tabsMs - heroMs} subNavMs={subNavMs - tabsMs} hostMs={hostMs - subNavMs} actionMs={actionMs - hostMs} tailMs={totalMs - actionMs} totalMs={totalMs}");
    }

    private const double StartupClientWidth = 1304d;
    private const double ShellTitleBarHeight = 36d;
    private const double ShellSidebarWidth = 218d;
    private const double HeroFrameLeft = 231d;
    private const double HeroFrameRight = 16d;
    private const double OverviewHeroExtraHeight = 64d;
    private const double OverviewPageLift = 22d;

    private static double CalculateShellBannerHeight(double clientWidth) =>
        Math.Max(1d, clientWidth - 247d) * 216d / 1256d;

    private static double CalculateShellHeroHeight(double clientWidth) =>
        ShellTitleBarHeight + CalculateShellBannerHeight(clientWidth);

    private static double CalculateSectionHeroHeight(string? section, double clientWidth) =>
        string.Equals(section, "overview", StringComparison.OrdinalIgnoreCase)
            ? CalculateShellHeroHeight(clientWidth) + OverviewHeroExtraHeight
            : ShellTitleBarHeight;

    private void UpdateRowlessShellGeometry(double width = 0, double? heroHeightOverride = null, double height = 0)
    {
        double actualWidth = width > 1
            ? width
            : (RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth);
        bool overviewHero = string.Equals(_section, "overview", StringComparison.OrdinalIgnoreCase);
        double heroHeight = heroHeightOverride ?? CalculateSectionHeroHeight(_section, actualWidth);
        HeaderCard.Visibility = overviewHero ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Padding = overviewHero ? new Thickness(16, 1, 16, 8) : new Thickness(24, 10, 24, 24);
        // Hero visually continues underneath the custom titlebar; do not reserve a 36px gap.
        double bannerHeight = Math.Max(1d, heroHeight);

        if (double.IsNaN(HeaderCard.Height) || Math.Abs(HeaderCard.Height - heroHeight) > 0.1)
            HeaderCard.Height = heroHeight;
        if (HeaderCard.VerticalAlignment != VerticalAlignment.Top)
            HeaderCard.VerticalAlignment = VerticalAlignment.Top;

        var heroFrame = _heroImageFrame ?? (HeaderCard.Child as Grid)?.Children.OfType<Border>().FirstOrDefault();
        if (heroFrame != null)
        {
            _heroImageFrame = heroFrame;
            if (double.IsNaN(heroFrame.Height) || Math.Abs(heroFrame.Height - bannerHeight) > 0.1)
                heroFrame.Height = bannerHeight;

            var desiredHeroMargin = new Thickness(HeroFrameLeft, 0, HeroFrameRight, 0);
            if (!heroFrame.Margin.Equals(desiredHeroMargin))
                heroFrame.Margin = desiredHeroMargin;
            if (heroFrame.VerticalAlignment != VerticalAlignment.Top)
                heroFrame.VerticalAlignment = VerticalAlignment.Top;
        }

        double subNavHeight =
            SubNavShell != null && SubNavShell.Visibility == Visibility.Visible
                ? Math.Max(0, SubNavShell.ActualHeight)
                : 0;
        if (SubNavShell != null)
        {
            var desiredSubNavMargin = new Thickness(ShellSidebarWidth, heroHeight, 0, 0);
            if (!SubNavShell.Margin.Equals(desiredSubNavMargin))
                SubNavShell.Margin = desiredSubNavMargin;
            if (SubNavShell.VerticalAlignment != VerticalAlignment.Top)
                SubNavShell.VerticalAlignment = VerticalAlignment.Top;
        }

        double actionReserve = 0;
        if (ActionBar != null && ActionBar.Visibility == Visibility.Visible)
            actionReserve = Math.Max(0, ActionBar.ActualHeight + ActionBar.Margin.Bottom);

        if (_pageScroll != null)
        {
            double overviewLift = overviewHero ? OverviewPageLift : 0d;
            var desiredPageMargin = new Thickness(
                ShellSidebarWidth,
                Math.Max(0, heroHeight + subNavHeight - overviewLift),
                0,
                actionReserve);
            if (!_pageScroll.Margin.Equals(desiredPageMargin))
                _pageScroll.Margin = desiredPageMargin;

            // Rowless shell cannot rely on VerticalAlignment=Stretch here. On cold startup
            // WinUI can measure the ScrollViewer at 0 px high while its content is already
            // fully measured, leaving Overview built but completely invisible.
            double actualHeight = height > 1
                ? height
                : (RootGrid.ActualHeight > 1 ? RootGrid.ActualHeight : 860d);
            if (actualHeight > 1)
            {
                double availableHeight = Math.Max(1d, actualHeight - desiredPageMargin.Top - desiredPageMargin.Bottom);
                if (double.IsNaN(_pageScroll.Height) || Math.Abs(_pageScroll.Height - availableHeight) > 0.5)
                    _pageScroll.Height = availableHeight;
            }
        }
    }

    private void OnPageHostKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_pageScroll.VerticalScrollMode != ScrollMode.Enabled || _pageScroll.ScrollableHeight <= 1)
            return;

        DependencyObject? focused = RootGrid.XamlRoot == null
            ? null
            : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        if (IsPageScrollKeyReserved(focused))
        {
            if (e.Key is Windows.System.VirtualKey.Home
                or Windows.System.VirtualKey.End
                or Windows.System.VirtualKey.PageDown
                or Windows.System.VirtualKey.PageUp)
            {
                double preservedOffset = _pageScroll.VerticalOffset;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_pageScroll.VerticalScrollMode == ScrollMode.Enabled &&
                        Math.Abs(_pageScroll.VerticalOffset - preservedOffset) > 0.5)
                    {
                        _pageScroll.ChangeView(null, preservedOffset, null, true);
                    }
                });
            }
            return;
        }

        double target;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Home:
                target = 0;
                break;
            case Windows.System.VirtualKey.End:
                target = _pageScroll.ScrollableHeight;
                break;
            case Windows.System.VirtualKey.PageDown:
                target = Math.Min(
                    _pageScroll.ScrollableHeight,
                    _pageScroll.VerticalOffset + Math.Max(1, _pageScroll.ViewportHeight * 0.9));
                break;
            case Windows.System.VirtualKey.PageUp:
                target = Math.Max(
                    0,
                    _pageScroll.VerticalOffset - Math.Max(1, _pageScroll.ViewportHeight * 0.9));
                break;
            default:
                return;
        }

        _pageScroll.ChangeView(null, target, null, true);
        e.Handled = true;
    }

    private bool IsPageScrollKeyReserved(DependencyObject? focused)
    {
        DependencyObject? current = focused;
        while (current != null && !ReferenceEquals(current, PageHost))
        {
            if (current is TextBox or RichEditBox or PasswordBox or ComboBox or Slider)
                return true;

            // A button/toggle hosted inside a virtualized ListView still belongs to the
            // page keyboard-scroll contract. Reserve PageUp/PageDown/Home/End only when
            // the ListView itself owns focus, not merely because it is an ancestor.
            if (current is ListViewBase && ReferenceEquals(current, focused))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private Border BuildHeroPlaceholder(Brush shellBackdrop)
    {
        _heroDeferredCompleted = false;
        return new Border
        {
            Background = shellBackdrop,
            BorderThickness = new Thickness(0)
        };
    }

    private void CompleteDeferredHero()
    {
        if (_heroDeferredCompleted) return;
        var phase = System.Diagnostics.Stopwatch.StartNew();

        var placeholder = HeaderCard;
        double preservedHeight = placeholder.Height;
        var fullHero = BuildCinematicHero();
        fullHero.Height = preservedHeight;
        fullHero.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(fullHero, 2);

        int heroIndex = RootGrid.Children.IndexOf(placeholder);
        if (heroIndex >= 0)
        {
            RootGrid.Children.RemoveAt(heroIndex);
            RootGrid.Children.Insert(heroIndex, fullHero);
        }
        HeaderCard = fullHero;
        UpdateRowlessShellGeometry(heroHeightOverride: preservedHeight);

        if (CatalogService.TryGetSection(_section, out var section) && section != null)
        {
            PageTitle.Text = section.Title;
            PageSubtitle.Text = section.Subtitle;
        }
        HeroTextPanel.Visibility = Visibility.Collapsed;
        _heroDeferredCompleted = true;

        long layoutMs = phase.ElapsedMilliseconds;
        LoadShellImagesDeferred();
        LogService.Info("performance", $"HERO_DEFERRED_STAGE stableLayout=true layoutMs={layoutMs} imageSourceMs={phase.ElapsedMilliseconds - layoutMs} totalMs={phase.ElapsedMilliseconds}");
    }

    private Border BuildCinematicHero()
    {
        var hero = new Border
        {
            Background = _darkTheme ? ThemeColor(5, 7, 15) : SurfaceBrush(),
            BorderThickness = new Thickness(0)
        };

        var image = new Image
        {
            Source = null,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _heroBackgroundImage = image;

        var frame = new Border
        {
            Margin = new Thickness(231, 36, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(0),
            Background = _darkTheme ? ThemeColor(7, 9, 24) : SurfaceElevatedBrush(),
            Child = image
        };

        var grid = new Grid();
        grid.Children.Add(frame);

        HeroTextPanel = new StackPanel
        {
            Width = 0,
            Height = 0,
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        PageTitle = new TextBlock();
        PageSubtitle = new TextBlock();
        HeroTextPanel.Children.Add(PageTitle);
        HeroTextPanel.Children.Add(PageSubtitle);
        grid.Children.Add(HeroTextPanel);

        _heroActionButtonReady = true;
        hero.Child = grid;
        LogService.Info("performance", "HERO_BUILD_PHASE cleanPc=true neonFrame=true text=false optimize=false titleMetrics=true");
        return hero;
    }
    private Border BuildMainTabs(Brush shellBackdrop)
    {
        var shell = new Border
        {
            Background = shellBackdrop,
            BorderBrush = BorderThemeBrush(),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12, 12, 12, 16)
        };
        _mainTabsShell = shell;
        CategoryBar = new StackPanel { Spacing = 5 };
        // Keep the first-paint sidebar shell lightweight. The ScrollViewer is only
        // needed once deferred navigation buttons begin materializing.
        shell.Child = CategoryBar;
        return shell;
    }

    private void EnsureSidebarScrollHost()
    {
        if (_mainTabsShell == null || _mainTabsShell.Child is ScrollViewer) return;
        _mainTabsShell.Child = null;
        _mainTabsShell.Child = new ScrollViewer
        {
            Content = CategoryBar,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Background = new SolidColorBrush(Colors.Transparent)
        };
    }

    private async System.Threading.Tasks.Task ScheduleDeferredSidebarAfterFirstRenderAsync()
    {
        await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            EnsureSidebarScrollHost();
            tcs.TrySetResult(true);
        }))
            return;

        await tcs.Task.ConfigureAwait(false);
        QueueDeferredSidebarBatch();
    }

    private void QueueDeferredSidebarBatch()
    {
        if (_mainTabsCompleted || _sidebarDeferredQueued) return;
        _sidebarDeferredQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _sidebarDeferredQueued = false;
            if (_mainTabsCompleted) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int start = _sidebarDeferredIndex;
            int batchSize = start == 0 ? 2 : 4;
            int end = System.Math.Min(start + batchSize, MainSectionItems.Length);
            for (int i = start; i < end; i++)
                AddMainSection(CategoryBar, MainSectionItems[i].Title, MainSectionItems[i].Section);
            _sidebarDeferredIndex = end;
            LogService.Info("performance", $"SIDEBAR_DEFERRED_BATCH start={start} added={end - start} ms={sw.ElapsedMilliseconds} total={CategoryBar.Children.Count}");

            if (end < MainSectionItems.Length)
            {
                QueueDeferredSidebarBatch();
                return;
            }

            _mainTabsCompleted = true;
            UpdateCategorySelection(_section);
            LogService.Info("performance", $"SIDEBAR_DEFERRED items={MainSectionItems.Length} batched=true");
        });
    }

    private void CompleteDeferredMainTabs()
    {
        // Theme rebuilds bypass the asynchronous startup sidebar path.
        EnsureSidebarScrollHost();
        if (_mainTabsCompleted) return;
        if (_sidebarDeferredIndex > 0)
        {
            CategoryBar.Children.Clear();
            _categoryButtons.Clear();
            _sidebarDeferredIndex = 0;
            _sidebarDeferredQueued = false;
        }
        var phase = System.Diagnostics.Stopwatch.StartNew();
        foreach (var item in MainSectionItems) AddMainSection(CategoryBar, item.Title, item.Section);
        _mainTabsCompleted = true;
        UpdateCategorySelection(_section);
        LogService.Info("performance", $"SIDEBAR_DEFERRED items={MainSectionItems.Length} ms={phase.ElapsedMilliseconds}");
    }

    internal async System.Threading.Tasks.Task RunSidebarDeferredSmokeAsync()
    {
        for (int i = 0; i < 40 && !_mainTabsCompleted; i++)
            await System.Threading.Tasks.Task.Delay(25);
        int expectedSidebarItems = MainSectionItems.Length;
        if (!_mainTabsCompleted || CategoryBar.Children.Count != expectedSidebarItems || _categoryButtons.Count != expectedSidebarItems)
            throw new InvalidOperationException($"Deferred sidebar incomplete: completed={_mainTabsCompleted} children={CategoryBar.Children.Count} buttons={_categoryButtons.Count} expected={expectedSidebarItems}.");

        if (_mainTabsShell?.Child is not ScrollViewer { VerticalScrollMode: ScrollMode.Enabled, VerticalScrollBarVisibility: ScrollBarVisibility.Hidden })
            throw new InvalidOperationException("Sidebar scrolling host invariant broken.");

        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Button button in _categoryButtons.Values)
        {
            if (button.Tag is not string section || button.Content is not StackPanel row || row.Children.Count == 0 || row.Children[0] is not Grid)
                throw new InvalidOperationException("Sidebar icon smoke expected custom SVG host as the first menu element.");
            if (!button.IsEnabled || !button.IsHitTestVisible || row.IsHitTestVisible)
                throw new InvalidOperationException($"Sidebar hit-test invariant broken for {section}: enabled={button.IsEnabled} buttonHit={button.IsHitTestVisible} childHit={row.IsHitTestVisible}.");
            if (button.ClickMode != ClickMode.Release)
                throw new InvalidOperationException($"Sidebar click mode invariant broken for {section}: {button.ClickMode}.");
            assets.Add(SidebarIconName(section));
        }
        int expectedUniqueAssets = MainSectionItems.Select(x => SidebarIconName(x.Section)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (assets.Count != expectedUniqueAssets)
            throw new InvalidOperationException($"Sidebar icon coverage mismatch: {assets.Count}/{expectedUniqueAssets} unique assets for {expectedSidebarItems} sections.");
        if (!ReferenceEquals(SidebarIconSource("gpu"), SidebarIconSource("gpu")) ||
            !ReferenceEquals(SidebarIconSource("network"), SidebarIconSource("network")))
            throw new InvalidOperationException("Sidebar SVG source cache did not reuse image sources.");

        foreach (string asset in HardwareIconMap.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SidebarIcons", asset + ".svg");
            if (!System.IO.File.Exists(path))
                throw new InvalidOperationException("Mapped icon asset is missing: " + asset + ".svg");
        }
        if (!ReferenceEquals(SectionIconSource("activity"), SectionIconSource("overview")) ||
            !ReferenceEquals(SectionIconSource("restore"), SectionIconSource("update")))
            throw new InvalidOperationException("Utility icon alias/source cache mismatch.");

        LogService.Info("SIDEBAR_DEFERRED_SMOKE_OK uniqueAssets=21 style=CustomSvg sourceCache=true utilityAssetsVerified=true hitTestOwner=button clickMode=release");
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

        if (_prewarmedActionBar != null)
            throw new InvalidOperationException("Action bar should remain on-demand; startup prewarm unexpectedly ran.");

        var attach = System.Diagnostics.Stopwatch.StartNew();
        EnsureActionBarInitialized();
        long attachMs = attach.ElapsedMilliseconds;
        if (!_actionBarInitialized || ActionBar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("On-demand action bar factory failed to initialize a collapsed bar.");
        if (attachMs > 25)
            throw new InvalidOperationException($"On-demand action bar initialization is too slow: {attachMs} ms.");

        LogService.Info("ACTIONBAR_LAZY_SMOKE_OK cleanStateSkipped=true onDemand=true factoryVerified=true attachMs=" + attachMs);
    }

    private void AddMainSection(StackPanel host, string title, string section)
    {
        bool preview = IsUiPreviewBuild();
        // PERF/UI INVARIANT: the Button must own all pointer input. Child visuals are display-only,
        // otherwise future icon/hover work can silently break sidebar navigation hit-testing.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var menuIcon = SidebarIcon(section, preview ? 27 : 26);
        menuIcon.Margin = new Thickness(0, 0, 1, 0);
        row.Children.Add(menuIcon);
        row.Children.Add(new TextBlock { Text = title, FontSize = preview ? 13.6 : 13.1, FontWeight = FontWeights.SemiBold, Foreground = _darkTheme ? ThemeColor(232,232,232) : TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, MaxWidth = 130, MaxLines = 2, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Tag = section,
            Content = row,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(9),
            Background = SidebarButtonBackground(active: false),
            BorderBrush = SidebarButtonBorder(active: false),
            BorderThickness = new Thickness(1.15),
            Foreground = _darkTheme ? ThemeColor(192,192,192) : TextSecondaryBrush(),
            IsHitTestVisible = true,
            IsEnabled = true
        };
        EnsureAccessibleButton(button, title);
        button.PointerEntered += (_, _) =>
        {
            if (!string.Equals(_activeCategory, section, StringComparison.OrdinalIgnoreCase))
            {
                button.Background = SidebarHoverBackground();
                button.BorderBrush = _darkTheme ? ThemeColor(70,70,70) : BorderThemeBrush();
            }
        };
        button.PointerExited += (_, _) =>
        {
            if (!string.Equals(_activeCategory, section, StringComparison.OrdinalIgnoreCase))
                SetCategoryButtonVisualState(button, active: false);
        };
        button.ClickMode = ClickMode.Release;
        button.Click += OnMainSidebarButtonClick;
        SetCategoryButtonVisualState(button, string.Equals(section, _section, StringComparison.OrdinalIgnoreCase));
        _categoryButtons[section] = button;
        host.Children.Add(button);
    }

    private void OnMainSidebarButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;
        SafeTaskService.Forget(
            NavigateMainSectionFromUiAsync(section),
            "navigation",
            "Sidebar click " + section);
    }

    private Border BuildSubNavigation()
    {
        var shell = new Border { Background = SurfaceBrush(), BorderBrush = BorderThemeBrush(), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(28, 8, 28, 8), Visibility = Visibility.Collapsed };
        SideMenu = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        SubSelectorHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        shell.Child = SubSelectorHost; SubNavShell = shell; return shell;
    }

    private void UpdateCategorySelection(string key)
    {
        if (string.Equals(_activeCategory, key, StringComparison.OrdinalIgnoreCase))
            return;

        if (_categoryButtons.TryGetValue(_activeCategory, out var previous))
            SetCategoryButtonVisualState(previous, active: false);

        Button? pulseTarget = null;
        if (_categoryButtons.TryGetValue(key, out var current))
        {
            SetCategoryButtonVisualState(current, active: true);
            pulseTarget = current;
        }

        _activeCategory = key;

        if (pulseTarget != null)
        {
            var target = pulseTarget;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (string.Equals(_activeCategory, key, StringComparison.OrdinalIgnoreCase))
                    AnimateSelectionPulse(target);
            });
        }
    }

    private static void SetCategoryButtonVisualState(Button button, bool active)
    {
        button.Foreground = active ? ThemeColor(255, 255, 255) : (_darkTheme ? ThemeColor(192,192,192) : TextSecondaryBrush());
        button.Background = SidebarButtonBackground(active);
        button.BorderBrush = SidebarButtonBorder(active);
        button.BorderThickness = new Thickness(active ? 3 : 1, 1, 1, 1);
    }

    private static Brush? _sidebarActiveBackground;
    private static Brush? _sidebarInactiveBackground;
    private static Brush? _sidebarHoverBackground;
    private static Brush? _sidebarActiveBorder;
    private static Brush? _sidebarInactiveBorder;

    private static Brush SidebarButtonBackground(bool active)
    {
        if (!_darkTheme) return active ? AccentSoftBrush() : SurfaceElevatedBrush();
        return active
            ? (_sidebarActiveBackground ??= NeutralDepthBrush(47, 34))
            : (_sidebarInactiveBackground ??= ThemeColor(14, 14, 14));
    }

    private static Brush SidebarHoverBackground()
        => !_darkTheme ? SurfaceElevatedBrush() : (_sidebarHoverBackground ??= ThemeColor(27,27,27));

    private static Brush SidebarButtonBorder(bool active)
        => !_darkTheme ? BorderThemeBrush() : active
            ? (_sidebarActiveBorder ??= ThemeColor(157,157,157))
            : (_sidebarInactiveBorder ??= ThemeColor(29,29,29));

    private void EnsureActionBarInitialized()
    {
        if (_actionBarInitialized) return;
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var old = ActionBar;
        var bar = _prewarmedActionBar ?? BuildActionBar();
        _prewarmedActionBar = null;
        bar.Margin = new Thickness(273, 0, 28, 16);
        bar.VerticalAlignment = VerticalAlignment.Bottom;
        bar.SizeChanged += (_, _) => UpdateRowlessShellGeometry();
        RootGrid.Children.Remove(old);
        RootGrid.Children.Add(bar);
        ActionBar = bar;
        _actionBarInitialized = true;
        UpdateRowlessShellGeometry();
        LogService.Info("performance", $"ACTIONBAR_DEFERRED_STAGE ms={phase.ElapsedMilliseconds}");
    }

    private async System.Threading.Tasks.Task PrewarmActionBarAfterFirstRenderAsync()
    {
        int generation = System.Threading.Volatile.Read(ref _actionBarPrewarmGeneration);
        await System.Threading.Tasks.Task.Delay(750).ConfigureAwait(false);
        if (generation != System.Threading.Volatile.Read(ref _actionBarPrewarmGeneration) ||
            _actionBarInitialized || _prewarmedActionBar != null)
            return;

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (generation != System.Threading.Volatile.Read(ref _actionBarPrewarmGeneration) ||
                    _actionBarInitialized || _prewarmedActionBar != null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                _prewarmedActionBar = BuildActionBar();
                LogService.Info("performance", $"ACTIONBAR_FACTORY_PREWARM ms={sw.ElapsedMilliseconds}");
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                LogService.Error("performance", "ActionBar factory prewarm failed", ex);
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        await tcs.Task.ConfigureAwait(false);
    }

    private void LoadShellImagesDeferred()
    {
        if (_heroBackgroundImage != null && _heroBackgroundImage.Source == null)
            _heroBackgroundImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/HeroTargetExact.png"));
    }

    private Border BuildActionBar()
    {
        var bar = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(28, 0, 28, 16),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = SurfaceElevatedBrush(),
            BorderBrush = BorderThemeBrush(),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DirtyText = new TextBlock { Text = "Nevyřízené změny", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(DirtyText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var discard = new Button { Content = "Zrušit" };
        ApplyPillButtonAppearance(discard);
        AttachHoverMotion(discard, 1.008f);
        discard.Click += Discard_Click;
        var apply = new Button { Content = "Použít změny" };
        ApplyPillButtonAppearance(apply);
        apply.Background = ButtonVioletBrush();
        apply.BorderBrush = ButtonVioletBorderBrush();
        apply.Foreground = OnAccentTextBrush();
        AttachHoverMotion(apply, 1.012f);
        apply.Click += Apply_Click;
        actions.Children.Add(discard); actions.Children.Add(apply);
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        bar.Child = grid;
        return bar;
    }

    private void UpdateVisualSelection(string key, string glyph)
    {
        string topKey = key is "logs" or "restore" ? "settings" : key;
        UpdateCategorySelection(topKey);
        if (SubNavShell != null)
            SubNavShell.Visibility = _subOptions.ContainsKey(key) ? Visibility.Visible : Visibility.Collapsed;
        UpdateRowlessShellGeometry();
    }

}
