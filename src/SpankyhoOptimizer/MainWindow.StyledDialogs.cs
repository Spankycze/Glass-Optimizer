using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private readonly Dictionary<Grid, WeakReference<Control>> _dialogFocusRestore = new();
    private readonly Dictionary<Grid, FrameworkElement> _dialogMotionCards = new();

    private double DialogMaxHeight(double cap = 720)
    {
        double available = RootGrid?.ActualHeight ?? 0;
        if (available <= 0) return Math.Min(cap, 680);
        return Math.Max(300, Math.Min(cap, available - 48));
    }

    private static Brush DialogSecondaryTextBrush()
        => _darkTheme ? ThemeColor(196,202,212) : TextSecondaryBrush();

    private static Brush? _dialogModalCardBrush;
    private static Brush? _dialogModalBodyBrush;

    private static Brush DialogBodyBrush()
        => _darkTheme ? (_dialogModalBodyBrush ??= GlassSurfaceElevatedBrush()) : SurfaceAltBrush();

    private static Brush DialogModalCardBrush()
        => _darkTheme ? (_dialogModalCardBrush ??= new AcrylicBrush
        {
            TintColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            TintOpacity = 0,
            TintLuminosityOpacity = 0,
            FallbackColor = Windows.UI.Color.FromArgb(64, 255, 255, 255)
        }) : SectionCardBackgroundBrush();

    private static Brush DialogNeutralBorderBrush()
        => _darkTheme ? GlassEdgeBrush() : BorderThemeBrush();

    private static Brush DialogNeutralHoverBorderBrush()
        => _darkTheme ? GlassBlueEdgeBrush() : BorderThemeBrush();

    private static Brush DialogInnerSurfaceBrush()
        => _darkTheme ? GlassDialogInnerSurfaceBrush() : SurfaceAltBrush();

    private static Brush DialogInnerStrongBrush()
        => _darkTheme ? GlassSurfaceElevatedBrush() : SurfaceElevatedBrush();

    private Grid CreateDialogOverlay(int zIndex = 30000, byte alpha = 72)
    {
        var overlay = new Grid
        {
            Background = _darkTheme ? GlassOverlayBrush(alpha) : DialogOverlayBrush(alpha),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, Math.Max(1, RootGrid?.RowDefinitions.Count ?? 1));
        Grid.SetColumn(overlay, 0);
        Grid.SetColumnSpan(overlay, Math.Max(1, RootGrid?.ColumnDefinitions.Count ?? 1));
        Canvas.SetZIndex(overlay, zIndex);
        return overlay;
    }

    private Border CreateDialogCard(double width, double maxHeight = 720, double? maxWidth = null, float z = 32)
    {
        double availableWidth = RootGrid?.ActualWidth ?? 0;
        double maxAllowed = availableWidth > 0 ? Math.Max(320, availableWidth - 48) : double.MaxValue;
        double preferredMax = Math.Max(width, maxWidth ?? width);
        double resolvedWidth = Math.Min(maxAllowed, preferredMax);
        double resolvedMaxHeight = DialogMaxHeight(maxHeight);
        double resolvedMinHeight = Math.Min(resolvedMaxHeight, 180);

        var card = new Border
        {
            Width = resolvedWidth,
            MaxWidth = resolvedWidth,
            MinHeight = resolvedMinHeight,
            MaxHeight = resolvedMaxHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = DialogModalCardBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_darkTheme ? 20 : 10),
            Padding = new Thickness(22),
            Shadow = _darkTheme ? new ThemeShadow() : null
        };
        card.Translation = new System.Numerics.Vector3(0, 0, z);
        return card;
    }

    internal void ValidateDialogLayoutSmoke()
    {
        var overlay = CreateDialogOverlay();
        int expectedRows = Math.Max(1, RootGrid.RowDefinitions.Count);
        if (Grid.GetRow(overlay) != 0 || Grid.GetRowSpan(overlay) != expectedRows)
            throw new InvalidOperationException($"Dialog overlay span invalid: row={Grid.GetRow(overlay)} span={Grid.GetRowSpan(overlay)} expected={expectedRows}.");
        if (overlay.Background is not SolidColorBrush scrim || scrim.Color.A != 72)
            throw new InvalidOperationException("Dialog overlay must dim the underlying page without hiding it.");
        var card = CreateDialogCard(760, 720, 860);
        if (card.MinHeight <= 0 || card.MaxHeight < card.MinHeight)
            throw new InvalidOperationException($"Dialog height invalid: min={card.MinHeight} max={card.MaxHeight}.");
        if (_darkTheme && (card.Background is not AcrylicBrush acrylic ||
            acrylic.TintOpacity != 0 || acrylic.TintLuminosityOpacity != 0 || acrylic.FallbackColor.A > 96))
            throw new InvalidOperationException("Dialog must blur the app background without adding a dark tint.");
        if (_darkTheme && card.BorderBrush == null)
            throw new InvalidOperationException("Dark glass dialog border is missing.");
        double maxAllowed = Math.Max(320, RootGrid.ActualWidth - 48);
        if (card.Width > maxAllowed + 0.5 || card.Width < Math.Min(760, maxAllowed) - 0.5)
            throw new InvalidOperationException($"Dialog width invalid: width={card.Width} root={RootGrid.ActualWidth}.");
        LogService.Info("regression", $"DIALOG_LAYOUT_OK width={card.Width:0} minH={card.MinHeight:0} maxH={card.MaxHeight:0} rowSpan={Grid.GetRowSpan(overlay)} neutralBorder=true");
    }

    private Button? MountDialog(Grid overlay, Border card, Action? escapeAction = null)
    {
        if (RootGrid.XamlRoot != null &&
            FocusManager.GetFocusedElement(RootGrid.XamlRoot) is Control previous &&
            previous.IsEnabled &&
            previous.Visibility == Visibility.Visible)
        {
            _dialogFocusRestore[overlay] = new WeakReference<Control>(previous);
        }

        overlay.Unloaded += (_, _) => _dialogFocusRestore.Remove(overlay);

        Button? close = null;
        if (escapeAction != null)
        {
            var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            esc.Invoked += (_, e) => { e.Handled = true; escapeAction(); };
            overlay.KeyboardAccelerators.Add(esc);

            var host = new Grid
            {
                Width = card.Width,
                MaxWidth = card.MaxWidth,
                MaxHeight = card.MaxHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TabFocusNavigation = KeyboardNavigationMode.Cycle
            };

            card.Width = double.NaN;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.Translation = new System.Numerics.Vector3(0, 0, 30);
            host.Children.Add(card);
            close = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 12, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = _darkTheme ? ThemeColor(54, 18, 26, 36) : SurfaceAltBrush(),
                BorderBrush = DialogNeutralBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Opacity = 1,
                Content = new FontIcon { Glyph = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 11.5, Foreground = TextPrimaryBrush() }
            };
            ToolTipService.SetToolTip(close, "Zavřít");
            EnsureAccessibleButton(close, "Zavřít dialog");
            close.Translation = new System.Numerics.Vector3(0, 0, 38);
            // Neutral close button uses color feedback without scaling.
            close.PointerEntered += (_, _) => { close.Background = DialogBodyBrush(); close.BorderBrush = DialogNeutralHoverBorderBrush(); close.Opacity = 1; };
            close.PointerExited += (_, _) => { close.Background = DialogInnerSurfaceBrush(); close.BorderBrush = DialogNeutralBorderBrush(); close.Opacity = 1; };
            close.Click += (_, _) => escapeAction();
            Canvas.SetZIndex(close, 10);
            host.Children.Add(close);
            overlay.Children.Add(host);
            close.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                () => close.Focus(FocusState.Programmatic));
        }
        else overlay.Children.Add(card);
        _dialogMotionCards[overlay] = card;
        RootGrid.Children.Add(overlay);
        overlay.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            AttachIosMotionTree(card);
            AnimateDialogIn(overlay, card);
        });
        return close;
    }

    private void UnmountDialog(Grid overlay)
    {
        const string unmountKey = "SpankyDialogUnmounting";
        if (overlay.Resources.ContainsKey(unmountKey)) return;
        overlay.Resources[unmountKey] = true;
        overlay.IsHitTestVisible = false;

        Control? restore = null;
        if (_dialogFocusRestore.TryGetValue(overlay, out var weak))
        {
            _dialogFocusRestore.Remove(overlay);
            weak.TryGetTarget(out restore);
        }

        _dialogMotionCards.TryGetValue(overlay, out FrameworkElement? motionCard);

        void FinalizeUnmount()
        {
            if (RootGrid.Children.Contains(overlay))
                RootGrid.Children.Remove(overlay);

            overlay.KeyboardAccelerators.Clear();
            _dialogMotionCards.Remove(overlay);

            if (restore != null)
            {
                restore.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    if (restore.IsLoaded &&
                        restore.IsEnabled &&
                        restore.Visibility == Visibility.Visible &&
                        restore.XamlRoot != null)
                    {
                        restore.Focus(FocusState.Programmatic);
                    }
                });
            }
        }

        bool smoke = Environment.GetCommandLineArgs().Any(x =>
            string.Equals(x, "--dialog-lifecycle-smoke", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x, "--release-ui-smoke", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x, "--progress-lifecycle-smoke", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x, "--keyboard-activation-smoke", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x, "--community-cursor-layout-smoke", StringComparison.OrdinalIgnoreCase));

        // Smoke/layout probes assert tree teardown synchronously. Runtime dialogs still get the
        // full iOS-style exit animation; test-only modes bypass it so QA cannot race the compositor.
        if (!smoke && motionCard != null && MotionEnabled)
            AnimateDialogOut(overlay, motionCard, FinalizeUnmount);
        else
            FinalizeUnmount();
    }

    internal string RunDialogLifecycleSmoke()
    {
        ValidateDialogLayoutSmoke();
        var warning = WarningBrush();
        var header = CreateDialogHeader("Dialog", "Popis", warning);
        var action = StyledDialogButton("Použít", true, () => Task.CompletedTask);
        if (header.Children.Count != 2 || header.Children[0] is not TextBlock title || title.FontSize != 20 ||
            header.Children[1] is not TextBlock subtitle || !ReferenceEquals(subtitle.Foreground, warning) ||
            action.Height != 36 || action.MinHeight > 36)
            throw new InvalidOperationException("Shared dialog header and actions must retain the compact layout.");
        var overlay = CreateDialogOverlay(12790);
        var card = CreateDialogCard(520, 460, z: 36);
        card.Child = new Grid();
        bool closed = false;
        int closeCount = 0;

        void CloseOnce()
        {
            if (closed) return;
            closed = true;
            closeCount++;
            UnmountDialog(overlay);
        }

        var close = MountDialog(overlay, card, CloseOnce);
        if (close == null) throw new InvalidOperationException("Dialog close button nebyl vytvořen.");
        if (_darkTheme && (card.Background is not AcrylicBrush || !ReferenceEquals(card.Background, DialogModalCardBrush())))
            throw new InvalidOperationException("Mounted dialog must retain its readable in-app acrylic surface.");
        if (overlay.KeyboardAccelerators.Count == 0)
            throw new InvalidOperationException("Dialog nemá Escape accelerator.");
        if (!RootGrid.Children.Contains(overlay))
            throw new InvalidOperationException("Dialog nebyl připojen do RootGrid.");

        CloseOnce();
        CloseOnce();

        if (RootGrid.Children.Contains(overlay))
            throw new InvalidOperationException("Dialog zůstal po close připojený.");
        if (closeCount != 1)
            throw new InvalidOperationException($"Dialog close nebyl idempotentní: count={closeCount}.");

        string community = RunCommunityCursorPopupLifecycleSmoke();
        return "DIALOG_LIFECYCLE_SMOKE_OK closeButton=yes escape=yes idempotent=yes • " + community;
    }

    private Button StyledDialogButton(string text, bool primary, Func<Task> action)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = primary ? 118 : 104,
            Height = 34,
            Padding = new Thickness(16, 0, 16, 0),
            FontSize = 13,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = _darkTheme ? ThemeColor(241,241,241) : (primary ? OnAccentTextBrush() : TextPrimaryBrush()),
            Background = _darkTheme ? (primary ? DialogInnerStrongBrush() : DialogInnerSurfaceBrush()) : (primary ? ButtonVioletBrush() : ButtonVioletSoftBrush()),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(primary ? 1.4 : 1),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            
        };
        ApplyMaterialButtonAppearance(b, primary);
        b.Height = 36;
        b.Padding = new Thickness(16, 0, 16, 0);
        b.CornerRadius = new CornerRadius(12);
        EnsureAccessibleButton(b, text);
        b.Translation = new System.Numerics.Vector3(0, 0, 0);


        int actionBusy = 0;
        b.Click += (_, _) =>
        {
            if (Interlocked.Exchange(ref actionBusy, 1) != 0)
            {
                LogService.Info("dialog", $"DIALOG_ACTION_DUPLICATE_BLOCKED text={text}");
                return;
            }

            SafeTaskService.Forget(RunDialogActionOnceAsync(), "dialog", "Dialog button action");
        };

        async Task RunDialogActionOnceAsync()
        {
            object normalContent = b.Content;
            // Preserve keyboard focus while the single-flight guard blocks duplicate activation.
            b.Content = "Pracuji\u2026";
            try
            {
                await action();
            }
            finally
            {
                b.Content = normalContent;
                b.IsEnabled = true;
                Interlocked.Exchange(ref actionBusy, 0);
            }
        }

        return b;
    }


    private StackPanel CreateDialogHeader(string title, string? subtitle = null, Brush? subtitleForeground = null)
    {
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 36, 0) };
        header.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            header.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 13, Foreground = subtitleForeground ?? DialogSecondaryTextBrush(),
                TextWrapping = TextWrapping.Wrap
            });
        return header;
    }

    private Grid CreateDialogLayout()
    {
        var grid = new Grid { RowSpacing = 14 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return grid;
    }

    private ScrollViewer DialogScroll(UIElement content)
    {
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private StackPanel DialogActions()
    {
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
    }

    private async Task<bool> ShowStyledConfirmAsync(string title, string message, string primaryText = "Použít", string secondaryText = "Zrušit", string? subtitle = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay();
        var card = CreateDialogCard(560, 620, 640);
        var root = CreateDialogLayout();
        root.Children.Add(CreateDialogHeader(title, subtitle));
        var info = new TextBlock { Text = message, FontSize = 13, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var scroll = DialogScroll(info); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var actions = DialogActions();
        Task Finish(bool result) { UnmountDialog(overlay); tcs.TrySetResult(result); return Task.CompletedTask; }
        if (!string.IsNullOrWhiteSpace(secondaryText))
            actions.Children.Add(StyledDialogButton(secondaryText, false, () => Finish(false)));
        var primary = StyledDialogButton(primaryText, true, () => Finish(true));
        actions.Children.Add(primary);
        Grid.SetRow(actions, 2); root.Children.Add(actions);
        card.Child = root;
        MountDialog(overlay, card, () => { UnmountDialog(overlay); tcs.TrySetResult(false); });
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => primary.Focus(FocusState.Programmatic));
        return await tcs.Task;
    }

    private async Task ShowStyledInfoAsync(string title, string message, string buttonText = "OK")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay();
        var card = CreateDialogCard(560, 700);
        var root = CreateDialogLayout();
        root.Children.Add(CreateDialogHeader(title));
        var body = new TextBlock { Text = message, FontSize = 13, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var scroll = DialogScroll(body); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var actions = DialogActions();
        Task Finish()
        {
            UnmountDialog(overlay);
            tcs.TrySetResult(true);
            return Task.CompletedTask;
        }
        var primary = StyledDialogButton(
            string.IsNullOrWhiteSpace(buttonText) ? "Zavřít" : buttonText,
            true,
            Finish);
        actions.Children.Add(primary);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        card.Child = root;
        MountDialog(overlay, card, () => { UnmountDialog(overlay); tcs.TrySetResult(true); });
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => primary.Focus(FocusState.Programmatic));
        await tcs.Task;
    }

    private async Task ShowTweakApplyFailureAsync(IReadOnlyList<string> failures)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay(alpha: 78);
        var card = CreateDialogCard(620, 620, 700);
        var root = CreateDialogLayout();

        root.Children.Add(CreateDialogHeader("Některé změny se nepodařilo potvrdit",
            "Windows po aplikaci nevrátil očekávaný stav u některých položek.", WarningBrush()));

        var rows = new StackPanel { Spacing = 8 };
        foreach (string raw in failures.Take(8))
        {
            string friendly = raw
                .Replace(": read-back nepotvrdil změnu.", ": Windows změnu po aplikaci nepotvrdil.", StringComparison.OrdinalIgnoreCase)
                .Replace("read-back nepotvrdil změnu", "Windows změnu po aplikaci nepotvrdil", StringComparison.OrdinalIgnoreCase)
                .Replace("read-back", "ověření ve Windows", StringComparison.OrdinalIgnoreCase);

            var item = new TextBlock
            {
                Text = friendly, FontSize = 13, Foreground = WarningBrush(),
                TextWrapping = TextWrapping.Wrap, LineHeight = 20
            };
            rows.Children.Add(item);
        }
        if (failures.Count > 8)
            rows.Children.Add(new TextBlock
            {
                Text = $"…a dalších {failures.Count - 8}",
                FontSize = 12,
                Foreground = DialogSecondaryTextBrush(),
                Margin = new Thickness(4, 2, 0, 0)
            });

        var scroll = DialogScroll(rows);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Task Finish()
        {
            UnmountDialog(overlay);
            tcs.TrySetResult(true);
            return Task.CompletedTask;
        }

        var actions = DialogActions();
        actions.Margin = new Thickness(0);
        var primary = StyledDialogButton("Zavřít", true, Finish);
        actions.Children.Add(primary);

        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        card.Child = root;
        MountDialog(overlay, card, () => { UnmountDialog(overlay); tcs.TrySetResult(true); });
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => primary.Focus(FocusState.Programmatic));
        await tcs.Task;
    }
}
