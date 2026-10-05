$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs'
$text=[IO.File]::ReadAllText($path).Replace([string][char]13,'')
$start=$text.IndexOf('                if (_overviewHistoryTimes == null || _overviewHistoryRows == null) return;')
$end=$text.IndexOf('            });', $start)
if($start -lt 0 -or $end -lt $start){throw 'History write block anchor'}
$text=$text.Remove($start,$end-$start)
$text=$text.Replace('    private TextBlock[]? _overviewHistoryTimes;', '    private TextBlock? _overviewChangelogCaption;'+[Environment]::NewLine+'    private TextBlock[]? _overviewHistoryTimes;')
$text=$text.Replace('        _overviewHistoryTimes = null;', '        _overviewChangelogCaption = null;'+[Environment]::NewLine+'        _overviewHistoryTimes = null;')
$text=$text.Replace('        root.Children.Add(history);',@'
        // This card belongs to release history, not the hidden tweak-state summary.
        _overviewChangelogCaption = new TextBlock
        {
            Text = "Na\u010d\u00edt\u00e1m changelog\u2026",
            FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = TextSecondaryBrush()
        };
        root.Children.Add(_overviewChangelogCaption);
'@)
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.Changelog.cs'
$text=[IO.File]::ReadAllText($path).Replace([string][char]13,'')
$text=$text.Replace('            var items = await Task.Run(() => LoadChangelogPreview(4), token).ConfigureAwait(false);',@'
            var rows = _overviewHistoryRows;
            var versions = _overviewHistoryTimes;
            var caption = _overviewChangelogCaption;
            if (rows == null || versions == null || caption == null) return;
            var items = await Task.Run(() => LoadChangelogPreview(4), token).ConfigureAwait(false);
'@)
$text=$text.Replace('if (_section != "overview" || _overviewHistoryTimes == null || _overviewHistoryRows == null) return;',
 'if (token.IsCancellationRequested || _section != "overview" || !ReferenceEquals(rows, _overviewHistoryRows) || !ReferenceEquals(caption, _overviewChangelogCaption)) return;')
$text=$text.Replace('if (_overviewTextRefs?.History != null)','if (caption != null)')
$text=$text.Replace('_overviewTextRefs.History.Text = items.Count == 0','caption.Text = items.Count == 0')
[IO.File]::WriteAllText($path,$text)
Write-Output 'CHANGELOG630_FIX_SAVED'
