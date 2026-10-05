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

    private double DialogMaxHeight(double cap = 720)
    {
        double available = RootGrid?.ActualHeight ?? 0;
        if (available <= 0) return Math.Min(cap, 680);
        return Math.Max(300, Math.Min(cap, available - 48));
    }

    private static Brush DialogSecondaryTextBrush()
        => _darkTheme ? ThemeColor(176,176,176) : TextSecondaryBrush();

    private static Brush DialogBodyBrush()
        => _darkTheme ? GlassPanelBrush() : SurfaceAltBrush();

    private static Brush DialogNeutralBorderBrush()
        => _darkTheme ? GlassEdgeBrush() : BorderThemeBrush();

    private static Brush DialogNeutralHoverBorderBrush()
        => _darkTheme ? GlassBlueEdgeBrush() : BorderThemeBrush();

    private static Brush DialogInnerSurfaceBrush()
        => _darkTheme ? ThemeColor(64, 20, 32, 46) : SurfaceAltBrush();

    private static Brush DialogInnerStrongBrush()
        => _darkTheme ? ThemeColor(82, 15, 27, 41) : SurfaceElevatedBrush();

    private Grid CreateDialogOverlay(int zIndex = 30000, byte alpha = 112)
    {
        var overlay = new Grid
        {
            Background = DialogOverlayBrush(alpha),
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
            Background = _darkTheme ? GlassPanelBrush() : SectionCardBackgroundBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_darkTheme ? 24 : 10),
            Padding = new Thickness(_darkTheme ? 26 : 24),
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
        var card = CreateDialogCard(760, 720, 860);
        if (card.MinHeight <= 0 || card.MaxHeight < card.MinHeight)
            throw new InvalidOperationException($"Dialog height invalid: min={card.MinHeight} max={card.MaxHeight}.");
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

            var depthShell = new Border
            {
                Margin = new Thickness(4, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = _darkTheme ? ThemeColor(62, 7, 12, 18) : SurfaceAltBrush(),
                BorderBrush = DialogNeutralBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(_darkTheme ? 26 : 8),
                Opacity = _darkTheme ? 0.72 : 0.72
            };
            depthShell.Translation = new System.Numerics.Vector3(0, 0, 14);
            host.Children.Add(depthShell);

            card.Width = double.NaN;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.Translation = new System.Numerics.Vector3(0, 0, 30);
            host.Children.Add(card);

            var topHighlight = new Border
            {
                Height = 1,
                Margin = new Thickness(28, 1, 70, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Background = _darkTheme ? ThemeColor(90, 255, 255, 255) : DialogNeutralBorderBrush(),
                CornerRadius = new CornerRadius(1),
                Opacity = _darkTheme ? 0.28 : 0.42
            };
            Canvas.SetZIndex(topHighlight, 6);
            host.Children.Add(topHighlight);

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
                CornerRadius = new CornerRadius(12),
                Opacity = 0.96,
                Content = new FontIcon { Glyph = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 11.5, Foreground = TextPrimaryBrush() }
            };
            ToolTipService.SetToolTip(close, "Zavřít");
            EnsureAccessibleButton(close, "Zavřít dialog");
            close.Translation = new System.Numerics.Vector3(0, 0, 38);
            // Neutral close button uses color feedback without scaling.
            close.PointerEntered += (_, _) => { close.Background = DialogBodyBrush(); close.BorderBrush = DialogNeutralHoverBorderBrush(); close.Opacity = 1; };
            close.PointerExited += (_, _) => { close.Background = DialogInnerSurfaceBrush(); close.BorderBrush = DialogNeutralBorderBrush(); close.Opacity = 0.94; };
            close.Click += (_, _) => escapeAction();
            Canvas.SetZIndex(close, 10);
            host.Children.Add(close);
            overlay.Children.Add(host);
            close.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => close.Focus(FocusState.Programmatic));
        }
        else overlay.Children.Add(card);
        RootGrid.Children.Add(overlay);
        overlay.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => AnimateDialogIn(overlay, card));
        return close;
    }

    private void UnmountDialog(Grid overlay)
    {
        Control? restore = null;
        if (_dialogFocusRestore.TryGetValue(overlay, out var weak))
        {
            _dialogFocusRestore.Remove(overlay);
            weak.TryGetTarget(out restore);
        }

        if (RootGrid.Children.Contains(overlay))
            RootGrid.Children.Remove(overlay);

        if (restore != null)
        {
            restore.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
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

    internal string RunDialogLifecycleSmoke()
    {
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
            Background = _darkTheme ? (primary ? ThemeColor(7,7,7) : ThemeColor(0,0,0)) : (primary ? ButtonVioletBrush() : ButtonVioletSoftBrush()),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(primary ? 1.4 : 1),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            
        };
        ApplyMaterialButtonAppearance(b, primary);
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
            b.IsEnabled = false;
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
        var card = CreateDialogCard(590, 700);
        var root = CreateDialogLayout();
        var header = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 48, 0) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle)) header.Children.Add(new TextBlock { Text = subtitle, FontSize = 13.3, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(header);
        var info = new Border { Background = DialogBodyBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(17), Child = new TextBlock { Text = message, FontSize = 14, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 21 } };
        info.Translation = new System.Numerics.Vector3(0, 0, 8);
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
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => primary.Focus(FocusState.Programmatic));
        return await tcs.Task;
    }

    private async Task ShowStyledInfoAsync(string title, string message, string buttonText = "OK")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay();
        var card = CreateDialogCard(560, 700);
        var root = CreateDialogLayout();
        root.Children.Add(new TextBlock { Text = title, FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 48, 0) });
        var body = new Border { Background = DialogBodyBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(17), Child = new TextBlock { Text = message, FontSize = 14, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 21 } };
        body.Translation = new System.Numerics.Vector3(0, 0, 8);
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
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => primary.Focus(FocusState.Programmatic));
        await tcs.Task;
    }
}

