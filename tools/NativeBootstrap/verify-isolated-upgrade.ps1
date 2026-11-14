param([string]$Version = '15.4.894', [string]$PriorVersion = '15.4.1473')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$older = Join-Path $root ('artifacts\release\Spankyho_Optimizer_V' + $PriorVersion.Replace('.','_') + '_Setup_Compact.exe')
$newer = Join-Path $root ('artifacts\native-candidate-' + $Version.Replace('.','-') +
  '\compact\Spankyho_Optimizer_V' + $Version.Replace('.','_') + '_Setup_Compact.exe')
if (!(Test-Path -LiteralPath $older -PathType Leaf) -or !(Test-Path -LiteralPath $newer -PathType Leaf)) {
  throw 'Required old/new compact test installer missing'
}
$priorProductVersion = (Get-Item $older).VersionInfo.ProductVersion
$currentVersion = (Get-Item $newer).VersionInfo.ProductVersion
if (!$priorProductVersion.StartsWith($PriorVersion,[StringComparison]::Ordinal) -or
    !$currentVersion.StartsWith($Version,[StringComparison]::Ordinal)) {
  throw ('Wrong real upgrade versions: ' + $priorProductVersion + ' -> ' + $currentVersion)
}
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('spanky-upgrade-' + $PriorVersion.Replace('.','-') + '-current-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $sandbox 'application'
New-Item -Path $sandbox -ItemType Directory -Force | Out-Null
try {
  $olderSetup = Start-Process -FilePath $older -ArgumentList @('--test-install', ('"' + $install + '"')) -Wait -PassThru
  if ($olderSetup.ExitCode -ne 0) { throw ('Previous release extraction exit ' + $olderSetup.ExitCode) }
  $app = Join-Path $install 'SpankyhoOptimizer.exe'
  if (!(Test-Path $app -PathType Leaf) -or
      !((Get-Item $app).VersionInfo.ProductVersion.StartsWith($PriorVersion,[StringComparison]::Ordinal))) {
    throw ('Previous release extraction did not contain v' + $PriorVersion)
  }
  Write-Output ('PREVIOUS_' + $PriorVersion.Replace('.','_') + '_EXTRACT_OK')
  $upgrade = Start-Process -FilePath $newer -ArgumentList @('--test-transactional-install', ('"' + $install + '"')) -Wait -PassThru
  if ($upgrade.ExitCode -ne 0) { throw ('Actual upgrade to ' + $Version + ' exit ' + $upgrade.ExitCode) }
  foreach ($name in @('SpankyhoOptimizer.exe','SpankyhoOptimizer.RuntimeHost.exe')) {
    $path = Join-Path $install $name
    if (!(Test-Path $path -PathType Leaf) -or
        !((Get-Item $path).VersionInfo.ProductVersion.StartsWith($Version,[StringComparison]::Ordinal))) {
      throw ('Post-upgrade wrong component/version: ' + $name)
    }
    Write-Output ('UPGRADE_COMPONENT_OK ' + $name + ' ' + $Version)
  }
  if (@(Get-ChildItem $sandbox -Directory -Force -Filter '.SpankyhoOptimizer-*').Count -ne 0) {
    throw 'Transaction backup/staging directory left behind'
  }
  Write-Output ('REAL_' + $PriorVersion.Replace('.','_') + '_TO_' + $Version.Replace('.','_') + '_ISOLATED_UPGRADE_PASS')
}
finally {
  Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
