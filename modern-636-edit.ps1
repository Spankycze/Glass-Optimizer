$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\ai-workflow.ps1 -Action start-task -TaskId 'modern-636' -Title 'Modern full-width workspace redesign' -Area 'ui' -Summary 'Full-width panels, header action toolbar and dimensional rounded navigation.' -NextAction 'Edit shared layout and verify captures, six suites, release and install.'
$dir=Join-Path $PWD 'artifacts\modern-636'
New-Item $dir -ItemType Directory -Force|Out-Null
$base=Join-Path $PWD 'src\SpankyhoOptimizer'
function ReplaceChecked($t,$o,$n){if(!$t.Contains($o)){throw "Missing: $o"};return $t.Replace($o,$n)}
$f=Join-Path $base 'MainWindow.NeutralUi.cs'
$t=[IO.File]::ReadAllText($f)
$a=$t.IndexOf('        UIElement body;', $t.IndexOf('private Border BuildWorkspaceSurface'))
$z=$t.IndexOf('        return new Border', $a)
if($a -lt 0 -or $z -lt 0){throw 'Surface block missing'}
$t=$t.Substring(0,$a)+@'
        var body = new StackPanel { Spacing = 18 };
        body.Children.Add(heading);
        body.Children.Add(new Border
        {
            Height = 1, Background = _darkTheme ? ThemeColor(30,30,30) : SectionBorderBrush(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        body.Children.Add(content);
'@+[Environment]::NewLine+$t.Substring($z)
$t=ReplaceChecked $t 'var heading = new StackPanel { Spacing = 8 };' 'var heading = new StackPanel { Spacing = 5 };'
$t=ReplaceChecked $t 'CornerRadius = new CornerRadius(8),' 'CornerRadius = new CornerRadius(12),'
$t=ReplaceChecked $t 'Padding = new Thickness(16,20,16,20), Margin = new Thickness(0,0,0,10)' 'Padding = new Thickness(22,20,22,22), Margin = new Thickness(0,0,0,14)'
$t=ReplaceChecked $t 'NeutralMaterialBrush(10, 3, 0)' 'NeutralMaterialBrush(14, 5, 1)'
$t=ReplaceChecked $t 'return new Border { Tag = "workspace-intro", Padding = new Thickness(0, 12, 0, 24), Child = grid };' 'return new Border { Tag = "workspace-intro", Padding = new Thickness(0,14,0,20), Margin = new Thickness(0,0,0,16), BorderBrush = _darkTheme ? ThemeColor(34,34,34) : SectionBorderBrush(), BorderThickness = new Thickness(0,0,0,1), Child = grid };'
$t=ReplaceChecked $t 'if (actions is FrameworkElement actionElement) Grid.SetColumn(actionElement, 1);' @'
            if ((RootGrid?.ActualWidth ?? StartupClientWidth) < 1180)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(actions, 1);
                Grid.SetColumnSpan(actions, 2);
                if (actions is FrameworkElement narrowActions) narrowActions.Margin = new Thickness(0,14,0,0);
            }
            else Grid.SetColumn(actions, 1);
'@
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.Shell.cs'
$t=[IO.File]::ReadAllText($f)
$t=ReplaceChecked $t 'CornerRadius = new CornerRadius(3),' 'CornerRadius = new CornerRadius(8),'
$t=ReplaceChecked $t 'button.BorderThickness = new Thickness(active ? 2 : 0, 0, 0, 0);' 'button.BorderThickness = new Thickness(active ? 1 : 0);'
$t=ReplaceChecked $t 'NeutralDepthBrush(8, 0)' 'NeutralDepthBrush(30, 13)'
$t=ReplaceChecked $t 'ThemeColor(6,6,6)' 'NeutralDepthBrush(18,8)'
$t=ReplaceChecked $t '(_sidebarActiveBorder ??= ThemeColor(99,161,212))' '(_sidebarActiveBorder ??= NeutralDepthBrush(68,34))'
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.NetworkDashboard.cs'
$t=[IO.File]::ReadAllText($f)
$t=ReplaceChecked $t 'UseCompactSectionChrome ? new GridLength(0) : new GridLength(320)' 'UseCompactSectionChrome ? GridLength.Auto : new GridLength(320)'
$t=ReplaceChecked $t '        left.Children.Add(actions);' @'
        if (UseCompactSectionChrome && (RootGrid?.ActualWidth ?? StartupClientWidth) >= 1180)
        {
            Grid.SetColumn(actions, 1);
            actions.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(actions);
        }
        else left.Children.Add(actions);
'@
$t=ReplaceChecked $t '        root.Children.Add(artHost);' '        if (!UseCompactSectionChrome) root.Children.Add(artHost);'
$t=ReplaceChecked $t 'new Border { Tag = "workspace-intro", Padding = new Thickness(0,12,0,22), Child = root }' 'new Border { Tag = "workspace-intro", Padding = new Thickness(0,14,0,20), Margin = new Thickness(0,0,0,16), BorderBrush = _darkTheme ? ThemeColor(34,34,34) : SectionBorderBrush(), BorderThickness = new Thickness(0,0,0,1), Child = root }'
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.VisualQa.cs'
$t=[IO.File]::ReadAllText($f)
$t=ReplaceChecked $t 'new[] { "overview", "network", "settings", "power", "ai-runtime" }' 'new[] { "overview", "network", "settings", "power", "ai-runtime", "gpu", "startup", "storage" }'
[IO.File]::WriteAllText($f,$t)
@('MainWindow.NeutralUi.cs','MainWindow.Shell.cs','MainWindow.NetworkDashboard.cs','MainWindow.VisualQa.cs')|ConvertTo-Json|Set-Content (Join-Path $dir 'changed-files.json') -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $dir 'release-note.txt'),"## 15.4.636"+[Environment]::NewLine+"- Redesigned workspace: full-width panels, section headers, deeper black rounded surfaces, responsive action toolbar and dimensional sidebar selection."+[Environment]::NewLine+[Environment]::NewLine)
$b=[IO.File]::ReadAllText((Join-Path $PWD 'weight-635-build.ps1')).Replace('weight-635','modern-636').Replace('635','636').Replace('WEIGHT636','MODERN636')
$b=$b.Replace('Global body Medium and explicit Normal overrides raised; sidebar13SemiBold and all material actions SemiBold.','Full-width panels, separated header toolbar and filled rounded navigation saved.').Replace("'Root cause traced','Failing test confirmed','Single fix saved'","'Full-width panels','Responsive header actions','Dimensional navigation'")
[IO.File]::WriteAllText((Join-Path $PWD 'modern-636-build.ps1'),$b)
Write-Output 'MODERN636_EDIT_SAVED'
& .\modern-636-build.ps1