// Progress overlay shared by long-running optimizer actions.
public sealed partial class MainWindow
{
    private bool? _lastProgressVerifiedForSmoke;
    private readonly SemaphoreSlim _progressOperationGate = new(1, 1);
    private readonly object _progressOperationSync = new();
    private CancellationTokenSource? _activeProgressCts;
    private Action? _activeProgressClose;

    internal void ShutdownActiveOperations()
    {
        CancelActiveProgressOperationForNavigation();
    }

    private void CancelActiveProgressOperationForNavigation()
    {
        Action? close = null;
        lock (_progressOperationSync)
        {
            try { _activeProgressCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            close = _activeProgressClose;
        }

        try { close?.Invoke(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<(bool Completed, T Result)> RunCancellableWithProgressAsync<T>(
        string title,
        string status,
        Func<CancellationToken, Task<T>> work,
        Func<T, bool>? isSuccess = null,
        Func<T, string>? failureStatus = null,
        Action<Action<string>>? liveStatusBinder = null)
    {
        if (!await _progressOperationGate.WaitAsync(0))
        {
            LogService.Info("ui", $"PROGRESS_OPERATION_DUPLICATE_BLOCKED title={title}");
            return (false, default!);
        }

        using var cts = new CancellationTokenSource();
        Grid? overlay = null;
        Microsoft.UI.Xaml.DispatcherTimer? timer = null;
        bool popupClosed = false;
        try
        {
            lock (_progressOperationSync) _activeProgressCts = cts;
            overlay = CreateDialogOverlay(11000, 145);
            var card = CreateDialogCard(520, 680, z: 36);
            var stack = new StackPanel { Spacing = 14 };
            var state = new TextBlock { Text = "APLIKUJI", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ProgressBrush() };
            var statusText = new TextBlock { Text = status, FontSize = 13.6, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            liveStatusBinder?.Invoke(message =>
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!popupClosed)
                        statusText.Text = message;
                });
            });
            stack.Children.Add(state);
            stack.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            stack.Children.Add(statusText);
            var activity = new Grid { ColumnSpacing = 6, Height = 6 };
            var bars = new Border[4];
            for (int i = 0; i < bars.Length; i++)
            {
                activity.ColumnDefinitions.Add(new ColumnDefinition());
                bars[i] = new Border { CornerRadius = new CornerRadius(3), Background = SurfaceAltBrush() };
                Grid.SetColumn(bars[i], i);
                activity.Children.Add(bars[i]);
            }
            stack.Children.Add(activity);
            int activityIndex = 0;
            timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            timer.Tick += (_, _) =>
            {
                for (int i = 0; i < bars.Length; i++) bars[i].Background = i == activityIndex ? ProgressBrush() : SurfaceAltBrush();
                activityIndex = (activityIndex + 1) % bars.Length;
            };

            void ClosePopup()
            {
                if (popupClosed) return;
                popupClosed = true;
                timer.Stop();
                try { cts.Cancel(); }
                finally { UnmountDialog(overlay); }
            }

            lock (_progressOperationSync)
            {
                if (ReferenceEquals(_activeProgressCts, cts))
                    _activeProgressClose = ClosePopup;
            }

            card.Child = stack;
            MountDialog(overlay, card, ClosePopup);
            timer.Start();
            T result = await work(cts.Token);
            if (popupClosed || cts.IsCancellationRequested)
                return (false, default!);
            bool verified = isSuccess?.Invoke(result) ?? true;
            _lastProgressVerifiedForSmoke = verified;
            state.Text = verified ? "OVĚŘENO ✓" : "CHYBA";
            state.Foreground = verified ? SuccessBrush() : WarningBrush();
            foreach (var bar in bars) bar.Background = verified ? SuccessBrush() : WarningBrush();
            if (!verified && failureStatus != null)
            {
                string message = failureStatus(result);
                if (!string.IsNullOrWhiteSpace(message)) statusText.Text = message;
            }
            timer.Stop();
            await Task.Delay(verified ? 140 : 260, cts.Token);
            return (true, result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _lastProgressVerifiedForSmoke = null;
            return (false, default!);
        }
        finally
        {
            // Setup, backend and visual cleanup failures must all release single-flight.
            popupClosed = true;
            try
            {
                timer?.Stop();
                if (overlay != null) UnmountDialog(overlay);
            }
            finally
            {
                lock (_progressOperationSync)
                {
                    if (ReferenceEquals(_activeProgressCts, cts))
                    {
                        _activeProgressCts = null;
                        _activeProgressClose = null;
                    }
                }
                _progressOperationGate.Release();
            }
        }
    }

    private async Task<bool> RunCancellableWithProgressAsync(
        string title,
        string status,
        Func<CancellationToken, Task> work)
    {
        var result = await RunCancellableWithProgressAsync<object?>(
            title, status, async token => { await work(token); return null; });
        return result.Completed;
    }

    internal async Task RunProgressPredicateSmokeAsync()
    {
        // Cold standalone smoke must finish initial navigation before opening operations.
        await ShowSectionAsync("overview");
        await WaitForVisualCaptureReadyAsync(_themePreference, "overview");
        int overlaysBefore = RootGrid.Children.Count;
        bool setupFailed = false;
        try
        {
            await RunCancellableWithProgressAsync(
                "Progress setup failure smoke", "Synthetic setup error",
                _ => Task.FromResult(1),
                liveStatusBinder: _ => throw new InvalidOperationException("synthetic-progress-setup-error"));
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic-progress-setup-error")
        {
            setupFailed = true;
        }
        if (!setupFailed || _progressOperationGate.CurrentCount != 1)
            throw new InvalidOperationException("Progress setup exception leaked the single-flight gate.");
        lock (_progressOperationSync)
        {
            if (_activeProgressCts != null || _activeProgressClose != null)
                throw new InvalidOperationException("Progress setup exception retained active operation state.");
        }
        if (RootGrid.Children.Count != overlaysBefore)
            throw new InvalidOperationException("Progress setup exception retained an overlay.");

        _lastProgressVerifiedForSmoke = null;
        _ = await RunCancellableWithProgressAsync(
            "Progress smoke",
            "Ověřuji úspěšný výsledek…",
            _ => Task.FromResult(Array.Empty<string>()),
            result => result.Length == 0);
        if (_lastProgressVerifiedForSmoke != true)
            throw new InvalidOperationException("Progress predicate smoke did not mark success as verified.");

        _lastProgressVerifiedForSmoke = null;
        _ = await RunCancellableWithProgressAsync(
            "Progress smoke",
            "Ověřuji chybový výsledek…",
            _ => Task.FromResult(new[] { "synthetic-error" }),
            result => result.Length == 0,
            result => string.Join(" • ", result));
        if (_lastProgressVerifiedForSmoke != false)
            throw new InvalidOperationException("Progress predicate smoke incorrectly marked an error result as verified.");

        var first = RunCancellableWithProgressAsync(
            "Progress single-flight smoke",
            "Držím první operaci…",
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return 1;
            });
        await Task.Delay(80);
        var duplicate = await RunCancellableWithProgressAsync(
            "Progress duplicate smoke",
            "Tato operace musí být zablokována…",
            _ => Task.FromResult(2));
        if (duplicate.Completed)
            throw new InvalidOperationException("Progress single-flight dovolil druhou souběžnou operaci.");

        CancelActiveProgressOperationForNavigation();
        var firstResult = await first;
        if (firstResult.Completed)
            throw new InvalidOperationException("Navigační cancel nezrušil aktivní progress operaci.");

        bool lateVerifierCalled = false;
        var ignoresCancellation = RunCancellableWithProgressAsync(
            "Progress stale-result smoke",
            "Backend ignoruje cancel a vrátí pozdní výsledek…",
            async _ =>
            {
                await Task.Delay(220, CancellationToken.None);
                return 42;
            },
            result =>
            {
                lateVerifierCalled = true;
                return result == 42;
            });
        await Task.Delay(60);
        CancelActiveProgressOperationForNavigation();
        var ignoredResult = await ignoresCancellation;
        if (ignoredResult.Completed)
            throw new InvalidOperationException("Pozdní výsledek backendu ignorujícího cancel byl chybně přijat.");
        if (lateVerifierCalled)
            throw new InvalidOperationException("Verifier se spustil nad stale výsledkem po zavření progress popupu.");
        lock (_progressOperationSync)
        {
            if (_activeProgressCts != null || _activeProgressClose != null)
                throw new InvalidOperationException("Progress lifecycle state nebyl po stale-result cancelu vyčištěn.");
        }

        foreach (bool failVerifier in new[] { false, true })
        {
            bool expectedFailure = false;
            try
            {
                await RunCancellableWithProgressAsync<int>(
                    "Progress exception recovery smoke", "Synthetic failure",
                    _ => failVerifier ? Task.FromResult(1) : Task.FromException<int>(new InvalidOperationException("synthetic-progress-work-error")),
                    _ => throw new InvalidOperationException("synthetic-progress-work-error"));
            }
            catch (InvalidOperationException ex) when (ex.Message == "synthetic-progress-work-error")
            {
                expectedFailure = true;
            }
            if (!expectedFailure || _progressOperationGate.CurrentCount != 1 || RootGrid.Children.Count != overlaysBefore)
                throw new InvalidOperationException("Progress backend/verifier failure did not release resources.");
        }

        var finishing = RunCancellableWithProgressAsync("Progress completion cancel smoke", "Complete then cancel", _ => Task.FromResult(1));
        CancelActiveProgressOperationForNavigation();
        if ((await finishing).Completed)
            throw new InvalidOperationException("Cancellation during the completion delay was accepted as success.");
        if (_progressOperationGate.CurrentCount != 1 || RootGrid.Children.Count != overlaysBefore)
            throw new InvalidOperationException("Completion cancel retained popup resources.");

        LogService.Info("regression", "PROGRESS_PREDICATE_SMOKE_OK success=true failure=false singleFlight=true navigationCancel=true staleResultDiscarded=true setupFailureRecovery=true backendFailureRecovery=true verifierFailureRecovery=true completionCancel=true");
    }
}

public sealed partial class MainWindow
{
    private async Task<bool> ShowStyledContentAsync(string title, string subtitle, UIElement content, string primaryText, string secondaryText = "Zrušit")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay();
        var card = CreateDialogCard(760, 720, 860);
        var root = CreateDialogLayout();
        var head = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 48, 0) };
        head.Children.Add(new TextBlock { Text = title, FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new TextBlock { Text = subtitle, FontSize = 13.3, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(head);
        var body = new Border { Background = DialogBodyBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(17), Child = content };
        body.Translation = new System.Numerics.Vector3(0, 0, 8);
        var scroll = DialogScroll(body); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        bool closeOnly = string.Equals(primaryText, "Zavřít", StringComparison.OrdinalIgnoreCase)
            || string.Equals(primaryText, "OK", StringComparison.OrdinalIgnoreCase);
        Task Finish(bool value) { UnmountDialog(overlay); tcs.TrySetResult(value); return Task.CompletedTask; }
        var actions = DialogActions();
        if (!closeOnly)
            actions.Children.Add(StyledDialogButton(secondaryText, false, () => Finish(false)));
        var primary = StyledDialogButton(
            string.IsNullOrWhiteSpace(primaryText) ? "Zavřít" : primaryText,
            true,
            () => Finish(true));
        actions.Children.Add(primary);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        card.Child = root;
        MountDialog(overlay, card, () => { UnmountDialog(overlay); tcs.TrySetResult(closeOnly); });
        return await tcs.Task;
    }
}

public sealed partial class MainWindow
{
    private async Task<bool> ShowTweakApplyConfirmationAsync(IReadOnlyList<TweakDefinition> dirty)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay();
        var card = CreateDialogCard(830, 720, 920);
        var root = CreateDialogLayout();

        var headerGrid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 48, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Border
        {
            Width = 38, Height = 34, CornerRadius = new CornerRadius(8),
            Background = AccentSoftBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(1),
            Child = new FontIcon { Glyph = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 15, Foreground = AccentBrush() }
        };
        headerGrid.Children.Add(icon);
        var headerText = new StackPanel { Spacing = 3 };
        headerText.Children.Add(new TextBlock { Text = "Potvrdit vybrané tweaky", FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        headerText.Children.Add(new TextBlock { Text = "Zkontroluj změny před aplikací.", FontSize = 13.3, Foreground = DialogSecondaryTextBrush() });
        Grid.SetColumn(headerText, 1); headerGrid.Children.Add(headerText);
        root.Children.Add(headerGrid);

        var content = new StackPanel { Spacing = 0 };
        var summary = new Grid { Padding = new Thickness(16, 14, 16, 14) };
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var summaryText = new StackPanel { Spacing = 2 };
        summaryText.Children.Add(new TextBlock { Text = "Vybráno změn", FontSize = 13.3, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        summaryText.Children.Add(new TextBlock { Text = "Budou aplikovány pouze položky níže.", FontSize = 12.2, Foreground = DialogSecondaryTextBrush() });
        summary.Children.Add(summaryText);
        var countBadge = new Border
        {
            MinWidth = 34, Height = 32, Padding = new Thickness(9, 0, 9, 0), CornerRadius = new CornerRadius(4),
            Background = AccentSoftBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = dirty.Count.ToString(), FontSize = 13.3, FontWeight = FontWeights.SemiBold, Foreground = AccentBrush(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
        };
        Grid.SetColumn(countBadge, 1); summary.Children.Add(countBadge);
        content.Children.Add(summary);

        var separator = new Border { Height = 1, Background = DialogNeutralBorderBrush(), Opacity = .7 };
        content.Children.Add(separator);

        var rows = new StackPanel();
        int index = 0;
        foreach (var tweak in dirty.Take(40))
        {
            index++;
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 12, 16, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var number = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
                Background = DialogBodyBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = index.ToString(), FontSize = 11.5, Foreground = DialogSecondaryTextBrush(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            row.Children.Add(number);
            var label = new TextBlock { Text = tweak.Name, FontSize = 13.3, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var state = new Border
            {
                MinWidth = 48, Height = 27, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8),
                Background = tweak.Pending ? AccentSoftBrush() : SurfaceAltBrush(),
                BorderBrush = tweak.Pending ? DialogNeutralBorderBrush() : DialogNeutralBorderBrush(), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = tweak.Pending ? "ZAP" : "VYP", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = tweak.Pending ? AccentBrush() : DialogSecondaryTextBrush(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            Grid.SetColumn(state, 2); row.Children.Add(state);
            rows.Children.Add(row);
            if (index < Math.Min(40, dirty.Count)) rows.Children.Add(new Border { Height = 1, Background = DialogNeutralBorderBrush(), Opacity = .45 });
        }
        if (dirty.Count > 40)
            rows.Children.Add(new TextBlock { Text = $"…a dalších {dirty.Count - 40}", Margin = new Thickness(16, 10, 16, 12), FontSize = 12.2, Foreground = DialogSecondaryTextBrush() });
        content.Children.Add(rows);

        var body = new Border
        {
            Background = DialogBodyBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1),
            CornerRadius = new CornerRadius(18), Child = content
        };
        body.Translation = new System.Numerics.Vector3(0, 0, 8);

        var bodyStack = new StackPanel { Spacing = 12 };
        bodyStack.Children.Add(body);
        bodyStack.Children.Add(new Border
        {
            Background = DialogBodyBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(13, 10, 13, 10),
            Child = new TextBlock { Text = "Po aplikaci se stav znovu ověří proti Windows. Neúspěšné změny se zobrazí zvlášť.", FontSize = 11.2, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap }
        });
        if (dirty.Any(x => x.Risk == TweakRisk.Advanced))
            bodyStack.Children.Add(new TextBlock { Text = "Některé změny jsou pokročilé a mohou ovlivnit zabezpečení nebo funkčnost Windows.", FontSize = 11.2, Foreground = WarningBrush(), TextWrapping = TextWrapping.Wrap });

        var scroll = DialogScroll(bodyStack); Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        bool finished = false;
        void Finish(bool value, string source)
        {
            if (finished) return;
            finished = true;
            LogService.Info("dialog", $"TWEAK_APPLY_CONFIRM_FINISH source={source} accepted={value} count={dirty.Count}");
            UnmountDialog(overlay);
            tcs.TrySetResult(value);
        }

        Button MakeActionButton(string text, bool primaryStyle)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = primaryStyle ? 116 : 98,
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                FontSize = 12,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = primaryStyle ? OnAccentTextBrush() : TextPrimaryBrush(),
                Background = primaryStyle ? ButtonVioletBrush() : ButtonVioletSoftBrush(),
                BorderBrush = DialogNeutralBorderBrush(),
                BorderThickness = new Thickness(primaryStyle ? 1.4 : 1),
                FontWeight = primaryStyle ? FontWeights.SemiBold : FontWeights.Medium,
                
            };
            ApplyMaterialButtonAppearance(button, primaryStyle);
            EnsureAccessibleButton(button, text);
            AttachHoverMotion(button, primaryStyle ? 1.018f : 1.012f);
            return button;
        }

        var actions = DialogActions();
        var cancel = MakeActionButton("Zrušit", false);
        cancel.Click += (_, _) => Finish(false, "cancel");
        actions.Children.Add(cancel);
        var primary = MakeActionButton("Použít vybrané", true);
        primary.Click += (_, _) => Finish(true, "apply");
        actions.Children.Add(primary);
        Grid.SetRow(actions, 2); root.Children.Add(actions);
        card.Child = root;
        MountDialog(overlay, card, () => Finish(false, "close"));
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => primary.Focus(FocusState.Programmatic));
        return await tcs.Task;
    }

    private async Task<int> ShowStyledChoiceAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Zrušit", string? subtitle = null)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay();
        var card = CreateDialogCard(610, 700);
        var root = CreateDialogLayout();
        var head = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 48, 0) };
        head.Children.Add(new TextBlock { Text = title, FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle)) head.Children.Add(new TextBlock { Text = subtitle, FontSize = 13.3, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(head);
        var body = new Border { Background = DialogBodyBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(17), Child = new TextBlock { Text = message, FontSize = 14, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 21 } };
        body.Translation = new System.Numerics.Vector3(0, 0, 8);
        var scroll = DialogScroll(body); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        Task Finish(int value) { UnmountDialog(overlay); tcs.TrySetResult(value); return Task.CompletedTask; }
        var actions = DialogActions();
        // The top-right X/Escape already owns the neutral close path.
        // Never render a third footer close/cancel action: it looked like a random
        // "Zavřít" button in choice popups and duplicated the window chrome.
        actions.Children.Add(StyledDialogButton(secondaryText, false, () => Finish(2)));
        var primary = StyledDialogButton(primaryText, true, () => Finish(1));
        actions.Children.Add(primary);
        Grid.SetRow(actions, 2); root.Children.Add(actions);
        card.Child = root;
        MountDialog(overlay, card, () => { UnmountDialog(overlay); tcs.TrySetResult(0); });
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => primary.Focus(FocusState.Programmatic));
        return await tcs.Task;
    }
}

public sealed partial class MainWindow
{
    private async Task<int> RunPowerProfileProgressPopupAsync(bool autoClose = false)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var overlay = CreateDialogOverlay(12000, 155);
        var card = CreateDialogCard(700, 680, z: 36);
        var root = CreateDialogLayout();
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 48, 0) };
        header.Children.Add(new TextBlock { Text = "Power Plan — Recommended", FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        var summary = new TextBlock { Text = "0 / 34 změn", FontSize = 13.3, Foreground = DialogSecondaryTextBrush() };
        var current = new TextBlock { Text = "Připravuji…", FontSize = 13.5, Foreground = ProgressBrush(), TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { Text = "Čekám na dokončení operace…", FontSize = 12, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap };
        header.Children.Add(summary); header.Children.Add(current); header.Children.Add(detail);
        var track = new Grid { Height = 7, Background = SurfaceAltBrush(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fill = new Border { Width = 0, Height = 7, CornerRadius = new CornerRadius(3.5), Background = ProgressBrush(), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill); header.Children.Add(track); root.Children.Add(header);
        var middle = new StackPanel { Spacing = 10 };
        middle.Children.Add(new TextBlock { Text = "Průběh změn", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = DialogSecondaryTextBrush() });
        var logText = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var logScroll = new ScrollViewer { Content = logText, Height = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Background = SurfaceAltBrush(), Padding = new Thickness(12) };
        middle.Children.Add(logScroll);
        var middleScroll = DialogScroll(middle); Grid.SetRow(middleScroll, 1); root.Children.Add(middleScroll);
        bool popupClosed = false;
        bool finished = false;
        void ClosePopup()
        {
            if (popupClosed) return;
            popupClosed = true;
            if (!finished) cts.Cancel();
            UnmountDialog(overlay);
            tcs.TrySetResult(true);
        }

        card.Child = root;
        var topClose = MountDialog(overlay, card, ClosePopup);
        var lines = new Queue<string>(128);
        int exitCode = -1;
        var progress = new Progress<PerformanceProfilesService.PowerProfileProgress>(p =>
        {
            if (popupClosed) return;
            double width = track.ActualWidth > 0 ? track.ActualWidth : 520;
            fill.Width = width * Math.Clamp(p.Percent, 0, 100) / 100d;
            summary.Text = $"{Math.Min(p.Current, 34)} / 34 změn • {p.Percent} %";
            current.Text = p.Stage;
            detail.Text = p.Detail;
            if (p.Stage is "Aplikuji" or "Ověření")
            {
                lines.Enqueue((p.Stage == "Ověření" ? "✓ " : "• ") + p.Detail);
                while (lines.Count > 100) lines.Dequeue();
                logText.Text = string.Join(Environment.NewLine, lines);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (popupClosed) return;
                    try { if (logScroll.XamlRoot != null) logScroll.ChangeView(null, logScroll.ScrollableHeight, null, true); } catch { }
                });
            }
        });
        try
        {
            LogService.Info("POWER_PROGRESS_POPUP_START");
            string result = await PerformanceProfilesService.ApplyPerformancePowerProfileAsync(progress, cts.Token);
            if (popupClosed || cts.IsCancellationRequested)
            {
                exitCode = -2;
                return exitCode;
            }
            PowerPlanStateService.InvalidateCache();
            var snap = await PowerPlanStateService.ReadAsync(forceRefresh: true);
            if (popupClosed || cts.IsCancellationRequested)
            {
                exitCode = -2;
                return exitCode;
            }
            var critical = EvaluateCriticalPowerState(snap);
            int total = critical.Total;
            int active = Math.Max(0, total - critical.Drift);
            exitCode = critical.ActiveOk && critical.Drift == 0 && total > 0 ? 0 : 2;
            fill.Width = track.ActualWidth > 0 ? track.ActualWidth : 520;
            summary.Text = $"{active} / {total} critical ověřeno • 100 %";
            current.Text = exitCode == 0 ? "Hotovo — Recommended profil je aktivní" : "Finální ověření není kompletní";
            detail.Text = $"Finální ověření: {active}/{total} • aktivní plán {(critical.ActiveOk ? "OK" : "NE")} • {PerformanceProfilesService.GetCpuName()}";
            lines.Enqueue((exitCode == 0 ? "✓ " : "! ") + $"Ověření: {active}/{total} • aktivní plán {(critical.ActiveOk ? "OK" : "NE")}");
            logText.Text = string.Join(Environment.NewLine, lines);
            SystemInfoService.InvalidateDynamic();
            LogService.Info($"POWER_PROGRESS_POPUP_OK activeGuid={critical.ActiveOk} critical={active}/{total} | " + result.Replace(Environment.NewLine, " | "));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            exitCode = -2;
            LogService.Info("power", "Power Recommended popup cancelled by user.");
        }
        catch (Exception ex)
        {
            exitCode = -1;
            if (!popupClosed)
            {
                current.Text = "Chyba při aplikaci Recommended profilu";
                detail.Text = ex.Message;
                lines.Enqueue("✕ " + ex.Message);
                logText.Text = string.Join(Environment.NewLine, lines);
            }
            LogService.Error("Power progress popup failed", ex);
        }
        finished = true;

        // A fully verified Recommended profile is a completed transient operation.
        // Keep the success state visible briefly, then close automatically.
        // Failures stay open so the user can inspect the read-back/log.
        if (autoClose || exitCode == 0)
        {
            if (!autoClose) await Task.Delay(700);
            overlay.Visibility = Visibility.Collapsed;
            await Task.Delay(100);
            try { UnmountDialog(overlay); } catch { }
            return exitCode;
        }

        await tcs.Task;
        return exitCode;
    }

}

public sealed partial class MainWindow
{
    private async Task<int> RunNicProgressPopupAsync(bool autoClose = false)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var overlay = CreateDialogOverlay(12000);
        var card = CreateDialogCard(590, 680, z: 36);
        var root = new StackPanel { Spacing = 16 };
        var head = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 0, 48, 0) }; head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(11), Background = SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = SectionIcon("network", 32) });
        var titles = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "NIC Max Performance", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        titles.Children.Add(new TextBlock { Text = "Realtek / Intel síťový profil", FontSize = 12, Foreground = DialogSecondaryTextBrush() });
        Grid.SetColumn(titles, 1); head.Children.Add(titles); root.Children.Add(head);
        root.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(13), Child = new TextBlock { Text = "Recommended profil vypne podporované úsporné funkce adaptéru, zapne RSS a vypne Selective Suspend. Interrupt Moderation, Flow Control ani Packet/DMA Coalescing nemění.", FontSize = 13.5, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 20 } });
        var status = new TextBlock { Text = "Načítám síťový adaptér…", FontSize = 13, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap }; root.Children.Add(status);
        var segments = new Grid { ColumnSpacing = 6, Height = 6 }; for (int x = 0; x < 4; x++) segments.ColumnDefinitions.Add(new ColumnDefinition());
        var bars = new Border[4]; for (int x = 0; x < 4; x++) { bars[x] = new Border { CornerRadius = new CornerRadius(3), Background = SurfaceAltBrush() }; Grid.SetColumn(bars[x], x); segments.Children.Add(bars[x]); }
        root.Children.Add(segments);
        bool popupClosed = false;
        bool finished = false;
        void ClosePopup()
        {
            if (popupClosed) return;
            popupClosed = true;
            if (!finished) cts.Cancel();
            UnmountDialog(overlay);
            tcs.TrySetResult(true);
        }

        card.Child = root;
        MountDialog(overlay, card, ClosePopup);
        int exitCode = -1;
        try
        {
            string[] steps = { "Načítám aktivní adaptér…", "Aplikuji Realtek / Intel vlastnosti…", "Restartuji síťový adaptér…", "Ověřuji dokončení profilu…" };
            var applyTask = PerformanceProfilesService.ApplyNicPerformanceProfileAsync(cts.Token);
            int step = 0;
            while (!applyTask.IsCompleted)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (popupClosed)
                {
                    exitCode = -2;
                    return exitCode;
                }

                int currentStep = Math.Min(step, steps.Length - 1);
                status.Text = steps[currentStep];
                for (int i = 0; i <= currentStep; i++)
                    bars[i].Background = ButtonVioletBrush();

                step++;
                await Task.WhenAny(applyTask, Task.Delay(500, cts.Token));
            }

            string backendResult = await applyTask;
            cts.Token.ThrowIfCancellationRequested();
            if (popupClosed)
            {
                exitCode = -2;
                return exitCode;
            }

            bool partialFailure =
                backendResult.Contains(" = Failed:", StringComparison.OrdinalIgnoreCase) ||
                backendResult.Contains(" = Detection failed:", StringComparison.OrdinalIgnoreCase);

            if (partialFailure)
            {
                exitCode = 2;
                status.Text = "NIC profil byl aplikován jen částečně — některé ověření selhalo.";
                LogService.Error("network", "NIC Recommended partial verification failure | " +
                    backendResult.Replace(Environment.NewLine, " | "));
            }
            else
            {
                exitCode = 0;
                foreach (var b in bars) b.Background = ButtonVioletBrush();
                status.Text = "Hotovo — NIC Max Performance byl aplikován a ověřen.";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            exitCode = -2;
            LogService.Info("network", "NIC Max Performance popup cancelled by user.");
        }
        catch (Exception ex)
        {
            exitCode = -1;
            if (!popupClosed)
            {
                LogService.Error("NIC progress popup failed", ex);
                status.Text = "Chyba: " + ex.Message;
            }
        }
        finally
        {
            finished = true;
        }

        if (autoClose || exitCode == 0)
        {
            if (!autoClose) await Task.Delay(700);
            if (!popupClosed) UnmountDialog(overlay);
            return exitCode;
        }

        if (!popupClosed) await tcs.Task;
        return exitCode;
    }
}
