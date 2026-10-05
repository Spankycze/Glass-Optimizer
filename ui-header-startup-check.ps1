$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$exe=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$p=Start-Process $exe -ArgumentList @('--open-section=network','--theme-render-smoke') -PassThru -WorkingDirectory (Split-Path $exe)
if(-not $p.WaitForExit(120000)){Stop-Process $p.Id -Force;throw 'Direct-network theme smoke timeout'}
if($p.ExitCode -ne 0){throw "Direct-network theme smoke failed $($p.ExitCode)"}
Write-Output 'DIRECT_NETWORK_THEME_SMOKE_OK'