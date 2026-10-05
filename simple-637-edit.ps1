$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\ai-workflow.ps1 -Action start-task -TaskId 'simple-637' -Title 'Simpler compact UI and immediate navigation' -Area 'ui' -Summary 'Compact panels with one heading; contextual descriptions in accessible tooltip. Remove page motion and navigation pulse/deferred callbacks.' -NextAction 'Save simplification, run fresh tests/captures, compare installed Release performance636/637.'
$dir=Join-Path $PWD 'artifacts\simple-637'
$base=Join-Path $PWD 'src\SpankyhoOptimizer'
$f=Join-Path $base 'MainWindow.NeutralUi.cs'
$t=[IO.File]::ReadAllText($f)
$a=$t.IndexOf('        var heading = new StackPanel', $t.IndexOf('private Border BuildWorkspaceSurface'))
$z=$t.IndexOf('        return new Border', $a)
if($a -lt 0 -or $z -lt 0){throw 'Surface missing'}
$t=$t.Substring(0,$a)+@'
        var heading = new TextBlock
        {
            Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap
        };
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            ToolTipService.SetToolTip(heading, subtitle);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(heading, subtitle);
        }
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(heading);
        body.Children.Add(content);
'@+[Environment]::NewLine+$t.Substring($z)
$t=$t.Replace('Padding = new Thickness(22,20,22,22), Margin = new Thickness(0,0,0,14)','Padding = new Thickness(16,14,16,16), Margin = new Thickness(0,0,0,10)').Replace('CornerRadius = new CornerRadius(12)','CornerRadius = new CornerRadius(10)').Replace('Padding = new Thickness(0,14,0,20), Margin = new Thickness(0,0,0,16)','Padding = new Thickness(0,8,0,14), Margin = new Thickness(0,0,0,12)')
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.cs'
$t=[IO.File]::ReadAllText($f)
$a=$t.IndexOf('                if (_initialSectionNavigationCompleted)')
$z=$t.IndexOf('                _lastNavigationErrorId', $a)
if($a -lt 0 -or $z -lt 0){throw 'Navigation block missing'}
$t=$t.Substring(0,$a)+'                _initialSectionNavigationCompleted = true;'+[Environment]::NewLine+$t.Substring($z)
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.Shell.cs'
$t=[IO.File]::ReadAllText($f)
$t=$t.Replace('        Button? pulseTarget = null;','').Replace('            pulseTarget = current;','')
$a=$t.IndexOf('        if (pulseTarget != null)')
$z=$t.IndexOf('    private static void SetCategoryButtonVisualState', $a)
if($a -lt 0 -or $z -lt 0){throw 'Selection motion missing'}
$t=$t.Substring(0,$a)+'    }'+[Environment]::NewLine+[Environment]::NewLine+$t.Substring($z)
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.Motion.cs'
$t=[IO.File]::ReadAllText($f)
$a=$t.IndexOf('    private ScalarKeyFrameAnimation? _pageFadeAnimation;')
$z=$t.IndexOf('    private static void AttachHoverMotion', $a)
$t=$t.Substring(0,$a)+$t.Substring($z)
$a=$t.IndexOf('    private static void AnimateSelectionPulse')
$z=$t.IndexOf('    private static void AnimateDialogIn', $a)
$t=$t.Substring(0,$a)+$t.Substring($z)
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.NetworkDashboard.cs'
$t=[IO.File]::ReadAllText($f).Replace('Padding = new Thickness(0,14,0,20), Margin = new Thickness(0,0,0,16)','Padding = new Thickness(0,8,0,14), Margin = new Thickness(0,0,0,12)')
[IO.File]::WriteAllText($f,$t)
@('MainWindow.NeutralUi.cs','MainWindow.cs','MainWindow.Shell.cs','MainWindow.Motion.cs','MainWindow.NetworkDashboard.cs')|ConvertTo-Json|Set-Content (Join-Path $dir 'changed-files.json') -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $dir 'release-note.txt'),"## 15.4.637"+[Environment]::NewLine+"- Simpler compact panels: one heading, descriptions in accessible tooltip, less spacing. Immediate navigation without page slide/fade or selection pulse. Overview hardware layout preserved."+[Environment]::NewLine+[Environment]::NewLine)
$b=[IO.File]::ReadAllText((Join-Path $PWD 'modern-636-build.ps1')).Replace('modern-636','simple-637').Replace('636','637').Replace('MODERN637','SIMPLE637').Replace('Full-width panels, separated header toolbar and filled rounded navigation saved.','Simpler compact panels and immediate navigation saved; baseline636 three Release samples recorded.').Replace("'Full-width panels','Responsive header actions','Dimensional navigation'","'Compact accessible panels','Immediate navigation','Baseline measured'")
[IO.File]::WriteAllText((Join-Path $PWD 'simple-637-build.ps1'),$b)
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'simple-637' -Summary 'Panel heading nested stack, subtitle TextBlock and separator removed: three fewer UI nodes per workspace; navigation composition animation and deferred callbacks removed.' -NextAction 'Fresh637 build, capture and regression tests, then Release performance comparison.' -Done @('Baseline636 measured','Compact panels saved','Navigation motion removed') -Pending @('Tests','Release/install','Comparison') -FilesTouched @('src\SpankyhoOptimizer\MainWindow.NeutralUi.cs','src\SpankyhoOptimizer\MainWindow.cs','src\SpankyhoOptimizer\MainWindow.Shell.cs','src\SpankyhoOptimizer\MainWindow.Motion.cs','src\SpankyhoOptimizer\MainWindow.NetworkDashboard.cs') -Evidence @('artifacts\simple-637\before-performance.json')
& .\simple-637-build.ps1
