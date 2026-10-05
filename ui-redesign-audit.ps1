$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$src='src\SpankyhoOptimizer'
$work='artifacts\ui-neutral-redesign'
$report=& {
"PROTECTED"
$entries=Get-Content "$work\protected.json" -Raw | ConvertFrom-Json
foreach($entry in $entries){
 $hash=(Get-FileHash $entry.path).Hash
 if($hash -eq $entry.hash){continue}
 if($entry.path.EndsWith('MainWindow.OverviewDashboard.cs')){
  $bytes=[IO.File]::ReadAllBytes($entry.path)
  $text=[Text.Encoding]::UTF8.GetString($bytes)
  $patched='RunWithOverviewPalette(PopulateCustomTitleBarContent);'
  if(([regex]::Matches($text,[regex]::Escape($patched))).Count -ne 1){throw 'Unexpected titlebar scope diff'}
  $restored=$text.Replace($patched,'PopulateCustomTitleBarContent();')
  $sha=[Security.Cryptography.SHA256]::Create()
  $normalizedHash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($restored))).Replace('-','')
  $sha.Dispose()
  if($normalizedHash -eq $entry.hash){'OVERVIEW_ONLY_DIFF=one titlebar palette scope call';continue}
 }
 throw "Protected file changed beyond allowed titlebar scope: $($entry.path)"
}
'PROTECTED_HASHES_OK except explicit titlebar palette scope'
"OVERVIEW_SHARED"
Select-String -Path "$src\MainWindow.Overview*.cs" -Pattern 'SectionCard|SectionBorder|BuildDashboardCard|QuickButton|AccentBrush|SurfaceBrush' | ForEach-Object {"$($_.Filename):$($_.LineNumber):$($_.Line.Trim())"}
"PREWARM_SCOPE"
Select-String -Path "$src\MainWindow*Prewarm*.cs" -Pattern '_themeSection|_section =|BuildDashboard|Ensure.*Card' | ForEach-Object {"$($_.Filename):$($_.LineNumber):$($_.Line.Trim())"} | Select-Object -First 30
"SMOKE_FLAGS"
Select-String -Path "$src\App.cs","$src\Program.cs","$src\App.Smoke*.cs" -Pattern '--.*smoke' -ErrorAction SilentlyContinue | ForEach-Object {$_.Line.Trim()} | Select-Object -First 65
} | Out-String -Width 200
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "$work\audit.txt"),$report)
Write-Output 'UI_AUDIT_OK'
