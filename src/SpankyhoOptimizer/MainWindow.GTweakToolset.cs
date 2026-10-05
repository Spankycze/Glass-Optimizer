using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task ShowGTweakToolsetAsync(CancellationToken token)
    {
        var tools = GTweakToolsetService.Load();
        if (tools.Count == 0)
        {
            await ShowStyledInfoAsync("Advanced Toolset", "Katalog AdvancedToolsetStore.json nebyl nalezen ve výstupu aplikace.");
            return;
        }
        var search = new TextBox { PlaceholderText = "Hledat nástroj nebo autora…", MinWidth = 260 };
        var tags = new[] { "Vše" }.Concat(tools.SelectMany(x => x.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        var tag = new ComboBox { MinWidth = 150, ItemsSource = tags, SelectedIndex = 0 };
        var count = new TextBlock { FontSize = 11.5, Foreground = TextSecondaryBrush() };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 560 };
        var root = new StackPanel { Spacing = 9 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filters.Children.Add(search); filters.Children.Add(tag);
        root.Children.Add(filters); root.Children.Add(count); root.Children.Add(list);
        void Render()
        {
            list.Items.Clear();
            string q = search.Text.Trim();
            string wanted = tag.SelectedItem?.ToString() ?? "Vše";
            var filtered = tools.Where(x =>
                (wanted == "Vše" || x.Tags.Contains(wanted, StringComparer.OrdinalIgnoreCase)) &&
                (q.Length == 0 || x.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || x.Author.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .ToArray();
            foreach (var tool in filtered) list.Items.Add(BuildGTweakToolRow(tool, token));
            count.Text = $"Zobrazeno {filtered.Length} z {tools.Count} • download: {GTweakToolsetService.DownloadRoot}";
        }
        search.TextChanged += (_, _) => Render();
        tag.SelectionChanged += (_, _) => Render();
        Render();
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderRow.Children.Add(QuickButton("Otevřít download složku", () =>
        {
            System.IO.Directory.CreateDirectory(GTweakToolsetService.DownloadRoot);
            ProcessRunner.Open(GTweakToolsetService.DownloadRoot);
            return Task.CompletedTask;
        }));
        root.Children.Add(folderRow);
        await ShowStyledContentAsync("Advanced Toolset", "Převzatý katalog nástrojů • bez NVIDIA Profile Inspectoru", root, "Zavřít");
    }
    private FrameworkElement BuildGTweakToolRow(GTweakToolsetItem tool, CancellationToken token)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 8, 10, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = tool.Name, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = $"{tool.Author} • {string.Join(" / ", tool.Tags)}", FontSize = 11, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(QuickButton("Zdroj", () => { ProcessRunner.Open(tool.Source); return Task.CompletedTask; }));
        actions.Children.Add(PrimaryQuickButton("Stáhnout", async () => await DownloadGTweakToolAsync(tool, token)));
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        var row = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(0, 0, 0, _darkTheme ? 1.1 : 1), Child = grid };
        AttachHoverMotion(row, 1.003f);
        return row;
    }

    private async Task DownloadGTweakToolAsync(GTweakToolsetItem tool, CancellationToken token)
    {
        try
        {
            var progressResult = await RunCancellableWithProgressAsync(
                "Advanced Toolset",
                $"Stahuji {tool.Name}…",
                async progressToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                    return await GTweakToolsetService.DownloadAsync(tool, linked.Token);
                });
            if (!progressResult.Completed)
            {
                LogService.Info("toolset", $"Download cancelled: {tool.Name}");
                return;
            }
            string path = progressResult.Result;
            await ShowMessageAsync(tool.Name, "Staženo do:\n" + path);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Info("toolset", $"Download cancelled by navigation: {tool.Name}");
        }
        catch (Exception ex)
        {
            LogService.Error("toolset", $"Download failed: {tool.Name}", ex);
            if (await ShowStyledConfirmAsync("Automatický download selhal", ex.Message + "\n\nOtevřít zdrojovou stránku?", "Otevřít zdroj", "Zrušit", tool.Name))
                ProcessRunner.Open(tool.Source);
        }
    }
}
