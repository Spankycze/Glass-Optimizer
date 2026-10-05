$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$utf8=[Text.UTF8Encoding]::new($false)
$p=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\MainWindow.cs'
$t=[IO.File]::ReadAllText($p)
$start=$t.IndexOf('        if (string.Equals(_section, "overview", StringComparison.OrdinalIgnoreCase))', $t.IndexOf('private void ApplyPageScrollPolicyNow()'))
$end=$t.IndexOf('        double viewport =',$start)
if($start -lt 0 -or $end -lt 0){throw 'Scroll policy target missing'}
$t=$t.Remove($start,$end-$start)
$t=$t.Replace('bool overviewNoScroll = string.Equals(key, "overview", StringComparison.OrdinalIgnoreCase);','bool overviewNoScroll = string.Equals(key, "overview", StringComparison.OrdinalIgnoreCase) &&'+[Environment]::NewLine+'            (RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth) >= 1200 &&'+[Environment]::NewLine+'            (RootGrid.ActualHeight > 1 ? RootGrid.ActualHeight : 860) >= 790;')
[IO.File]::WriteAllText($p,$t,$utf8)
$p=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\MainWindow.VisualQa.cs'
$t=[IO.File]::ReadAllText($p)
$t=[regex]::Replace($t,'ScrollMode expected = string.Equals\(_section, "overview", StringComparison.OrdinalIgnoreCase\)\s*\? ScrollMode.Disabled\s*: overflow \? ScrollMode.Enabled : ScrollMode.Disabled;', 'ScrollMode expected = overflow ? ScrollMode.Enabled : ScrollMode.Disabled;')
$t=[regex]::Replace($t,'ScrollMode expectedVertical = string.Equals\(section, "overview", StringComparison.OrdinalIgnoreCase\)\s*\? ScrollMode.Disabled\s*: verticalOverflow \? ScrollMode.Enabled : ScrollMode.Disabled;', 'ScrollMode expectedVertical = verticalOverflow ? ScrollMode.Enabled : ScrollMode.Disabled;')
$t=$t.Replace('for (int i = 0; i < 50; i++)','for (int i = 0; i < 100; i++)')
$t=$t.Replace('_overviewTelemetryGrid?.Children.Count == 4 && _overviewMetricsHost?.Children.Count > 0','_cpuGauge?.ValueText.Text is not (null or "—") && _ramGauge?.ValueText.Text is not (null or "—") && _overviewTelemetryGrid?.Children.Count == 4 && _overviewMetricsHost?.Children.Count > 0')
# Use unicode escapes for the em dash comparison in C# after a PowerShell 5 read.
$t=$t.Replace('"—"', '"\u2014"')
[IO.File]::WriteAllText($p,$t,$utf8)
$p=Join-Path $PSScriptRoot 'WORK_PROGRESS.md'
$t=[IO.File]::ReadAllText($p)
$start=$t.LastIndexOf('## 15.4.621')
if($start -ge 0){$t=$t.Substring(0,$start)}
$t+=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'artifacts\black-ui-621\release-note.txt'))
[IO.File]::WriteAllText($p,$t,$utf8)
Write-Output 'BLACK_UI_POLISH_OK'
