$ErrorActionPreference='Stop'
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
function Tool([string]$name,[string[]]$fallback=@()){
  $c=Get-Command $name -ErrorAction SilentlyContinue
  if($c){return $c.Source}
  foreach($p in $fallback){if(Test-Path $p){return $p}}
  return $null
}
$obj=[ordered]@{
 generated=(Get-Date).ToString('o');elevated=$admin
 dotnetTrace=Tool 'dotnet-trace'
 dotnetCounters=Tool 'dotnet-counters'
 wpr=Tool 'wpr'
 wpa=Tool 'wpa' @('C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe')
 perfView=Tool 'PerfView' @('C:\Tools\PerfView\PerfView.exe')
 ripgrep=Tool 'rg'
}
$out=Join-Path $PSScriptRoot '.perf\latest\tools.json'
New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent)|Out-Null
[pscustomobject]$obj|ConvertTo-Json -Depth 3|Set-Content $out -Encoding utf8
Write-Output ("PERF_TOOLS admin={0} trace={1} counters={2} wpr={3} wpa={4} perfview={5} rg={6}" -f $admin,[bool]$obj.dotnetTrace,[bool]$obj.dotnetCounters,[bool]$obj.wpr,[bool]$obj.wpa,[bool]$obj.perfView,[bool]$obj.ripgrep)
