using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task ShowGTweakLocalScriptsAsync(CancellationToken token)
    {
        var scripts = GTweakLocalScriptService.Scan();
        var root = new StackPanel { Spacing = 9 };
        var combo = new ComboBox { MinWidth = 460, ItemsSource = scripts.Select(x => x.Name).ToArray(), SelectedIndex = scripts.Count > 0 ? 0 : -1 };
        root.Children.Add(new TextBlock { Text = $"Složka: {GTweakLocalScriptService.Root}", TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush() });
        root.Children.Add(combo);
        var row = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("Otevřít složku", () => { System.IO.Directory.CreateDirectory(GTweakLocalScriptService.Root); ProcessRunner.Open(GTweakLocalScriptService.Root); return Task.CompletedTask; }));
        row.Children.Add(QuickButton("Obnovit", async () => await ShowGTweakLocalScriptsAsync(token)));
        root.Children.Add(row);
        if (scripts.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = "Vlož .ps1, .bat, .cmd nebo .reg soubor a pak stiskni Obnovit.", TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush() });
            await ShowStyledContentAsync("Local Scripts", "Advanced Addons kompatibilita", root, "Zavřít");
            return;
        }
        if (!await ShowStyledContentAsync("Local Scripts", "Vyber lokální script. Před spuštěním dostaneš ještě potvrzení.", root, "Pokračovat")) return;
        int index = Math.Clamp(combo.SelectedIndex, 0, scripts.Count - 1);
        var selected = scripts[index];
        if (!await ShowStyledConfirmAsync("Spustit lokální script?", selected.Path, "Spustit", "Zrušit", selected.Extension)) return;
        try
        {
            var progressResult = await RunCancellableWithProgressAsync(
                "Local Scripts",
                selected.Name,
                async progressToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                    return await GTweakLocalScriptService.RunAsync(selected, linked.Token);
                },
                result => result.Success,
                result => result.Success ? string.Empty : $"Exit code {result.ExitCode}");
            if (!progressResult.Completed)
            {
                LogService.Info("extensions", $"Local script cancelled: {selected.Name}");
                return;
            }
            var result = progressResult.Result;
            string detail = result.Success ? "Script doběhl úspěšně." : $"Script skončil chybou {result.ExitCode}.\n\n{result.StdErr}";
            await ShowMessageAsync("Local Scripts", detail);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Info("extensions", $"Local script cancelled by navigation: {selected.Name}");
        }
    }
}
