using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private StackPanel? _aiPowerControlsHost;
    private static readonly SemaphoreSlim AiScopeSaveGate = new(1, 1);

    private static string NetworkEvidenceText(AiNetworkRuntimeHealth health)
    {
        static string Window(AiNetworkProbeResult? sample)
        {
            if (sample == null || !sample.Valid || sample.TransferMs.Length != 5) return "čeká";
            return $"{AiNetworkRuntimePolicy.Median(sample.TransferMs):0.0} ms";
        }
        return $"\nTCP přenos 256 KiB: před {Window(health.Before)} • změna {Window(health.CandidateResult)} • původní znovu {Window(health.Reference)}";
    }

    private async Task SaveAiMasterAsync(bool enable)
    {
        await AiScopeSaveGate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                var profile = FanProfileService.Load();
                FanProfileService.Save(profile with { AiSmartRuntimeEnabled = enable });
                var config = AiSystemResourceManagerService.LoadConfig();
                AiSystemResourceManagerService.SaveConfig(config with { Enabled = enable });
                if (!enable) await AiSystemResourceManagerService.RestoreAllNowAsync();
                FanProfileService.StartAgentIfNeeded();
            });
            InvalidatePowerPageCache();
        }
        finally { AiScopeSaveGate.Release(); }
    }

    private async Task SaveAiScopeAsync(Func<AiSystemRuntimeConfig, AiSystemRuntimeConfig> update, bool enable)
    {
        await AiScopeSaveGate.WaitAsync();
        try
        {
        await Task.Run(async () =>
        {
            var config = AiSystemResourceManagerService.LoadConfig();
            var next = update(config) with { Enabled = enable || config.Enabled };
            AiSystemResourceManagerService.SaveConfig(next);
            if (config.TweaksEnabled && !next.TweaksEnabled)
                await new AiTweakRuntimeService().RestoreAllAsync();
            if (config.NetworkEnabled && !next.NetworkEnabled)
                await new AiNetworkRuntimeService().RestoreAllAsync();
            if (config.SecurityEnabled && !next.SecurityEnabled)
                await new AiSecurityHardeningService().RestoreAllAsync();
            if (config.PowerPlanEnabled && !next.PowerPlanEnabled)
                await AdaptivePowerRuntimeService.RestoreAllAiSettingsAsync();
            if (enable)
            {
                var profile = FanProfileService.Load();
                FanProfileService.Save(profile with { AiSmartRuntimeEnabled = true });
            }
            FanProfileService.StartAgentIfNeeded();
        });
        InvalidatePowerPageCache();
        }
        finally { AiScopeSaveGate.Release(); }
    }

    private async Task LoadAiPowerControlsAsync(StackPanel host, int generation, CancellationToken token)
    {
        try
        {
            var config = await Task.Run(AiSystemResourceManagerService.LoadConfig, token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "power") return;
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(BuildRuntimeToggleRow(
                "Automatické řízení přes AI",
                "AI přizpůsobuje aktivní Spankyho plán zátěži CPU, teplotě a hraní. Po vypnutí vrátí hodnoty, které změnila.",
                config.Enabled && config.PowerPlanEnabled,
                enabled => SafeTaskService.Forget(
                    SaveAiScopeAsync(c => c with { PowerPlanEnabled = enabled }, enabled),
                    "ai-runtime", "Save AI power scope")));
            panel.Children.Add(QuickButton("Správa všech tweaků přes AI", () => ShowSectionAsync("ai-runtime")));
            host.Children.Clear();
            host.Children.Add(BuildSectionSurface("power", "AI Power Plan", "Automatický režim i ruční volby na jednom místě.", panel));
            SchedulePageScrollPolicyRefresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "Power controls load failed", ex);
        }
    }
}
