$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$backup=Join-Path $PSScriptRoot 'artifacts\black-ui-621\before'
New-Item $backup -ItemType Directory -Force|Out-Null
$utf8=[Text.UTF8Encoding]::new($false)
function Save([string]$file,[string]$text){
 $path=Join-Path $PSScriptRoot ('src\SpankyhoOptimizer\'+$file)
 if(Test-Path $path){$b=Join-Path $backup $file;if(!(Test-Path $b)){Copy-Item $path $b}}
 [IO.File]::WriteAllText($path,$text,$utf8)
}
function Read([string]$file){[IO.File]::ReadAllText((Join-Path $PSScriptRoot ('src\SpankyhoOptimizer\'+$file)))}
function Swap([string]$text,[string]$old,[string]$new){
 if(!$text.Contains($old)){throw ('Missing patch target: '+$old.Substring(0,[Math]::Min(90,$old.Length)))}
 return $text.Replace($old,$new)
}
function Method([string]$text,[string]$signature,[string]$body){
 $start=$text.IndexOf($signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw "Missing method $signature"}
 $open=$text.IndexOf('{',$start);$depth=0;$end=-1
 for($i=$open;$i -lt $text.Length;$i++){
  if($text[$i] -eq '{'){$depth++}
  elseif($text[$i] -eq '}'){$depth--;if($depth -eq 0){$end=$i;break}}
 }
 if($end -lt 0){throw 'Unbalanced method'}
 return $text.Substring(0,$open)+$body+$text.Substring($end+1)
}
& .\ai-workflow.ps1 -Action start-task -TaskId 'black-ui-621' -Title 'Complete native black UI redesign including Overview header icons' -Area ui -ValidationRequired @('build-fast','build-test','theme','layout','capture','release')

$t=Read 'MainWindow.NeutralUi.cs'
$t=Swap $t '!string.Equals(_themeSection, "overview", StringComparison.OrdinalIgnoreCase);' 'true;'
$t=Swap $t '// Overview keeps its original palette; all other pages use compact workspace chrome.' '// One neutral design system covers every page, including Overview.'
$t=Swap $t 'NeutralDepthBrush(24, 18)' 'NeutralDepthBrush(25, 17)'
$t=Swap $t 'NeutralDepthBrush(53, 35)' 'NeutralDepthBrush(59, 31)'
Save 'MainWindow.NeutralUi.cs' $t

$t=Read 'MainWindow.Shell.cs'
$t=Swap $t 'private const double OverviewPageLift = 22d;' 'private const double OverviewPageLift = 0d;'
$t=Swap $t '? CalculateShellHeroHeight(clientWidth) + OverviewHeroExtraHeight' '? 142d'
$t=Swap $t 'PageHost.Padding = overviewHero ? new Thickness(16, 1, 16, 8) : new Thickness(24, 10, 24, 24);' 'PageHost.Padding = new Thickness(24, 10, 24, 24);'
$t=Swap $t 'HeroTextPanel.Visibility = Visibility.Collapsed;' 'HeroTextPanel.Visibility = Visibility.Visible;'
$t=Method $t 'private Border BuildCinematicHero()' '{ return BuildBlackOverviewHeader(); }'
$t=Swap $t 'CategoryBar = new StackPanel { Spacing = 5 };' 'CategoryBar = new StackPanel { Spacing = 3 };'
$t=Swap $t 'SidebarIcon(section, preview ? 27 : 26)' 'SidebarIcon(section, 20)'
$t=Swap $t 'FontSize = preview ? 13.6 : 13.1' 'FontSize = 12.5'
$t=Swap $t 'MinHeight = 44,' 'MinHeight = 37,'
$t=Swap $t 'Padding = new Thickness(10, 7, 10, 7),' 'Padding = new Thickness(10, 5, 10, 5),'
$t=Swap $t 'button.BorderThickness = new Thickness(active ? 3 : 1, 1, 1, 1);' 'button.BorderThickness = new Thickness(active ? 2 : 0, 0, 0, 0);'
$t=Swap $t 'NeutralDepthBrush(47, 34)' 'NeutralDepthBrush(42, 27)'
$t=Swap $t 'ThemeColor(14, 14, 14)' 'ThemeColor(10, 10, 10)'
Save 'MainWindow.Shell.cs' $t

$t=Read 'MainWindow.OverviewHudTelemetry.cs'
$t=Method $t 'private Border BuildHudTelemetryCard(' '{ return BuildBlackTelemetryCard(label, subtitle, accent, out refs); }'
Save 'MainWindow.OverviewHudTelemetry.cs' $t

$t=Read 'MainWindow.OverviewDashboard.cs'
$t=Method $t 'private Border BuildOverviewHardwareStrip(' '{ return BuildBlackHardwareStrip(refs, compact); }'
$t=Method $t 'private FrameworkElement OverviewGlowIcon(' '{ return SectionIcon(tag, iconSize); }'
$t=Method $t 'private static Brush OverviewCardBackgroundBrush()' '{ return _darkTheme ? NeutralCardSurface() : SurfaceElevatedBrush(); }'
$t=Method $t 'private static Brush OverviewNeonBorderBrush()' '{ return _darkTheme ? NeutralCardEdge() : BorderThemeBrush(); }'
$t=Method $t 'private Border BuildOverviewSystemRichCard(' '{ return BuildBlackSystemCard(os, board); }'
$t=Method $t 'private Border BuildOverviewUptimeCard(' '{ return BuildBlackUptimeCard(uptime); }'
$t=Swap $t 'Source = new BitmapImage(new Uri("ms-appx:///Assets/AppLogo.png"))' 'Source = SidebarIconSource("overview")'
$t=Swap $t 'new GridLength(1.4, GridUnitType.Star)' 'GridLength.Auto'
$t=Swap $t 'var chart = BuildOverviewPerformanceChart();' 'var chart = BuildOverviewPerformanceChart(); chart.Visibility = Visibility.Collapsed;'
$t=Swap $t '(!_heroDeferredCompleted || _heroBackgroundImage?.Source == null)' '(!_heroDeferredCompleted || _heroImageFrame?.Child is not Grid)'
$t=Swap $t 'if (!_heroDeferredCompleted || _heroBackgroundImage?.Source == null ||' 'if (!_heroDeferredCompleted || _heroImageFrame?.Child is not Grid ||'
$t=$t.Replace('BorderThickness = new Thickness(1.55)','BorderThickness = new Thickness(1)')
$t=$t.Replace('ThemeColor(145, 12, 17, 45)','SurfaceAltBrush()')
Save 'MainWindow.OverviewDashboard.cs' $t

$t=Read 'MainWindow.VisualQa.cs'
$t=Swap $t @'
_heroBackgroundImage?.Source is BitmapImage heroBitmap &&
                heroBitmap.PixelWidth > 0 &&
                heroBitmap.PixelHeight > 0
'@ @'
_heroDeferredCompleted && _heroImageFrame?.Child is Grid &&
                HeaderCard.Height >= 100
'@
Save 'MainWindow.VisualQa.cs' $t

$t=Read 'MainWindow.Theme.cs'
$start=$t.IndexOf('    private static SolidColorBrush CpuMetricBrush()')
$end=$t.IndexOf('    private static void ResetThemeBrushCaches()', $start)
if($start -lt 0 -or $end -lt 0){throw 'Metric methods missing'}
$t=$t.Substring(0,$start)+@'
    private static SolidColorBrush CpuMetricBrush() => _darkTheme ? ThemeColor(218,224,231) : ThemeColor(55,65,81);
    private static SolidColorBrush GpuMetricBrush() => _darkTheme ? ThemeColor(136,192,166) : ThemeColor(35,115,82);
    private static SolidColorBrush GpuHardwareBrush() => GpuMetricBrush();
    private static SolidColorBrush RamMetricBrush() => _darkTheme ? ThemeColor(204,184,141) : ThemeColor(141,104,41);
    private static SolidColorBrush DiskMetricBrush() => _darkTheme ? ThemeColor(150,174,196) : ThemeColor(59,102,138);
    private static SolidColorBrush DiskHardwareBrush() => DiskMetricBrush();

'@+$t.Substring($end)
Save 'MainWindow.Theme.cs' $t

$t=Read 'MainWindow.Motion.cs'
$t=$t.Replace('visual.Opacity = 0.0f;', 'visual.Opacity = 0.86f;').Replace('new Vector3(0, 10, 0)', 'new Vector3(0, 4, 0)').Replace('FromMilliseconds(150)', 'FromMilliseconds(110)').Replace('hoverScale = 1.018f', 'hoverScale = 1.006f')
Save 'MainWindow.Motion.cs' $t

& .\ai-workflow.ps1 -Action checkpoint -TaskId 'black-ui-621' -Phase 'native-redesign'
Write-Output 'BLACK_UI_PATCH_OK'
