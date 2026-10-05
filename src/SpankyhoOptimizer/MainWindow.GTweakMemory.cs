using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private UIElement BuildGTweakMemoryAdvancedActions()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("Advanced: purge standby/file cache", RunGTweakMemoryPurgeAsync));
        return row;
    }

    private async Task RunGTweakMemoryPurgeAsync()
    {
        if (!await ShowStyledConfirmAsync("Purge standby a file-system cache?",
            "Toto je agresivní jednorázová RAM údržba. Uvolní standby/file cache, ale může krátce zvýšit disk I/O a page faulty. Není součástí Recommended.",
            "Spustit", "Zrušit", "Paměť & Latence")) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await GTweakMemoryPurgeService.PurgeAsync(cts.Token);
            await ShowMessageAsync("Paměť", "Standby list a file-system cache byly vyprázdněny.");
        }
        catch (Exception ex)
        {
            LogService.Error("memory", "Advanced memory purge failed", ex);
            await ShowMessageAsync("Paměť", "Purge selhal: " + ex.Message);
        }
    }
}
