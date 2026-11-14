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
    private async Task<int> RunNicProgressPopupAsync(bool autoClose = false, Func<CancellationToken, Task<string>>? applyWork = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var overlay = CreateDialogOverlay(12000);
        var card = CreateDialogCard(590, 680, z: 36);
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(CreateDialogHeader("NIC Max Performance", "Realtek / Intel síťový profil"));
        root.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = DialogNeutralBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(13), Child = new TextBlock { Text = "Recommended profil vypne podporované úsporné funkce adaptéru, zapne RSS a vypne Selective Suspend. Interrupt Moderation, Flow Control ani Packet/DMA Coalescing nemění.", FontSize = 13.5, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 20 } });
        var status = new TextBlock { Text = "Načítám síťový adaptér…", FontSize = 13, Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap }; root.Children.Add(status);
        var segments = new Grid { ColumnSpacing = 6, Height = 6 }; for (int x = 0; x < 4; x++) segments.ColumnDefinitions.Add(new ColumnDefinition());
        var bars = new Border[4]; for (int x = 0; x < 4; x++) { bars[x] = new Border { CornerRadius = new CornerRadius(3), Background = _darkTheme ? GlassTrackBrush() : SurfaceAltBrush() }; Grid.SetColumn(bars[x], x); segments.Children.Add(bars[x]); }
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
            var applyTask = (applyWork ?? PerformanceProfilesService.ApplyNicPerformanceProfileAsync)(cts.Token);
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

            // UnmountDialog now performs the iOS 27 exit animation asynchronously.
            // Progress operations must not report completion until their modal is actually gone.
            if (RootGrid.Children.Contains(overlay))
            {
                for (int i = 0; i < 12 && RootGrid.Children.Contains(overlay); i++)
                    await Task.Delay(20);
            }

            return exitCode;
        }

        if (!popupClosed) await tcs.Task;
        return exitCode;
    }
}
