param(
  [Parameter(Mandatory=$true,Position=0)][string]$Task,
  [int]$Window = 20,
  [double]$DriftThreshold = 10
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$audit=Join-Path $root '.token-audit'
New-Item -ItemType Directory -Force $audit | Out-Null
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$prefix=Join-Path $audit ('run-'+$stamp)
& 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\redcon.exe' pack $Task --repo $root --out-prefix $prefix --format human
if($LASTEXITCODE -ne 0){ exit $LASTEXITCODE }
$run=$prefix+'.json'
& 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\redcon.exe' read-profiler $run --out-prefix (Join-Path $audit ('read-'+$stamp)) --format human
& 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\redcon.exe' drift --repo $root --window $Window --threshold $DriftThreshold --out-prefix (Join-Path $audit ('drift-'+$stamp)) --format human
& 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\redcon.exe' roi $run --format human | Set-Content -Encoding utf8 (Join-Path $audit ('roi-'+$stamp+'.txt'))
$latest=[ordered]@{
  generated=(Get-Date).ToString('o')
  task=$Task
  run=$run
  read_profile=(Join-Path $audit ('read-'+$stamp+'.json'))
  drift=(Join-Path $audit ('drift-'+$stamp+'.json'))
  roi=(Join-Path $audit ('roi-'+$stamp+'.txt'))
}
$latest | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $audit 'latest.json')
Write-Host ('TOKEN_AUDIT_OK='+$run)
