$ErrorActionPreference='Continue'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$tools=@('dotnet','rg','ninja','sccache','uv','pnpm')
foreach($t in $tools){
  $c=Get-Command $t -ErrorAction SilentlyContinue
  if(!$c){Write-Host ($t+'=missing');continue}
  $v=switch($t){
    'dotnet' { & dotnet --version 2>$null }
    'rg' { (& rg --version 2>$null | Select-Object -First 1) }
    'ninja' { & ninja --version 2>$null }
    'sccache' { & sccache --version 2>$null }
    'uv' { & uv --version 2>$null }
    'pnpm' { & pnpm --version 2>$null }
  }
  Write-Host ($t+'='+$v)
}
if(Get-Command sccache -ErrorAction SilentlyContinue){
  Write-Host '--- SCCACHE ---'
  & sccache --show-stats
}
