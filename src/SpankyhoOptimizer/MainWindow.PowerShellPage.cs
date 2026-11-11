using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Task BuildPowerShellPageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var actions = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 8
        };
        actions.Children.Add(QuickButton("PowerShell 7", OpenPowerShell7Async));

        FrameworkElement hero = BuildModernSectionHero(
            "powershell",
            "PowerShell",
            "Chris Titus profil, Titus AI, shell health a doporučené PowerShell / CMD nástroje.",
            "TERMINAL",
            "ChrisTitusTech • PowerShell Profile • Titus AI",
            actions);

        ContentStack.Children.Add(hero);
        BuildPowerShellCenterExtras(token);
        return Task.CompletedTask;
    }

    private static string FmtMs(double value) => value < 0 ? "n/a" : Math.Round(value) + " ms";

    private Task OpenPowerShell7Async()
    {
        const string path = @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (!File.Exists(path))
            throw new FileNotFoundException("PowerShell 7 není nainstalovaný.", path);

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
