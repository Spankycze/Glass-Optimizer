using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _selectedFixerScanRunning;
    private StackPanel? _selectedFixerHost;

    private UIElement BuildSelectedFixerPanel()
    {
        var host = new StackPanel { Spacing = 8, Tag = "selected-fixer-on-demand" };
        _selectedFixerHost = host;
        host.Children.Add(new TextBlock
        {
            Text = "Xbox/Game Pass, Bluetooth, NVIDIA Control Panel a Windows Search. " +
                   "Kontroly běží jen po kliknutí. Otevření opravy či Store samo nic nepřeinstaluje ani neresetuje.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush()
        });
        host.Children.Add(PrimaryQuickButton("Zkontrolovat vybrané funkce",
            () => LoadSelectedFixerChecksAsync(host)));
        host.Children.Add(QuickButton("GameReadyX – závislosti her", CheckGameReadyCompatibilityAsync));
        return BuildDashboardExpander("Další vybrané opravy",
            "4 konkrétní oblasti • jen po kliknutí • bez zásahů do systému", host);
    }

    private async Task LoadSelectedFixerChecksAsync(StackPanel host)
    {
        if (Interlocked.CompareExchange(ref _selectedFixerScanRunning, 1, 0) != 0) return;
        int generation = _navigationGeneration;
        try
        {
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Text = "Kontroluji čtyři vybrané oblasti…",
                Foreground = TextSecondaryBrush()
            });
            var result = await SelectedFixerChecksService.ScanAsync(
                _loadCts?.Token ?? _windowLifetimeCts.Token);
            if (generation != _navigationGeneration || _section != "fixer" ||
                !ReferenceEquals(_selectedFixerHost, host)) return;

            host.Children.Clear();
            host.Children.Add(QuickButton("Obnovit vybrané kontroly",
                () => LoadSelectedFixerChecksAsync(host)));
            host.Children.Add(QuickButton("GameReadyX – závislosti her", CheckGameReadyCompatibilityAsync));
            foreach (var item in result)
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock
                {
                    Text = item.Title + " — " + item.State,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = item.Attention ? WarningBrush() : TextPrimaryBrush()
                });
                row.Children.Add(new TextBlock
                {
                    Text = item.Detail + "\n" + item.Guidance,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = TextSecondaryBrush()
                });
                var repair = SelectedFixerChecksService.ResolveRepairRoute(item);
                row.Children.Add(QuickButton(repair.Label,
                    () => OpenSelectedFixerSettingsAsync(repair.Uri)));
                host.Children.Add(new Border
                {
                    Background = SectionCardBackgroundBrush(),
                    BorderBrush = SectionBorderBrush(),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10),
                    Child = row
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("fixer", "Selected optional repair checks failed", ex);
            if (_section == "fixer" && ReferenceEquals(_selectedFixerHost, host))
            {
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Vybrané kontroly nejsou dostupné. Nic nebylo změněno.",
                    Foreground = WarningBrush()
                });
                host.Children.Add(QuickButton("Zkusit znovu",
                    () => LoadSelectedFixerChecksAsync(host)));
            }
        }
        finally { Interlocked.Exchange(ref _selectedFixerScanRunning, 0); }
    }

    private Task OpenSelectedFixerSettingsAsync(string target)
    {
        if (!SelectedFixerChecksService.IsAllowedRepairUri(target))
            throw new ArgumentException("Unknown repair destination.", nameof(target));
        if (target == "indexing-options")
        {
            // Built-in Control Panel entry. Launches the index settings only;
            // never automatically rebuilds, clears or modifies the index.
            using var started = Process.Start(new ProcessStartInfo("control.exe")
            {
                UseShellExecute = true,
                Arguments = "/name Microsoft.IndexingOptions"
            });
            if (started is null)
                throw new InvalidOperationException("Windows neotevřel Možnosti indexování.");
            return Task.CompletedTask;
        }
        ProcessRunner.Open(target);
        return Task.CompletedTask;
    }
}
