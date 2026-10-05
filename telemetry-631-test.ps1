$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item 'artifacts\telemetry-631\before' -ItemType Directory -Force|Out-Null
Copy-Item 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs','src\SpankyhoOptimizer\MainWindow.VisualQa.cs' 'artifacts\telemetry-631\before'
& .\ai-workflow.ps1 -Action start-task -TaskId 'telemetry-631' -Summary 'Fix RAM summary/live gauge sample mismatch and late snapshot overwrite.' -NextAction 'Prove real telemetry loop leaves stale RAM summary, then unify sample updates.' 
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.RamTelemetryRegression.cs'
$test=@'
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
'@
[IO.File]::WriteAllText($path,$test)
$visual=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.VisualQa.cs'
$text=[IO.File]::ReadAllText($visual).Replace('await ValidateChangelogRefreshSmokeAsync();','await ValidateChangelogRefreshSmokeAsync();'+[Environment]::NewLine+'        await ValidateRamTelemetrySmokeAsync();')
[IO.File]::WriteAllText($visual,$text)
$red=[IO.File]::ReadAllText((Join-Path $PWD 'changelog-630-red.ps1')).Replace('changelog-630','telemetry-631').Replace('CHANGELOG_REFRESH_REGRESSION','RAM_TELEMETRY_REGRESSION').Replace('CHANGELOG630','RAM631')
[IO.File]::WriteAllText((Join-Path $PWD 'telemetry-631-red.ps1'),$red)
& .\telemetry-631-red.ps1
