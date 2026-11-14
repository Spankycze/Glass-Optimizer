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
    private async Task RunDialogRestoreEdgeCasesSmokeAsync()
    {
        async Task RunCaseAsync(string name, Action<Button>? mutateOrigin, bool externalRemove = false)
        {
            var origin = new Button
            {
                Content = "Restore edge " + name,
                Width = 150,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 76, 4, 4)
            };
            EnsureAccessibleButton(origin, "Restore edge " + name);
            Canvas.SetZIndex(origin, 19980);
            RootGrid.Children.Add(origin);

            if (!origin.Focus(FocusState.Programmatic))
                throw new InvalidOperationException($"{name}: origin could not receive focus.");
            await Task.Delay(30);

            var overlay = CreateDialogOverlay(19981);
            var card = CreateDialogCard(460, 400, z: 36);
            card.Child = StyledDialogButton("Edge dialog", true, () => Task.CompletedTask);
            MountDialog(overlay, card, () => UnmountDialog(overlay));
            await Task.Delay(80);

            mutateOrigin?.Invoke(origin);

            if (externalRemove)
            {
                if (RootGrid.Children.Contains(overlay))
                    RootGrid.Children.Remove(overlay);
            }
            else
            {
                UnmountDialog(overlay);
                UnmountDialog(overlay);
            }

            await Task.Delay(100);

            if (_dialogFocusRestore.ContainsKey(overlay))
                throw new InvalidOperationException($"{name}: focus restore entry leaked.");

            if (externalRemove && RootGrid.Children.Contains(overlay))
                throw new InvalidOperationException($"{name}: overlay still attached after external remove.");

            if (origin.IsLoaded &&
                origin.IsEnabled &&
                origin.Visibility == Visibility.Visible &&
                origin.XamlRoot != null)
            {
                object? focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                if (!externalRemove && !ReferenceEquals(focused, origin))
                    throw new InvalidOperationException($"{name}: valid origin did not regain focus.");
            }

            if (RootGrid.Children.Contains(origin))
                RootGrid.Children.Remove(origin);
        }

        await RunCaseAsync("double-unmount", null);
        await RunCaseAsync("disabled-origin", b => b.IsEnabled = false);
        await RunCaseAsync("collapsed-origin", b => b.Visibility = Visibility.Collapsed);
        await RunCaseAsync("detached-origin", b => RootGrid.Children.Remove(b));
        await RunCaseAsync("external-overlay-remove", null, externalRemove: true);

        if (_dialogFocusRestore.Count != 0)
            throw new InvalidOperationException($"Focus restore dictionary leak count={_dialogFocusRestore.Count}.");

        LogService.Info("visual-qa", "DIALOG_RESTORE_EDGE_CASES_OK cases=5");
    }

    private async Task RunDialogChurnSmokeAsync()
    {
        var origin = new Button
        {
            Content = "Dialog churn origin",
            Width = 160,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 112, 4, 4)
        };
        EnsureAccessibleButton(origin, "Dialog churn origin");
        Canvas.SetZIndex(origin, 19970);
        RootGrid.Children.Add(origin);

        if (!origin.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Dialog churn origin could not receive focus.");
        await Task.Delay(30);

        int baselineChildren = RootGrid.Children.Count;
        for (int i = 0; i < 20; i++)
        {
            var overlay = CreateDialogOverlay(19971 + i);
            var card = CreateDialogCard(440, 380, z: 36);
            var action = StyledDialogButton("Churn " + i, true, () => Task.CompletedTask);
            card.Child = action;
            MountDialog(overlay, card, () => UnmountDialog(overlay));
            await Task.Delay(10);

            UnmountDialog(overlay);
            UnmountDialog(overlay);
            await Task.Delay(10);

            if (RootGrid.Children.Contains(overlay))
                throw new InvalidOperationException($"Dialog churn overlay leaked at iteration {i}.");
            if (_dialogFocusRestore.ContainsKey(overlay))
                throw new InvalidOperationException($"Dialog churn focus entry leaked at iteration {i}.");
        }

        await Task.Delay(120);

        if (_dialogFocusRestore.Count != 0)
            throw new InvalidOperationException($"Dialog churn left focus restore entries={_dialogFocusRestore.Count}.");
        if (RootGrid.Children.Count != baselineChildren)
            throw new InvalidOperationException($"Dialog churn child-count drift baseline={baselineChildren} current={RootGrid.Children.Count}.");

        object? restored = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (!ReferenceEquals(restored, origin))
            throw new InvalidOperationException("Dialog churn did not restore focus to origin.");

        RootGrid.Children.Remove(origin);
        LogService.Info("visual-qa", "DIALOG_CHURN_SMOKE_OK iterations=20");
    }

    private async Task<bool> WaitForFocusAsync(DependencyObject expected, int timeoutMs = 400)
    {
        int steps = Math.Max(1, timeoutMs / 10);
        for (int i = 0; i < steps; i++)
        {
            if (RootGrid.XamlRoot != null &&
                ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), expected))
                return true;
            await Task.Delay(10);
        }
        return RootGrid.XamlRoot != null &&
               ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), expected);
    }

    private async Task<bool> FocusControlAsync(Control control, int timeoutMs = 400)
    {
        int steps = Math.Max(1, timeoutMs / 10);
        for (int i = 0; i < steps; i++)
        {
            control.Focus(FocusState.Programmatic);
            if (await WaitForFocusAsync(control, 20))
                return true;
            await Task.Delay(10);
        }
        return false;
    }

    internal async Task RunKeyboardActivationSmokeAsync()
    {
        for (int i = 0; i < 80 && !_mainTabsCompleted; i++)
            await Task.Delay(25);
        if (!_mainTabsCompleted)
            throw new InvalidOperationException("Keyboard smoke started before deferred sidebar completed.");

        int quickActivations = 0;
        int dialogActivations = 0;
        int toggleActivations = 0;
        var quickRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Canvas.SetZIndex(overlay, 19990);

        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle
        };
        var panel = new StackPanel { Spacing = 8 };

        var quick = PrimaryQuickButton("Keyboard activation test", async () =>
        {
            quickActivations++;
            await quickRelease.Task;
        });
        var dialog = StyledDialogButton("Potvrdit test", true, async () =>
        {
            dialogActivations++;
            await dialogRelease.Task;
        });
        var toggle = new ToggleSwitch
        {
            Header = "Keyboard toggle test",
            IsOn = false,
            IsTabStop = true
        };
        toggle.Toggled += (_, _) => toggleActivations++;

        panel.Children.Add(quick);
        panel.Children.Add(dialog);
        panel.Children.Add(toggle);
        host.Children.Add(panel);
        overlay.Children.Add(host);
        RootGrid.Children.Add(overlay);

        try
        {
            if (!ActivateKeyboardInputWindow())
                throw new InvalidOperationException("Keyboard smoke window could not become foreground before SendInput.");
            await Task.Delay(80);

            if (!await FocusControlAsync(quick))
                throw new InvalidOperationException("QuickButton could not receive focus.");

            SendKeyboardKey(0x09);
            if (!await WaitForFocusAsync(dialog))
            {
                object? focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                string focusedName = focused is FrameworkElement element
                    ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element)
                    : string.Empty;
                throw new InvalidOperationException(
                    $"Tab did not move focus QuickButton -> StyledDialogButton; focused={focused?.GetType().Name ?? "null"}, name={focusedName}, foreground={GetForegroundWindow()}, window={_windowHandle}.");
            }

            SendKeyboardKey(0x09);
            if (!await WaitForFocusAsync(toggle))
                throw new InvalidOperationException("Tab did not move focus StyledDialogButton -> ToggleSwitch.");

            SendKeyboardKey(0x09);
            if (!await WaitForFocusAsync(quick))
                throw new InvalidOperationException("Tab cycle did not return focus ToggleSwitch -> QuickButton.");

            SendShiftTab();
            if (!await WaitForFocusAsync(toggle))
                throw new InvalidOperationException("Shift+Tab did not move focus QuickButton -> ToggleSwitch.");

            if (!await FocusControlAsync(quick))
                throw new InvalidOperationException("QuickButton could not regain focus after Tab traversal.");
            await Task.Delay(30);
            SendKeyboardKey(0x0D);
            for (int i = 0; i < 20 && quickActivations == 0; i++)
                await Task.Delay(10);
            if (quickActivations != 1)
                throw new InvalidOperationException("QuickButton first activation did not start.");

            SendKeyboardKey(0x0D);
            await Task.Delay(80);
            if (quickActivations != 1)
                throw new InvalidOperationException($"QuickButton double-Enter guard count={quickActivations}, expected=1.");
            if (!await WaitForFocusAsync(quick))
                throw new InvalidOperationException("QuickButton lost focus while guarded async action was running.");

            quickRelease.TrySetResult(true);
            quickRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!await FocusControlAsync(quick))
                throw new InvalidOperationException("QuickButton could not regain focus before Space.");
            for (int i = 0; i < 20 && quickActivations < 2; i++)
            {
                SendKeyboardKey(0x20);
                await Task.Delay(20);
            }
            if (quickActivations != 2)
                throw new InvalidOperationException($"QuickButton guard did not release for Space activation count={quickActivations}, expected=2.");
            quickRelease.TrySetResult(true);
            await Task.Delay(30);

            if (!await FocusControlAsync(dialog))
                throw new InvalidOperationException("StyledDialogButton could not receive focus.");
            await Task.Delay(30);
            SendKeyboardKey(0x0D);
            for (int i = 0; i < 20 && dialogActivations == 0; i++)
                await Task.Delay(10);
            if (dialogActivations != 1)
                throw new InvalidOperationException("Dialog first activation did not start.");

            SendKeyboardKey(0x0D);
            await Task.Delay(80);
            if (dialogActivations != 1)
                throw new InvalidOperationException($"Dialog double-Enter guard count={dialogActivations}, expected=1.");
            if (!await WaitForFocusAsync(dialog))
                throw new InvalidOperationException("Dialog button lost focus while guarded async action was running.");

            dialogRelease.TrySetResult(true);
            dialogRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!await FocusControlAsync(dialog))
                throw new InvalidOperationException("StyledDialogButton could not regain focus after guarded action.");
            for (int i = 0; i < 20 && dialogActivations < 2; i++)
            {
                SendKeyboardKey(0x0D);
                await Task.Delay(20);
            }
            if (dialogActivations != 2)
                throw new InvalidOperationException($"Dialog guard did not release for next activation count={dialogActivations}, expected=2.");
            dialogRelease.TrySetResult(true);
            await Task.Delay(30);

            if (!await FocusControlAsync(toggle))
                throw new InvalidOperationException("ToggleSwitch could not receive focus.");
            await Task.Delay(30);
            SendKeyboardKey(0x20);
            await Task.Delay(100);
            if (!toggle.IsOn || toggleActivations != 1)
                throw new InvalidOperationException($"Toggle Space activation state={toggle.IsOn} count={toggleActivations}, expected=True/1.");

            LogService.Info("visual-qa",
                $"KEYBOARD_ACTIVATION_SMOKE_OK quick={quickActivations} dialog={dialogActivations} toggle={toggleActivations}");
        }
        finally
        {
            RootGrid.Children.Remove(overlay);
        }

        await RunRealDialogKeyboardInputSmokeAsync();
        await RunNestedDialogFocusRestoreSmokeAsync();
        await RunDialogRestoreEdgeCasesSmokeAsync();
        await RunDialogChurnSmokeAsync();
    }

    internal async Task RunNavigationRaceSmokeAsync()
    {
        string originalSection = _section;
        string? originalSub = _sub;
        try
        {
            async Task AssertFinalAsync(string expected, int settleMs = 220)
            {
                await Task.Delay(settleMs);
                for (int i = 0; i < 20 && !_heroDeferredCompleted; i++)
                    await Task.Delay(25);
                if (!_heroDeferredCompleted)
                    throw new InvalidOperationException("Navigation race smoke timed out waiting for deferred hero.");
                if (!string.Equals(_section, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Navigation race final section={_section}, expected={expected}.");

                string expectedTitle = expected == "overview"
                    ? "P\u0159ehled"
                    : CatalogService.GetSection(expected).Title;
                if (!string.Equals(PageTitle.Text, expectedTitle, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Navigation race title='{PageTitle.Text}', expected='{expectedTitle}'.");

                if (!string.IsNullOrWhiteSpace(_lastNavigationErrorId))
                    throw new InvalidOperationException($"Navigation race left error id={_lastNavigationErrorId}.");

                if (_loadCts?.IsCancellationRequested == true)
                    throw new InvalidOperationException("Final navigation CTS is unexpectedly cancelled.");
            }

            await ShowSectionAsync("settings");
            await AssertFinalAsync("settings", 120);
            int staleGeneration = Math.Max(0, _navigationGeneration - 1);
            int beforeErrorChildren = ContentStack.Children.Count;
            string beforeErrorTitle = PageTitle.Text;
            string? beforeErrorId = _lastNavigationErrorId;
            RenderSectionError(
                "settings",
                "STALE-QA",
                new InvalidOperationException("stale navigation error surface smoke"),
                staleGeneration);
            await Task.Delay(40);
            if (ContentStack.Children.Count != beforeErrorChildren ||
                !string.Equals(PageTitle.Text, beforeErrorTitle, StringComparison.Ordinal) ||
                !string.Equals(_lastNavigationErrorId, beforeErrorId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stale navigation error surface mutated the active page.");
            }

            for (int i = 0; i < 6; i++)
            {
                Task a = ShowSectionAsync("network");
                await Task.Delay(5);
                Task b = ShowSectionAsync("settings");
                await Task.WhenAll(a, b);
                await AssertFinalAsync("settings", 180);

                string text = CollectVisualText(ContentStack);
                if (text.Contains("DNS Benchmark", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("MTU", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Stale Network UI leaked into Settings at iteration {i}.");
                }
            }

            Task runtimeA = ShowSectionAsync("ai-runtime");
            await Task.Delay(5);
            Task settings = ShowSectionAsync("settings");
            await Task.Delay(5);
            Task runtimeB = ShowSectionAsync("ai-runtime");
            await Task.WhenAll(runtimeA, settings, runtimeB);
            await AssertFinalAsync("ai-runtime", 280);

            Task networkA = ShowSectionAsync("network");
            await Task.Delay(5);
            Task settingsB = ShowSectionAsync("settings");
            await Task.Delay(5);
            Task networkB = ShowSectionAsync("network");
            await Task.WhenAll(networkA, settingsB, networkB);
            await AssertFinalAsync("network", 320);

            LogService.Info("visual-qa",
                $"NAVIGATION_RACE_SMOKE_OK iterations=6 generation={_navigationGeneration}");
        }
        finally
        {
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }

    private static string CollectVisualText(DependencyObject root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                sb.AppendLine(tb.Text);
            else if (node is TextBox box && !string.IsNullOrWhiteSpace(box.Text))
                sb.AppendLine(box.Text);

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }

        Walk(root);
        return sb.ToString();
    }
}
