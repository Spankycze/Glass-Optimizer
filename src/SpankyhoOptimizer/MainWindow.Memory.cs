using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void BuildMemoryState()
    {
        var host = new StackPanel { Spacing = 7 };
        host.Children.Add(new TextBlock { Text = "Načítám skutečný stav paměti…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("memory", "Paměť", "Komprese • stránkovací soubor • plánování • cache • RAM", host));
        CancellationToken token = _loadCts?.Token ?? CancellationToken.None;
        SafeTaskService.Forget(LoadMemoryStateAsync(host, token), "memory", "Memory V3 state");
    }

    private async Task LoadMemoryStateAsync(StackPanel host, CancellationToken token)
    {
        try
        {
            var state = await MemoryStateService.ReadAsync(token);
            if (token.IsCancellationRequested || _section != "memory") return;
            DispatcherQueue.TryEnqueue(() => RenderMemoryState(host, state));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("memory", "Memory V3 state failed", ex);
            if (!token.IsCancellationRequested) DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "memory") return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Aktuální stav paměti se nepodařilo načíst: " + ex.Message,
                    Foreground = UiBrush("SpankyWarningBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
                host.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadMemoryStateAsync(host, _loadCts?.Token ?? CancellationToken.None)));
                ShowActionFeedback("Paměť", ActionFeedbackKind.Failed, ex.Message);
            });
        }
    }

    private void RenderMemoryState(StackPanel host, MemoryStateSnapshot s)
    {
        if (_section != "memory") return;
        host.Children.Clear();
        string pageMode = s.AutomaticManagedPagefile == true ? "Řídí Windows" : s.AutomaticManagedPagefile == false ? "Ručně" : "Nezjištěno";
        host.Children.Add(BuildPageSummaryStrip(
            ("Komprese", s.CompressionEnabled ? "ON" : "OFF", s.CompressionEnabled ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            ("Stránkovací soubor", pageMode, s.AutomaticManagedPagefile == false ? "SpankyWarningBrush" : "SpankySuccessBrush"),
            ("Systémová cache", s.LargeSystemCache == 0 ? "Výchozí Windows" : s.LargeSystemCache.ToString(), s.LargeSystemCache == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            ("RAM volná", $"{s.AvailableRamGb:0.0}/{s.TotalRamGb:0.0} GB", "SpankyInfoBrush")));
        host.Children.Add(InsightRow("PAGEFILE", s.PagefileSummary));
        host.Children.Add(InsightRow("SCHEDULER", $"Win32PrioritySeparation = {s.Win32PrioritySeparation} (Windows řídí foreground scheduling)"));
        host.Children.Add(new TextBlock
        {
            Text = "Doporučené nastavení ponechává stránkovací soubor pod správou Windows, systémovou cache na výchozí hodnotě a kompresi paměti zapnutou. Ruční čištění RAM není součástí doporučeného profilu.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
    }
}
