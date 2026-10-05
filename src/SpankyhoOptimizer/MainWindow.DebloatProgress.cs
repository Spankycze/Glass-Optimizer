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
    private async Task RunDebloatProgressAsync(InstalledAppInfo[] apps, CancellationToken externalToken, bool dryRun = false, bool allUsers = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var overlay = CreateDialogOverlay(13000, 155);
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
        void ClosePopup()
        {
            if (popupClosed) return;
            popupClosed = true;
            cts.Cancel();
            UnmountDialog(overlay);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = StyledDialogButton("Zrušit", false, () => { ClosePopup(); return Task.CompletedTask; });
        buttons.Children.Add(cancel); Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        card.Child = root;
        var topClose = MountDialog(overlay, card, ClosePopup);

        var lines = new Queue<string>(220);
        void Add(string text)
        {
            if (popupClosed) return;
            lines.Enqueue(text); while (lines.Count > 200) lines.Dequeue();
            log.Text = string.Join(Environment.NewLine, lines);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (popupClosed) return;
                if (scroll.XamlRoot != null)
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
        }
        int ok = 0, failed = 0;
        try
        {
            for (int i = 0; i < apps.Length; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var app = apps[i];
                current.Text = $"{i + 1}/{apps.Length} • {app.DisplayName}";
                Add($"[{i + 1}/{apps.Length}] {app.DisplayName} …");
                var result = dryRun ? new DebloatResult(app, true, "Dry-run ověřen.", TimeSpan.FromMilliseconds(80)) : await DebloatService.RemoveAsync(app, cts.Token, allUsers);
                cts.Token.ThrowIfCancellationRequested();
                if (popupClosed) return;
                if (result.Success) { ok++; Add($"  OK  {result.Message} ({result.Duration.TotalSeconds:0.0}s)"); }
                else { failed++; Add($"  CHYBA  {result.Message}"); }
                double ratio = (double)(i + 1) / Math.Max(1, apps.Length);
                fill.Width = Math.Max(0, (track.ActualWidth > 0 ? track.ActualWidth : 650) * ratio);
                summary.Text = $"{i + 1}/{apps.Length} • OK {ok} • Chyby {failed}";
            }
            current.Text = failed == 0 ? "Hotovo — všechny změny byly ověřené." : $"Hotovo s {failed} chybami.";
            Add(current.Text);
            if (!dryRun)
                ShowActionFeedback("Debloater", failed == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                    failed == 0 ? $"{ok}/{apps.Length} balíků odebráno a ověřeno." : $"Ověřeno {ok}/{apps.Length}; chyby {failed}.");
        }
        catch (OperationCanceledException)
        {
            if (!popupClosed)
            {
                current.Text = "Operace byla zrušena.";
                Add("ZRUŠENO uživatelem.");
            }
        }
        finally
        {
            if (!popupClosed)
            {
                cancel.Visibility = Visibility.Collapsed;
                if (topClose != null) topClose.Visibility = Visibility.Visible;
                if (dryRun) UnmountDialog(overlay);
            }
        }
    }

    public Task RunDebloatProgressUiSmokeAsync(CancellationToken token = default)
    {
        var demo = new InstalledAppInfo("Smoke.App", "Smoke.App_1.0_x64__test", "Smoke.App_test", "Smoke App", "Spankyho", null, true, DebloatCategory.Tools, DebloatRisk.Safe, false, "Dry-run");
        return RunDebloatProgressAsync(new[] { demo }, token, true);
    }
}
