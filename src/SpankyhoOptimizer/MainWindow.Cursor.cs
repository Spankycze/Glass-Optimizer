using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private string _cursorStatusText = "Vyber cursor pack a aplikuj ho do Windows.";
    private bool _cursorApplyInProgress;

    private void BuildCursorPage()
    {
        int generation = _navigationGeneration;
        var phase = System.Diagnostics.Stopwatch.StartNew();

        ContentStack.Children.Add(BuildSectionHero(
            "cursor",
            "Kurzory",
            "Cursor library",
            "Vyber cursor pack přímo tady, zkontroluj náhled a aplikuj ho. Zůstane aktivní, dokud nezvolíš jiný.",
            new StackPanel()));

        var body = new StackPanel { Spacing = 10 };
        var activePackText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var activePreviewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var statusText = new TextBlock
        {
            Text = _cursorStatusText,
            FontSize = 11.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        body.Children.Add(activePackText);
        body.Children.Add(activePreviewRow);
        body.Children.Add(statusText);
        body.Children.Add(new TextBlock
        {
            Text = "Zeleně označené packy jsou animované a jsou seřazené nahoře.",
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        StackPanel BuildPreviewRow(CommunityCursorPack pack)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (string role in new[] { "Arrow", "Hand", "IBeam", "Wait" })
                row.Children.Add(BuildCommunityPreview(System.IO.Path.Combine(pack.Folder, "_preview", role + ".png")));
            return row;
        }

        void ShowActivePack(CommunityCursorPack pack)
        {
            activePackText.Text = "Aktivní cursor pack: " + pack.Name;
            activePackText.Foreground = TextPrimaryBrush();
            activePreviewRow.Children.Clear();
            activePreviewRow.Children.Add(BuildPreviewRow(pack));
        }

        bool applying = false;
        async Task ApplyPackAsync(CommunityCursorPack pack, Button button)
        {
            if (applying || _cursorApplyInProgress) return;
            applying = true;
            _cursorApplyInProgress = true;
            button.IsEnabled = false;
            statusText.Text = "Aplikuji • " + pack.Name + "…";
            try
            {
                LogService.Info("cursor", $"COMMUNITY_APPLY_START id={pack.Id} name={pack.Name}");
                await Task.Run(() => CursorCommunityPackService.Apply(pack));
                _cursorStatusText = "Aktivováno: " + pack.Name;
                ShowActivePack(pack);
                statusText.Text = "Aktivováno • " + pack.Name;
                ShowActionFeedback("Kurzory", ActionFeedbackKind.Verified, pack.Name + " je aktivní.");
                LogService.Info("cursor", $"COMMUNITY_APPLY_OK id={pack.Id} name={pack.Name}");
            }
            catch (Exception ex)
            {
                statusText.Text = "Apply selhal • " + ex.Message;
                LogService.Error("cursor", $"COMMUNITY_APPLY_FAIL id={pack.Id} name={pack.Name}", ex);
                ShowActionFeedback("Kurzory", ActionFeedbackKind.Failed, ex.Message);
            }
            finally
            {
                applying = false;
                _cursorApplyInProgress = false;
                button.IsEnabled = true;
            }
        }

        var packs = CursorCommunityPackService.LoadBundledPacks()
            .OrderByDescending(p => p.AnimatedRoleCount >= 15)
            .ThenBy(p => CursorPackRank(p.Id))
            .ThenByDescending(p => p.AnimatedRoleCount)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        CommunityCursorPack? activePack = packs.FirstOrDefault(p =>
            string.Equals(p.Id, CursorCommunityPackService.CurrentCommunityPackId, StringComparison.OrdinalIgnoreCase));
        if (activePack != null)
            ShowActivePack(activePack);
        else
        {
            activePackText.Text = "Žádný cursor pack není aktuálně označený jako aktivní.";
            activePackText.Foreground = TextPrimaryBrush();
        }

        foreach (CommunityCursorPack pack in packs)
        {
            var rowGrid = new Grid { ColumnSpacing = 12 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var previews = BuildPreviewRow(pack);
            Grid.SetColumn(previews, 0);
            rowGrid.Children.Add(previews);

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = pack.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            info.Children.Add(new TextBlock
            {
                Text = pack.Category + " • " + pack.Author,
                FontSize = 10.5,
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            if (pack.IsAnimated)
            {
                info.Children.Add(new TextBlock
                {
                    Text = pack.AnimatedRoleCount >= 15
                        ? $"ANIMOVANÝ • {pack.AnimatedRoleCount} animovaných slotů"
                        : $"ČÁSTEČNĚ ANIMOVANÝ • {pack.AnimatedRoleCount} animace",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiBrush("SpankySuccessBrush")
                });
            }
            Grid.SetColumn(info, 1);
            rowGrid.Children.Add(info);

            var apply = QuickButton("POUŽÍT", () => Task.CompletedTask);
            apply.MinWidth = 96;
            apply.HorizontalAlignment = HorizontalAlignment.Right;
            apply.VerticalAlignment = VerticalAlignment.Center;
            apply.IsEnabled = !_cursorApplyInProgress;
            apply.Click += (_, _) => _ = ApplyPackAsync(pack, apply);
            Grid.SetColumn(apply, 2);
            rowGrid.Children.Add(apply);

            body.Children.Add(new Border
            {
                Background = SectionCardBackgroundBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Child = rowGrid
            });
        }

        body.Children.Add(QuickButton("Obnovit aktivní pack", RefreshDetectedPackAsync));
        ContentStack.Children.Add(BuildSectionSurface(
            "cursor",
            "Cursor library",
            "Náhledy a aplikace cursor packů",
            body));

        async Task RefreshDetectedPackAsync()
        {
            try
            {
                var detected = await Task.Run(() =>
                {
                    CursorCommunityPackService.SyncDetectedActivePackMetadata();
                    string? id = CursorCommunityPackService.CurrentCommunityPackId;
                    return CursorCommunityPackService.LoadBundledPacks()
                        .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                }).ConfigureAwait(false);

                if (generation != _navigationGeneration || _section != "cursor") return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (generation != _navigationGeneration || _section != "cursor" || activePackText.Parent == null) return;
                    if (detected != null)
                        ShowActivePack(detected);
                    else
                    {
                        activePackText.Text = "Žádný cursor pack není aktuálně označený jako aktivní.";
                        activePreviewRow.Children.Clear();
                    }
                });
            }
            catch (Exception ex)
            {
                LogService.Error("cursor", "Deferred cursor readback failed", ex);
                if (generation != _navigationGeneration || _section != "cursor") return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (generation != _navigationGeneration || _section != "cursor" || activePackText.Parent == null) return;
                    activePackText.Text = "Aktivní cursor pack se nepodařilo ověřit: " + ex.Message;
                    activePackText.Foreground = WarningBrush();
                    ShowActionFeedback("Kurzory", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }

        SafeTaskService.Forget(RefreshDetectedPackAsync(), "cursor", "Deferred cursor readback");
        LogService.Info("performance", $"CURSOR_BUILD_PHASE inlineCatalog=true packs={packs.Length} fullyAnimated={packs.Count(p => p.AnimatedRoleCount >= 15)} partialAnimated={packs.Count(p => p.AnimatedRoleCount is > 0 and < 15)} totalMs={phase.ElapsedMilliseconds}");
    }

    private static int CursorPackRank(string id) => id.ToLowerInvariant() switch
    {
        "bibata-rainbow-modern" => 0,
        "bibata-rainbow-original" => 1,
        "bibata-pink" => 2,
        "bibata-turquoise" => 3,
        "bibata-dodgerblue" => 4,
        "glass-glow" => 5,
        "openzone-black" => 6,
        "openzone-white" => 7,
        "vision-black" => 8,
        "nailong" => 9,
        "clawd" => 11,
        _ => 10
    };

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
