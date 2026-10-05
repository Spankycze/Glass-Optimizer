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
    internal async Task RunVisualLayoutSmokeAsync()
    {
        string? originalSub = _sub;
        string originalSection = _section;
        var failures = new List<string>();
        var warnings = new List<string>();

        try
        {
            var targets = new (string Section, string? Sub)[]
            {
                ("overview", null),
                ("system", null),
                ("network", null),
                ("storage", null),
                ("startup", null),
                ("gpu", null),
                ("power", null),
                ("defender", null),
                ("browsers", "Firefox"),
                ("components", "Služby"),
                ("components", "Task Scheduler"),
                ("debloat", null),
                ("fixer", null),
                ("settings", null)
            };

            foreach (var target in targets)
            {
                _sub = target.Sub;
                await ShowSectionAsync(target.Section);
                await Task.Delay(25);

                AuditVisualTree(RootGrid, target.Section + (target.Sub == null ? "" : "/" + target.Sub), failures, warnings);
                if (failures.Count > 0)
                    throw new InvalidOperationException("Visual layout regression: " + string.Join(" | ", failures.Take(12)));
            }

            foreach (string warning in warnings.Take(30))
                LogService.Info("visual-qa", "VISUAL_QA_WARN " + warning);

            LogService.Info("visual-qa",
                $"VISUAL_LAYOUT_SMOKE_OK sections={targets.Length} warnings={warnings.Count}");
        }
        finally
        {
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }

    private static void AuditVisualTree(DependencyObject root, string scope, List<string> failures, List<string> warnings)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
            {
                if (double.IsNaN(fe.ActualWidth) || double.IsNaN(fe.ActualHeight) ||
                    double.IsInfinity(fe.ActualWidth) || double.IsInfinity(fe.ActualHeight) ||
                    fe.ActualWidth < 0 || fe.ActualHeight < 0)
                {
                    failures.Add($"{scope}: invalid-size {fe.GetType().Name} {fe.ActualWidth:0.##}x{fe.ActualHeight:0.##}");
                }

                if (fe is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                {
                    if (tb.IsTextTrimmed && tb.TextTrimming == TextTrimming.None)
                        failures.Add($"{scope}: clipped-text '{Short(tb.Text)}'");
                }

                if (fe is Button button)
                {
                    string accessible = AutomationProperties.GetName(button);
                    bool hasTextContent = button.Content is string s && !string.IsNullOrWhiteSpace(s);
                    if (!hasTextContent && string.IsNullOrWhiteSpace(accessible))
                        warnings.Add($"{scope}: unnamed-button");
                }

                if (fe is ToggleSwitch toggle)
                {
                    string accessible = AutomationProperties.GetName(toggle);
                    bool hasHeader = toggle.Header != null && !string.IsNullOrWhiteSpace(toggle.Header.ToString());
                    if (!hasHeader && string.IsNullOrWhiteSpace(accessible))
                    {
                        DependencyObject? parent = VisualTreeHelper.GetParent(toggle);
                        string parentType = parent?.GetType().Name ?? "none";
                        string tag = toggle.Tag?.ToString() ?? "";
                        string data = toggle.DataContext?.GetType().Name ?? "";
                        string siblingText = "";
                        var ancestorTypes = new List<string>();
                        DependencyObject? cursor = parent;
                        var labels = new List<string>();
                        for (int depth = 0; cursor != null && depth < 4; depth++)
                        {
                            ancestorTypes.Add(cursor.GetType().Name);
                            CollectNearbyText(cursor, labels, 4);
                            cursor = VisualTreeHelper.GetParent(cursor);
                        }
                        siblingText = string.Join(" / ", labels.Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
                        string ancestorPath = string.Join(">", ancestorTypes);
                        warnings.Add($"{scope}: unnamed-toggle parent={parentType} tag={Short(tag)} data={data} ancestors={ancestorPath} text={siblingText}");
                    }
                }
            }

            AuditVisualTree(child, scope, failures, warnings);
        }
    }

    private static void CollectNearbyText(DependencyObject root, List<string> labels, int maxDepth, int depth = 0)
    {
        if (root == null || depth > maxDepth || labels.Count >= 8) return;

        if (root is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            labels.Add(Short(tb.Text));
        else if (root is ContentControl cc && cc.Content is string content && !string.IsNullOrWhiteSpace(content))
            labels.Add(Short(content));

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count && labels.Count < 8; i++)
            CollectNearbyText(VisualTreeHelper.GetChild(root, i), labels, maxDepth, depth + 1);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KeyboardInputPacket
    {
        public uint Type;
        public KeyboardInputUnion Data;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 32)]
    private struct KeyboardInputUnion
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public KeyboardInput Keyboard;

        [System.Runtime.InteropServices.FieldOffset(0)]
        public MouseInput Mouse;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, KeyboardInputPacket[] inputs, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private static void SendKeyboardKey(ushort virtualKey)
    {
        const uint InputKeyboard = 1;
        const uint KeyUp = 0x0002;
        var inputs = new[]
        {
            new KeyboardInputPacket
            {
                Type = InputKeyboard,
                Data = new KeyboardInputUnion
                {
                    Keyboard = new KeyboardInput { VirtualKey = virtualKey }
                }
            },
            new KeyboardInputPacket
            {
                Type = InputKeyboard,
                Data = new KeyboardInputUnion
                {
                    Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = KeyUp }
                }
            }
        };

        uint sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<KeyboardInputPacket>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent}/{inputs.Length} keyboard packets.");
    }

    private static void SendMouseWheel(int delta)
    {
        const uint InputMouse = 0;
        const uint MouseWheel = 0x0800;
        var inputs = new[]
        {
            new KeyboardInputPacket
            {
                Type = InputMouse,
                Data = new KeyboardInputUnion
                {
                    Mouse = new MouseInput
                    {
                        MouseData = unchecked((uint)delta),
                        Flags = MouseWheel
                    }
                }
            }
        };

        uint sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<KeyboardInputPacket>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput mouse wheel sent {sent}/{inputs.Length} packets.");
    }

    private void PositionCursorInsidePage()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            _ = SetForegroundWindow(_windowHandle);
            if (GetWindowRect(_windowHandle, out NativeRect rect))
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int x = rect.Left + Math.Max(240, (int)(width * 0.72));
                int y = rect.Top + Math.Max(300, (int)(height * 0.72));
                if (SetCursorPos(x, y))
                    return;
            }
            System.Threading.Thread.Sleep(15);
        }

        throw new InvalidOperationException("SetCursorPos failed for mouse-wheel smoke after retries.");
    }

    private static void SendShiftTab()
    {
        const uint InputKeyboard = 1;
        const uint KeyUp = 0x0002;
        const ushort Shift = 0x10;
        const ushort Tab = 0x09;
        var inputs = new[]
        {
            new KeyboardInputPacket { Type = InputKeyboard, Data = new KeyboardInputUnion { Keyboard = new KeyboardInput { VirtualKey = Shift } } },
            new KeyboardInputPacket { Type = InputKeyboard, Data = new KeyboardInputUnion { Keyboard = new KeyboardInput { VirtualKey = Tab } } },
            new KeyboardInputPacket { Type = InputKeyboard, Data = new KeyboardInputUnion { Keyboard = new KeyboardInput { VirtualKey = Tab, Flags = KeyUp } } },
            new KeyboardInputPacket { Type = InputKeyboard, Data = new KeyboardInputUnion { Keyboard = new KeyboardInput { VirtualKey = Shift, Flags = KeyUp } } }
        };
        uint sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<KeyboardInputPacket>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput Shift+Tab sent {sent}/{inputs.Length} packets.");
    }

    private async Task RunRealDialogKeyboardInputSmokeAsync()
    {
        int secondaryActivations = 0;
        int primaryActivations = 0;
        int escapeActivations = 0;

        var origin = new Button
        {
            Content = "Dialog focus restore origin",
            Width = 180,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4)
        };
        EnsureAccessibleButton(origin, "Dialog focus restore origin");
        Canvas.SetZIndex(origin, 19994);
        RootGrid.Children.Add(origin);
        if (!origin.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Dialog focus restore origin could not receive focus.");
        await Task.Delay(30);

        var overlay = CreateDialogOverlay(19995);
        var card = CreateDialogCard(520, 460, z: 36);
        var actions = DialogActions();
        var secondary = StyledDialogButton("Zrušit", false, () =>
        {
            secondaryActivations++;
            return Task.CompletedTask;
        });
        var primary = StyledDialogButton("Použít", true, () =>
        {
            primaryActivations++;
            return Task.CompletedTask;
        });
        actions.Children.Add(secondary);
        actions.Children.Add(primary);
        card.Child = actions;

        Button? close = MountDialog(overlay, card, () =>
        {
            escapeActivations++;
            UnmountDialog(overlay);
        });

        if (close == null)
            throw new InvalidOperationException("Real dialog keyboard smoke close button missing.");

        SetForegroundWindow(_windowHandle);
        await Task.Delay(80);

        if (!secondary.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Real dialog secondary button could not receive focus.");
        await Task.Delay(30);

        SendKeyboardKey(0x09);
        await Task.Delay(80);
        if (!ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), primary))
            throw new InvalidOperationException("Real dialog Tab did not move secondary -> primary.");

        SendKeyboardKey(0x09);
        await Task.Delay(80);
        if (!ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), close))
            throw new InvalidOperationException("Real dialog Tab did not move primary -> close.");

        SendKeyboardKey(0x09);
        await Task.Delay(80);
        if (!ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), secondary))
            throw new InvalidOperationException("Real dialog Tab cycle did not return close -> secondary.");

        SendShiftTab();
        await Task.Delay(80);
        if (!ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), close))
            throw new InvalidOperationException("Real dialog Shift+Tab did not move secondary -> close.");

        if (!secondary.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Real dialog secondary could not regain focus before Enter.");
        await Task.Delay(30);
        SendKeyboardKey(0x0D);
        await Task.Delay(100);
        if (secondaryActivations != 1 || primaryActivations != 0)
            throw new InvalidOperationException($"Real dialog focused-secondary Enter mismatch secondary={secondaryActivations} primary={primaryActivations}.");

        if (!primary.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Real dialog primary could not receive focus before Enter.");
        await Task.Delay(30);
        SendKeyboardKey(0x0D);
        await Task.Delay(100);
        if (primaryActivations != 1 || secondaryActivations != 1)
            throw new InvalidOperationException($"Real dialog focused-primary Enter mismatch secondary={secondaryActivations} primary={primaryActivations}.");

        SendKeyboardKey(0x1B);
        await Task.Delay(100);
        if (escapeActivations != 1)
            throw new InvalidOperationException($"Real dialog Escape accelerator count={escapeActivations}, expected=1.");
        if (RootGrid.Children.Contains(overlay))
            throw new InvalidOperationException("Real dialog Escape did not remove the overlay.");

        await Task.Delay(120);
        object? restored = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (!ReferenceEquals(restored, origin))
            throw new InvalidOperationException("Dialog close did not restore focus to the invoking control.");

        RootGrid.Children.Remove(origin);

        LogService.Info("visual-qa",
            $"REAL_DIALOG_KEYBOARD_SMOKE_OK secondary={secondaryActivations} primary={primaryActivations} escape={escapeActivations} focusRestore=true");
    }

    private async Task RunNestedDialogFocusRestoreSmokeAsync()
    {
        var origin = new Button
        {
            Content = "Nested focus origin",
            Width = 160,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 40, 4, 4)
        };
        EnsureAccessibleButton(origin, "Nested focus origin");
        Canvas.SetZIndex(origin, 19990);
        RootGrid.Children.Add(origin);

        if (!origin.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Nested focus origin could not receive focus.");
        await Task.Delay(30);

        var overlayA = CreateDialogOverlay(19991);
        var cardA = CreateDialogCard(500, 440, z: 36);
        var actionsA = DialogActions();
        var primaryA = StyledDialogButton("Dialog A", true, () => Task.CompletedTask);
        actionsA.Children.Add(primaryA);
        cardA.Child = actionsA;
        MountDialog(overlayA, cardA, () => UnmountDialog(overlayA));
        await Task.Delay(100);
        if (!primaryA.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Dialog A primary could not receive focus.");
        await Task.Delay(50);
        if (!ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), primaryA))
            throw new InvalidOperationException("Dialog A primary did not retain focus before opening dialog B.");

        var overlayB = CreateDialogOverlay(19992);
        var cardB = CreateDialogCard(480, 420, z: 36);
        var actionsB = DialogActions();
        var primaryB = StyledDialogButton("Dialog B", true, () => Task.CompletedTask);
        actionsB.Children.Add(primaryB);
        cardB.Child = actionsB;
        MountDialog(overlayB, cardB, () => UnmountDialog(overlayB));
        if (!primaryB.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("Dialog B primary could not receive focus.");
        await Task.Delay(40);

        UnmountDialog(overlayB);
        await Task.Delay(100);
        object? afterB = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (!ReferenceEquals(afterB, primaryA))
            throw new InvalidOperationException("Closing nested dialog B did not restore focus to dialog A.");

        UnmountDialog(overlayA);
        await Task.Delay(100);
        object? afterA = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (!ReferenceEquals(afterA, origin))
            throw new InvalidOperationException("Closing dialog A did not restore focus to the original control.");

        RootGrid.Children.Remove(origin);
        LogService.Info("visual-qa", "NESTED_DIALOG_FOCUS_RESTORE_OK");
    }

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
            VerticalAlignment = VerticalAlignment.Stretch,
            TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle
        };
        Canvas.SetZIndex(overlay, 19990);

        var panel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

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
        overlay.Children.Add(panel);
        RootGrid.Children.Add(overlay);

        try
        {
            SetForegroundWindow(_windowHandle);
            await Task.Delay(80);

            if (!await FocusControlAsync(quick))
                throw new InvalidOperationException("QuickButton could not receive focus.");

            SendKeyboardKey(0x09);
            if (!await WaitForFocusAsync(dialog))
                throw new InvalidOperationException("Tab did not move focus QuickButton -> StyledDialogButton.");

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

            SetForegroundWindow(_windowHandle);
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
                ("gpu", null), ("power", null), ("defender", null), ("browsers", "Firefox"),
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref SidebarNativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out SidebarNativePoint point);

    internal async Task RunSidebarScrollInputSmokeAsync()
    {
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        SizeInt32 originalSize = appWindow.Size;
        bool restoreCursor = GetCursorPos(out SidebarNativePoint originalCursor);
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
                    await Task.Delay(60);
                    for (int i = 0; i < 10 && GetForegroundWindow() != _windowHandle; i++)
                    {
                        SetForegroundWindow(_windowHandle);
                        await Task.Delay(30);
                    }
                    if (GetForegroundWindow() != _windowHandle)
                        throw new InvalidOperationException("Sidebar smoke could not acquire foreground input.");
                    var origin = scroll.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
                    double scale = RootGrid.XamlRoot.RasterizationScale;
                    var client = new SidebarNativePoint();
                    if (!ClientToScreen(_windowHandle, ref client) ||
                        !SetCursorPos(client.X + (int)((origin.X + scroll.ActualWidth * .5) * scale),
                                      client.Y + (int)((origin.Y + scroll.ActualHeight * .4) * scale)))
                        throw new InvalidOperationException("Sidebar smoke could not position wheel input.");
                    double pageOffset = _pageScroll.VerticalOffset;
                    SendMouseWheel(-120);
                    for (int i = 0; i < 30 && scroll.VerticalOffset < 1; i++) await Task.Delay(20);
                    double wheelDown = scroll.VerticalOffset;
                    if (wheelDown < 1)
                        throw new InvalidOperationException($"Sidebar real wheel input did not scroll rebuild={rebuild} size={size}.");
                    for (int i = 0; i < 20 && scroll.VerticalOffset < scroll.ScrollableHeight - 2; i++)
                    {
                        SendMouseWheel(-480);
                        await Task.Delay(70);
                    }
                    Button last = _categoryButtons[MainSectionItems[^1].Section];
                    var lastOrigin = last.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point(0, 0));
                    if (scroll.VerticalOffset < scroll.ScrollableHeight - 2 ||
                        lastOrigin.Y < -2 || lastOrigin.Y + last.ActualHeight > scroll.ViewportHeight + 2)
                        throw new InvalidOperationException($"Last sidebar item not fully reachable rebuild={rebuild} top={lastOrigin.Y} height={last.ActualHeight} viewport={scroll.ViewportHeight}.");
                    for (int i = 0; i < 20 && scroll.VerticalOffset > 1; i++)
                    {
                        SendMouseWheel(480);
                        await Task.Delay(70);
                    }
                    if (scroll.VerticalOffset > 1 || Math.Abs(_pageScroll.VerticalOffset - pageOffset) > 1)
                        throw new InvalidOperationException("Sidebar wheel did not return to top or leaked into page scrolling.");
                    LogService.Info("visual-qa", $"SIDEBAR_SCROLL_INPUT_OK rebuild={rebuild} size={size} wheelDown={wheelDown:0.#} lastReachable=true hiddenScrollbar=true pageUnchanged=true");
                }
            }
        }
        finally
        {
            appWindow.Resize(originalSize);
            if (_mainTabsShell?.Child is ScrollViewer current) current.ChangeView(null, 0, null, true);
            if (restoreCursor) SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    internal async Task RunPageScrollInputSmokeAsync()
    {
        await RunSidebarScrollInputSmokeAsync();
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        SizeInt32 originalSize = appWindow.Size;
        string originalSection = _section;
        string? originalSub = _sub;

        try
        {
            appWindow.Resize(new SizeInt32(1320, 860));
            await Task.Delay(140);

            async Task AssertScrollableKeyboardContractAsync(string section)
            {
                _sub = null;
                await ShowSectionAsync(section);

                // Several pages intentionally materialize their heavy tail after the foreground
                // navigation completes. Also wait for the resized smoke window to receive a real
                // ScrollViewer viewport; Release/CI can otherwise report ViewportHeight=0 for a
                // few composition turns even though PageHost is already measured.
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

                if (_pageScroll.VerticalScrollMode != ScrollMode.Enabled || _pageScroll.ScrollableHeight <= 1)
                    throw new InvalidOperationException(
                        $"{section}: expected a vertically scrollable final page viewport={_pageScroll.ViewportHeight:0.##} height={PageHost.ActualHeight:0.##}.");

                _pageScroll.ChangeView(null, 0, null, true);
                await Task.Delay(40);

                for (int i = 0; i < 10 && GetForegroundWindow() != _windowHandle; i++)
                {
                    _ = SetForegroundWindow(_windowHandle);
                    await Task.Delay(30);
                }
                if (GetForegroundWindow() != _windowHandle)
                    throw new InvalidOperationException($"{section}: smoke window could not acquire foreground input.");

                var tabStops = new List<Control>();
                CollectVisibleTabStops(ContentStack, tabStops);
                Control? scrollFocus = tabStops.FirstOrDefault(c =>
                    c is Button or ToggleSwitch or CheckBox or RadioButton);
                if (scrollFocus == null || !await FocusControlAsync(scrollFocus))
                    throw new InvalidOperationException($"{section}: no non-reserved focusable page control available for keyboard scrolling.");

                // Let focus routing and the ChangeView-to-top operation settle before sending
                // synthetic keyboard input. Without this, a deferred layout/focus turn can
                // occasionally consume the first PageDown even though the page is scrollable.
                for (int i = 0; i < 8; i++)
                {
                    var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
                    if (focused != null && IsDescendantOf(focused, PageHost) && _pageScroll.VerticalOffset <= 1)
                        break;
                    await Task.Delay(20);
                }
                await Task.Delay(30);

                SendKeyboardKey(0x22); // PageDown
                for (int i = 0; i < 20 && _pageScroll.VerticalOffset < 1; i++)
                    await Task.Delay(15);
                double afterPageDown = _pageScroll.VerticalOffset;
                if (afterPageDown < 1)
                    throw new InvalidOperationException($"{section}: PageDown did not move the scroll offset.");

                SendKeyboardKey(0x23); // End
                for (int i = 0; i < 25 && _pageScroll.VerticalOffset < _pageScroll.ScrollableHeight - 2; i++)
                    await Task.Delay(15);
                double afterEnd = _pageScroll.VerticalOffset;
                if (afterEnd < _pageScroll.ScrollableHeight - 3)
                    throw new InvalidOperationException($"{section}: End did not reach the bottom offset={afterEnd:0.##}/{_pageScroll.ScrollableHeight:0.##}.");

                SendKeyboardKey(0x24); // Home
                for (int i = 0; i < 25 && _pageScroll.VerticalOffset > 1; i++)
                    await Task.Delay(15);
                if (_pageScroll.VerticalOffset > 1)
                    throw new InvalidOperationException($"{section}: Home did not return to top offset={_pageScroll.VerticalOffset:0.##}.");

                PositionCursorInsidePage();
                SendMouseWheel(-120);
                for (int i = 0; i < 20 && _pageScroll.VerticalOffset < 1; i++)
                    await Task.Delay(15);
                double afterWheelDown = _pageScroll.VerticalOffset;
                if (afterWheelDown < 1)
                    throw new InvalidOperationException($"{section}: mouse wheel down did not move the scroll offset.");

                for (int i = 0; i < 8 && _pageScroll.VerticalOffset > 1; i++)
                {
                    SendMouseWheel(120);
                    await Task.Delay(20);
                }
                if (_pageScroll.VerticalOffset > 1)
                    throw new InvalidOperationException($"{section}: mouse wheel up did not return near top offset={_pageScroll.VerticalOffset:0.##}.");

                LogService.Info("visual-qa",
                    $"PAGE_SCROLL_INPUT section={section} pageDown={afterPageDown:0.##} end={afterEnd:0.##} wheelDown={afterWheelDown:0.##} max={_pageScroll.ScrollableHeight:0.##}");
            }

            await AssertScrollableKeyboardContractAsync("settings");
            await AssertScrollableKeyboardContractAsync("network");

            await ShowSectionAsync("settings");
            await WaitForPageScrollPolicyStableAsync();
            var reservedTextBox = new TextBox
            {
                Text = "abcdef",
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ContentStack.Children.Add(reservedTextBox);
            await Task.Delay(40);
            reservedTextBox.SelectionStart = 3;
            if (!await FocusControlAsync(reservedTextBox))
                throw new InvalidOperationException("Reserved TextBox could not receive focus.");

            await Task.Delay(100);
            double reservedOffset = _pageScroll.VerticalOffset;
            if (reservedOffset < 1)
                throw new InvalidOperationException("Reserved TextBox test could not establish a non-zero page offset.");

            SendKeyboardKey(0x24); // Home
            await Task.Delay(80);
            if (Math.Abs(_pageScroll.VerticalOffset - reservedOffset) > 1)
                throw new InvalidOperationException($"TextBox Home leaked into page scroll offset={_pageScroll.VerticalOffset:0.##} expected={reservedOffset:0.##}.");
            if (reservedTextBox.SelectionStart != 0)
                throw new InvalidOperationException($"TextBox Home did not move caret to start selection={reservedTextBox.SelectionStart}.");

            _ = SetForegroundWindow(_windowHandle);
            if (!await FocusControlAsync(reservedTextBox))
                throw new InvalidOperationException("Reserved TextBox lost focus before End.");
            SendKeyboardKey(0x23); // End
            await Task.Delay(80);
            if (Math.Abs(_pageScroll.VerticalOffset - reservedOffset) > 1)
                throw new InvalidOperationException($"TextBox End leaked into page scroll offset={_pageScroll.VerticalOffset:0.##} expected={reservedOffset:0.##}.");
            if (reservedTextBox.SelectionStart != reservedTextBox.Text.Length)
                throw new InvalidOperationException($"TextBox End did not move caret to end selection={reservedTextBox.SelectionStart}.");

            ContentStack.Children.Remove(reservedTextBox);

            var reservedSlider = new Slider
            {
                Minimum = 0,
                Maximum = 10,
                Value = 5,
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsTabStop = true
            };
            ContentStack.Children.Add(reservedSlider);
            await Task.Delay(40);
            if (!await FocusControlAsync(reservedSlider))
                throw new InvalidOperationException("Reserved Slider could not receive focus.");

            await Task.Delay(100);
            reservedOffset = _pageScroll.VerticalOffset;
            if (reservedOffset < 1)
                throw new InvalidOperationException("Reserved Slider test could not establish a non-zero page offset.");

            _ = SetForegroundWindow(_windowHandle);
            if (!await FocusControlAsync(reservedSlider))
                throw new InvalidOperationException("Reserved Slider lost focus before Home.");
            SendKeyboardKey(0x24); // Home
            await Task.Delay(80);
            if (Math.Abs(_pageScroll.VerticalOffset - reservedOffset) > 1)
                throw new InvalidOperationException($"Slider Home leaked into page scroll offset={_pageScroll.VerticalOffset:0.##} expected={reservedOffset:0.##}.");
            if (Math.Abs(reservedSlider.Value - reservedSlider.Minimum) > 0.01)
                throw new InvalidOperationException($"Slider Home did not select minimum value={reservedSlider.Value:0.##}.");

            _ = SetForegroundWindow(_windowHandle);
            if (!await FocusControlAsync(reservedSlider))
                throw new InvalidOperationException("Reserved Slider lost focus before End.");
            SendKeyboardKey(0x23); // End
            await Task.Delay(80);
            if (Math.Abs(_pageScroll.VerticalOffset - reservedOffset) > 1)
                throw new InvalidOperationException($"Slider End leaked into page scroll offset={_pageScroll.VerticalOffset:0.##} expected={reservedOffset:0.##}.");
            if (Math.Abs(reservedSlider.Value - reservedSlider.Maximum) > 0.01)
                throw new InvalidOperationException($"Slider End did not select maximum value={reservedSlider.Value:0.##}.");

            ContentStack.Children.Remove(reservedSlider);

            LogService.Info("visual-qa", "PAGE_SCROLL_RESERVED_INPUT_OK textbox=true slider=true");

            await ShowSectionAsync("overview");
            await WaitForPageScrollPolicyStableAsync();
            if (_pageScroll.VerticalOffset > 0.5)
                throw new InvalidOperationException($"Navigation did not reset page scroll offset={_pageScroll.VerticalOffset:0.##}.");
            if (_pageScroll.VerticalScrollMode != ScrollMode.Disabled)
                throw new InvalidOperationException($"Overview 1320x860 should not be scrollable mode={_pageScroll.VerticalScrollMode}.");

            PositionCursorInsidePage();
            SendMouseWheel(-120);
            await Task.Delay(120);
            if (_pageScroll.VerticalOffset > 0.5)
                throw new InvalidOperationException($"Overview moved on mouse wheel despite fitting the viewport offset={_pageScroll.VerticalOffset:0.##}.");

            LogService.Info("visual-qa", "PAGE_SCROLL_INPUT_SMOKE_OK keyboard=true wheel=true");
        }
        finally
        {
            appWindow.Resize(originalSize);
            _sub = originalSub;
            await ShowSectionAsync(originalSection);
        }
    }

    internal async Task RunVisualSizeMatrixSmokeAsync()
    {
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
            string[] sections = { "overview", "network", "settings" };

            foreach (SizeInt32 size in sizes)
            {
                appWindow.Resize(size);
                await Task.Delay(120);
                foreach (string section in sections)
                {
                    _sub = null;
                    await ShowSectionAsync(section);
                    await WaitForPageScrollPolicyStableAsync();
                    AuditVisualTree(RootGrid, $"{section}@{size.Width}x{size.Height}", failures, warnings);

                    bool verticalOverflow = PageHost.ActualHeight > _pageScroll.ViewportHeight + 1.0;
                    ScrollMode expectedVertical = verticalOverflow ? ScrollMode.Enabled : ScrollMode.Disabled;
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

    private async Task WaitForVisualCaptureReadyAsync(AppThemePreference theme, string section)
    {
        string? previous = null;
        int stableSamples = 0;

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
                (_cpuGauge?.ValueText.Text is not (null or "\u2014") && _ramGauge?.ValueText.Text is not (null or "\u2014") && _overviewTelemetryGrid?.Children.Count == 4 && _gpuGauge?.StatusText.Text is not (null or "NA\u010c\u00cdT\u00c1M") && _diskGauge?.StatusText.Text is not (null or "NA\u010c\u00cdT\u00c1M") && _overviewMetricsHost?.Children.Count > 0 && _overviewLiveHost?.Children.Count == 4 && _pageScroll.ActualHeight > 100);
            bool sectionReady = overviewReady && (!section.Equals("network", StringComparison.OrdinalIgnoreCase) ||
                (!visualText.Contains("Zjišťuji aktivní připojení", StringComparison.OrdinalIgnoreCase) &&
                 !visualText.Contains("Načítám stav adaptéru", StringComparison.OrdinalIgnoreCase)));

            string signature = GetVisualStabilitySignature(RootGrid, out int pendingImages);
            if (themeReady && navReady && shellReady && heroReady && sectionReady && pendingImages == 0)
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
            else
            {
                stableSamples = 0;
            }

            previous = signature;
            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"Visual capture did not stabilize theme={theme} section={section} signature={previous}");
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
            var targets = new[] { "overview", "network", "settings", "power", "ai-runtime" };
            foreach (var theme in themes)
            {
                await ChangeThemeAsync(theme);
                foreach (string section in targets)
                {
                    _sub = null;
                    await ShowSectionAsync(section);
                    _windowTelemetryActive = true; // Capture uses real telemetry without taking foreground input.
                    await WaitForVisualCaptureReadyAsync(theme, section);
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

    private async Task SaveVisualSnapshotAsync(string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(RootGrid);
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
