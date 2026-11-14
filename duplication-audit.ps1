param([int]$MinLines=12,[switch]$UpdateBaseline)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$baseline=Join-Path $root '.ai-state\duplication-baseline.json'
$out=Join-Path $root 'artifacts\quality\duplication-latest.json'
$project=Join-Path $root 'tools\DuplicationAudit\DuplicationAudit.csproj'

New-Item -ItemType Directory -Force -Path (Split-Path $out)|Out-Null
$result=@(& dotnet run --project $project -c Release -- $root $MinLines $out 2>&1)
if($LASTEXITCODE -ne 0){throw ('DuplicationAudit failed: '+(($result|Select-Object -Last 12)-join [Environment]::NewLine))}
$json=$result|Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') }|Select-Object -Last 1
if(-not $json){throw 'DuplicationAudit returned no JSON summary'}
$count=[int](($json|ConvertFrom-Json).count)

if($UpdateBaseline -or !(Test-Path $baseline)){
  [pscustomobject]@{count=$count}|ConvertTo-Json|Set-Content $baseline -Encoding UTF8
  Write-Host ('SPANKY_DUPLICATION_BASELINE '+$count)
  exit 0
}

$b=Get-Content $baseline -Raw|ConvertFrom-Json
if($count -gt [int]$b.count){throw ('SPANKY_DUPLICATION_REGRESSION current='+$count+' baseline='+$b.count)}
Write-Host ('SPANKY_DUPLICATION_OK current='+$count+' baseline='+$b.count)
