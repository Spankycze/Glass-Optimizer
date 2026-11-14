param([string]$Version = '15.4.894')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$folder = Join-Path $root ('artifacts\native-candidate-'+$Version.Replace('.','-'))
$native = Join-Path $folder ('Spankyho_Optimizer_V'+$Version.Replace('.','_')+'_Native_Setup_TEST.exe')
$wpf = Join-Path $folder ('compact\Spankyho_Optimizer_V'+$Version.Replace('.','_')+'_Setup_Compact.exe')
$report = Join-Path $folder 'local-release-readiness.json'
$published = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Spankyho_Optimizer_V15_4_889_Setup_Compact.exe'
$sourcePayload = Join-Path $root 'installer\payload.tar.zst'
$watch = [Diagnostics.Stopwatch]::StartNew()
$details = [ordered]@{
  version = $Version
  kind = 'LOCAL_NON_INSTALLING_GATE'
  status = 'FAIL'
  verified_at_utc = [DateTime]::UtcNow.ToString('o')
  tests = [ordered]@{}
  limitations = @('Actual missing-.NET online install/UAC not executed',
    'Clean Windows VM not required for local gate',
    'Authenticode publisher signing not available')
}
try {
  $changelogHead = Get-Content -LiteralPath (Join-Path $root 'WORK_PROGRESS.md') -TotalCount 1 -Encoding UTF8
  if ($changelogHead -notmatch ('^##\s+' + [regex]::Escape($Version) + '(?:\s|$)')) {
    throw ('Changelog version mismatch before release: ' + $changelogHead)
  }
  $details.tests.current_changelog_version = 'PASS'
  $publishedChangelog = Join-Path $folder 'app\Assets\Changelog.md'
  if (!(Test-Path -LiteralPath $publishedChangelog -PathType Leaf)) {
    throw 'Published app is missing its bundled changelog'
  }
  $publishedFirst = Get-Content -LiteralPath $publishedChangelog -TotalCount 1 -Encoding UTF8
  if ($publishedFirst -notmatch ('^##\s+' + [regex]::Escape($Version) + '(?:\s|$)')) {
    throw ('Published changelog does not match app version: ' + $publishedFirst)
  }
  $details.tests.published_changelog_version = 'PASS'
  if (!(Test-Path $native -PathType Leaf) -or !(Test-Path $wpf -PathType Leaf)) {
    throw 'Current-version candidate missing'
  }
  $baseHash = '07D90C5E52905D9A4E267D0B10D46247E467D9F79A9FD062C9AE64E5172AFD93'
  $payloadHash = '51B0383EAD693F271F65E59EC2B5103FA700438D3F984CB46A7DCB4BFA4BE199'
  if ((Get-FileHash $published).Hash -ne $baseHash -or
      (Get-FileHash $sourcePayload).Hash -ne $payloadHash) {
    throw 'Protected production 889 EXE / original source payload unexpectedly modified'
  }
  $details.tests.production_889_unchanged = 'PASS'
  foreach ($stale in @('app\stale-obsolete-tool.dll','runtime-host\stale-obsolete-runtime.dll')) {
    if (Test-Path (Join-Path $folder $stale)) { throw 'Stale publish file leaked: '+$stale }
  }
  $details.tests.publish_staging_clean = 'PASS'
  $check = Start-Process -FilePath $native -ArgumentList '--verify-package' -Wait -PassThru
  if ($check.ExitCode -ne 0) { throw ('Native payload verify exit '+$check.ExitCode) }
  $details.tests.native_overlay_integrity = 'PASS'
  $pre = Start-Process -FilePath $native -ArgumentList '--preflight-package-ready' -Wait -PassThru
  if ($pre.ExitCode -notin @(0,10)) { throw ('Read-only full preflight failed exit '+$pre.ExitCode) }
  $details.tests.non_installing_preflight = if ($pre.ExitCode -eq 0) {'READY'} else {'RUNTIME_MISSING_SIMULATION_ONLY'}
  $nativeOutput = & dotnet run --project (Join-Path $root 'tools\NativeBootstrap.Tests\NativeBootstrap.Tests.csproj') -c Release --nologo
  if ($LASTEXITCODE -ne 0 -or -not ($nativeOutput -match 'NATIVE_TOTAL_TESTS_OK 44/44')) { throw 'Native regression suite failed' }
  $details.tests.native_regressions = '44/44 PASS'
  $telemetryOutput = & dotnet run --project (Join-Path $root 'tools\TelemetryCacheTests\TelemetryCacheTests.csproj') -c Release --nologo
  if ($LASTEXITCODE -ne 0 -or -not ($telemetryOutput -match 'TELEMETRY_CACHE_TESTS_OK 10/10')) {
    throw 'Hardware telemetry cache regressions failed'
  }
  $details.tests.telemetry_cache_regressions = '10/10 PASS'
  $uiOutput = & dotnet run --project (Join-Path $root 'tests\InstallerUiTests\InstallerUiTests.csproj') -c Release --no-restore
  $uiExit = $LASTEXITCODE
  $row = $uiOutput | Where-Object {$_ -match '^\{'} | Select-Object -First 1 | ConvertFrom-Json
  if ($uiExit -ne 0 -or !$row.passed -or $row.count -ne 48 -or $row.failures -ne 0) { throw 'WPF UI suite failed' }
  $details.tests.wpf_ui_regressions = '48/48 PASS'
  $upgrade = & (Join-Path $PSScriptRoot 'verify-isolated-upgrade.ps1') -Version $Version
  if (!$? -or -not ($upgrade -match 'ISOLATED_UPGRADE_PASS')) { throw 'Isolated upgrade gate failed' }
  $details.tests.upgrade_from_889_to_current = 'PASS'
  $details.native_bytes = (Get-Item $native).Length
  $details.wpf_bytes = (Get-Item $wpf).Length
  $details.native_sha256 = (Get-FileHash $native).Hash
  $details.wpf_sha256 = (Get-FileHash $wpf).Hash
  $details.publisher_signature = (Get-AuthenticodeSignature $native).Status.ToString()
  $details.status = 'LOCAL_GATE_PASS'
}
catch {
  $details.error = $_.Exception.Message
  Write-Output ('LOCAL_GATE_FAILED ' + $details.error)
}
finally {
  $watch.Stop()
  $details.duration_ms = $watch.ElapsedMilliseconds
  $json = $details | ConvertTo-Json -Depth 6
  [IO.File]::WriteAllText($report, $json, [Text.UTF8Encoding]::new($false))
  Write-Output ('LOCAL_GATE_REPORT '+$report)
  Write-Output ('LOCAL_GATE_STATUS '+$details.status+' ms='+$watch.ElapsedMilliseconds)
}
if ($details.status -ne 'LOCAL_GATE_PASS') { exit 1 }
