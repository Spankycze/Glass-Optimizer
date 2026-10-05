param([switch]$SkipRelease,[switch]$Force)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build-state.ps1')
if(-not $Force -and -not $SkipRelease -and (Test-SpankyGateFresh)){
  Save-SpankyStructuralState
  Write-Host 'FULL_TEST_CACHE_HIT source already passed full gate'
  return
}
$total=[Diagnostics.Stopwatch]::StartNew()
$src=Join-Path $root 'src\SpankyhoOptimizer'
$app=Join-Path $src 'SpankyhoOptimizer.csproj'

if($SkipRelease){
  $devArgs=@{}
  if($Force){$devArgs['ForceBuild']=$true}
  & (Join-Path $root 'build-dev.ps1') @devArgs
  if($LASTEXITCODE -ne 0){throw 'Dev build failed.'}
  $testArgs=@{ NoBuild=$true; Full=$true }
}else{
  $assets=Join-Path $src 'obj\project.assets.json'
  $restoreState=Join-Path $root 'artifacts\.cache\release-app-restore.sha256'
  $versionProps=Join-Path $root 'Directory.Build.props'
  $restoreHash=Get-SpankyRestoreInputHash @(
    $app,
    (Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'),
    (Join-Path $root 'NuGet.Config'),
    $versionProps
  ) $versionProps
  $needsRestore=-not(Test-Path $assets) -or -not(Test-Path $restoreState) -or ((Get-Content $restoreState -Raw -ErrorAction SilentlyContinue).Trim() -ne $restoreHash)
  if($needsRestore){
    dotnet restore $app -r win-x64 --nologo | Out-Host
    if($LASTEXITCODE -ne 0){throw 'Release restore failed.'}
    Set-Content $restoreState $restoreHash -NoNewline
    Write-Host 'RELEASE_RESTORE_REFRESHED'
  } else {
    Write-Host 'RELEASE_RESTORE_CACHE_HIT'
  }
  dotnet build $app -c Release -r win-x64 --no-restore -m -p:RunAnalyzersDuringBuild=true -p:WindowsAppSDKSelfContained=true -p:SelfContained=false | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Release analyzer build failed.'}
  Write-Host 'RELEASE_ANALYZER_OK'
  $releaseExe=Join-Path $src 'bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
  if(-not(Test-Path $releaseExe)){throw 'Release EXE nebyl nalezen.'}
  $testArgs=@{ NoBuild=$true; Full=$true; ExePath=$releaseExe }
}
if($Force){$testArgs['ForceTests']=$true}
& (Join-Path $root 'build-test.ps1') @testArgs
if($LASTEXITCODE -ne 0){throw 'Runtime smoke suite failed.'}

& (Join-Path $root 'fault-audit.ps1')
if($LASTEXITCODE -ne 0){throw 'Fault injection gate failed.'}
Write-Host 'FAULT_INJECTION_GATE_OK'

& (Join-Path $root 'run-fuzz.ps1') -Mode smoke
if($LASTEXITCODE -ne 0){throw 'Fuzz smoke gate failed.'}
Write-Host 'FUZZ_GATE_OK'

& (Join-Path $root 'api-compat-gate.ps1')
if($LASTEXITCODE -ne 0){throw 'API compatibility gate failed.'}
Write-Host 'API_COMPAT_GATE_OK'

& (Join-Path $root 'sdk-upgrade-gate.ps1')
if($LASTEXITCODE -ne 0){throw 'SDK/Windows App SDK upgrade gate failed.'}
Write-Host 'SDK_UPGRADE_GATE_OK'

$criticalExe=if($SkipRelease){Join-Path $src 'bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'}else{$releaseExe}
if(-not(Test-Path $criticalExe)){throw 'Critical leak gate EXE nebyl nalezen.'}

& (Join-Path $root 'visual-regression.ps1') -ExePath $criticalExe
if($LASTEXITCODE -ne 0){throw 'Visual regression gate failed.'}
Write-Host 'VISUAL_REGRESSION_GATE_OK'

$criticalLeak=Start-Process -FilePath $criticalExe -ArgumentList '--critical-leak-smoke' -Wait -PassThru
if($criticalLeak.ExitCode -ne 0){throw "Critical leak gate failed: $($criticalLeak.ExitCode)"}
Write-Host 'CRITICAL_LEAK_GATE_OK'

$files=@(
  Get-ChildItem $src -Recurse -File -Include *.cs,*.xaml,*.ps1 | Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'}
  Get-ChildItem (Join-Path $root 'installer') -Recurse -File -Include *.cs,*.xaml,*.ps1,*.iss | Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'}
  Get-ChildItem $root -File -Include *.ps1
)
$strictUtf8=[Text.UTF8Encoding]::new($false,$true)
$bad=@()
foreach($f in $files | Sort-Object FullName -Unique){
  try {
    $t=$strictUtf8.GetString([IO.File]::ReadAllBytes($f.FullName))
    if($t.IndexOf([char]0xFFFD) -ge 0){$bad+=$f.FullName}
  } catch { $bad+=$f.FullName }
}
if($bad.Count -gt 0){
  $bad | Select-Object -Unique | Select-Object -First 20 | Out-Host
  throw 'Strict UTF-8 audit failed.'
}
Write-Host ('STRICT_UTF8_AUDIT_OK files={0}' -f (($files | Sort-Object FullName -Unique).Count))
$legacyPatterns=@('Adaptive Engine','BuildAdaptivePageAsync','ShowSectionAsync\(\"adaptive\"\)','AddNav\(\"Adaptive Engine\"')
$legacy=$files | Where-Object {$_.FullName -notmatch '\\ThirdParty\\'} | Select-String -Pattern $legacyPatterns
if($legacy){$legacy | Select-Object -First 20 | Out-Host; throw 'Legacy Adaptive reference found.'}
$dupAudit=Join-Path $root 'audit_duplicates.py'
if(Test-Path $dupAudit){
  $python=Get-Command python.exe -ErrorAction SilentlyContinue
  $py=Get-Command py.exe -ErrorAction SilentlyContinue
  if($python -and $python.Source -notmatch '\\WindowsApps\\'){
    & $python.Source $dupAudit | Out-Host
  } elseif($py) {
    & $py.Source -3 $dupAudit | Out-Host
  } else {
    throw 'Tweak duplicate audit requires Python or the Python launcher.'
  }
  if($LASTEXITCODE -ne 0){throw 'Tweak duplicate audit failed.'}
}
Write-Host 'SOURCE_AUDIT_OK'
if(-not $SkipRelease){
  Save-SpankyGateState
  Save-SpankyStructuralState
}
$total.Stop(); Write-Host ('FULL_TEST_OK total={0:n2}s' -f $total.Elapsed.TotalSeconds)
