param([switch]$Force)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=$PSScriptRoot
$stateDir=Join-Path $root '.context-refresh'
New-Item -ItemType Directory -Force $stateDir | Out-Null
$stampFile=Join-Path $stateDir 'last-head.txt'
$head=if(Test-Path (Join-Path $root '.git')){git -c core.autocrlf=false -C $root rev-parse HEAD}else{'nogit'}
$old=if(Test-Path $stampFile){(Get-Content -Raw $stampFile).Trim()}else{''}
$files=@()
if($old -and $old -ne 'nogit' -and $head -ne 'nogit'){
  $files+=@(git -c core.autocrlf=false -C $root diff --name-only $old $head 2>$null)
}
if($head -ne 'nogit'){
  $files+=@(git -c core.autocrlf=false -C $root diff --name-only HEAD 2>$null)
}
$files=@($files | Where-Object {$_} | Sort-Object -Unique)
$structural=$Force -or -not $old -or ($files | Where-Object {$_ -match '\.(csproj|sln|props|targets|json|toml|js|ts|cs|cpp|h)$'}).Count -gt 0
if(-not $structural){
  Write-Host 'REFRESH_SKIPPED=no-structural-change'
  exit 0
}
Write-Host ('REFRESH_FILES='+$files.Count)
if(Get-Command atlas -ErrorAction SilentlyContinue){
  atlas $root --budget 2048 | Set-Content -Encoding utf8 (Join-Path $root 'atlas-map.md')
  Write-Host 'ATLAS_REFRESHED'
}
$oldEA=$ErrorActionPreference
$ErrorActionPreference='Continue'
$calmOut=& 'C:\Users\spnka\AppData\Roaming\npm\calm-mcp.cmd' index --project-root $root 2>&1
$calmCode=$LASTEXITCODE
$ErrorActionPreference=$oldEA
$calmOut | Select-Object -Last 8
if($calmCode -ne 0){throw ('CALM_REFRESH_FAIL exit='+$calmCode)}
Set-Content -LiteralPath $stampFile -Value $head -Encoding ascii
Write-Host 'SMART_REFRESH_OK'