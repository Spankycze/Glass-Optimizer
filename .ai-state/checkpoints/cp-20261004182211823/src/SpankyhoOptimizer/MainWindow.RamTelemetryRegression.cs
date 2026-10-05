using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;
namespace SpankyhoOptimizer;
public sealed partial class MainWindow
{
    private async Task ValidateRamTelemetrySmokeAsync()
    {
        for (int i=0; i<100 && (_ramGauge == null || _overviewTextRefs == null); i++) await Task.Delay(25);
        if (_ramGauge == null || _overviewTextRefs == null) throw new InvalidOperationException("RAM visuals missing.");
        bool active = _windowTelemetryActive;
        bool suppress = _liveMetricsDiagnosticSuppressUi;
        try
        {
            _windowTelemetryActive = true;
            _liveMetricsDiagnosticSuppressUi = false;
            _ramGauge.SecondaryText.Text = "STALE_RAM_SENTINEL";
            _overviewTextRefs.Ram.Text = "STALE_RAM_SENTINEL";
            for(int i=0;i<160 && _ramGauge.SecondaryText.Text == "STALE_RAM_SENTINEL";i++) await Task.Delay(50);
            void Check()
            {
                string percent = _ramGauge.ValueText.Text;
                if (_ramGauge.SecondaryText.Text == "STALE_RAM_SENTINEL" ||
                    !_ramGauge.SecondaryText.Text.Contains(percent) ||
                    _overviewTextRefs.Ram.Text != _ramGauge.SecondaryText.Text)
                    throw new InvalidOperationException("RAM_TELEMETRY_REGRESSION: live gauge and memory summary use different samples.");
            }
            Check();
            var refs = _overviewTextRefs;
            await CompleteOverviewDashboardAsync(HardwareSnapshotService.GetOverviewAsync(false, CancellationToken.None),
                refs.Cpu,refs.Gpu,refs.Ram,refs.Disk,refs.Os,refs.Board,refs.Power,refs.Network,refs.Driver,refs.History,refs.Windows,CancellationToken.None);
            await FlushOverviewUiForSmokeAsync();
            Check();
            LogService.Info("visual-qa","RAM_TELEMETRY_SMOKE_OK liveSummary=true lateSnapshotStable=true");
        }
        finally { _windowTelemetryActive=active; _liveMetricsDiagnosticSuppressUi=suppress; }
    }
}