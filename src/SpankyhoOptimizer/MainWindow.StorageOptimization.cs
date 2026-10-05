using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void ScheduleStorageOptimizationAdvisor(CancellationToken token)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "storage") return;
            BuildStorageOptimizationAdvisor(token);
        });
    }

    private void BuildStorageOptimizationAdvisor(CancellationToken token)
    {
        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(new TextBlock
        {
            Text = "Načítám skutečný stav TRIM / NTFS / Optimize Drives…",
            Foreground = TextSecondaryBrush()
        });
        ContentStack.Children.Add(BuildDashboardExpander(
            "Storage Recommended",
            "TRIM • NTFS Last Access • Scheduled Optimize • compatibility-safe hodnoty",
            host));

        SafeTaskService.Forget(
            LoadStorageOptimizationAdvisorAsync(host, token),
            "storage",
            "Storage optimization advisor");
    }

    private async Task LoadStorageOptimizationAdvisorAsync(StackPanel host, CancellationToken token)
    {
        try
        {
            var state = await StorageOptimizationService.ReadAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _section != "storage") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "storage" || token.IsCancellationRequested) return;
                host.Children.Clear();
                RenderStorageOptimizationAdvisor(host, state);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("storage", "Storage optimization advisor failed", ex);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "storage") return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Aktuální stav úložiště není dostupný: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                });
            });
        }
    }

    private void RenderStorageOptimizationAdvisor(StackPanel host, StorageOptimizationSnapshot s)
    {
        string trimCurrent = s.TrimEnabled switch
        {
            true => "ON",
            false => "OFF",
            _ => "Nezjištěno"
        };
        host.Children.Add(BuildOptimizationAdvisorRow(
            "TRIM / DeleteNotify",
            trimCurrent,
            "ON",
            "SSD/NVMe má dostávat informace o uvolněných blocích; Optimizer změnu vždy následně ověří.",
            s.TrimEnabled == true
                ? OptimizationAdvisorKind.Ok
                : s.TrimEnabled == false
                    ? OptimizationAdvisorKind.Recommended
                    : OptimizationAdvisorKind.Info));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "NTFS Last Access",
            s.LastAccessText,
            "Disabled",
            "Aktualizace last-access timestampu není na běžném desktopu potřeba a vytváří zbytečné metadata zápisy.",
            s.LastAccessMode is 1 or 3
                ? OptimizationAdvisorKind.Ok
                : s.LastAccessMode.HasValue
                    ? OptimizationAdvisorKind.Recommended
                    : OptimizationAdvisorKind.Info));

        string scheduled = s.ScheduledOptimizeEnabled switch
        {
            true => "ON",
            false => "OFF",
            _ => "Nezjištěno"
        };
        host.Children.Add(BuildOptimizationAdvisorRow(
            "Scheduled Optimize Drives",
            scheduled,
            "ON",
            "Windows na SSD provádí správnou údržbu/TRIM; vypínat ScheduledDefrag plošně není doporučené.",
            s.ScheduledOptimizeEnabled == true
                ? OptimizationAdvisorKind.Ok
                : s.ScheduledOptimizeEnabled == false
                    ? OptimizationAdvisorKind.Recommended
                    : OptimizationAdvisorKind.Info));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "NTFS 8.3 names",
            s.EightDotThreeText,
            "TEST / kompatibilita",
            "Starší instalátory a aplikace mohou 8.3 názvy potřebovat, proto se tato hodnota nikdy nemění automaticky.",
            OptimizationAdvisorKind.Test));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "Write cache / buffer flushing",
            s.WriteCacheText,
            "Ponechat Windows / driver",
            "Optimizer nebude vypínat write-cache buffer flushing; při výpadku napájení by to mohlo zvýšit riziko ztráty dat.",
            OptimizationAdvisorKind.Info));

        bool needsApply =
            s.TrimEnabled == false ||
            (s.LastAccessMode.HasValue && s.LastAccessMode is not (1 or 3)) ||
            s.ScheduledOptimizeEnabled == false;

        if (needsApply)
        {
            host.Children.Add(PrimaryQuickButton("Použít Storage Recommended", async () =>
            {
                try
                {
                    var after = await StorageOptimizationService.ApplyRecommendedAsync(
                        _loadCts?.Token ?? CancellationToken.None);
                    if (_section != "storage") return;
                    host.Children.Clear();
                    RenderStorageOptimizationAdvisor(host, after);
                    SystemInsightService.InvalidateStorage(
                        preserveStableDriverMetadata: true,
                        invalidateTemperatures: false,
                        invalidateTrim: true);
                    ShowActionFeedback("Storage Recommended", ActionFeedbackKind.Verified,
                        "TRIM • Last Access • Optimize Drives byly ověřeny.");
                }
                catch (Exception ex)
                {
                    LogService.Error("storage", "Storage Recommended apply failed", ex);
                    ShowActionFeedback("Storage Recommended", ActionFeedbackKind.Failed, ex.Message);
                    await ShowMessageAsync("Storage Recommended", ex.Message);
                }
            }));
        }
    }
}
