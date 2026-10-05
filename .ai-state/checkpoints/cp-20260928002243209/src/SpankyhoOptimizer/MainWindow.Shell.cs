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
    private Border HeaderCard = null!;
    private Border SubNavShell = null!;
    private Image? _heroBackgroundImage;
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
        ("USB / HID","usb"),("Po spuštění","startup"),("UI / Explorer","ui"),("Windows Update","update"),
        ("Prohlížeče","browsers"),("Spotify / Spicetify","spicetify"),("Programy / Launchery","apps"),("Soukromí","privacy"),("AI / Recall","ai"),
        ("Defender","defender"),("Debloater","debloat"),("Správa a služby","components"),("Fixer","fixer"),("PowerShell","powershell"),("Nastavení","settings")
    };

    private static Brush? _shellDarkBackdrop;

    private static Brush BuildShellBackdrop()
    {
        if (!_darkTheme) return SurfaceAltBrush();
        if (_shellDarkBackdrop != null) return _shellDarkBackdrop;

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
        LogService.Info("performance", $"SHELL_ROOT_PHASE backdropMs={backdropMs} gridMs={rootCreateMs-backdropMs} totalMs={rootCreateMs}");
        // Root uses fixed sidebar geometry without ColumnDefinition activation.
        // ColumnDefinition WinUI activation costs ~14-21 ms per fresh process on this machine.
        long rootColumnsMs = shellPhase.ElapsedMilliseconds;
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(272) });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        long rootLayoutMs = shellPhase.ElapsedMilliseconds;

        HeaderCard = BuildHeroPlaceholder();
        Grid.SetColumnSpan(HeaderCard, 2);
        RootGrid.Children.Add(HeaderCard);
        long heroMs = shellPhase.ElapsedMilliseconds;

        var mainTabs = BuildMainTabs();
        mainTabs.Width = 218;
        mainTabs.HorizontalAlignment = HorizontalAlignment.Left;
        mainTabs.Margin = new Thickness(0, 62, 0, 0);
        Grid.SetRow(mainTabs, 0);
        Grid.SetRowSpan(mainTabs, 4);
        Canvas.SetZIndex(mainTabs, 40);
        RootGrid.Children.Add(mainTabs);
        long tabsMs = shellPhase.ElapsedMilliseconds;

        var subNav = BuildSubNavigation();
        subNav.Margin = new Thickness(218, 0, 0, 0);
        Grid.SetRow(subNav, 1);
        RootGrid.Children.Add(subNav);
        long subNavMs = shellPhase.ElapsedMilliseconds;

        _pageScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Background = shellBackdrop
        };
        PageHost = new Grid { Padding = new Thickness(16, 8, 16, 10), Background = shellBackdrop };
        PageHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PageHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ContentStack = new StackPanel { Spacing = 0 };
        Grid.SetRow(ContentStack, 0);
        PageHost.Children.Add(ContentStack);
        TopActions = new StackPanel { Spacing = 10, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(TopActions, 1);
        PageHost.Children.Add(TopActions);
        _pageScroll.Content = PageHost;
        _pageScroll.Margin = new Thickness(218, 0, 0, 0);
        Grid.SetRow(_pageScroll, 2);
        RootGrid.Children.Add(_pageScroll);
        long hostMs = shellPhase.ElapsedMilliseconds;

        ActionBar = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(218, 0, 0, 0) };
        Grid.SetRow(ActionBar, 3);
        RootGrid.Children.Add(ActionBar);
        long actionMs = shellPhase.ElapsedMilliseconds;

        RootGrid.SizeChanged += (_, e) =>
        {
            bool compactWidth = e.NewSize.Width < 1180;
            PageHost.Padding = compactWidth ? new Thickness(13, 7, 13, 9) : new Thickness(16, 8, 16, 10);
            double exactHeroHeight = Math.Clamp(((e.NewSize.Width - 254d) * 216d) / 1256d + 62d, 246d, 284d);
            RootGrid.RowDefinitions[0].Height = new GridLength(exactHeroHeight);
        };

        AdminBadge = new TextBlock { Text = "Správce", Visibility = Visibility.Collapsed };
        long titleStartMs = shellPhase.ElapsedMilliseconds;
        AppTitleBar = BuildCustomTitleBar();
        AppTitleBar.Visibility = Visibility.Collapsed;
        long titleBuildMs = shellPhase.ElapsedMilliseconds;
        Grid.SetColumnSpan(AppTitleBar, 2);
        Grid.SetRow(AppTitleBar, 0);
        AppTitleBar.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetZIndex(AppTitleBar, 200);
        RootGrid.Children.Add(AppTitleBar);
        long titleAttachMs = shellPhase.ElapsedMilliseconds;
        Content = RootGrid;
        long titleSetMs = shellPhase.ElapsedMilliseconds;
        LogService.Info("performance", $"TITLEBAR_BUILD_PHASE buildMs={titleBuildMs-titleStartMs} attachMs={titleAttachMs-titleBuildMs} setMs={titleSetMs-titleAttachMs} deferredActivation=true");
        long totalMs = shellPhase.ElapsedMilliseconds;
        LogService.Info("performance", $"SHELL_PHASE rootCreateMs={rootCreateMs} columnsMs={rootColumnsMs - rootCreateMs} rowsMs={rootLayoutMs - rootColumnsMs} rootLayoutMs={rootLayoutMs - rootCreateMs} placeholderMs={heroMs - rootLayoutMs} heroMs={heroMs} tabsMs={tabsMs - heroMs} subNavMs={subNavMs - tabsMs} hostMs={hostMs - subNavMs} actionMs={actionMs - hostMs} tailMs={totalMs - actionMs} totalMs={totalMs}");
    }

    private Border BuildHeroPlaceholder()
    {
        HeroTextPanel = new StackPanel { Visibility = Visibility.Collapsed };
        PageTitle = new TextBlock();
        PageSubtitle = new TextBlock();
        return new Border
        {
            Background = _darkTheme ? ThemeColor(5, 7, 12) : SurfaceBrush(),
            BorderBrush = _darkTheme ? ThemeColor(26, 30, 42) : BorderThemeBrush(),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    private void CompleteDeferredHero()
    {
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var old = HeaderCard;
        var full = BuildCinematicHero();
        full.Margin = new Thickness(0);
        Grid.SetColumnSpan(full, 2);
        int index = RootGrid.Children.IndexOf(old);
        if (index >= 0) RootGrid.Children.RemoveAt(index);
        if (index >= 0) RootGrid.Children.Insert(index, full);
        else RootGrid.Children.Add(full);
        HeaderCard = full;

        if (CatalogService.TryGetSection(_section, out var section) && section != null)
        {
            PageTitle.Text = section.Title;
            PageSubtitle.Text = section.Subtitle;
        }
        HeroTextPanel.Visibility = Visibility.Collapsed;
        long layoutMs = phase.ElapsedMilliseconds;
        LoadShellImagesDeferred();
        LogService.Info("performance", $"HERO_DEFERRED_STAGE layoutMs={layoutMs} imageSourceMs={phase.ElapsedMilliseconds - layoutMs} totalMs={phase.ElapsedMilliseconds}");
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
            Source = new BitmapImage(new Uri("ms-appx:///Assets/HeroTargetExact.png")),
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _heroBackgroundImage = image;

        var frame = new Border
        {
            Margin = new Thickness(231, 62, 16, 0),
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(0),
            Background = _darkTheme ? ThemeColor(7, 9, 24) : SurfaceElevatedBrush(),
            Child = image
        };

        var grid = new Grid();
        grid.Children.Add(frame);

        var titleHeroBlend = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        if (_darkTheme)
        {
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(248, 8, 11, 17), Offset = 0 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(190, 14, 19, 29), Offset = .46 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(112, 20, 28, 43), Offset = .72 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(42, 31, 43, 64), Offset = .91 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 35, 48, 70), Offset = 1 });
        }
        else
        {
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(252, _themePalette.Surface.R, _themePalette.Surface.G, _themePalette.Surface.B), Offset = 0 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(220, _themePalette.Surface.R, _themePalette.Surface.G, _themePalette.Surface.B), Offset = .46 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(140, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B), Offset = .72 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(55, _themePalette.AccentSoft.R, _themePalette.AccentSoft.G, _themePalette.AccentSoft.B), Offset = .91 });
            titleHeroBlend.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, _themePalette.Accent.R, _themePalette.Accent.G, _themePalette.Accent.B), Offset = 1 });
        }
        grid.Children.Add(new Border
        {
            Height = 76,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = titleHeroBlend,
            IsHitTestVisible = false
        });

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
    private Border BuildMainTabs()
    {
        var shell = new Border
        {
            Background = BuildShellBackdrop(),
            BorderBrush = BorderThemeBrush(),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12, 12, 12, 16)
        };
        CategoryBar = new StackPanel { Spacing = 5 };
        shell.Child = new ScrollViewer
        {
            Content = CategoryBar,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };
        return shell;
    }

    private async System.Threading.Tasks.Task ScheduleDeferredSidebarAfterFirstRenderAsync()
    {
        await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
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
        if (!_mainTabsCompleted || CategoryBar.Children.Count != 21 || _categoryButtons.Count != 21)
            throw new InvalidOperationException($"Deferred sidebar incomplete: completed={_mainTabsCompleted} children={CategoryBar.Children.Count} buttons={_categoryButtons.Count}.");

        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Button button in _categoryButtons.Values)
        {
            if (button.Tag is not string section || button.Content is not StackPanel row || row.Children.Count == 0 || row.Children[0] is not Grid)
                throw new InvalidOperationException("Sidebar icon smoke expected custom SVG host as the first menu element.");
            assets.Add(SidebarIconName(section));
        }
        if (assets.Count != 21)
            throw new InvalidOperationException($"Sidebar icons are not unique: {assets.Count}/21 unique assets.");
        if (!ReferenceEquals(SidebarIconSource("gpu"), SidebarIconSource("gpu")) ||
            !ReferenceEquals(SidebarIconSource("network"), SidebarIconSource("network")))
            throw new InvalidOperationException("Sidebar SVG source cache did not reuse image sources.");

        foreach (string asset in HardwareIconMap.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SidebarIcons", asset + ".svg");
            if (!System.IO.File.Exists(path))
                throw new InvalidOperationException("Mapped V13 icon asset is missing: " + asset + ".svg");
        }
        if (!ReferenceEquals(V13IconSource("activity"), V13IconSource("overview")) ||
            !ReferenceEquals(V13IconSource("restore"), V13IconSource("update")))
            throw new InvalidOperationException("V13 utility icon alias/source cache mismatch.");

        LogService.Info("SIDEBAR_DEFERRED_SMOKE_OK uniqueAssets=21 style=CustomSvg sourceCache=true utilityAssetsVerified=true");
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

        for (int i = 0; i < 60 && _prewarmedActionBar == null; i++)
            await System.Threading.Tasks.Task.Delay(25);
        if (_prewarmedActionBar == null)
            throw new InvalidOperationException("Action bar idle prewarm did not complete.");

        var attach = System.Diagnostics.Stopwatch.StartNew();
        EnsureActionBarInitialized();
        long attachMs = attach.ElapsedMilliseconds;
        if (!_actionBarInitialized || ActionBar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Action bar factory path failed to initialize a collapsed bar.");
        if (attachMs > 12)
            throw new InvalidOperationException($"Prewarmed action bar attach is too slow: {attachMs} ms.");

        LogService.Info("ACTIONBAR_LAZY_SMOKE_OK cleanStateSkipped=true prewarmed=true factoryVerified=true attachMs=" + attachMs);
    }

    private void AddMainSection(StackPanel host, string title, string section)
    {
        bool preview = IsUiPreviewBuild();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, VerticalAlignment = VerticalAlignment.Center };
        var menuIcon = SidebarIcon(section, preview ? 27 : 26);
        menuIcon.Margin = new Thickness(0, 0, 1, 0);
        row.Children.Add(menuIcon);
        row.Children.Add(new TextBlock { Text = title, FontSize = preview ? 13.2 : 12, FontWeight = FontWeights.SemiBold, Foreground = preview ? ThemeColor(232, 237, 248) : null, TextWrapping = TextWrapping.Wrap, MaxLines = 2, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Tag = section,
            Content = row,
            MinHeight = preview ? 49 : 49,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = preview ? new Thickness(9, 5, 10, 5) : new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(10),
            Background = SidebarButtonBackground(active: false),
            BorderBrush = SidebarButtonBorder(active: false),
            BorderThickness = new Thickness(1),
            Foreground = preview ? ThemeColor(232, 237, 248) : TextSecondaryBrush()
        };
        AttachHoverMotion(button, 1.008f);
        button.Click += (_, _) => SafeTaskService.Forget(NavigateMainSectionFromUiAsync(section), "navigation", "Open " + section);
        SetCategoryButtonVisualState(button, string.Equals(section, _section, StringComparison.OrdinalIgnoreCase));
        _categoryButtons[section] = button;
        host.Children.Add(button);
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
        button.Foreground = active ? ThemeColor(255, 255, 255) : TextSecondaryBrush();
        button.Background = SidebarButtonBackground(active);
        button.BorderBrush = SidebarButtonBorder(active);
        button.BorderThickness = active ? new Thickness(1.4, 1.2, 3.0, 1.2) : new Thickness(1);
    }

    private static Brush? _sidebarActiveBackground;
    private static Brush? _sidebarInactiveBackground;
    private static Brush? _sidebarActiveBorder;
    private static Brush? _sidebarInactiveBorder;

    private static Brush SidebarButtonBackground(bool active)
    {
        if (active && _sidebarActiveBackground != null) return _sidebarActiveBackground;
        if (!active && _sidebarInactiveBackground != null) return _sidebarInactiveBackground;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        if (active)
        {
            var soft = _themePalette.PrimaryButtonSoft;
            var surface = _themePalette.Surface;
            var accent = _themePalette.Accent;
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, soft.R, soft.G, soft.B), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, surface.R, surface.G, surface.B), Offset = .22 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, surface.R, surface.G, surface.B), Offset = .76 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(120, accent.R, accent.G, accent.B), Offset = 1 });
        }
        else
        {
            var alt = _themePalette.SurfaceAlt;
            var surface = _themePalette.Surface;
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, alt.R, alt.G, alt.B), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, surface.R, surface.G, surface.B), Offset = .82 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, alt.R, alt.G, alt.B), Offset = 1 });
        }
        if (active) _sidebarActiveBackground = brush;
        else _sidebarInactiveBackground = brush;
        return brush;
    }

    private static Brush SidebarButtonBorder(bool active)
    {
        if (active && _sidebarActiveBorder != null) return _sidebarActiveBorder;
        if (!active && _sidebarInactiveBorder != null) return _sidebarInactiveBorder;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0)
        };
        var border = _themePalette.Border;
        var primary = _themePalette.PrimaryButtonBorder;
        var accent = _themePalette.Accent;
        brush.GradientStops.Add(new GradientStop
        {
            Color = active
                ? Windows.UI.Color.FromArgb(255, primary.R, primary.G, primary.B)
                : Windows.UI.Color.FromArgb(255, border.R, border.G, border.B),
            Offset = 0
        });
        brush.GradientStops.Add(new GradientStop
        {
            Color = active
                ? Windows.UI.Color.FromArgb(220, border.R, border.G, border.B)
                : Windows.UI.Color.FromArgb(255, border.R, border.G, border.B),
            Offset = .68
        });
        brush.GradientStops.Add(new GradientStop
        {
            Color = active
                ? Windows.UI.Color.FromArgb(210, accent.R, accent.G, accent.B)
                : Windows.UI.Color.FromArgb(210, border.R, border.G, border.B),
            Offset = 1
        });
        if (active) _sidebarActiveBorder = brush;
        else _sidebarInactiveBorder = brush;
        return brush;
    }


    private void EnsureActionBarInitialized()
    {
        if (_actionBarInitialized) return;
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var old = ActionBar;
        var bar = _prewarmedActionBar ?? BuildActionBar();
        _prewarmedActionBar = null;
        Grid.SetRow(bar, 3);
        bar.Margin = new Thickness(273, 0, 28, 16);
        RootGrid.Children.Remove(old);
        RootGrid.Children.Add(bar);
        ActionBar = bar;
        _actionBarInitialized = true;
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
    }

}
