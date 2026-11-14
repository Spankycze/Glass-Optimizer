param([string]$Version = '15.4.899', [string]$ExpectedInstalledVersion = '15.4.898')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$folder=Join-Path $root ('artifacts\native-candidate-'+$Version.Replace('.','-'))
$gate=Join-Path $folder 'local-release-readiness.json'
$native=Join-Path $folder ('Spankyho_Optimizer_V'+$Version.Replace('.','_')+'_Native_Setup_TEST.exe')
$wpf=Join-Path $folder ('compact\Spankyho_Optimizer_V'+$Version.Replace('.','_')+'_Setup_Compact.exe')
$installed=Join-Path $env:ProgramFiles 'Spankyho Optimizer\SpankyhoOptimizer.exe'
$desktop=Join-Path ([Environment]::GetFolderPath('Desktop')) ('Spankyho_Optimizer_V'+$Version.Replace('.','_')+'_Setup_RC.exe')
$reportPath=Join-Path $folder 'real-installation-verification.json'
$log=[ordered]@{version=$Version;status='FAIL';tested_at_utc=[DateTime]::UtcNow.ToString('o')}
function Fingerprint([string]$path) {
 if(Test-Path -LiteralPath $path -PathType Leaf){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
 if(Test-Path -LiteralPath $path -PathType Container){
  $rows=@(Get-ChildItem -LiteralPath $path -File -Recurse | Sort-Object FullName |
    ForEach-Object { $_.FullName.Substring($path.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
  return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join [Environment]::NewLine))))
 }
 return 'MISSING'
}
function StateSnapshot {
 $data=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SpankyhoOptimizer'
 $theme=$null
 try {$theme=(Get-ItemProperty -Path 'HKCU:\Software\SpankyhoOptimizer\UI' -Name Theme -ErrorAction Stop).Theme} catch {}
 return [ordered]@{
  uiState=Fingerprint (Join-Path $data 'ui-state.json')
  featureProfile=Fingerprint (Join-Path $data 'user-feature-profile.json')
  hardwareCache=Fingerprint (Join-Path $data 'HardwareCache')
  theme=[string]$theme
 }
}
try {
 $g=Get-Content -LiteralPath $gate -Raw | ConvertFrom-Json
 if($g.version -ne $Version -or $g.status -ne 'LOCAL_GATE_PASS' -or
    $g.tests.current_changelog_version -ne 'PASS' -or
    $g.tests.published_changelog_version -ne 'PASS'){throw 'Release gate or bundled changelog not green'}
 if(!(Test-Path -LiteralPath $native) -or !(Test-Path -LiteralPath $wpf)){throw 'Candidate missing'}
 $nativeHash=(Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash
 if($nativeHash -ne $g.native_sha256){throw 'Candidate changed after gate'}
 $before=StateSnapshot
 $log.settings_before=$before
 $log.installed_before=(Get-Item -LiteralPath $installed).VersionInfo.ProductVersion
 if(-not $log.installed_before.StartsWith($ExpectedInstalledVersion,[StringComparison]::Ordinal)){throw ('Unexpected installed version: '+$log.installed_before)}
 Copy-Item -LiteralPath $native -Destination $desktop -Force
 $log.desktop_installer=$desktop
 if((Get-FileHash -LiteralPath $desktop).Hash -ne $nativeHash){throw 'Desktop RC installer hash mismatch'}
 $proc=Start-Process -FilePath $wpf -ArgumentList '--silent-install' -Wait -PassThru
 $log.installer_exit=$proc.ExitCode
 if($proc.ExitCode -ne 0){throw ('Installer exited '+$proc.ExitCode)}
 $after=StateSnapshot
 $log.settings_after=$after
 $log.installed_after=(Get-Item -LiteralPath $installed).VersionInfo.ProductVersion
 if(-not $log.installed_after.StartsWith($Version,[StringComparison]::Ordinal)){throw 'New installed binary version mismatch'}
 foreach($field in @('featureProfile','hardwareCache','theme')){
  if($before[$field] -ne $after[$field]){throw ('User setting changed during install: '+$field)}
 }
 $log.ui_state_unchanged=($before.uiState -eq $after.uiState)
 $log.settings_preserved='PASS'
 $log.status='INSTALL_PASS'
}
catch { $log.error=$_.Exception.Message; Write-Output ('INSTALL_FAIL '+$log.error) }
finally {
 [IO.File]::WriteAllText($reportPath,($log|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
 Write-Output ('INSTALL_REPORT '+$reportPath)
 Write-Output ('INSTALL_STATUS '+$log.status)
}
if($log.status -ne 'INSTALL_PASS'){exit 1}
