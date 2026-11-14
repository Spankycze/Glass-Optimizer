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
    private bool _mainTabsCompleted;
    private bool _heroActionButtonReady;
    private readonly Dictionary<string, Button> _categoryButtons = new();
    private string _activeCategory = "home";

    private static readonly (string Title, string Section)[] MainSectionItems =
    {
        ("Přehled","overview"),("Systémové tweaky","system"),("Power Plan","power"),
        ("Internet / Síť","network"),("GPU / Display","gpu"),("Úložiště / NVMe","storage"),
        ("USB / HID","usb"),("Ventilátory","lianli"),("Po spuštění","startup"),("UI / Explorer","ui"),("Personalizace","themes"),("Windows Update","update"),
        ("Spotify / Spicetify","spicetify"),("Programy / Launchery","apps"),("Soukromí","privacy"),("AI Debloater","ai"),("AI System Runtime","ai-runtime"),
        ("Defender","defender"),("Debloater","debloat"),("Fixer","fixer"),("Nastavení","settings")
    };

    private (string Title, string Section)[] GetAvailableMainSectionItems()
    {
        return IsLianLiSupported()
            ? MainSectionItems
            : MainSectionItems.Where(item => item.Section != "lianli").ToArray();
    }

    private bool _shellFirstPaintCompleted;
    private Border? _mainTabsShell;

    // Desktop acrylic is drawn behind XAML. Every full-window layer must
    // remain transparent, including the first frame and theme rebuilds.
    private Brush BuildShellBackdrop() => UseGlassChrome
        ? ThemeColor(0, 0, 0, 0)
        : SurfaceAltBrush();

    private void CompleteDeferredShellBackdrop()
    {
        if (_shellFirstPaintCompleted) return;
        _shellFirstPaintCompleted = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Brush backdrop = BuildShellBackdrop();

        RootGrid.Background = backdrop;
        if (_mainTabsShell != null) _mainTabsShell.Background = UseGlassChrome ? SidebarGlassBrush() : backdrop;
        _pageScroll.Background = backdrop;
        PageHost.Background = backdrop;
        UpdateGlassShell();

        LogService.Info("performance",
            $"SHELL_BACKDROP_DEFERRED ms={sw.ElapsedMilliseconds} dark={_darkTheme}");
    }

    private void BuildShell()
    {
        _actionBarInitialized = false;
        _mainTabsCompleted = false;
        _deferredSidebarBatchCount = 0;
        _deferredSidebarLongestBatchMs = 0;
        _heroActionButtonReady = false;
        _categoryButtons.Clear();
        _activeCategory = string.Empty;
        var shellPhase = System.Diagnostics.Stopwatch.StartNew();
        long backdropStartMs = shellPhase.ElapsedMilliseconds;
        SystemBackdrop = UseDarkAcrylicBackdrop ? new OptimizerGlassBackdrop() : null;
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
        Grid.SetColumnSpan(HeaderCard, 2);
        RootGrid.Children.Add(HeaderCard);
        long heroMs = shellPhase.ElapsedMilliseconds;

        var mainTabs = BuildMainTabs(shellBackdrop);
        // The complete sidebar is attached atomically after the
        // initial shell paint by ScheduleDeferredSidebarAfterFirstRenderAsync.
        // Building it here added ~40–50ms to Window construction AND made
        // that existing deferred startup path an expensive no-op.
        mainTabs.Width = ShellSidebarWidth;
        mainTabs.HorizontalAlignment = HorizontalAlignment.Left;
        mainTabs.VerticalAlignment = VerticalAlignment.Stretch;
        mainTabs.Margin = new Thickness(0, _darkTheme ? ShellTitleBarHeight : 38, 0, 0);
        Canvas.SetZIndex(mainTabs, 40);
        RootGrid.Children.Add(mainTabs);
        long tabsMs = shellPhase.ElapsedMilliseconds;

        var subNav = BuildSubNavigation();
        subNav.Margin = new Thickness(ShellSidebarWidth, 223, 0, 0);
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
            Padding = new Thickness(24, 1, 24, 12),
            Background = shellBackdrop,
            Spacing = 0
        };
        // Content height changes are not HWND resizes. They must respect any
        // recent real resize quiet window but must not restart its 110ms clock.
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
        _pageScroll.Margin = new Thickness(ShellSidebarWidth, 223, 0, 0);
        RootGrid.Children.Add(_pageScroll);
        long hostMs = shellPhase.ElapsedMilliseconds;

        ActionBar = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(ShellSidebarWidth, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ActionBar.SizeChanged += (_, _) => UpdateRowlessShellGeometry();
        RootGrid.Children.Add(ActionBar);
        UpdateRowlessShellGeometry();
        long actionMs = shellPhase.ElapsedMilliseconds;

        RootGrid.SizeChanged += (_, e) =>
        {
            // Width/height changes still update geometry; only crossing the
            // 1180px breakpoint needs a WinUI Padding property write/layout.
            // The rowless shell is the sole writer of the page padding.
            // A separate compact-padding write here used to be immediately
            // overwritten by UpdateRowlessShellGeometry on this very event,
            // causing duplicate XAML layout invalidation at the breakpoint.
            UpdateRowlessShellGeometry(e.NewSize.Width, null, e.NewSize.Height);
            // Preserve all six existing live gauges when crossing the
            // two-/three-column Overview breakpoint during window resize.
            ReflowOverviewGaugeColumns(e.NewSize.Width);
            SchedulePageScrollPolicyRefresh(fromResize: true);
        };

        AdminBadge = new TextBlock { Text = "Správce", Visibility = Visibility.Collapsed };
        long titleStartMs = shellPhase.ElapsedMilliseconds;
        AppTitleBar = new Grid
        {
            Height = _darkTheme ? ShellTitleBarHeight : 36,
            Padding = new Thickness(_darkTheme ? 20 : 12, 0, 142, 0),
            Visibility = Visibility.Collapsed
        };
        long titleBuildMs = shellPhase.ElapsedMilliseconds;
        Grid.SetColumnSpan(AppTitleBar, 2);
        Grid.SetRow(AppTitleBar, 0);
        AppTitleBar.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetZIndex(AppTitleBar, 200);
        RootGrid.Children.Add(AppTitleBar);
        long titleAttachMs = shellPhase.ElapsedMilliseconds;
        InitializeRoundedGlassWindow();
        Content = RootGrid;
        long titleSetMs = shellPhase.ElapsedMilliseconds;
        StartupDiagnostics.BufferInfo($"TITLEBAR_BUILD_PHASE buildMs={titleBuildMs-titleStartMs} attachMs={titleAttachMs-titleBuildMs} setMs={titleSetMs-titleAttachMs} syncActivation=true");
        long totalMs = shellPhase.ElapsedMilliseconds;
        StartupDiagnostics.BufferInfo($"SHELL_PHASE rootCreateMs={rootCreateMs} columnsMs={rootColumnsMs - rootCreateMs} rowsMs={rootLayoutMs - rootColumnsMs} rootLayoutMs={rootLayoutMs - rootCreateMs} placeholderMs={heroMs - rootLayoutMs} heroMs={heroMs} tabsMs={tabsMs - heroMs} subNavMs={subNavMs - tabsMs} hostMs={hostMs - subNavMs} actionMs={actionMs - hostMs} tailMs={totalMs - actionMs} totalMs={totalMs}");
    }

    private const double StartupClientWidth = 1304d;
    private const double ShellTitleBarHeight = 44d;
    private static double ShellSidebarWidth => _darkTheme ? 200d : 188d;
    private static double HeroFrameLeft => ShellSidebarWidth + 1;
    private const double HeroFrameRight = 16d;
    private const double OverviewHeroExtraHeight = 64d;
    private const double OverviewPageLift = 0d;

    private static double CalculateShellBannerHeight(double clientWidth) =>
        Math.Max(1d, clientWidth - 247d) * 216d / 1256d;

    private static double CalculateShellHeroHeight(double clientWidth) =>
        ShellTitleBarHeight + CalculateShellBannerHeight(clientWidth);

    private static double CalculateSectionHeroHeight(string? section, double clientWidth) =>
        string.Equals(section, "overview", StringComparison.OrdinalIgnoreCase)
            ? (_darkTheme ? 116d : 108d)
            : ShellTitleBarHeight;

    private void UpdateRowlessShellGeometry(double width = 0, double? heroHeightOverride = null, double height = 0)
    {
        double actualWidth = width > 1
            ? width
            : (RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth);
        bool overviewHero = string.Equals(_section, "overview", StringComparison.OrdinalIgnoreCase);
        double heroHeight = heroHeightOverride ?? CalculateSectionHeroHeight(_section, actualWidth);
        Visibility headerVisibility = overviewHero ? Visibility.Visible : Visibility.Collapsed;
        if (HeaderCard.Visibility != headerVisibility)
            HeaderCard.Visibility = headerVisibility;
        // Keep horizontal padding consistent through Resize, navigation,
        // and the breakpoint. Vertical Overview insets remain 8/10 so all
        // six meters retain the original scroll-free layout.
        double sidePadding = ShellViewportPaddingPolicy.IsCompact(actualWidth) ? 16d : 24d;
        var pagePadding = overviewHero
            ? new Thickness(sidePadding, 8, sidePadding, 10)
            : new Thickness(sidePadding, 24, sidePadding, 24);
        if (!PageHost.Padding.Equals(pagePadding))
            PageHost.Padding = pagePadding;
        if (_mainTabsShell != null)
        {
            if (_mainTabsShell.Width != ShellSidebarWidth)
                _mainTabsShell.Width = ShellSidebarWidth;
            var sidebarMargin = new Thickness(0, UseGlassChrome ? ShellTitleBarHeight : 38, 0, 0);
            if (!_mainTabsShell.Margin.Equals(sidebarMargin))
                _mainTabsShell.Margin = sidebarMargin;
            if (_mainTabsShell.Visibility != Visibility.Visible)
                _mainTabsShell.Visibility = Visibility.Visible;
            if (_mainTabsShell.Opacity != 1)
                _mainTabsShell.Opacity = 1;
            if (_mainTabsShell.HorizontalAlignment != HorizontalAlignment.Left)
                _mainTabsShell.HorizontalAlignment = HorizontalAlignment.Left;
            if (_mainTabsShell.VerticalAlignment != VerticalAlignment.Stretch)
                _mainTabsShell.VerticalAlignment = VerticalAlignment.Stretch;
            if (Canvas.GetZIndex(_mainTabsShell) != 40)
                Canvas.SetZIndex(_mainTabsShell, 40);
            EnsureSidebarScrollHost();
        }
        if (_actionBarInitialized)
        {
            var actionMargin = new Thickness(ShellSidebarWidth + 20,0,20,20);
            if (!ActionBar.Margin.Equals(actionMargin))
                ActionBar.Margin = actionMargin;
        }
        // Hero visually continues underneath the custom titlebar; do not reserve a separate titlebar gap.
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
        // Navigation changes the caption extent only when switching between
        // Overview and a regular page. Don't make expensive native region calls
        // for each intermediate SizeChanged during a mouse resize.
        if (_customTitleBarPrimed &&
            (_heroDragSignature == null || _heroDragSignature.Value.Overview != overviewHero))
            UpdateHeroDragRegions();
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

        if (ApplyPageScrollKeyForPolicy(e.Key))
            e.Handled = true;
    }

    private bool ApplyPageScrollKeyForPolicy(Windows.System.VirtualKey key)
    {
        if (_pageScroll.VerticalScrollMode != ScrollMode.Enabled || _pageScroll.ScrollableHeight <= 1)
            return false;

        double target;
        switch (key)
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
                return false;
        }

        _pageScroll.ChangeView(null, target, null, true);
        return true;
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
        // The current Overview hero is intentionally lightweight. Building it on the
        // first frame avoids a visible empty-header -> real-header swap with negligible
        // startup cost. Expensive image/data work remains deferred separately.
        Border hero = BuildBlackOverviewHeader();
        hero.Background = UseGlassChrome ? ThemeColor(0, 0, 0, 0) : shellBackdrop;
        hero.BorderThickness = new Thickness(0);
        _heroDeferredCompleted = true;
        return hero;
    }

    private Border BuildMainTabs(Brush shellBackdrop)
    {
        var shell = new Border
        {
            Background = shellBackdrop,
            BorderBrush = _darkTheme ? ThemeColor(0,0,0,0) : BorderThemeBrush(),
            BorderThickness = _darkTheme ? new Thickness(0) : new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 16, 10, 16)
        };
        _mainTabsShell = shell;
        CategoryBar = new StackPanel { Spacing = 4 };
        shell.Child = new ScrollViewer
        {
            Content = CategoryBar,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        return shell;
    }

    private void EnsureSidebarScrollHost()
    {
        if (_mainTabsShell == null) return;
        if (_mainTabsShell.Child is ScrollViewer sv)
        {
            if (!ReferenceEquals(sv.Content, CategoryBar))
                sv.Content = CategoryBar;
            return;
        }

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

    private const int DeferredSidebarBatchSize = 3;
    private const int DeferredSidebarBatchBudgetMs = 8;
    private int _deferredSidebarBatchCount;
    private long _deferredSidebarLongestBatchMs;
}
