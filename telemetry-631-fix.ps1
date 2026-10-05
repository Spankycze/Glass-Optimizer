$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs'
$text=[IO.File]::ReadAllText($path)
function Replace-Once([string]$old,[string]$new) {
 if(!$script:text.Contains($old)){throw ('Anchor missing: '+$old)}
 $script:text=$script:text.Replace($old,$new)
}
Replace-Once 'private volatile bool _windowTelemetryActive = true;' @'
private volatile bool _windowTelemetryActive = true;
    private (double Percent, double UsedGb, double TotalGb)? _latestOverviewRamSample;

    private void UpdateOverviewRamSummary((double Percent, double UsedGb, double TotalGb) sample)
    {
        string summary = $"{sample.UsedGb:0.0} / {sample.TotalGb:0.0} GB \u2022 {sample.Percent:0}% vyu\u017eito";
        if (_overviewTextRefs != null) _overviewTextRefs.Ram.Text = summary;
        if (_ramGauge != null) _ramGauge.SecondaryText.Text = summary;
    }
'@
Replace-Once 'ram.Text = s.Ram;' 'ram.Text = s.Ram;'
Replace-Once 'if (_ramGauge != null) _ramGauge.SecondaryText.Text = s.Ram;' @'
if (_latestOverviewRamSample is { } liveRam)
                    UpdateOverviewRamSummary(liveRam);
                else if (_ramGauge != null) _ramGauge.SecondaryText.Text = s.Ram;
'@
Replace-Once 'AnimateGauge(_ramGauge, ram);' @'
AnimateGauge(_ramGauge, ram);
                        _latestOverviewRamSample = ramSample;
                        UpdateOverviewRamSummary(ramSample);
'@
Replace-Once '_overviewTextRefs = null;' ('_overviewTextRefs = null;'+[Environment]::NewLine+'        _latestOverviewRamSample = null;')
[IO.File]::WriteAllText($path,$text)
Write-Output 'RAM631_FIX_SAVED'
