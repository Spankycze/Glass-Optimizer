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
        int generation = _navigationGeneration;
        var host = new StackPanel { Spacing = 7 };
        host.Children.Add(new TextBlock { Text = "Načítám skutečný stav paměti…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("memory", "Paměť", "Komprese • stránkovací soubor • plánování • cache • RAM", host));
        // A fresh native/WMI snapshot is already safe to render synchronously.
        // Avoid the loading placeholder -> dispatcher rerender round trip on
        // rapid returns to the Memory page (the service owns its 10s TTL).
        var warm = MemoryStateService.TryGetFreshSnapshot();
        if (warm != null)
        {
            RenderMemoryState(host, warm);
            return;
        }
        CancellationToken token = _loadCts?.Token ?? _windowLifetimeCts.Token;
        SafeTaskService.Forget(LoadMemoryStateAsync(host, generation, token), "memory", "Memory V3 state");
    }

    private async Task LoadMemoryStateAsync(StackPanel host, int generation, CancellationToken token)
    {
        try
        {
            var state = await MemoryStateService.ReadAsync(token);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "memory" ||
                host.Parent == null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "memory" ||
                    host.Parent == null) return;
                RenderMemoryState(host, state);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("memory", "Memory V3 state failed", ex);
            if (!token.IsCancellationRequested) DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "memory" ||
                    host.Parent == null) return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Aktuální stav paměti se nepodařilo načíst: " + ex.Message,
                    Foreground = UiBrush("SpankyWarningBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
                host.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadMemoryStateAsync(
                        host,
                        _navigationGeneration,
                        _loadCts?.Token ?? _windowLifetimeCts.Token)));
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

        // Keep the opt-in Memory Recommended action in its dedicated section.
        bool needsRecommended = !s.CompressionEnabled ||
                                s.AutomaticManagedPagefile == false ||
                                s.LargeSystemCache != 0;
        if (needsRecommended)
        {
            host.Children.Add(PrimaryQuickButton("Použít doporučené nastavení paměti", async () =>
            {
                try
                {
                    var token = _loadCts?.Token ?? _windowLifetimeCts.Token;
                    await MemoryStateService.ApplyRecommendedAsync(token);
                    MemoryStateService.Invalidate();
                    if (_section != "memory" || token.IsCancellationRequested) return;
                    await LoadMemoryStateAsync(host, _navigationGeneration, token);
                    ShowActionFeedback("Paměť", ActionFeedbackKind.Verified,
                        "Doporučené hodnoty paměti byly použity a znovu načteny.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    LogService.Error("memory", "Memory Recommended failed", ex);
                    ShowActionFeedback("Paměť", ActionFeedbackKind.Failed, ex.Message);
                }
            }));
        }
    }
}
