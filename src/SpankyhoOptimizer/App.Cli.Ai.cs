using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchAiAsync(string[] cli)
    {
        if (HasArg(cli, "--ai-power-evidence-smoke"))
        {
            try
            {
                await AiPowerTelemetryEvidenceSmoke.RunAsync();
                LogService.Info("AI_POWER_EVIDENCE_SMOKE_OK cases=38 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AI_POWER_EVIDENCE_SMOKE_FAIL", ex); Environment.Exit(112); }
            return true;
        }
        if (HasArg(cli, "--ai-power-details-cache-smoke"))
        {
            try
            {
                await AiPowerDetailsSnapshotSmoke.RunAsync();
                LogService.Info("AI_POWER_DETAILS_CACHE_SMOKE_OK cases=25 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AI_POWER_DETAILS_CACHE_SMOKE_FAIL", ex); Environment.Exit(116); }
            return true;
        }

        if (HasArg(cli, "--ai-power-lazy-details-smoke"))
        {
            try
            {
                await AiPowerDetailsLoadSmoke.RunAsync();
                LogService.Info("AI_POWER_LAZY_DETAILS_SMOKE_OK cases=22 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AI_POWER_LAZY_DETAILS_SMOKE_FAIL", ex); Environment.Exit(115); }
            return true;
        }

        if (HasArg(cli, "--ai-power-live-smoke"))
        {
            try
            {
                await AiPowerCompactLiveSmoke.RunAsync();
                LogService.Info("AI_POWER_LIVE_SMOKE_OK cases=30 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AI_POWER_LIVE_SMOKE_FAIL", ex); Environment.Exit(117); }
            return true;
        }

        if (HasArg(cli, "--ai-power-refresh-smoke"))
        {
            try
            {
                await AiPowerControlsRefreshSmoke.RunAsync();
                LogService.Info("AI_POWER_REFRESH_SMOKE_OK cases=29 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AI_POWER_REFRESH_SMOKE_FAIL", ex); Environment.Exit(114); }
            return true;
        }

        if (HasArg(cli, "--ai-power-compact-smoke"))
        {
            try
            {
                await AiPowerCompactPresentationSmoke.RunAsync();
                LogService.Info("AI_POWER_COMPACT_SMOKE_OK cases=13 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AI_POWER_COMPACT_SMOKE_FAIL", ex); Environment.Exit(113); }
            return true;
        }

        return false;
    }
}
