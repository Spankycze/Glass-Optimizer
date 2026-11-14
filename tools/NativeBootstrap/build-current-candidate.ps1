
param([string]$Version = '15.4.894')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$current = ([regex]::Match([IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props')), '<SpankyVersion>([^<]+)</SpankyVersion>')).Groups[1].Value
if ($current -ne $Version) { throw ('Current source version mismatch: ' + $current + ' != ' + $Version) }
$changelogHead = Get-Content -LiteralPath (Join-Path $root 'WORK_PROGRESS.md') -TotalCount 1 -Encoding UTF8
if ($changelogHead -notmatch ('^##\s+' + [regex]::Escape($Version) + '(?:\s|$)')) {
  throw ('Current changelog version mismatch: expected first heading ' + $Version + ', found ' + $changelogHead)
}
Write-Output ('CANDIDATE_CHANGELOG_VERSION_OK ' + $Version)
$folder = Join-Path $root ('artifacts\native-candidate-' + $Version.Replace('.','-'))
$appOut = Join-Path $folder 'app'
$hostOut = Join-Path $folder 'runtime-host'
$payloadOut = Join-Path $folder 'candidate-payload.tar.zst'
$compactOut = Join-Path $folder 'compact'
$nativeOut = Join-Path $folder ('Spankyho_Optimizer_V' + $Version.Replace('.','_') + '_Native_Setup_TEST.exe')
$oldPayload = Join-Path $root 'installer\payload.tar.zst'
# Fresh publish roots on every run: do not accidentally include stale artifacts
# from an earlier source version or failed/partial build. Limit deletion strictly
# to candidate-owned directories, never the user's installation or release files.
New-Item -Path $folder -ItemType Directory -Force | Out-Null
foreach ($staging in @($appOut,$hostOut,$compactOut)) {
  if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
  }
  New-Item -ItemType Directory -Path $staging -Force | Out-Null
}
Write-Output 'CANDIDATE_STAGING_CLEAN_OK'
if (!(Test-Path $oldPayload -PathType Leaf)) { throw 'Existing WPF installer payload missing' }
$oldHash = (Get-FileHash $oldPayload -Algorithm SHA256).Hash
Write-Output ('CURRENT_VERSION_OK ' + $Version + ' previousPayloadSha256=' + $oldHash)
$app = Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$appArgs = @('publish',$app,'-c','Release','-r','win-x64','--self-contained','false','--no-restore','-m','-o',$appOut,
 '-p:WindowsAppSDKSelfContained=true','-p:PublishReadyToRun=false','-p:DebugType=None','-p:DebugSymbols=false')
& dotnet @appArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Current app candidate publish failed' }
$versionApp = (Get-Item (Join-Path $appOut 'SpankyhoOptimizer.exe')).VersionInfo.ProductVersion
if (!$versionApp -or !$versionApp.StartsWith($Version, [StringComparison]::Ordinal)) {
  throw ('Current app product version mismatch: ' + $versionApp)
}
Write-Output ('CANDIDATE_APP_PUBLISH_OK version=' + $versionApp)
$runtimeHostProject = Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'
$hostArgs = @('publish',$runtimeHostProject,'-c','Release','-r','win-x64','--self-contained','false','-m','-o',$hostOut,'--nologo','-v:q',
 '-p:RunAnalyzers=false','-p:RunAnalyzersDuringBuild=false')
& dotnet @hostArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Runtime host candidate publish failed' }
foreach($name in @('SpankyhoOptimizer.RuntimeHost.exe','SpankyhoOptimizer.RuntimeHost.dll',
  'SpankyhoOptimizer.RuntimeHost.deps.json','SpankyhoOptimizer.RuntimeHost.runtimeconfig.json')) {
  if (!(Test-Path (Join-Path $hostOut $name))) { throw ('Runtime Host missing file ' + $name) }
  Copy-Item (Join-Path $hostOut $name) (Join-Path $appOut $name) -Force
}
$hostVersion = (Get-Item (Join-Path $appOut 'SpankyhoOptimizer.RuntimeHost.exe')).VersionInfo.ProductVersion
if (!$hostVersion -or !$hostVersion.StartsWith($Version, [StringComparison]::Ordinal)) {
  throw ('Runtime Host version mismatch: ' + $hostVersion)
}
$hostProbeExe = Join-Path $hostOut 'SpankyhoOptimizer.RuntimeHost.exe'
$hostProbe = Start-Process -FilePath $hostProbeExe -ArgumentList '--startup-probe' -Wait -PassThru
if ($hostProbe.ExitCode -ne 0) {
  throw ('RuntimeHost read-only startup probe failed exit=' + $hostProbe.ExitCode)
}
Write-Output 'RUNTIMEHOST_STARTUP_PROBE_OK fanHardwareUntouched=true'
foreach($name in @('App.xbf','SpankyhoOptimizer.pri')) {
  if (!(Test-Path (Join-Path $appOut $name))) {
    $candidate = Join-Path $root ('src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\' + $name)
    if (!(Test-Path $candidate)) { throw ('Missing WinUI runtime artifact ' + $name) }
    Copy-Item $candidate (Join-Path $appOut $name)
  }
}
Remove-Item (Join-Path $appOut 'createdump.exe') -Force -ErrorAction SilentlyContinue
$packer = Join-Path $root 'tools\PayloadPacker\PayloadPacker.csproj'
& dotnet run --project $packer -c Release -- pack $appOut $payloadOut $Version 9 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Candidate Zstd pack failed' }
& dotnet run --project $packer -c Release -- verify $payloadOut | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Candidate Zstd verify failed' }
# Two independent compression runs of identical inputs must have identical SHA256.
# This protects against nondeterministic random PAX header names / timestamps.
$repeat = Join-Path $folder 'candidate-repack-determinism.tar.zst'
try {
  $packerDll = Join-Path $root 'tools\PayloadPacker\bin\Release\net10.0\PayloadPacker.dll'
  & dotnet $packerDll pack $appOut $repeat $Version 9 | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'Second Zstd packaging attempt failed' }
  $firstHash = (Get-FileHash $payloadOut -Algorithm SHA256).Hash
  $secondHash = (Get-FileHash $repeat -Algorithm SHA256).Hash
  if ($firstHash -ne $secondHash) { throw ('Zstd non-deterministic packaging: ' + $firstHash + ' vs ' + $secondHash) }
  Write-Output ('CANDIDATE_ZSTD_REPRODUCIBLE_OK sha256=' + $firstHash)
} finally {
  Remove-Item $repeat -Force -ErrorAction SilentlyContinue
}
Write-Output ('CANDIDATE_PAYLOAD_OK bytes=' + (Get-Item $payloadOut).Length)
# The compact builder accepts SpankyInstallerPayloadPath and embeds this
# candidate archive directly. The original source payload is NEVER replaced.
& (Join-Path $root 'build-installer-compact.ps1') -Version $Version -PayloadPath $payloadOut -OutputDir $compactOut | Out-Host
if (!$?) { throw 'Candidate compact installer failed' }
$originalHash = (Get-FileHash $oldPayload -Algorithm SHA256).Hash
if ($originalHash -ne $oldHash) { throw 'CRITICAL: existing source payload changed during build' }
Write-Output 'ORIGINAL_SOURCE_PAYLOAD_UNTOUCHED_OK'
$wpf = Join-Path $compactOut ('Spankyho_Optimizer_V' + $Version.Replace('.','_') + '_Setup_Compact.exe')
if (!(Test-Path $wpf -PathType Leaf)) { throw 'Current-version WPF candidate missing' }
# True unpacked payload test: verify both main and RuntimeHost binaries are current,
# and exercise WPF transactional install/upgrade/recovery only inside a test folder.
$transactionFixture = Join-Path $folder 'transaction-test-fixture'
try {
  $test = Start-Process -FilePath $wpf -ArgumentList @('--test-release-bundle', ('"' + $transactionFixture + '"')) -PassThru -Wait
  if ($test.ExitCode -ne 0) { throw ('Current WPF transactional test failed exit=' + $test.ExitCode) }
  foreach($name in @('SpankyhoOptimizer.exe','SpankyhoOptimizer.RuntimeHost.exe')) {
    $component = Join-Path (Join-Path $transactionFixture 'extract') $name
    if (!(Test-Path $component -PathType Leaf)) { throw ('Missing extracted product component: ' + $name) }
    $v = (Get-Item $component).VersionInfo.ProductVersion
    if (!$v -or !$v.StartsWith($Version,[StringComparison]::Ordinal)) {
      throw ('Actual extracted product version mismatch: ' + $name + ' ' + $v)
    }
    Write-Output ('EXTRACTED_CURRENT_VERSION_OK ' + $name + ' ' + $v)
  }
  Write-Output 'CURRENT_WPF_TRANSACTION_GATE_OK'
} finally {
  Remove-Item $transactionFixture -Recurse -Force -ErrorAction SilentlyContinue
}
& (Join-Path $PSScriptRoot 'build-overlay-prototype.ps1') -InstallerPath $wpf -OutputPath $nativeOut -ExpectedVersion $Version | Out-Host
if (!$?) { throw 'Current-version native overlay gate failed' }
# Exercise the actual 889 -> current transaction inside a disposable TEMP folder.
# No real Windows installer registration, services, Start-menu or user files touched.
& (Join-Path $PSScriptRoot 'verify-isolated-upgrade.ps1') -Version $Version | Out-Host
if (!$?) { throw 'Isolated real-upgrade regression gate failed' }
Write-Output 'CURRENT_ISOLATED_UPGRADE_GATE_OK'
$nativeSize = (Get-Item $nativeOut).Length
$wpfSize = (Get-Item $wpf).Length
Write-Output ('CURRENT_NATIVE_CANDIDATE_OK version=' + $Version + ' combinedBytes=' + $nativeSize +
  ' wpfBytes=' + $wpfSize + ' sha256=' + (Get-FileHash $nativeOut).Hash + ' file=' + $nativeOut)
# Remove obsolete diagnostics and pre-refactor backup files created by prior
# development runs; these are disposable and not inputs to future candidates.
$obsolete = @('previous-production-payload.backup.zst',
  'determinism-a.tar.zst','determinism-b.tar.zst')
$freed = 0L
foreach ($name in $obsolete) {
  $file = Join-Path $folder $name
  if (Test-Path -LiteralPath $file -PathType Leaf) {
    $freed += (Get-Item -LiteralPath $file).Length
    Remove-Item -LiteralPath $file -Force
  }
}
Write-Output ('OBSOLETE_TEST_ARCHIVES_REMOVED bytes=' + $freed)
Write-Output 'TEST_ONLY_NOT_RELEASED'
