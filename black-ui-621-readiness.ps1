$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$utf8=[Text.UTF8Encoding]::new($false)
function Edit([string]$name,[scriptblock]$change){
 $p=Join-Path $PSScriptRoot ('src\SpankyhoOptimizer\'+$name)
 $t=[IO.File]::ReadAllText($p)
 $t=& $change $t
 [IO.File]::WriteAllText($p,$t,$utf8)
}
Edit 'MainWindow.cs' {
 param($t)
 $t=[regex]::Replace($t,'string heroTitle = key == "overview" \? "[^"]*" : sectionDefinition.Title;', 'string heroTitle = key == "overview" ? "P\u0159ehled" : sectionDefinition.Title;')
 $t=[regex]::Replace($t,'(?s)(string heroSubtitle = key == "overview"\s*\? )"[^"]*"', '$1"Hardware, \u017eiv\u00e9 vyu\u017eit\u00ed a stav syst\u00e9mu"')
 $t
}
Edit 'MainWindow.Shell.cs' {
 param($t)
 $t=[regex]::Replace($t,'PageTitle.Text = _section == "overview" \? "[^"]*" : section.Title;', 'PageTitle.Text = _section == "overview" ? "P\u0159ehled" : section.Title;')
 $t=[regex]::Replace($t,'PageSubtitle.Text = _section == "overview" \? "[^"]*" : section.Subtitle;', 'PageSubtitle.Text = _section == "overview" ? "Hardware, \u017eiv\u00e9 vyu\u017eit\u00ed a stav syst\u00e9mu" : section.Subtitle;')
 $t
}
Edit 'MainWindow.OverviewDashboard.cs' {
 param($t)
 $t=$t.Replace('private Grid? _overviewGaugeQueuedHost;', 'private Grid? _overviewGaugeQueuedHost;'+[Environment]::NewLine+'    private CancellationToken _overviewGaugeQueuedToken;')
 $t=$t.Replace('private Grid? _overviewCardsQueuedHost;', 'private Grid? _overviewCardsQueuedHost;'+[Environment]::NewLine+'    private CancellationToken _overviewCardsQueuedToken;')
 $t=$t.Replace('host.Children.Count > 0 || ReferenceEquals(_overviewGaugeQueuedHost, host)', 'host.Children.Count > 0 || (ReferenceEquals(_overviewGaugeQueuedHost, host) && _overviewGaugeQueuedToken == token)')
 $t=$t.Replace('_overviewGaugeQueuedHost = host;', '_overviewGaugeQueuedHost = host;'+[Environment]::NewLine+'        _overviewGaugeQueuedToken = token;')
 $t=$t.Replace('ReferenceEquals(_overviewCardsQueuedHost, metricsHost))', '(ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))')
 $t=$t.Replace('_overviewCardsQueuedHost = metricsHost;', '_overviewCardsQueuedHost = metricsHost;'+[Environment]::NewLine+'        _overviewCardsQueuedToken = token;')
 $pattern='(internal async Task RunOverviewCacheSmokeAsync\(\)\s*\{\s*InvalidateOverviewPageCache\(forceRelease: true\);\s*await ShowSectionAsync\("overview"\);)'
 $t=[regex]::Replace($t,$pattern,'$1'+[Environment]::NewLine+'        await ShowSectionAsync("overview"); // Canceled factories must be rescheduled for repeated navigation.')
 $t
}
Edit 'MainWindow.VisualQa.cs' {
 param($t)
 $t=[regex]::Replace($t,'(string expectedTitle = expected == "overview"\s*\? )"[^"]*"','$1"P\u0159ehled"')
 $t=$t.Replace('            nodes++;', '            if (node is UIElement { Visibility: Visibility.Collapsed }) return;'+[Environment]::NewLine+'            nodes++;')
 $start=$t.IndexOf('            bool sectionReady =')
 $end=$t.IndexOf('            string signature =', $start)
 if($start -lt 0 -or $end -lt 0){throw 'Readiness target missing'}
 $expr=$t.Substring($start,$end-$start).Replace('bool sectionReady =','').Trim().TrimEnd(';')
 $new='            bool overviewReady = !section.Equals("overview", StringComparison.OrdinalIgnoreCase) ||'+[Environment]::NewLine+'                (_overviewTelemetryGrid?.Children.Count == 4 && _overviewMetricsHost?.Children.Count > 0 && _overviewLiveHost?.Children.Count == 4 && _pageScroll.ActualHeight > 100);'+[Environment]::NewLine+'            bool sectionReady = overviewReady && ('+$expr+');'+[Environment]::NewLine+[Environment]::NewLine
 $t=$t.Substring(0,$start)+$new+$t.Substring($end)
 $t
}
Write-Output 'BLACK_UI_READINESS_FIXED'
