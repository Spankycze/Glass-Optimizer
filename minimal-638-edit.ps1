$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\ai-workflow.ps1 -Action start-task -TaskId 'minimal-638' -Title 'Replace boxed cards with compact open sections' -Area 'ui' -Summary 'User rejected similar appearance637. Remove workspace card fill/outer frame, compact titles/navigation, delete redundant DNS/MTU banner. Keep hardware Overview and functional controls.' -NextAction 'Build/test actual638 captures then release/install.'
$dir=Join-Path $PWD 'artifacts\minimal-638'
New-Item $dir -ItemType Directory -Force|Out-Null
$base=Join-Path $PWD 'src\SpankyhoOptimizer'
$f=Join-Path $base 'MainWindow.NeutralUi.cs'
$t=[IO.File]::ReadAllText($f)
$old='return new Border { Background = _darkTheme ? (_neutralWorkspaceSurface ??= NeutralMaterialBrush(14, 5, 1)) : SurfaceAltBrush(), BorderBrush = _darkTheme ? NeutralCardEdge() : SectionBorderBrush(),'+[Environment]::NewLine+'            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),'+[Environment]::NewLine+'            Padding = new Thickness(16,14,16,16), Margin = new Thickness(0,0,0,10), Child = body };'
# Normalize line endings for exact replacement.
$t=$t.Replace([Environment]::NewLine,[string][char]10)
$old=$old.Replace([Environment]::NewLine,[string][char]10)
if(!$t.Contains($old)){throw 'Workspace return mismatch'}
$t=$t.Replace($old,@'
return new Border
        {
            BorderBrush = _darkTheme ? ThemeColor(27,27,27) : SectionBorderBrush(),
            BorderThickness = new Thickness(0,0,0,1),
            Padding = new Thickness(0,10,0,18), Margin = new Thickness(0,0,0,8),
            Child = body
        };
'@).Replace('Text = title, FontSize = 18,','Text = title, FontSize = 16,').Replace('Text = title, FontSize = 30,','Text = title, FontSize = 26,').Replace('var body = new StackPanel { Spacing = 12 };','var body = new StackPanel { Spacing = 10 };').Replace('    private static Brush? _neutralWorkspaceSurface;','')
# Cached workspace surface no longer allocated.
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.Theme.cs'
# Clear cache reset reference wherever it lives.
$files=Get-ChildItem $base -Filter 'MainWindow*.cs' -File
foreach($file in $files){$v=[IO.File]::ReadAllText($file.FullName);if($v.Contains('_neutralWorkspaceSurface = null;')){[IO.File]::WriteAllText($file.FullName,$v.Replace('_neutralWorkspaceSurface = null;',''))}}
$f=Join-Path $base 'MainWindow.NetworkDashboard.cs'
$t=[IO.File]::ReadAllText($f).Replace('FontSize = UseCompactSectionChrome ? 30 : 22,','FontSize = UseCompactSectionChrome ? 26 : 22,')
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.PowerNetwork.cs'
$t=[IO.File]::ReadAllText($f)
$t=[regex]::Replace($t,'        ContentStack.Children.Add\(BuildFlatSectionHeader\(\s*"DNS & MTU",[\s\S]*?\)\);\r?\n','')
[IO.File]::WriteAllText($f,$t)
$f=Join-Path $base 'MainWindow.Shell.cs'
$t=[IO.File]::ReadAllText($f).Replace('MinHeight = 34,','MinHeight = 32,').Replace('Padding = new Thickness(8, 5, 8, 5),','Padding = new Thickness(8, 3, 8, 3),')
[IO.File]::WriteAllText($f,$t)
[IO.File]::WriteAllText((Join-Path $dir 'release-note.txt'),"## 15.4.638"+[Environment]::NewLine+"- Open compact sections replace large framed workspace cards; thin separators, smaller headings and tighter navigation. Redundant DNS/MTU banner removed. Overview hardware preserved."+[Environment]::NewLine+[Environment]::NewLine)
$b=[IO.File]::ReadAllText((Join-Path $PWD 'simple-637-build.ps1')).Replace('simple-637','minimal-638').Replace('637','638').Replace('SIMPLE638','MINIMAL638').Replace('Simpler compact panels and immediate navigation saved; baseline636 three Release samples recorded.','Open compact sections replace large framed panels; tighter navigation and headings, redundant banner removed.')
[IO.File]::WriteAllText((Join-Path $PWD 'minimal-638-build.ps1'),$b)
& .\minimal-638-build.ps1
