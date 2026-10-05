using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private string? _lastAiProgressFinalSummary;

    private sealed class AiProgressRow
    {
        public required AiChoice Choice { get; init; }
        public required TextBlock Status { get; init; }
        public required TextBlock Time { get; init; }
    }

    private async Task RunAiModulesProgressAsync(AiChoice[] selected, bool revert, CancellationToken externalToken = default, bool dryRun = false)
    {
        var totalSw = Stopwatch.StartNew();
        bool cancelled = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var overlay = CreateDialogOverlay(13000, 155);
        var card = CreateDialogCard(760, 680, z: 36);
        var root = CreateDialogLayout();
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 48, 0) };
        header.Children.Add(new TextBlock
        {
            Text = revert ? "Obnova Windows AI" : "Windows AI — přesný průběh",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        var summary = new TextBlock { Text = $"0 / {selected.Length} modulů", FontSize = 12.5, Foreground = TextSecondaryBrush() };
        var current = new TextBlock { Text = "Připravuji…", FontSize = 13.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        header.Children.Add(summary); header.Children.Add(current);

        var track = new Grid { Height = 6, Background = SurfaceAltBrush(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fill = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), Background = ButtonVioletBrush(), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill); header.Children.Add(track); root.Children.Add(header);
        var middle = new StackPanel { Spacing = 10 };

        var rowsHost = new StackPanel { Spacing = 6 };
        var rows = new List<AiProgressRow>();
        foreach (var item in selected)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.Children.Add(new TextBlock { Text = item.Label, FontSize = 12.5, Foreground = TextPrimaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis });
            var st = new TextBlock { Text = "Čeká", FontSize = 11.5, Foreground = TextSecondaryBrush() };
            Grid.SetColumn(st, 1); row.Children.Add(st);
            var tm = new TextBlock { Text = "—", FontSize = 11.5, Foreground = TextSecondaryBrush(), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(tm, 2); row.Children.Add(tm);
            rowsHost.Children.Add(row);
            rows.Add(new AiProgressRow { Choice = item, Status = st, Time = tm });
        }
        middle.Children.Add(new ScrollViewer { Content = rowsHost, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });

        var consoleText = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var consoleScroll = new ScrollViewer
        {
            Content = consoleText,
            Height = 210,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Background = SurfaceAltBrush(),
            Padding = new Thickness(12)
        };
        middle.Children.Add(consoleScroll);
        var middleScroll = DialogScroll(middle); Grid.SetRow(middleScroll, 1); root.Children.Add(middleScroll);

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
        var lines = new Queue<string>(256);
        long lastScriptOutputTicks = DateTime.UtcNow.Ticks;
        void AppendLine(string line, bool error = false)
        {
            if (popupClosed) return;
            Interlocked.Exchange(ref lastScriptOutputTicks, DateTime.UtcNow.Ticks);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (popupClosed) return;
                string clean = line.Replace("\0", string.Empty).TrimEnd();
                if (clean.Length == 0) return;
                lines.Enqueue((error ? "ERR  " : string.Empty) + clean);
                while (lines.Count > 240) lines.Dequeue();
                consoleText.Text = string.Join(Environment.NewLine, lines);
                current.Text = clean;
                consoleScroll.ChangeView(null, consoleScroll.ScrollableHeight, null, true);
            });
        }

        int completed = 0;
        int failed = 0;
        try
        {
            AppendLine("Připravuji RemoveWindowsAI…");
            foreach (var row in rows)
            {
                cts.Token.ThrowIfCancellationRequested();
                row.Status.Text = "Běží";
                row.Status.Foreground = TextSecondaryBrush();
                current.Text = $"{row.Choice.Label} — spouštím…";
                AppendLine($"=== {row.Choice.Label} ({row.Choice.Key}) ===");
                var sw = Stopwatch.StartNew();
                string opId = ErrorService.NewCorrelationId();
                await OperationJournalService.WriteAsync(opId, "ai", revert ? "revert" : "apply", row.Choice.Key, "Started", row.Choice.Label, cts.Token);
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                Task heartbeatTask = RunAiHeartbeatAsync(row.Choice, sw, current,
                    () => TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - Interlocked.Read(ref lastScriptOutputTicks))),
                    heartbeatCts.Token);
                try
                {
                    var result = dryRun
                        ? await SimulateAiStepAsync(row.Choice, cts.Token, line => AppendLine(line))
                        : await ZoicwareToolsService.RunAiOptionStreamingAsync(row.Choice.Key, revert, !revert,
                            cts.Token, line => AppendLine(line), line => AppendLine(line, true));
                    sw.Stop();
                    if (!result.Success) throw new InvalidOperationException($"Proces skončil kódem {result.ExitCode}.");
                    completed++;
                    row.Status.Text = "Dokončeno";
                    row.Status.Foreground = UiBrush("SpankySuccessBrush");
                    row.Time.Text = $"{sw.Elapsed.TotalSeconds:0.0}s";
                    AppendLine($"HOTOVO: {row.Choice.Label} ({sw.Elapsed.TotalSeconds:0.0}s)");
                    await OperationJournalService.WriteAsync(opId, "ai", revert ? "revert" : "apply", row.Choice.Key, "ProcessCompleted", $"Exit code 0 • {sw.Elapsed.TotalSeconds:0.0}s", CancellationToken.None);
                }
                catch (OperationCanceledException) { throw; }
                catch (TimeoutException ex)
                {
                    sw.Stop(); failed++;
                    row.Status.Text = "Timeout";
                    row.Status.Foreground = UiBrush("SpankyWarningBrush");
                    row.Time.Text = $"{sw.Elapsed.TotalSeconds:0.0}s";
                    AppendLine($"TIMEOUT: {row.Choice.Label}: {ex.Message}", true);
                    AppendLine("Procesní strom byl ukončen. Můžeš pokračovat dalšími moduly nebo operaci zavřít.", true);
                    LogService.Error("ai", $"Module {row.Choice.Key} timed out", ex);
                    try { await OperationJournalService.WriteAsync(opId, "ai", revert ? "revert" : "apply", row.Choice.Key, "TimedOut", ex.Message, CancellationToken.None); } catch { }
                }
                catch (Exception ex)
                {
                    sw.Stop(); failed++;
                    row.Status.Text = "Chyba";
                    row.Status.Foreground = UiBrush("SpankyWarningBrush");
                    row.Time.Text = $"{sw.Elapsed.TotalSeconds:0.0}s";
                    AppendLine($"CHYBA: {row.Choice.Label}: {ex.Message}", true);
                    LogService.Error("ai", $"Module {row.Choice.Key} failed", ex);
                    try { await OperationJournalService.WriteAsync(opId, "ai", revert ? "revert" : "apply", row.Choice.Key, "Failed", ex.Message, CancellationToken.None); } catch { }
                }
                finally
                {
                    await heartbeatCts.CancelAsync();
                    try { await heartbeatTask; } catch (OperationCanceledException) { }
                }
                double ratio = selected.Length == 0 ? 1 : (double)(completed + failed) / selected.Length;
                fill.Width = Math.Max(0, (track.ActualWidth > 0 ? track.ActualWidth : 700) * ratio);
                summary.Text = $"{completed + failed} / {selected.Length} modulů • OK {completed} • Chyby {failed}";
            }

            current.Text = failed == 0 ? "Všechny vybrané AI moduly byly zpracované." : $"Dokončeno s {failed} chybami.";
            AppendLine(current.Text, failed > 0);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            current.Text = "Operace byla zrušena.";
            AppendLine("Operace byla zrušena uživatelem.", true);
        }
        finally
        {
            totalSw.Stop();
            int processed = completed + failed;
            int pending = Math.Max(0, selected.Length - processed);
            _lastAiProgressFinalSummary =
                $"AI FINAL | OK {completed} | Chyby {failed} | Zrušeno {(cancelled ? 1 : 0)} | Nezpracováno {pending} | {totalSw.Elapsed.TotalSeconds:0.0}s";
            AppendLine(_lastAiProgressFinalSummary, failed > 0 || cancelled);
            LogService.Info("ai", _lastAiProgressFinalSummary);

            if (!popupClosed)
            {
                cancel.Visibility = Visibility.Collapsed;
                if (topClose != null) topClose.Visibility = Visibility.Visible;
            }
            if (selected.Length > 0)
            {
                double ratio = (double)(completed + failed) / selected.Length;
                fill.Width = Math.Max(0, (track.ActualWidth > 0 ? track.ActualWidth : 700) * ratio);
            }
            if (!popupClosed && dryRun)
            {
                UnmountDialog(overlay);
            }
            else if (!popupClosed && !cancelled && failed == 0)
            {
                await Task.Delay(800);
                UnmountDialog(overlay);
            }
        }
    }

    private async Task RunAiHeartbeatAsync(AiChoice choice, Stopwatch sw, TextBlock current,
        Func<TimeSpan> getOutputIdle, CancellationToken token)
    {
        bool trustedInstallerHeavy = choice.Key is "RemoveCBSPackages" or "RemoveAIFiles";
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(12), token);
            TimeSpan idle = getOutputIdle();
            string backend = await Task.Run(ZoicwareToolsService.GetAiBackendActivitySummary, token).ConfigureAwait(false);
            bool suspiciousSilence = idle >= TimeSpan.FromMinutes(2) && backend.Contains("žádný známý", StringComparison.OrdinalIgnoreCase);
            string phase = trustedInstallerHeavy ? "TrustedInstaller / CBS" : "RemoveWindowsAI";
            string message = suspiciousSilence
                ? $"{choice.Label} — čekám na další výstup ({idle.TotalSeconds:0}s)"
                : $"{choice.Label} — {phase} běží • poslední výstup před {idle.TotalSeconds:0}s • {sw.Elapsed.TotalSeconds:0}s";
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested) current.Text = message;
            });
        }
    }

    private static async Task<ProcessResult> SimulateAiStepAsync(AiChoice choice, CancellationToken token, Action<string> output)
    {
        output("[ + ] Dry-run: " + choice.Label);
        await Task.Delay(80, token);
        output("[ + ] Ověřuji průběh UI a streaming výstupu");
        await Task.Delay(80, token);
        return new ProcessResult(0, "dry-run ok", string.Empty);
    }

    public async Task RunAiProgressUiSmokeAsync(CancellationToken token = default)
    {
        _lastAiProgressFinalSummary = null;
        await RunAiModulesProgressAsync(new[] { AiChoices[0], AiChoices[1] }, false, token, true);
        if (string.IsNullOrWhiteSpace(_lastAiProgressFinalSummary) ||
            !_lastAiProgressFinalSummary.Contains("OK 2", StringComparison.Ordinal) ||
            !_lastAiProgressFinalSummary.Contains("Nezpracováno 0", StringComparison.Ordinal))
            throw new InvalidOperationException("AI progress smoke is missing a valid final summary.");
        LogService.Info("ai", "AI_PROGRESS_FINAL_SUMMARY_SMOKE_OK " + _lastAiProgressFinalSummary);
    }
}
