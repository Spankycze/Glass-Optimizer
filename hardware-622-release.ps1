$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PWD 'artifacts\hardware-622'
& .\ai-workflow.ps1 -Action start-task -TaskId 'hardware-622' -Title 'Large hardware pictures in Overview' -Area 'winui' -ValidationRequired 'build-test','visual-capture','visual-size','full-release'
$exe=(& .\resolve-debug-exe.ps1 | Select-Object -Last 1)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $artifactDir 'build-test.log')
if(!$?){throw 'Build tests failed'}
foreach($flag in @('--visual-size-smoke','--theme-render-smoke')){
 $p=Start-Process $exe -ArgumentList $flag -PassThru
 if(!$p.WaitForExit(120000)){throw "Timed out $flag"}
 if($p.ExitCode -ne 0){throw "Failed $flag exit=$($p.ExitCode)"}
 Write-Output ("HARDWARE_GATE_OK "+$flag)
}
Copy-Item 'C:\ProgramData\SpankyhoOptimizer\visual-current' (Join-Path $artifactDir 'screens') -Recurse -Force
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
[IO.File]::WriteAllText($progress,[IO.File]::ReadAllText((Join-Path $artifactDir 'release-note.txt'))+[IO.File]::ReadAllText($progress))
& .\build-release.ps1 *> (Join-Path $artifactDir 'release.log')
if(!$?){throw 'Release failed'}
if([IO.File]::ReadAllText((Join-Path $artifactDir 'release.log')) -notmatch 'RELEASE_OK|CACHE_HIT'){throw 'Release marker missing'}
$src=Join-Path $PWD 'artifacts\release\Spankyho_Optimizer_V15_4_622_Setup_Compact.exe'
if(!(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.622*'){throw 'Installer version mismatch'}
$dest=Join-Path ([Environment]::GetFolderPath('Desktop')) (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop hash mismatch'}
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe'|Where-Object {$_.FullName -ne $dest}|Remove-Item -Force
[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8
$installText=[IO.File]::ReadAllText((Join-Path $PWD 'black-ui-621-install.ps1')).Replace('black-ui-621','hardware-622').Replace('15.4.621','15.4.622')
[IO.File]::WriteAllText((Join-Path $PWD 'hardware-622-install.ps1'),$installText)
& .\hardware-622-install.ps1
if(!$?){throw 'Install verification failed'}
& .\ai-workflow.ps1 -Action complete-task -TaskId 'hardware-622'
& .\update-project-state.ps1 -Reason 'hardware-622-installed-verified'
Write-Output 'HARDWARE622_RELEASE_INSTALLED_OK'
