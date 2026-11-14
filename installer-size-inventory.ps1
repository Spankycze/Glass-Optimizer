param([string]$Root=(Split-Path -Parent $MyInvocation.MyCommand.Path),[string]$OutputPath)
$ErrorActionPreference='Stop'
$folder=Join-Path $Root 'artifacts\.cache\app-publish'
if(-not (Test-Path -LiteralPath $folder -PathType Container)){throw 'Published app folder missing'}
$files=@(Get-ChildItem -LiteralPath $folder -File -Recurse)
if($files.Count -eq 0){throw 'Published app folder empty'}
$bytes=[long](($files|Measure-Object Length -Sum).Sum)
$categories=[ordered]@{
 'SDK.NET'=@($files|Where-Object Name -eq 'Microsoft.Windows.SDK.NET.dll')
 'WinUI'=@($files|Where-Object { $_.Name -match '^(Microsoft\.ui\.xaml|Microsoft\.WinUI|Microsoft\.UI\.Xaml\.Controls)\.dll$' })
 'Core'=@($files|Where-Object Name -eq 'SpankyhoOptimizer.Core.dll')
}
$groups=@(foreach($key in $categories.Keys){$members=@($categories[$key]);[pscustomobject]@{category=$key;raw_bytes=[long](($members|Measure-Object Length -Sum).Sum);files=$members.Count}})
$archive=Join-Path $Root 'installer\payload.tar.zst'
if(-not(Test-Path -LiteralPath $archive -PathType Leaf)){throw 'Missing payload archive'}
$result=[ordered]@{source='published-app';file_count=$files.Count;raw_bytes=$bytes;compressed_bytes=(Get-Item -LiteralPath $archive).Length;groups=$groups;not_a_removal_recommendation=$true}
$json=$result|ConvertTo-Json -Depth 4
if($OutputPath){[IO.File]::WriteAllText($OutputPath,$json,[Text.UTF8Encoding]::new($false))}
Write-Output $json
