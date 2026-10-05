$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$utf8=[Text.UTF8Encoding]::new($false)
$f='src\SpankyhoOptimizer\MainWindow.cs';$p=Join-Path $PSScriptRoot $f
Copy-Item $p 'artifacts\black-ui-621\before\MainWindow.cs' -Force
$t=[IO.File]::ReadAllText($p)
$t=$t.Replace('const Visibility targetHeroVisibility = Visibility.Collapsed;','Visibility targetHeroVisibility = key == "overview" ? Visibility.Visible : Visibility.Collapsed;')
$t=$t.Replace('key == "overview" ? "Spankyho Optimizer" : sectionDefinition.Title','key == "overview" ? "Přehled" : sectionDefinition.Title')
$t=$t.Replace('Rychlejší systém. Lepší výkon. Vše na jednom místě.','Hardware, živé využití a stav systému')
[IO.File]::WriteAllText($p,$t,$utf8)
$f='src\SpankyhoOptimizer\MainWindow.Shell.cs';$p=Join-Path $PSScriptRoot $f
$t=[IO.File]::ReadAllText($p).Replace('PageTitle.Text = section.Title;','PageTitle.Text = _section == "overview" ? "Přehled" : section.Title;').Replace('PageSubtitle.Text = section.Subtitle;','PageSubtitle.Text = _section == "overview" ? "Hardware, živé využití a stav systému" : section.Subtitle;').Replace('new Thickness(273, 0, 28, 16)','new Thickness(242, 0, 24, 16)')
[IO.File]::WriteAllText($p,$t,$utf8)
$f='src\SpankyhoOptimizer\MainWindow.VisualQa.cs';$p=Join-Path $PSScriptRoot $f
$t=[IO.File]::ReadAllText($p).Replace('? "Spankyho Optimizer"', '? "Přehled"')
[IO.File]::WriteAllText($p,$t,$utf8)
$f='build-release.ps1';$p=Join-Path $PSScriptRoot $f
Copy-Item $p 'artifacts\black-ui-621\before\build-release.ps1' -Force
$t=[IO.File]::ReadAllText($p).Replace('$sidebarIconFiles.Count -ne 21','$sidebarIconFiles.Count -ne 24').Replace('expected 21 SVG assets','expected 24 SVG assets')
[IO.File]::WriteAllText($p,$t,$utf8)
$note=@'

## 15.4.621 — kompletní černé UI
- Nový Přehled a nativní header bez velkého rastrového banneru.
- Kompaktní měřáky se skutečnými daty a grafy, nové systémové a provozní karty.
- Jednotný černý vzhled všech sekcí, jemná hloubka a kompaktní navigace.
- 24 nových barevných vektorových ikon inspirovaných hardwarem, samostatný ventilátor, RAM a kurzor.
- Kratší přechody stránek s menším posunem; měření rychlosti po ověření buildu.
- Oprávnění: uživatel 4. října výslovně zrušil zachování starého Přehledu/headeru/ikon a chce změnit vše.
'@
[IO.File]::AppendAllText((Join-Path $PSScriptRoot 'WORK_PROGRESS.md'),$note,$utf8)
Write-Output 'BLACK_UI_FINISH_OK'
