using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    internal async Task RunVisualSizeMatrixSmokeAsync()
    {
        await RunDeferredTitlebarSmokeAsync(); // Native border policy is applied after first paint.
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        SizeInt32 originalSize = appWindow.Size;
        string originalSection = _section;
        string? originalSub = _sub;
        var failures = new List<string>();
        var warnings = new List<string>();

        try
        {
            var sizes = new[]
            {
                new SizeInt32(980, 680),
                new SizeInt32(1320, 860),
                new SizeInt32(1600, 1000)
            };
            string[] sections = { "overview", "system", "network", "settings" };

            foreach (SizeInt32 size in sizes)
            {
                appWindow.Resize(size);
                await Task.Delay(120);
                foreach (string section in sections)
                {
                    _sub = null;
                    await ShowSectionAsync(section);
                    await WaitForPageScrollPolicyStableAsync();
                    if (section == "system")
                        await WaitForDeferredVirtualGroupsAsync("system");
                    AuditVisualTree(RootGrid, $"{section}@{size.Width}x{size.Height}", failures, warnings);
                    VerifyUiTypography(RootGrid, section == "system");
                    // One owner for padding across both the resize handler
                    // and section navigation. Previously a 16px compact write
                    // was immediately overwritten with 24px in the same turn.
                    double expectedSide = ShellViewportPaddingPolicy.IsCompact(RootGrid.ActualWidth) ? 16d : 24d;
                    var padding = PageHost.Padding;
                    if (Math.Abs(padding.Left - expectedSide) > .1 ||
                        Math.Abs(padding.Right - expectedSide) > .1 ||
                        Math.Abs(padding.Top - (section == "overview" ? 8d : 24d)) > .1 ||
                        Math.Abs(padding.Bottom - (section == "overview" ? 10d : 24d)) > .1)
                        failures.Add($"{section}@{size.Width}x{size.Height}: inconsistent responsive padding={padding.Left:0.#}/{padding.Top:0.#}/{padding.Right:0.#}/{padding.Bottom:0.#}, expectedSide={expectedSide:0.#}.");
                    if (UseGlassChrome) VerifyRoundedGlassWindow();

                    bool verticalOverflow = PageHost.ActualHeight > _pageScroll.ViewportHeight + 1.0;
                    ScrollMode expectedVertical = verticalOverflow ? ScrollMode.Enabled : ScrollMode.Disabled;
                    if (_pageScroll.VerticalScrollMode != expectedVertical)
                    {
                        // The virtualized System page can finish a deferred layout
                        // after the initial stable check. Recheck the real policy
                        // after its coalesced refresh; never silently ignore a
                        // persistent vertical overflow or stuck scroll state.
                        SchedulePageScrollPolicyRefresh();
                        await WaitForPageScrollPolicyStableAsync(1200);
                        verticalOverflow = PageHost.ActualHeight > _pageScroll.ViewportHeight + 1.0;
                        expectedVertical = verticalOverflow ? ScrollMode.Enabled : ScrollMode.Disabled;
                    }
                    if (_pageScroll.VerticalScrollMode != expectedVertical)
                        failures.Add($"{section}@{size.Width}x{size.Height}: vertical-scroll-policy actual={_pageScroll.VerticalScrollMode} expected={expectedVertical}");
                    if (_pageScroll.ScrollableWidth > 1.0)
                        failures.Add($"{section}@{size.Width}x{size.Height}: horizontal-overflow {_pageScroll.ScrollableWidth:0.##}px");

                    LogService.Info("visual-qa",
                        $"SIZE_MATRIX_METRICS section={section} size={size.Width}x{size.Height} viewport={_pageScroll.ViewportWidth:0.##}x{_pageScroll.ViewportHeight:0.##} pageHost={PageHost.ActualWidth:0.##}x{PageHost.ActualHeight:0.##} content={ContentStack.ActualWidth:0.##}x{ContentStack.ActualHeight:0.##} scrollable={_pageScroll.ScrollableWidth:0.##}x{_pageScroll.ScrollableHeight:0.##} mode={_pageScroll.VerticalScrollMode}");
                    if (failures.Count > 0)
                        throw new InvalidOperationException("Visual size regression: " + string.Join(" | ", failures.Take(12)));
                }
            }

            if (UseGlassChrome && appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                try
                {
                    presenter.Maximize();
                    await Task.Delay(120);
                    RootGrid.UpdateLayout();
                    UpdateRoundedGlassWindow();
                    if (NativeTitleBarService.HasCustomWindowRegion(_windowHandle))
                        throw new InvalidOperationException("Maximized glass frame clips the desktop or retains its rounded region.");
                }
                finally { presenter.Restore(); }
                await Task.Delay(120);
                RootGrid.UpdateLayout();
                VerifyRoundedGlassWindow();
                LogService.Info("visual-qa", "GLASS_MAXIMIZE_RESTORE_OK resizeEdges=4 regionRestored=true");
            }
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            LogService.Info("visual-qa",
                $"VISUAL_SIZE_MATRIX_OK sizes={sizes.Length} sections={sections.Length} scale={scale:0.###} warnings={warnings.Count}");
        }
        finally
        {
            appWindow.Resize(originalSize);
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }

    private async Task WaitForVisualCaptureReadyAsync(AppThemePreference theme, string section, bool requirePixelStability = true, bool requireSectionData = true)
    {
        string? previous = null;
        int stableSamples = 0;
        string lastReadiness = "not evaluated";

        for (int i = 0; i < 240; i++)
        {
            if (section == "overview") _windowTelemetryActive = true;
            bool themeReady =
                _themePreference == theme &&
                RootGrid.RequestedTheme == (_darkTheme ? ElementTheme.Dark : ElementTheme.Light);
            bool navReady =
                string.Equals(_section, section, StringComparison.OrdinalIgnoreCase) &&
                _loadCts?.IsCancellationRequested != true;
            bool shellReady = _mainTabsCompleted;
            bool heroReady =
                _heroDeferredCompleted && _heroImageFrame?.Child is Grid &&
                (section != "overview" || HeaderCard.Height >= 100);

            string visualText = CollectVisualText(ContentStack);
            bool overviewReady = !section.Equals("overview", StringComparison.OrdinalIgnoreCase) ||
                (_cpuGauge?.ValueText.Text is not (null or "\u2014") && _ramGauge?.ValueText.Text is not (null or "\u2014") && _overviewTelemetryGrid?.Children.Count == 6 && _gpuGauge?.StatusText.Text is not (null or "NA\u010c\u00cdT\u00c1M") && _diskGauge?.StatusText.Text is not (null or "NA\u010c\u00cdT\u00c1M") && _overviewMetricsHost?.Visibility == Visibility.Collapsed && _overviewInfoCardsGrid?.Children.Count == 3 && _overviewInfoFlyout == null && _overviewLiveHost?.Children.Count == 1 && _pageScroll.ActualHeight > 100);
            bool sectionReady = !requireSectionData || (overviewReady && (!section.Equals("network", StringComparison.OrdinalIgnoreCase) ||
                (!visualText.Contains("Zjišťuji aktivní připojení", StringComparison.OrdinalIgnoreCase) &&
                 !visualText.Contains("Načítám stav adaptéru", StringComparison.OrdinalIgnoreCase))));

            string signature = GetVisualStabilitySignature(RootGrid, out int pendingImages);
            lastReadiness = $"theme={themeReady} navigation={navReady} shell={shellReady} hero={heroReady} section={sectionReady} pendingImages={pendingImages} stableSamples={stableSamples}" +
                $" cpu={_cpuGauge?.ValueText.Text} ram={_ramGauge?.ValueText.Text} gpu={_gpuGauge?.StatusText.Text} disk={_diskGauge?.StatusText.Text}" +
                $" telemetry={_overviewTelemetryGrid?.Children.Count} metrics={_overviewMetricsHost?.Children.Count} live={_overviewLiveHost?.Children.Count} pageScroll={_pageScroll.ActualHeight:0}";
            if (themeReady && navReady && shellReady && heroReady && sectionReady && pendingImages == 0)
            {
                if (!requirePixelStability)
                {
                    if (++stableSamples >= 2)
                    {
                        await Task.Delay(80);
                        return;
                    }
                }
                else
                {
                    if (string.Equals(signature, previous, StringComparison.Ordinal))
                        stableSamples++;
                    else
                        stableSamples = 0;

                    if (stableSamples >= 2)
                    {
                        await Task.Delay(180);
                        return;
                    }
                }
            }
            else
            {
                stableSamples = 0;
            }

            previous = signature;
            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"Visual capture did not stabilize theme={theme} section={section} signature={previous} readiness={lastReadiness}");
    }

    private static string GetVisualStabilitySignature(DependencyObject root, out int pendingImages)
    {
        int nodes = 0;
        int visible = 0;
        int images = 0;
        int imagesPending = 0;
        long geometry = 0;

        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            nodes++;
            if (node is FrameworkElement fe && fe.Visibility == Visibility.Visible)
            {
                visible++;
                geometry += (long)Math.Round(fe.ActualWidth / 2.0);
                geometry += ((long)Math.Round(fe.ActualHeight / 2.0)) << 10;
            }

            if (node is Image image)
            {
                images++;
                if (image.Source == null || image.ActualWidth <= 0 || image.ActualHeight <= 0)
                    imagesPending++;
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }

        Walk(root);
        pendingImages = imagesPending;
        return $"{nodes}:{visible}:{images}:{geometry}:{(root as FrameworkElement)?.ActualWidth:0}:{(root as FrameworkElement)?.ActualHeight:0}";
    }

    internal async Task RunCommunityCursorLayoutSmokeAsync()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        var originalSize = appWindow.Size;
        string originalSection = _section;
        string? originalSub = _sub;
        var originalTheme = _themePreference;
        string output = Environment.GetEnvironmentVariable("SPANKY_CURSOR_VISUAL_DIR") ?? Path.Combine(LogService.Root, "community-cursor-visual");
        try
        {
            await ChangeThemeAsync(AppThemePreference.Dark);
            _sub = null;
            await ShowSectionAsync("cursor");
            foreach (var size in new[] { new SizeInt32(980, 680), new SizeInt32(1320, 860) })
            {
                appWindow.Resize(size);
                await Task.Delay(150);
                Task popup = ShowCommunityCursorPopupAsync();
                await Task.Delay(250);
                RootGrid.UpdateLayout();
                var elements = new List<FrameworkElement>();
                void Walk(DependencyObject node)
                {
                    if (node is FrameworkElement element) elements.Add(element);
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
                }
                Walk(RootGrid);
                var overlay = elements.OfType<Grid>().SingleOrDefault(e => e.Tag as string == "CommunityCursorModal");
                if (overlay == null)
                    throw new InvalidOperationException("Community cursor modal does not cover RootGrid/sidebar.");
                if (overlay.Background is not SolidColorBrush scrim || scrim.Color.A < 64 || scrim.Color.A > 112 ||
                    Math.Abs(overlay.ActualWidth - RootGrid.ActualWidth) > 1 || Math.Abs(overlay.ActualHeight - RootGrid.ActualHeight) > 1)
                    throw new InvalidOperationException("Community cursor modal glass scrim is outside the translucent separation range.");
                var regions = new[] { "CursorHeader", "CursorSearch", "CursorCategories", "CursorCards", "CursorActions" }
                    .Select(name => elements.Single(e => e.Name == name)).ToArray();
                double previousBottom = -1;
                foreach (var region in regions)
                {
                    var bounds = region.TransformToVisual(RootGrid).TransformBounds(new Windows.Foundation.Rect(0, 0, region.ActualWidth, region.ActualHeight));
                    if (region.ActualHeight <= 0 || bounds.Top < previousBottom - 0.5 || bounds.Bottom > RootGrid.ActualHeight + 0.5)
                        throw new InvalidOperationException("Community cursor layout overlap/clipping: " + region.Name);
                    previousBottom = bounds.Bottom;
                }
                var modalElements = new List<FrameworkElement>();
                void CollectModal(DependencyObject node)
                {
                    if (node is FrameworkElement element) modalElements.Add(element);
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) CollectModal(VisualTreeHelper.GetChild(node, i));
                }
                CollectModal(overlay);
                if (modalElements.OfType<TextBlock>().Count(e => e.Text == "Kurzory") != 1)
                    throw new InvalidOperationException("Cursor title is duplicated.");
                var cards = (ScrollViewer)regions[3];
                var categoryHost = ((ScrollViewer)regions[2]).Content as StackPanel;
                var originalCategoryChips = categoryHost?.Children.OfType<Button>().ToArray() ?? Array.Empty<Button>();
                if (originalCategoryChips.Length != CommunityCursorCategories.Length)
                    throw new InvalidOperationException("Cursor category buttons were not built.");
                var initialCount = modalElements.OfType<TextBlock>().FirstOrDefault(e => e.Name == "CursorCount");
                if (initialCount == null ||
                    !System.Text.RegularExpressions.Regex.IsMatch(initialCount.Text, @"^[1-9]\d* packů"))
                    throw new InvalidOperationException("Cursor library did not expose a non-empty pack count: " + (initialCount?.Text ?? "missing"));
                if (cards.ScrollableWidth > 1 || cards.ViewportHeight < 120)
                    throw new InvalidOperationException("Community cursor list viewport is clipped.");
                var search = elements.OfType<TextBox>().Single(e => e.Name == "CursorQuery");
                search.Text = "__no_cursor_match__";
                bool filtered = false;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    await Task.Delay(25);
                    RootGrid.UpdateLayout();
                    elements.Clear();
                    Walk(RootGrid);
                    if (elements.OfType<TextBlock>().Any(e => e.Name == "CursorCount" && e.Text == "0 packů kurzorů"))
                    {
                        filtered = true;
                        break;
                    }
                }
                if (!filtered)
                    throw new InvalidOperationException("Community cursor search did not filter after rendering settled.");
                var resultHost = cards.Content as StackPanel;
                var emptyResult = resultHost?.Children.FirstOrDefault();
                if (emptyResult is not TextBlock)
                    throw new InvalidOperationException("Community cursor empty-state card is missing.");
                search.Text = "__no_cursor_match__and_still_empty__";
                await Task.Delay(35);
                if (resultHost == null || resultHost.Children.Count != 1 ||
                    !ReferenceEquals(emptyResult, resultHost.Children[0]))
                    throw new InvalidOperationException(
                        "Unchanged cursor search results rebuilt the empty-state XAML.");
                search.Text = "";
                await Task.Delay(50);
                RootGrid.UpdateLayout();
                // Every search edit must preserve the button controls, focus,
                // handlers and scroll position rather than recreate nine chips.
                if (categoryHost == null ||
                    categoryHost.Children.Count != originalCategoryChips.Length ||
                    originalCategoryChips.Where((chip, i) =>
                        !ReferenceEquals(chip, categoryHost.Children[i])).Any())
                    throw new InvalidOperationException(
                        "Community cursor search recreated stable category controls.");

                // A real category change still selects a new category and
                // then restores All without reallocating the nine chips.
                foreach (int categoryIndex in new[] { 1, 0 })
                {
                    var chipPeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(
                        originalCategoryChips[categoryIndex]);
                    var chipInvoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)
                        chipPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                    chipInvoke.Invoke();
                    await Task.Delay(35);
                }
                if (categoryHost.Children.Count != originalCategoryChips.Length ||
                    originalCategoryChips.Where((chip, i) =>
                        !ReferenceEquals(chip, categoryHost.Children[i])).Any())
                    throw new InvalidOperationException(
                        "Community cursor category selection recreated stable controls.");
                RootGrid.UpdateLayout();
                // Filtering recreates previews; wait for the visible PNGs to decode before capture.
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    elements.Clear();
                    Walk(RootGrid);
                    var previews = elements.OfType<Image>().Where(image =>
                    {
                        var b = image.TransformToVisual(cards).TransformBounds(new Windows.Foundation.Rect(0, 0, image.ActualWidth, image.ActualHeight));
                        return b.Left >= 0 && b.Right <= cards.ActualWidth + 1 && b.Top < cards.ViewportHeight && b.Bottom > 0;
                    }).ToArray();
                    if (previews.Length >= 4 && previews.All(image => image.Source is BitmapImage bitmap && bitmap.PixelWidth > 0)) break;
                    if (attempt == 29) throw new InvalidOperationException("Visible cursor preview images did not decode.");
                    await Task.Delay(100);
                }
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    await SaveVisualSnapshotAsync(Path.Combine(output, $"community-cursor-{size.Width}x{size.Height}.png"));
                }
                var close = elements.OfType<Button>().Single(e => e.Name == "CursorClose");
                var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(close);
                var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                invoke.Invoke();
                await popup.WaitAsync(TimeSpan.FromSeconds(3));
                if (RootGrid.Children.Contains(overlay)) throw new InvalidOperationException("Community cursor close leaked overlay.");
                LogService.Info("visual-qa", $"COMMUNITY_CURSOR_LAYOUT_OK size={size.Width}x{size.Height} separation=yes overlap=no search=yes close=yes");
            }
        }
        finally
        {
            _closeCommunityCursorDialog?.Invoke();
            appWindow.Resize(originalSize);
            await ChangeThemeAsync(originalTheme);
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }
    internal async Task RunVisualCaptureSmokeAsync()
    {
        string outputDir = Environment.GetEnvironmentVariable("SPANKY_VISUAL_DIR") ?? "";
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = Path.Combine(LogService.Root, "visual-current");

        Directory.CreateDirectory(outputDir);
        foreach (string old in Directory.EnumerateFiles(outputDir, "*.png"))
            File.Delete(old);
        bool originalTelemetryActive = _windowTelemetryActive;
        if (_liveMetricsCts == null) StartLiveMetricsLoop();
        var originalTheme = _themePreference;
        string originalSection = _section;
        string? originalSub = _sub;

        try
        {
            var themes = new[] { AppThemePreference.Light, AppThemePreference.Dark };
            var targets = new[] { "overview", "system", "privacy", "network", "settings", "power", "ai-runtime", "gpu", "startup", "storage", "defender" };
            foreach (var theme in themes)
            {
                await ChangeThemeAsync(theme);
                foreach (string section in targets)
                {
                    _sub = null;
                    await ShowSectionAsync(section);
                    _windowTelemetryActive = true; // Capture uses real telemetry without taking foreground input.
                    await WaitForVisualCaptureReadyAsync(theme, section);
                    if (section == "overview")
                    {
                        var subtitleBounds = PageSubtitle.TransformToVisual(HeaderCard).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, PageSubtitle.ActualWidth, PageSubtitle.ActualHeight));
                        if (subtitleBounds.Bottom > HeaderCard.ActualHeight - 8)
                            throw new InvalidOperationException($"Overview subtitle clipped: theme={theme} bottom={subtitleBounds.Bottom:0.0} header={HeaderCard.ActualHeight:0.0}.");
                    }
                    string file = Path.Combine(outputDir, theme.ToString().ToLowerInvariant() + "-" + section + ".png");
                    await SaveVisualSnapshotAsync(file);
                    LogService.Info("visual-qa", "VISUAL_CAPTURE_OK " + file);
                }
                await CaptureStyledConfirmVisualAsync(theme, outputDir);
            }
        }
        finally
        {
            _sub = originalSub;
            await ChangeThemeAsync(originalTheme);
            await ShowSectionAsync(originalSection);
            _windowTelemetryActive = originalTelemetryActive;
        }
    }

    private async Task CaptureStyledConfirmVisualAsync(AppThemePreference theme, string outputDir)
    {
        await ShowSectionAsync("overview");
        await ValidateChangelogRefreshSmokeAsync();
        await ValidateRamTelemetrySmokeAsync();
        var dialogTask = ShowStyledConfirmAsync("Potvrdit zm\u011bny",
            "Vybran\u00e9 zm\u011bny jsou p\u0159ipraven\u00e9 k pou\u017eit\u00ed. Stav aplikace se po dokon\u010den\u00ed znovu ov\u011b\u0159\u00ed.",
            "Pou\u017e\u00edt", "Zru\u0161it");
        await Task.Delay(250);
        await SaveVisualSnapshotAsync(Path.Combine(outputDir, theme.ToString().ToLowerInvariant() + "-dialog.png"));
        Button? close = null;
        void FindClose(DependencyObject node)
        {
            if (node is Button b && AutomationProperties.GetName(b) is "Zav\u0159\u00edt" or "Zav\u0159\u00edt dialog") close = b;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) FindClose(VisualTreeHelper.GetChild(node, i));
        }
        FindClose(RootGrid);
        if (close == null) throw new InvalidOperationException("Confirmation close button missing.");
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(close);
        var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
        invoke.Invoke();
        if (await dialogTask.WaitAsync(TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("Visual confirmation smoke must cancel without applying.");
        LogService.Info("visual-qa", "VISUAL_DIALOG_CAPTURE_OK theme=" + theme + " canceled=true");
    }

    private Task SaveVisualSnapshotAsync(string path) => SaveVisualSnapshotAsync(RootGrid, path);

    private async Task SaveVisualSnapshotAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = buffer.ToArray();

        string dir = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        string name = Path.GetFileName(path);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(dir);
        StorageFile file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            pixels);
        await encoder.FlushAsync();
    }

    private static string Short(string value)
    {
        string oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 72 ? oneLine : oneLine[..69] + "...";
    }
}
