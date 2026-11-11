using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task<bool> RunDebloatProgressAsync(InstalledAppInfo[] apps, CancellationToken externalToken, bool dryRun = false, bool allUsers = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var overlay = CreateDialogOverlay(13000, 96);
        var card = CreateDialogCard(700, 680, z: 36);
        var root = CreateDialogLayout();
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 48, 0) };
        header.Children.Add(new TextBlock { Text = "Debloater V3 — průběh", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        var summary = new TextBlock { Text = $"0 / {apps.Length}", Foreground = TextSecondaryBrush() };
        var current = new TextBlock { Text = "Připravuji…", FontSize = 13.5, Foreground = ProgressBrush(), TextWrapping = TextWrapping.Wrap };
        header.Children.Add(summary); header.Children.Add(current);
        var track = new Grid { Height = 6, Background = DialogInnerSurfaceBrush(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fill = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), Background = ProgressBrush(), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill); header.Children.Add(track); root.Children.Add(header);
        var log = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var scroll = new ScrollViewer
        {
            Content = log,
            Height = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Background = DialogInnerSurfaceBrush(),
            Padding = new Thickness(12)
        };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        bool popupClosed = false;
        bool operationFinished = false;
        bool cancelRequested = false;
        Button? cancel = null;
        void ClosePopup()
        {
            if (popupClosed) return;
            if (!operationFinished)
            {
                // Cancellation is a request, not proof that an elevated Appx
                // removal has stopped. Keep the progress visible until the
                // in-flight step reports its outcome or cancellation.
                if (cancelRequested) return;
                cancelRequested = true;
                current.Text = "Zastavuji další kroky; dokončuji probíhající operaci…";
                if (cancel != null) { cancel.IsEnabled = false; cancel.Content = "Zastavuji…"; }
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
            popupClosed = true;
            UnmountDialog(overlay);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        cancel = StyledDialogButton("Zrušit", false, () => { ClosePopup(); return Task.CompletedTask; });
        buttons.Children.Add(cancel); Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        card.Child = root;
        var topClose = MountDialog(overlay, card, ClosePopup);

        var lines = new Queue<string>(220);
        int scrollUiQueued = 0;
        void Add(string text)
        {
            if (popupClosed) return;
            lines.Enqueue(text); while (lines.Count > 200) lines.Dequeue();
            string logValue = string.Join(Environment.NewLine, lines);
            if (!string.Equals(log.Text, logValue, StringComparison.Ordinal)) log.Text = logValue;
            if (System.Threading.Interlocked.Exchange(ref scrollUiQueued, 1) != 0) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (popupClosed) return;
                    if (scroll.XamlRoot != null)
                        scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                }
                finally { System.Threading.Interlocked.Exchange(ref scrollUiQueued, 0); }
            });
        }
        int ok = 0, failed = 0, uncertain = 0, auditWarnings = 0;
        bool removalInFlight = false;
        bool removalAttempted = false;
        bool allowAutomaticRefresh = false;
        try
        {
            for (int i = 0; i < apps.Length; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var app = apps[i];
                current.Text = $"{i + 1}/{apps.Length} • {app.DisplayName}";
                Add($"[{i + 1}/{apps.Length}] {app.DisplayName} …");
                if (!dryRun)
                {
                    removalAttempted = true;
                    removalInFlight = true;
                }
                var result = dryRun ? new DebloatResult(app, true, "Dry-run ověřen.", TimeSpan.FromMilliseconds(80)) : await DebloatService.RemoveAsync(app, cts.Token, allUsers);
                removalInFlight = false;
                // Record an already returned result even if cancellation was
                // requested while the worker was finishing. The next loop
                // iteration checks cancellation before starting any new app.
                if (popupClosed) return false;
                if (result.Success)
                {
                    ok++;
                    if (result.AuditWarning) auditWarnings++;
                    Add($"  OK  {result.Message} ({result.Duration.TotalSeconds:0.0}s)");
                }
                else if (result.Unverified)
                {
                    uncertain++;
                    Add($"  NEOVĚŘENO  {result.Message}");
                }
                else { failed++; Add($"  CHYBA  {result.Message}"); }
                double ratio = (double)(i + 1) / Math.Max(1, apps.Length);
                fill.Width = Math.Max(0, (track.ActualWidth > 0 ? track.ActualWidth : 650) * ratio);
                summary.Text = $"{i + 1}/{apps.Length} • OK {ok} • Chyby {failed} • Neověřeno {uncertain} • Audit {auditWarnings}";
                if (uncertain > 0)
                {
                    // Do not start any further Appx removal while the prior
                    // worker-acknowledged result could not be verified.
                    Add("ZASTAVENO: nejprve obnov seznam a ověř nejasný balíček.");
                    break;
                }
                if (auditWarnings > 0)
                {
                    // The package was verified removed, but the journal is
                    // no longer reliable. Never start another destructive
                    // operation until the audit issue is investigated.
                    Add("ZASTAVENO: ověřené odebrání, ale selhal auditní zápis. Další aplikace nebyly odebrány.");
                    break;
                }
            }
            current.Text = uncertain > 0 ? $"Zastaveno • {uncertain} neověřených, {failed} chyb."
                : auditWarnings > 0 ? $"Hotovo • {ok} ověřeno, {failed} chyb, {auditWarnings} auditních upozornění."
                : failed == 0 ? "Hotovo — všechny změny byly ověřené." : $"Hotovo s {failed} chybami.";
            Add(current.Text);
            if (!dryRun)
                ShowActionFeedback("Debloater", failed == 0 && uncertain == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                    failed == 0 && uncertain == 0 && auditWarnings == 0
                        ? $"{ok}/{apps.Length} balíků odebráno a ověřeno."
                        : failed == 0 && uncertain == 0
                            ? $"{ok}/{apps.Length} balíků odebráno a ověřeno, ale {auditWarnings} auditních záznamů se nepodařilo dokončit."
                            : $"Ověřeno {ok}/{apps.Length}; chyby {failed}; neověřeno {uncertain}; auditní upozornění {auditWarnings}. Neověřené balíčky před dalším pokusem zkontroluj v seznamu.");
            // Do not automatically navigate away from an uncertain or
            // cancelled result; the user needs to see the warning first.
            allowAutomaticRefresh = uncertain == 0 && auditWarnings == 0 &&
                !cancelRequested && !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            // A cancelled worker wait can end before the worker itself does.
            // Count the in-flight item as uncertain, not as verified failed.
            if (removalInFlight && !dryRun) uncertain++;
            if (!popupClosed)
            {
                current.Text = $"Přerušeno • ověřeno {ok}, chyby {failed}, neověřeno {uncertain}.";
                Add("ZRUŠENO. U neověřeného balíčku obnov seznam aplikací a neopakuj odebrání naslepo.");
                if (!dryRun)
                    ShowActionFeedback("Debloater", ActionFeedbackKind.Failed,
                        $"Přerušeno: {ok} ověřených, {failed} chyb, {uncertain} neověřených. Zbývající balíčky nebudou spuštěny.");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("debloat", "Debloat progress failed", ex);
            if (!popupClosed)
            {
                current.Text = "Operace selhala: " + ex.Message;
                Add(current.Text);
                if (!dryRun) ShowActionFeedback("Debloater", ActionFeedbackKind.Failed, ex.Message);
            }
        }
        finally
        {
            // A successful removal also makes the cached Debloater page
            // stale. Invalidate after ANY real removal attempt, not only
            // cancellation/uncertainty; dry-run is strictly read-only.
            if (!dryRun && (removalAttempted || cts.IsCancellationRequested || uncertain > 0))
            {
                try { InstalledAppService.InvalidateCache(); }
                catch (Exception ex) { LogService.Error("debloat", "Inventory invalidation failed", ex); }
                InvalidateDebloatPageCache();
            }
            operationFinished = true;
            if (!popupClosed)
            {
                cancel!.Visibility = Visibility.Collapsed;
                if (topClose != null) topClose.Visibility = Visibility.Visible;
                if (dryRun) { popupClosed = true; UnmountDialog(overlay); }
            }
        }
        return allowAutomaticRefresh;
    }

    public Task RunDebloatProgressUiSmokeAsync(CancellationToken token = default)
    {
        var demo = new InstalledAppInfo("Smoke.App", "Smoke.App_1.0_x64__test", "Smoke.App_test", "Smoke App", "Spankyho", null, true, DebloatCategory.Tools, DebloatRisk.Safe, false, "Dry-run");
        return RunDebloatProgressAsync(new[] { demo }, token, true);
    }
}
