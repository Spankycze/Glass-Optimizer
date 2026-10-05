param([string]$Plan='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$outDir=Join-Path $root '.agentdiff'
New-Item -ItemType Directory -Force $outDir | Out-Null
$diff=Join-Path $outDir 'current.diff'
$report=Join-Path $outDir 'latest.json'
git -C $root diff HEAD --binary | Set-Content -Encoding utf8 $diff
if((Get-Item $diff).Length -eq 0){ Write-Host 'AGENTDIFF_CLEAN'; exit 0 }
$env:PYTHONPATH='C:\Users\spnka\Desktop\ChatGPT Projects\.tools\token-savers\AgentDiff'
$args=@('-m','agentdiff.cli','analyze','--diff',$diff,'--output',$report)
if($Plan){$args += @('--plan',$Plan)}
& py @args
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
Write-Host ('AGENTDIFF_OK='+$report)
