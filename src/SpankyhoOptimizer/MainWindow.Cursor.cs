using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private string _cursorStatusText = "Vyber skin a aplikuj ho do Windows.";

    private void BuildCursorPage()
    {
        int generation = _navigationGeneration;
        // Community-only cursor experience. Render immediately from cached metadata;
        // the slower Windows/file-system readback is refreshed after first paint.
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(PrimaryQuickButton("Community cursors", ShowCommunityCursorPopupAsync));

        ContentStack.Children.Add(BuildSectionHero(
            "cursor",
            "Community Cursors",
            "Community Cursor Library",
            "Vyber community pack, použij ho a zůstane aktivní, dokud sám nezvolíš jiný.",
            heroActions));

        string activeName = CursorCommunityPackService.CurrentCommunityPackName;
        var body = new StackPanel { Spacing = 8 };
        var activePackText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(activeName)
                ? "Žádný community cursor pack není aktuálně označený jako aktivní."
                : "Aktivní community pack: " + activeName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        body.Children.Add(activePackText);
        body.Children.Add(new TextBlock
        {
            Text = "Otevři knihovnu, vyber pack a použij ho tlačítkem Použít vybraný.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(PrimaryQuickButton("Otevřít Community Cursor Library", ShowCommunityCursorPopupAsync));
        body.Children.Add(QuickButton("Obnovit aktivní pack", RefreshDetectedPackAsync));

        ContentStack.Children.Add(BuildSectionSurface(
            "cursor",
            "Community Cursor Library",
            "Jediné místo pro výběr a aplikaci cursor packů",
            body));

        async Task RefreshDetectedPackAsync()
        {
            try
            {
                string detectedName = await Task.Run(() =>
                {
                    CursorCommunityPackService.SyncDetectedActivePackMetadata();
                    return CursorCommunityPackService.CurrentCommunityPackName;
                }).ConfigureAwait(false);

                if (generation != _navigationGeneration || _section != "cursor") return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (generation != _navigationGeneration || _section != "cursor" || activePackText.Parent == null) return;
                    activePackText.Text = string.IsNullOrWhiteSpace(detectedName)
                        ? "Žádný community cursor pack není aktuálně označený jako aktivní."
                        : "Aktivní community pack: " + detectedName;
                    activePackText.Foreground = TextPrimaryBrush();
                });
            }
            catch (Exception ex)
            {
                LogService.Error("cursor", "Deferred community cursor readback failed", ex);
                if (generation != _navigationGeneration || _section != "cursor") return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (generation != _navigationGeneration || _section != "cursor" || activePackText.Parent == null) return;
                    activePackText.Text = "Aktivní cursor pack se nepodařilo ověřit: " + ex.Message;
                    activePackText.Foreground = WarningBrush();
                    ShowActionFeedback("Community Cursors", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }

        SafeTaskService.Forget(RefreshDetectedPackAsync(), "cursor", "Deferred community cursor readback");
        LogService.Info("performance", $"CURSOR_BUILD_PHASE communityOnly=true totalMs={phase.ElapsedMilliseconds}");
    }

    private static StackPanel NewCursorSkinRow() =>
        new() { Orientation = Orientation.Horizontal, Spacing = 10 };

    private void BuildCursorPageRefresh()
    {
        if (_section != "cursor") return;
        ContentStack.Children.Clear();
        TopActions.Children.Clear();
        ActionBar.Visibility = Visibility.Collapsed;
        BuildCursorPage();
    }

    private async Task RunCursorStartupWatchdogAsync()
    {
        if (!CursorThemeService.StartupWatchdog) return;
        string skin = CursorThemeService.CurrentSkin;
        if (skin.Equals("WindowsDefault", StringComparison.OrdinalIgnoreCase)) return;

        System.Threading.CancellationToken token = _windowLifetimeCts.Token;
        try
        {
            await Task.Delay(4000, token).ConfigureAwait(false);
            await Task.Run(CursorThemeService.EnsureCurrentApplied, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            LogService.Info("cursor", "CURSOR_STARTUP_WATCHDOG_OK skin=" + skin);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal window shutdown.
        }
        catch (Exception ex)
        {
            LogService.Error("cursor", "Cursor startup watchdog failed", ex);
        }
    }

}

