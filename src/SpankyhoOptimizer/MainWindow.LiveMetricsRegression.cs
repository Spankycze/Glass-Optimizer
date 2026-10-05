using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;
namespace SpankyhoOptimizer;
public sealed partial class MainWindow
{
    internal async Task RunLiveMetricsLifecycleSmokeAsync()
    {
        for (int i = 0; i < 80 && !_initialSectionNavigationCompleted; i++) await Task.Delay(25);
        if (!_initialSectionNavigationCompleted) throw new InvalidOperationException("Initial navigation did not settle.");
        string section = _section;
        bool active = _windowTelemetryActive;
        using var source = new CancellationTokenSource();
        try
        {
            foreach (string scenario in new[] { "navigation", "deactivation", "cancellation", "active" })
            {
                _section = "overview";
                _windowTelemetryActive = true;
                int applied = 0;
                QueueLiveMetricsUiUpdate(() => applied++, source.Token);
                // Change lifecycle state after scheduling, before the UI dispatcher can run it.
                if (scenario == "navigation") _section = "settings";
                if (scenario == "deactivation") _windowTelemetryActive = false;
                if (scenario == "cancellation") source.Cancel();
                await Task.Delay(40);
                if (scenario == "active")
                {
                    // Use a fresh token after cancellation, and verify live updates still work.
                    applied = 0;
                    QueueLiveMetricsUiUpdate(() => applied++, CancellationToken.None);
                    await Task.Delay(40);
                }
                int expected = scenario == "active" ? 1 : 0;
                if (applied != expected)
                    throw new InvalidOperationException($"Late telemetry callback applied after {scenario}: count={applied}, expected={expected}.");
            }
            LogService.Info("LIVE_METRICS_LIFECYCLE_SMOKE_OK navigation=true deactivation=true cancellation=true active=true");
        }
        finally { _section = section; _windowTelemetryActive = active; }
    }
}