param([switch]$After)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=$PSScriptRoot
$rows=@()

function Measure-Step([string]$Name,[scriptblock]$Action){
  $before=@(Get-Process powershell,pwsh,cmd,dotnet -ErrorAction SilentlyContinue).Count
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $text=''
  $exitCode=0
  try{$text=(& $Action 2>&1 | Out-String)}catch{$exitCode=1;$text=$_.Exception.Message}
  $sw.Stop()
  $afterCount=@(Get-Process powershell,pwsh,cmd,dotnet -ErrorAction SilentlyContinue).Count
  $rows+=[pscustomobject]@{
    name=$Name
    durationMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
    processDelta=[math]::Max(0,$afterCount-$before)
    consoleLines=@($text -split "\r?\n"|Where-Object{$_}).Count
    consoleBytes=[Text.Encoding]::UTF8.GetByteCount($text)
    exitCode=$exitCode
  }
}

Measure-Step 'state-light' { & (Join-Path $root 'update-project-state.ps1') -Light }
Measure-Step 'project-index' { & (Join-Path $root 'project-index.ps1') }
Measure-Step 'build-fast' { & (Join-Path $root 'build-fast.ps1') }

$out=Join-Path $root ('artifacts\tooling\baseline-'+$(if($After){'after'}else{'before'})+'.json')
New-Item -ItemType Directory -Force -Path (Split-Path $out)|Out-Null
$rows|ConvertTo-Json -Depth 5|Set-Content $out -Encoding UTF8
$rows|ForEach-Object{Write-Output ('[BASE] {0} {1}ms lines={2} bytes={3} procDelta={4}' -f $_.name,$_.durationMs,$_.consoleLines,$_.consoleBytes,$_.processDelta)}
