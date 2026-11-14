using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    internal async Task RunSectionTabTraversalSmokeAsync()
    {
        string originalSection = _section;
        string? originalSub = _sub;
        var failures = new List<string>();

        try
        {
            var targets = new (string Section, string? Sub)[]
            {
                ("overview", null),
                ("network", null),
                ("power", null),
                ("settings", null)
            };

            if (!ActivateKeyboardInputWindow())
                throw new InvalidOperationException("Section Tab smoke window could not become foreground before SendInput.");
            await Task.Delay(60);

            foreach (var target in targets)
            {
                _sub = target.Sub;
                await ShowSectionAsync(target.Section);
                await Task.Delay(80);

                var contentStops = new List<Control>();
                CollectVisibleTabStops(ContentStack, contentStops);
                if (contentStops.Count == 0)
                    continue;

                Control first = contentStops[0];
                if (!first.Focus(FocusState.Programmatic))
                {
                    failures.Add($"{target.Section}: first content control could not receive focus");
                    continue;
                }

                await Task.Delay(30);
                object? previous = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                var visited = new HashSet<DependencyObject>();
                if (previous is DependencyObject initial) visited.Add(initial);

                int steps = Math.Min(Math.Max(contentStops.Count - 1, 2), 8);
                for (int i = 0; i < steps; i++)
                {
                    SendKeyboardKey(0x09);
                    await Task.Delay(65);

                    object? focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                    if (focused is not Control control)
                    {
                        failures.Add($"{target.Section}: Tab landed on non-control/null at step {i + 1}");
                        break;
                    }
                    if (!control.IsEnabled || !control.IsTabStop || control.Visibility != Visibility.Visible)
                    {
                        failures.Add($"{target.Section}: Tab landed on invalid control {control.GetType().Name}");
                        break;
                    }
                    if (!IsDescendantOf(control, RootGrid))
                    {
                        failures.Add($"{target.Section}: focus escaped the window");
                        break;
                    }
                    if (ReferenceEquals(previous, focused))
                    {
                        failures.Add($"{target.Section}: focus got stuck on {control.GetType().Name}");
                        break;
                    }

                    visited.Add(control);
                    previous = focused;
                }

                if (visited.Count < Math.Min(3, Math.Max(1, contentStops.Count)))
                    failures.Add($"{target.Section}: too few unique focus targets visited={visited.Count} contentStops={contentStops.Count}");

                object? beforeBack = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                SendShiftTab();
                await Task.Delay(65);
                object? afterBack = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                if (afterBack is not Control backControl ||
                    !backControl.IsEnabled ||
                    !backControl.IsTabStop ||
                    !IsDescendantOf(backControl, RootGrid) ||
                    ReferenceEquals(beforeBack, afterBack))
                {
                    failures.Add($"{target.Section}: Shift+Tab traversal failed");
                }

                LogService.Info("visual-qa",
                    $"SECTION_TAB_TRAVERSAL section={target.Section} contentStops={contentStops.Count} visited={visited.Count}");
            }

            if (failures.Count > 0)
                throw new InvalidOperationException("Section Tab traversal regression: " + string.Join(" | ", failures.Take(12)));

            LogService.Info("visual-qa", "SECTION_TAB_TRAVERSAL_SMOKE_OK sections=4");
        }
        finally
        {
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }

    private static void CollectVisibleTabStops(DependencyObject root, List<Control> output)
    {
        if (root is Control control &&
            control.Visibility == Visibility.Visible &&
            control.IsEnabled &&
            control.IsTabStop &&
            control.ActualWidth > 0 &&
            control.ActualHeight > 0)
        {
            output.Add(control);
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            CollectVisibleTabStops(VisualTreeHelper.GetChild(root, i), output);
    }

    internal async Task RunKeyboardAccessibilitySmokeAsync()
    {
        string originalSection = _section;
        string? originalSub = _sub;
        var failures = new List<string>();
        try
        {
            var targets = new (string Section, string? Sub)[]
            {
                ("overview", null), ("network", null), ("storage", null), ("startup", null),
                ("gpu", null), ("power", null), ("defender", null),
                ("components", "Služby"), ("components", "Task Scheduler"), ("settings", null)
            };

            foreach (var target in targets)
            {
                _sub = target.Sub;
                await ShowSectionAsync(target.Section);
                await Task.Delay(20);
                AuditKeyboardTree(RootGrid, target.Section + (target.Sub == null ? "" : "/" + target.Sub), failures);
                if (failures.Count > 0)
                    throw new InvalidOperationException("Keyboard/accessibility regression: " + string.Join(" | ", failures.Take(12)));
            }

            RunDialogKeyboardContractSmoke();
            LogService.Info("visual-qa", $"KEYBOARD_ACCESSIBILITY_SMOKE_OK sections={targets.Length}");
        }
        finally
        {
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }

    private static void AuditKeyboardTree(DependencyObject root, string scope, List<string> failures)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Control control && child is FrameworkElement fe &&
                fe.Visibility == Visibility.Visible && control.IsEnabled)
            {
                bool interactive = control is Button or ToggleSwitch or CheckBox or ComboBox;
                if (interactive && !control.IsTabStop)
                    failures.Add($"{scope}: not-tabstop {control.GetType().Name}");

                if (interactive)
                {
                    string name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        if (control is Button button)
                        {
                            name = ExtractAccessibleText(button.Content) ?? "";
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                object? tip = ToolTipService.GetToolTip(button);
                                name = tip switch { string s => s, TextBlock tb => tb.Text, _ => "" };
                            }
                        }
                        else if (control is ToggleSwitch ts && ts.Header != null) name = ts.Header.ToString() ?? "";
                        else if (control is CheckBox cb && cb.Content != null) name = ExtractAccessibleText(cb.Content) ?? cb.Content.ToString() ?? "";
                        else if (control is ComboBox combo && combo.Header != null) name = combo.Header.ToString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(name))
                        failures.Add($"{scope}: unnamed {control.GetType().Name}");
                }
            }
            AuditKeyboardTree(child, scope, failures);
        }
    }

    private void RunDialogKeyboardContractSmoke()
    {
        var overlay = CreateDialogOverlay(12795);
        var card = CreateDialogCard(520, 460, z: 36);
        var actions = DialogActions();
        var secondary = StyledDialogButton("Zrušit", false, () => Task.CompletedTask);
        var primary = StyledDialogButton("Použít", true, () => Task.CompletedTask);
        actions.Children.Add(secondary);
        actions.Children.Add(primary);
        card.Child = actions;

        var close = MountDialog(overlay, card, () => UnmountDialog(overlay));
        if (close == null) throw new InvalidOperationException("Dialog close button missing.");
        if (!close.IsTabStop || !primary.IsTabStop || !secondary.IsTabStop)
            throw new InvalidOperationException("Dialog action is not reachable by Tab.");
        if (string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(close)) ||
            string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(primary)) ||
            string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(secondary)))
            throw new InvalidOperationException("Dialog action has no AutomationName.");

        bool hasEscape = overlay.KeyboardAccelerators.Any(x => x.Key == Windows.System.VirtualKey.Escape);
        bool hasEnter = overlay.KeyboardAccelerators.Any(x => x.Key == Windows.System.VirtualKey.Enter);
        if (!hasEscape || hasEnter)
            throw new InvalidOperationException($"Dialog accelerator contract mismatch: escape={hasEscape} globalEnter={hasEnter}.");

        if (overlay.Children.FirstOrDefault() is not Grid host ||
            host.TabFocusNavigation != Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle)
            throw new InvalidOperationException("Dialog host does not cycle Tab focus.");

        FrameworkElement[] tabOrder = { secondary, primary, close };
        foreach (FrameworkElement target in tabOrder)
        {
            if (!target.Focus(FocusState.Programmatic))
                throw new InvalidOperationException($"Dialog focus transition failed for {target.GetType().Name}.");
            object? focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
            if (!ReferenceEquals(focused, target))
                throw new InvalidOperationException("Dialog focus transition did not land on the requested control.");
            if (focused is not DependencyObject dep || !IsDescendantOf(dep, host))
                throw new InvalidOperationException("Dialog focus transition escaped the focus trap.");
        }

        if (!secondary.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Dialog focus cycle did not return to the first action.");

        UnmountDialog(overlay);
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        DependencyObject? current = child;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private async Task WaitForPageScrollPolicyStableAsync(int timeoutMs = 1200)
    {
        int stable = 0;
        double lastHeight = -1;
        int steps = Math.Max(1, timeoutMs / 25);

        for (int i = 0; i < steps; i++)
        {
            double viewport = _pageScroll.ViewportHeight;
            double height = PageHost.ActualHeight;
            bool overflow = viewport > 0 && height > viewport + 1.0;
            ScrollMode expected = overflow ? ScrollMode.Enabled : ScrollMode.Disabled;
            bool heightStable = lastHeight >= 0 && Math.Abs(height - lastHeight) < 0.5;
            bool policyMatches = _pageScroll.VerticalScrollMode == expected;

            if (heightStable && policyMatches)
            {
                stable++;
                if (stable >= 3)
                    return;
            }
            else
            {
                stable = 0;
            }

            lastHeight = height;
            await Task.Delay(25);
        }

        throw new InvalidOperationException(
            $"Page scroll policy did not stabilize viewport={_pageScroll.ViewportHeight:0.##} height={PageHost.ActualHeight:0.##} mode={_pageScroll.VerticalScrollMode}");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SidebarNativePoint { public int X; public int Y; }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref SidebarNativePoint point);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out SidebarNativePoint point);

    internal async Task RunSidebarScrollInputSmokeAsync()
    {
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        SizeInt32 originalSize = appWindow.Size;
        try
        {
            for (int rebuild = 0; rebuild < 2; rebuild++)
            {
                if (rebuild == 1)
                    await ChangeThemeAsync(_themePreference);
                for (int size = 0; size < 2; size++)
                {
                    appWindow.Resize(size == 0 ? new SizeInt32(1320, 860) : new SizeInt32(1100, 650));
                    await Task.Delay(140);
                    for (int i = 0; i < 80 && (!_mainTabsCompleted ||
                        _mainTabsShell?.Child is not ScrollViewer { ViewportHeight: > 0 }); i++)
                        await Task.Delay(25);
                    if (_mainTabsShell?.Child is not ScrollViewer scroll)
                        throw new InvalidOperationException($"Sidebar scroll host missing after theme rebuild={rebuild}.");
                    RootGrid.UpdateLayout();
                    LogService.Info("visual-qa", $"SIDEBAR_SCROLL_GEOMETRY rebuild={rebuild} size={size} root={RootGrid.ActualHeight:0.#} viewport={scroll.ViewportHeight:0.#} extent={scroll.ExtentHeight:0.#} range={scroll.ScrollableHeight:0.#}");
                    if (scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Hidden ||
                        scroll.VerticalScrollMode != ScrollMode.Enabled ||
                        scroll.ViewportHeight < 50 || scroll.ScrollableHeight <= 1 ||
                        scroll.ViewportHeight > RootGrid.ActualHeight)
                        throw new InvalidOperationException($"Sidebar viewport not scrollable rebuild={rebuild} viewport={scroll.ViewportHeight} extent={scroll.ExtentHeight} range={scroll.ScrollableHeight}.");

                    scroll.ChangeView(null, 0, null, true);
                    await Task.Delay(40);
                    double pageOffset = _pageScroll.VerticalOffset;

                    double probeOffset = Math.Min(
                        scroll.ScrollableHeight,
                        Math.Max(24, scroll.ViewportHeight * 0.35));
                    scroll.ChangeView(null, probeOffset, null, true);
                    await Task.Delay(50);
                    double scrollProbe = scroll.VerticalOffset;
                    if (scrollProbe < 1)
                        throw new InvalidOperationException($"Sidebar programmatic scroll did not move rebuild={rebuild} size={size}.");

                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                    await Task.Delay(50);
                    Button last = _categoryButtons[MainSectionItems[^1].Section];
                    var lastOrigin = last.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point(0, 0));
                    if (scroll.VerticalOffset < scroll.ScrollableHeight - 2 ||
                        lastOrigin.Y < -2 || lastOrigin.Y + last.ActualHeight > scroll.ViewportHeight + 2)
                        throw new InvalidOperationException($"Last sidebar item not fully reachable rebuild={rebuild} top={lastOrigin.Y} height={last.ActualHeight} viewport={scroll.ViewportHeight}.");

                    scroll.ChangeView(null, 0, null, true);
                    await Task.Delay(50);
                    if (scroll.VerticalOffset > 1 || Math.Abs(_pageScroll.VerticalOffset - pageOffset) > 1)
                        throw new InvalidOperationException("Sidebar scroll did not return to top or leaked into page scrolling.");

                    LogService.Info("visual-qa", $"SIDEBAR_SCROLL_POLICY_OK rebuild={rebuild} size={size} probe={scrollProbe:0.#} lastReachable=true hiddenScrollbar=true pageUnchanged=true");
                }
            }
        }
        finally
        {
            appWindow.Resize(originalSize);
            if (_mainTabsShell?.Child is ScrollViewer current) current.ChangeView(null, 0, null, true);
        }
    }

    internal async Task RunPageScrollInputSmokeAsync()
    {
        // Coalescing policy: page hydration must not force synchronous layout
        // while a window resize is still producing SizeChanged events. Once
        // the final resize quiet period expires, a queued pass must execute.
        if (PageScrollResizeRefreshDue(TimeSpan.Zero) ||
            PageScrollResizeRefreshDue(TimeSpan.FromMilliseconds(60)) ||
            PageScrollResizeRefreshDue(TimeSpan.FromMilliseconds(109)) ||
            !PageScrollResizeRefreshDue(TimeSpan.FromMilliseconds(110)) ||
            !PageScrollResizeRefreshDue(TimeSpan.MaxValue))
            throw new InvalidOperationException(
                "Resize quiet-window policy lost coalescing or first-page immediacy.");

        // Scheduling new page content (e.g. metrics added after navigation)
        // cannot restart the native window-resize clock. A real viewport
        // resize still must update it, and subsequent content hydration must
        // leave the recorded timestamp untouched.
        long savedResizeTicks = _lastPageScrollResizeTicks;
        try
        {
            _lastPageScrollResizeTicks = 0;
            SchedulePageScrollPolicyRefresh();
            if (_lastPageScrollResizeTicks != 0)
                throw new InvalidOperationException("Content hydration incorrectly marked a window resize.");
            SchedulePageScrollPolicyRefresh(fromResize: true);
            long nativeResizeTicks = _lastPageScrollResizeTicks;
            if (nativeResizeTicks == 0)
                throw new InvalidOperationException("Actual window resize did not establish a quiet fence.");
            SchedulePageScrollPolicyRefresh();
            if (_lastPageScrollResizeTicks != nativeResizeTicks)
                throw new InvalidOperationException("Content hydration extended active resize quiet fence.");
        }
        finally
        {
            _lastPageScrollResizeTicks = savedResizeTicks;
        }

        await RunSidebarScrollInputSmokeAsync();
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        SizeInt32 originalSize = appWindow.Size;
        string originalSection = _section;
        string? originalSub = _sub;

        try
        {
            appWindow.Resize(new SizeInt32(1320, 860));
            await Task.Delay(140);

            async Task AssertScrollablePolicyAsync(string section)
            {
                _sub = null;
                await ShowSectionAsync(section);

                for (int i = 0; i < 80; i++)
                {
                    UpdateRowlessShellGeometry();
                    RootGrid.UpdateLayout();
                    if (_pageScroll.ViewportHeight > 0 &&
                        PageHost.ActualHeight > _pageScroll.ViewportHeight + 1)
                        break;
                    await Task.Delay(25);
                }

                ApplyPageScrollPolicyNow();
                await WaitForPageScrollPolicyStableAsync(2000);

                double lastScrollableHeight = -1;
                int stableHeightSamples = 0;
                for (int i = 0; i < 50 && stableHeightSamples < 4; i++)
                {
                    UpdateRowlessShellGeometry();
                    RootGrid.UpdateLayout();
                    double currentScrollableHeight = _pageScroll.ScrollableHeight;
                    if (lastScrollableHeight >= 0 && Math.Abs(currentScrollableHeight - lastScrollableHeight) <= 1)
                        stableHeightSamples++;
                    else
                        stableHeightSamples = 0;
                    lastScrollableHeight = currentScrollableHeight;
                    if (stableHeightSamples < 4)
                        await Task.Delay(50);
                }

                if (_pageScroll.VerticalScrollMode != ScrollMode.Enabled || _pageScroll.ScrollableHeight <= 1)
                    throw new InvalidOperationException(
                        $"{section}: expected a vertically scrollable final page viewport={_pageScroll.ViewportHeight:0.##} height={PageHost.ActualHeight:0.##}.");

                _pageScroll.ChangeView(null, 0, null, true);
                await Task.Delay(35);

                if (!ApplyPageScrollKeyForPolicy(Windows.System.VirtualKey.PageDown))
                    throw new InvalidOperationException($"{section}: PageDown policy was not handled.");
                await Task.Delay(45);
                double afterPageDown = _pageScroll.VerticalOffset;
                if (afterPageDown < 1)
                    throw new InvalidOperationException($"{section}: PageDown policy did not move the scroll offset.");

                if (!ApplyPageScrollKeyForPolicy(Windows.System.VirtualKey.End))
                    throw new InvalidOperationException($"{section}: End policy was not handled.");
                await Task.Delay(45);
                double afterEnd = _pageScroll.VerticalOffset;
                if (afterEnd < _pageScroll.ScrollableHeight - 3)
                    throw new InvalidOperationException($"{section}: End policy did not reach bottom offset={afterEnd:0.##}/{_pageScroll.ScrollableHeight:0.##}.");

                if (!ApplyPageScrollKeyForPolicy(Windows.System.VirtualKey.Home))
                    throw new InvalidOperationException($"{section}: Home policy was not handled.");
                await Task.Delay(45);
                if (_pageScroll.VerticalOffset > 1)
                    throw new InvalidOperationException($"{section}: Home policy did not return to top offset={_pageScroll.VerticalOffset:0.##}.");

                ApplyPageScrollKeyForPolicy(Windows.System.VirtualKey.PageDown);
                await Task.Delay(35);
                double beforePageUp = _pageScroll.VerticalOffset;
                if (beforePageUp < 1 || !ApplyPageScrollKeyForPolicy(Windows.System.VirtualKey.PageUp))
                    throw new InvalidOperationException($"{section}: PageUp policy precondition failed.");
                await Task.Delay(45);
                if (_pageScroll.VerticalOffset >= beforePageUp)
                    throw new InvalidOperationException($"{section}: PageUp policy did not move upward.");

                LogService.Info("visual-qa",
                    $"PAGE_SCROLL_POLICY section={section} pageDown={afterPageDown:0.##} end={afterEnd:0.##} max={_pageScroll.ScrollableHeight:0.##}");
            }

            await AssertScrollablePolicyAsync("settings");
            await AssertScrollablePolicyAsync("network");

            await ShowSectionAsync("settings");
            await WaitForPageScrollPolicyStableAsync();

            var reservedTextBox = new TextBox { Text = "abcdef", Width = 220 };
            var reservedSlider = new Slider { Minimum = 0, Maximum = 10, Value = 5, Width = 220 };
            ContentStack.Children.Add(reservedTextBox);
            ContentStack.Children.Add(reservedSlider);
            RootGrid.UpdateLayout();

            if (!IsPageScrollKeyReserved(reservedTextBox))
                throw new InvalidOperationException("TextBox is no longer reserved from page Home/End/PageUp/PageDown.");
            if (!IsPageScrollKeyReserved(reservedSlider))
                throw new InvalidOperationException("Slider is no longer reserved from page Home/End/PageUp/PageDown.");

            ContentStack.Children.Remove(reservedTextBox);
            ContentStack.Children.Remove(reservedSlider);
            LogService.Info("visual-qa", "PAGE_SCROLL_RESERVED_POLICY_OK textbox=true slider=true");

            await ShowSectionAsync("overview");
            await WaitForPageScrollPolicyStableAsync();
            string childGeometry = string.Join(";", ContentStack.Children.Select((child, index) =>
                $"{index}:{child.GetType().Name}:{(child as FrameworkElement)?.ActualHeight ?? -1:0.##}:vis={(child as FrameworkElement)?.Visibility}"));
            LogService.Info("visual-qa",
                $"OVERVIEW_SCROLL_GEOMETRY viewport={_pageScroll.ViewportHeight:0.##} content={PageHost.ActualHeight:0.##} stack={ContentStack.ActualHeight:0.##} gauges={_overviewGaugeHost?.ActualHeight ?? -1:0.##} live={_overviewLiveHost?.ActualHeight ?? -1:0.##} compact={_overviewCacheCompact} children={childGeometry}");
            if (_pageScroll.VerticalOffset > 0.5)
                throw new InvalidOperationException($"Navigation did not reset page scroll offset={_pageScroll.VerticalOffset:0.##}.");
            if (_pageScroll.VerticalScrollMode != ScrollMode.Disabled)
                throw new InvalidOperationException($"Overview 1320x860 should not be scrollable mode={_pageScroll.VerticalScrollMode}.");
            if (ApplyPageScrollKeyForPolicy(Windows.System.VirtualKey.PageDown))
                throw new InvalidOperationException("Overview accepted page-scroll input despite fitting the viewport.");
            if (_pageScroll.VerticalOffset > 0.5)
                throw new InvalidOperationException($"Overview moved despite disabled scrolling offset={_pageScroll.VerticalOffset:0.##}.");

            LogService.Info("visual-qa", "PAGE_SCROLL_INPUT_SMOKE_OK policy=true reserved=true hiddenSmoke=true");
        }
        finally
        {
            appWindow.Resize(originalSize);
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }
}
