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
                if (target.Section == "debloat")
                {
                    var boxes = new List<CheckBox>();
                    void CollectRemoveBoxes(DependencyObject node)
                    {
                        if (node is CheckBox box && box.Visibility == Visibility.Visible) boxes.Add(box);
                        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                            CollectRemoveBoxes(VisualTreeHelper.GetChild(node, i));
                    }
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        boxes.Clear();
                        CollectRemoveBoxes(RootGrid);
                        if (boxes.Count > 0 && boxes.Any(box => box.IsChecked == true)) break;
                        await Task.Delay(50);
                    }
                    if (boxes.Count == 0 || !boxes.Any(box => box.IsChecked == true))
                        throw new InvalidOperationException("Debloater remove checkboxes did not bind to selected rows.");
                }
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

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, KeyboardInputPacket[] inputs, int size);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private bool ActivateKeyboardInputWindow()
    {
        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
                        AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            if (!SetForegroundWindow(_windowHandle))
                return false;
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }

        return GetForegroundWindow() == _windowHandle;
    }

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
}
