$ErrorActionPreference='Continue'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$tools=@('dotnet','rg','ninja','sccache','uv','pnpm','hyperfine','watchexec')
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
    'hyperfine' { & hyperfine --version 2>$null }
    'watchexec' { (& watchexec --version 2>$null | Select-Object -First 1) }
  }
  Write-Host ($t+'='+$v)
}
if(Get-Command sccache -ErrorAction SilentlyContinue){
  Write-Host '--- SCCACHE ---'
  & sccache --show-stats
}
Write-Host ('DEV_DRIVE_ACTIVE='+$(if($env:SPANKY_DEV_DRIVE_ACTIVE){$env:SPANKY_DEV_DRIVE_ACTIVE}else{'none'}))
Write-Host ('SCCACHE_DIR='+$env:SCCACHE_DIR)
