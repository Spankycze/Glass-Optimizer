using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{


    private async Task AddSnapshotCenterAsync()
    {
        // Snapshot enumeration touches disk. Run it before creating WinUI controls so callers
        // immediately yield and the Restore navigation frame is not charged for this module.
        var loadPerf = System.Diagnostics.Stopwatch.StartNew();
        var snapshots = await Task.Run(() => SystemStateSnapshotService.ListRecent(12));
        if (_section != "restore") return;
        LogService.Info("performance", $"SNAPSHOT_CENTER_DATA ms={loadPerf.ElapsedMilliseconds} count={snapshots.Length}");

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Vytvořit systémový snapshot", async () =>
        {
            var progressResult = await RunCancellableWithProgressAsync(
                "System Snapshot",
                "Tweak stavy → Services → Tasks → Power…",
                token => SystemStateSnapshotService.CaptureAsync(token));
            if (!progressResult.Completed)
            {
                LogService.Info("restore", "System snapshot capture cancelled by user.");
                return;
            }
            string file = progressResult.Result;
            InvalidateRestorePageCache();
            await ShowMessageAsync("Snapshot vytvořen", file);
            await ShowSectionAsync("restore");
        }));
        actions.Children.Add(QuickButton("Otevřít snapshoty", () =>
        {
            ProcessRunner.Open(System.IO.Path.Combine(LogService.Root, "snapshots"));
            return Task.CompletedTask;
        }));
        ContentStack.Children.Add(BuildSectionSurface("restore", "System Snapshot V2",
            "Tweaky, služby, Task Scheduler a power plan", actions));

        var host = new StackPanel { Spacing = 8 };
        foreach (var snap in snapshots.Take(12))
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = snap.Created.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = $"Tweaky {snap.Tweaks.Length} • služby {snap.Services.Length} • tasky {snap.Tasks.Length} • build {snap.WindowsBuild}",
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            });
            row.Children.Add(text);
            var restore = QuickButton("Obnovit…", async () => await RestoreSnapshotAsync(snap));
            Grid.SetColumn(restore, 1); row.Children.Add(restore);
            host.Children.Add(new Border
            {
                Background = SectionCardBackgroundBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Child = row
            });
        }
        if (snapshots.Length == 0)
            host.Children.Add(new TextBlock { Text = "Zatím není vytvořený žádný systémový snapshot.", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("restore", "Snapshot historie", "Obnovit lze jen vybrané části", host));
    }

    private async Task RestoreSnapshotAsync(OptimizerSystemSnapshot snap)
    {
        var tweaks = new CheckBox { Content = "Tweaky", IsChecked = true };
        var services = new CheckBox { Content = "Services", IsChecked = true };
        var tasks = new CheckBox { Content = "Task Scheduler", IsChecked = true };
        var power = new CheckBox { Content = "Power plan", IsChecked = true };
        var body = new StackPanel { Spacing = 8, Children = { tweaks, services, tasks, power } };
        if (!await ShowStyledContentAsync("Obnovit snapshot", snap.Created.ToLocalTime().ToString("g"), body, "Obnovit vybrané")) return;
        var options = new SnapshotRestoreOptions(tweaks.IsChecked == true, services.IsChecked == true,
            tasks.IsChecked == true, power.IsChecked == true);
        var progressResult = await RunCancellableWithProgressAsync(
            "Obnova snapshotu",
            "Restore → read-back → verify…",
            token => SystemStateSnapshotService.RestoreAsync(snap.Id, options, token),
            result => result.Length == 0,
            result => result.Length == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("restore", $"Snapshot restore cancelled: {snap.Id}");
            InvalidateRestorePageCache();
            ServiceCatalogService.InvalidateCache();
            TaskSchedulerService.InvalidateCache();
            PowerPlanStateService.InvalidateCache();
            MemoryStateService.Invalidate();
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            if (_section == "restore")
                await ShowSectionAsync("restore");
            return;
        }
        var errors = progressResult.Result;
        InvalidateRestorePageCache();
        await ShowMessageAsync("Snapshot restore", errors.Length == 0 ? "Vybrané části snapshotu byly obnoveny a ověřeny." : string.Join(Environment.NewLine, errors.Take(12)));
        await ShowSectionAsync("restore");
    }

    private async Task ShowBatchItemsAsync(TweakBatchRecord batch)
    {
        var host = new StackPanel { Spacing = 7 };
        foreach (var item in batch.Items)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            text.Children.Add(new TextBlock { Text = $"{item.Section} • návrat na {(item.Previous ? "ZAP" : "VYP")}" + (item.Restored ? " • vráceno" : ""), FontSize = 11, Foreground = TextSecondaryBrush() });
            row.Children.Add(text);
            var undo = QuickButton(item.Restored ? "Vráceno" : "Vrátit", async () =>
            {
                string? error = await TweakBatchHistoryService.RestoreItemAsync(batch.BatchId, item.Id);
                await ShowMessageAsync("Undo položky", error ?? "Položka byla vrácena a ověřena read-backem.");
            });
            undo.IsEnabled = !item.Restored;
            Grid.SetColumn(undo, 1); row.Children.Add(undo);
            host.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = row });
        }
        await ShowStyledContentAsync("Položky dávky", $"{batch.Time:dd.MM.yyyy HH:mm} • {batch.Area}", new ScrollViewer { Content = host, MaxHeight = 560 }, "Zavřít");
        await ShowSectionAsync("restore");
    }

}

