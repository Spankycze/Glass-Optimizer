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
        System.EventHandler<object>? activityTick = null;
        bool popupClosed = false;
        try
        {
            lock (_progressOperationSync) _activeProgressCts = cts;
            overlay = CreateDialogOverlay(11000, 96);
            var card = CreateDialogCard(520, 680, z: 36);
            var stack = new StackPanel { Spacing = 14 };
            var state = new TextBlock { Text = "APLIKUJI", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ProgressBrush() };
            var statusText = new TextBlock { Text = status, FontSize = 13.6, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            liveStatusBinder?.Invoke(message =>
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!popupClosed && !string.Equals(statusText.Text, message, StringComparison.Ordinal))
                        statusText.Text = message;
                });
            });
            stack.Children.Add(CreateDialogHeader(title));
            stack.Children.Add(state);
            stack.Children.Add(statusText);
            var activity = new Grid { ColumnSpacing = 6, Height = 6 };
            var bars = new Border[4];
            for (int i = 0; i < bars.Length; i++)
            {
                activity.ColumnDefinitions.Add(new ColumnDefinition());
                bars[i] = new Border { CornerRadius = new CornerRadius(3), Background = _darkTheme ? GlassTrackBrush() : SurfaceAltBrush() };
                Grid.SetColumn(bars[i], i);
                activity.Children.Add(bars[i]);
            }
            stack.Children.Add(activity);
            int activityIndex = 0;
            int previousActivityIndex = -1;
            Brush activityIdleBrush = _darkTheme ? GlassTrackBrush() : SurfaceAltBrush();
            Brush activityActiveBrush = ProgressBrush();
            timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
            activityTick = (_, _) =>
            {
                if (previousActivityIndex >= 0) bars[previousActivityIndex].Background = activityIdleBrush;
                bars[activityIndex].Background = activityActiveBrush;
                previousActivityIndex = activityIndex;
                activityIndex = (activityIndex + 1) % bars.Length;
            };
            timer.Tick += activityTick;

            void ClosePopup()
            {
                if (popupClosed) return;
                popupClosed = true;
                timer.Stop();
                if (activityTick != null)
                    timer.Tick -= activityTick;
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
            string finalState = verified ? "OVĚŘENO ✓" : "CHYBA";
            if (!string.Equals(state.Text, finalState, StringComparison.Ordinal)) state.Text = finalState;
            state.Foreground = verified ? SuccessBrush() : WarningBrush();
            foreach (var bar in bars) bar.Background = verified ? SuccessBrush() : WarningBrush();
            if (!verified && failureStatus != null)
            {
                string message = failureStatus(result);
                if (!string.IsNullOrWhiteSpace(message) && !string.Equals(statusText.Text, message, StringComparison.Ordinal)) statusText.Text = message;
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
                if (timer != null)
                {
                    timer.Stop();
                    if (activityTick != null)
                        timer.Tick -= activityTick;
                }
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
        // Progress lifecycle needs a fully loaded Overview, not identical pixels. Live telemetry
        // legitimately changes gauge geometry/text between samples and must not block this smoke.
        await WaitForVisualCaptureReadyAsync(_themePreference, "overview", requirePixelStability: false, requireSectionData: false);
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
        root.Children.Add(CreateDialogHeader(title, subtitle));
        var scroll = DialogScroll(content); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
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
    internal async Task RunTweakConfirmationPreviewAsync()
    {
        await ShowTweakApplyConfirmationAsync(new[]
        {
            new TweakDefinition
            {
                Id = "preview.noop", Section = "system", Group = "Náhled",
                Name = "Vypnout Game DVR — pouze náhled", Description = "Žádná změna se neprovede."
            }
        });
    }

    private async Task<bool> ShowTweakApplyConfirmationAsync(IReadOnlyList<TweakDefinition> dirty)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay(alpha: 78);
        var card = CreateDialogCard(560, 620, 640);
        var root = CreateDialogLayout();

        root.Children.Add(CreateDialogHeader("Potvrdit změny", $"Vybráno změn: {dirty.Count}"));

        var rows = new StackPanel();
        int index = 0;
        foreach (var tweak in dirty.Take(40))
        {
            index++;
            var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 10, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = tweak.Name, FontSize = 13.3, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
            var state = new TextBlock
            {
                Text = tweak.Pending ? "ZAP" : "VYP", FontSize = 12,
                Foreground = DialogSecondaryTextBrush(), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(state, 1); row.Children.Add(state);
            rows.Children.Add(row);
            if (index < Math.Min(40, dirty.Count)) rows.Children.Add(new Border { Height = 1, Background = DialogNeutralBorderBrush(), Opacity = .45 });
        }
        if (dirty.Count > 40)
            rows.Children.Add(new TextBlock { Text = $"…a dalších {dirty.Count - 40}", Margin = new Thickness(0, 10, 0, 0), FontSize = 12.2, Foreground = DialogSecondaryTextBrush() });

        var bodyStack = new StackPanel { Spacing = 12 };
        bodyStack.Children.Add(rows);
        bodyStack.Children.Add(new TextBlock
        {
            Text = "Po aplikaci se stav ověří. Neúspěšné změny se zobrazí zvlášť.",
            FontSize = 12, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap
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

        var actions = DialogActions();
        actions.Margin = new Thickness(0);
        var cancel = StyledDialogButton("Zrušit", false, () => { Finish(false, "cancel"); return Task.CompletedTask; });
        actions.Children.Add(cancel);
        var primary = StyledDialogButton("Použít změny", true, () => { Finish(true, "apply"); return Task.CompletedTask; });
        actions.Children.Add(primary);

        Grid.SetRow(actions, 2); root.Children.Add(actions);
        card.Child = root;
        MountDialog(overlay, card, () => Finish(false, "close"));
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => primary.Focus(FocusState.Programmatic));
        return await tcs.Task;
    }

    private async Task<int> ShowStyledChoiceAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Zrušit", string? subtitle = null)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay();
        var card = CreateDialogCard(610, 700);
        var root = CreateDialogLayout();
        root.Children.Add(CreateDialogHeader(title, subtitle));
        var body = new TextBlock { Text = message, FontSize = 13, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
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
        primary.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
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
        var overlay = CreateDialogOverlay(12000, 96);
        var card = CreateDialogCard(700, 680, z: 36);
        var root = CreateDialogLayout();
        var header = CreateDialogHeader("Power Plan — Recommended");
        var summary = new TextBlock { Text = "0 / 34 změn", FontSize = 13.3, Foreground = DialogSecondaryTextBrush() };
        var current = new TextBlock { Text = "Připravuji…", FontSize = 13.5, Foreground = ProgressBrush(), TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { Text = "Čekám na dokončení operace…", FontSize = 12, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap };
        header.Children.Add(summary); header.Children.Add(current); header.Children.Add(detail);
        var track = new Grid { Height = 7, Background = _darkTheme ? GlassTrackBrush() : SurfaceAltBrush(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fill = new Border { Width = 0, Height = 7, CornerRadius = new CornerRadius(3.5), Background = ProgressBrush(), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill); header.Children.Add(track); root.Children.Add(header);
        var middle = new StackPanel { Spacing = 10 };
        middle.Children.Add(new TextBlock { Text = "Průběh změn", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = DialogSecondaryTextBrush() });
        var logText = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var logScroll = new ScrollViewer { Content = logText, Height = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Background = _darkTheme ? DialogInnerSurfaceBrush() : SurfaceAltBrush(), Padding = new Thickness(12) };
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
        string? lastProgressLogEntry = null;
        int exitCode = -1;
        var progress = new Progress<PerformanceProfilesService.PowerProfileProgress>(p =>
        {
            if (popupClosed) return;
            double width = track.ActualWidth > 0 ? track.ActualWidth : 520;
            double targetWidth = width * Math.Clamp(p.Percent, 0, 100) / 100d;
            if (Math.Abs(fill.Width - targetWidth) >= 0.5) fill.Width = targetWidth;
            string summaryValue = $"{Math.Min(p.Current, 34)} / 34 změn • {p.Percent} %";
            if (!string.Equals(summary.Text, summaryValue, StringComparison.Ordinal)) summary.Text = summaryValue;
            if (!string.Equals(current.Text, p.Stage, StringComparison.Ordinal)) current.Text = p.Stage;
            if (!string.Equals(detail.Text, p.Detail, StringComparison.Ordinal)) detail.Text = p.Detail;
            if (p.Stage is "Aplikuji" or "Ověření")
            {
                string logEntry = (p.Stage == "Ověření" ? "✓ " : "• ") + p.Detail;
                if (!string.Equals(lastProgressLogEntry, logEntry, StringComparison.Ordinal))
                {
                    lastProgressLogEntry = logEntry;
                    lines.Enqueue(logEntry);
                    while (lines.Count > 100) lines.Dequeue();
                    string logValue = string.Join(Environment.NewLine, lines);
                    if (!string.Equals(logText.Text, logValue, StringComparison.Ordinal)) logText.Text = logValue;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (popupClosed) return;
                        try { if (logScroll.XamlRoot != null) logScroll.ChangeView(null, logScroll.ScrollableHeight, null, true); } catch { }
                    });
                }
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
            double finalWidth = track.ActualWidth > 0 ? track.ActualWidth : 520;
            if (Math.Abs(fill.Width - finalWidth) >= 0.5) fill.Width = finalWidth;
            string finalSummary = $"{active} / {total} critical ověřeno • 100 %";
            string finalCurrent = exitCode == 0 ? "Hotovo — Recommended profil je aktivní" : "Finální ověření není kompletní";
            string finalDetail = $"Finální ověření: {active}/{total} • aktivní plán {(critical.ActiveOk ? "OK" : "NE")} • {PerformanceProfilesService.GetCpuName()}";
            if (!string.Equals(summary.Text, finalSummary, StringComparison.Ordinal)) summary.Text = finalSummary;
            if (!string.Equals(current.Text, finalCurrent, StringComparison.Ordinal)) current.Text = finalCurrent;
            if (!string.Equals(detail.Text, finalDetail, StringComparison.Ordinal)) detail.Text = finalDetail;
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
