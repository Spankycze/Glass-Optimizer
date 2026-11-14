param(
    [Parameter(Mandatory)][string]$PreviousSnapshot,
    [Parameter(Mandatory)][string]$CurrentSnapshot,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
$old=Get-Content -LiteralPath $PreviousSnapshot -Raw | ConvertFrom-Json
$new=Get-Content -LiteralPath $CurrentSnapshot -Raw | ConvertFrom-Json
if([datetime]::Parse($new.CompletedAt) -le [datetime]::Parse($old.CompletedAt)){throw 'Current passing UI smoke is not later than previous baseline'}
$diff=@(
    foreach($property in $old.Metrics.PSObject.Properties){
        $name=$property.Name
        $newProperty=$new.Metrics.PSObject.Properties[$name]
        if($null -eq $newProperty){continue}
        $previous=[long]$property.Value
        $current=[long]$newProperty.Value
        [pscustomobject]@{
            Metric=$name
            PreviousMs=$previous
            CurrentMs=$current
            DeltaMs=$current-$previous
            Trend=if($current -lt $previous){'zlepseni'}elseif($current -gt $previous){'zhorseni'}else{'beze zmeny'}
        }
    }
)
if($diff.Count -lt 18){throw "Too few comparable UI smoke gates: $($diff.Count)"}
$diff=@($diff | Sort-Object Metric)
$rows=@(
    "# UI smoke timings: $($old.Version) -> $($new.Version)"
    ''
    "Recorded passing UI smoke: $($old.CompletedAt) -> $($new.CompletedAt)"
    ''
    '| UI smoke gate | Previous (ms) | Current (ms) | Delta (ms) | Trend |'
    '|---|---:|---:|---:|---|'
)
foreach($m in $diff){$rows+="| $($m.Metric) | $($m.PreviousMs) | $($m.CurrentMs) | $($m.DeltaMs) | $($m.Trend) |"}
$rows+=''
$rows+='These are actual timed UI smoke operations, NOT interactive navigation FPS or real-world cold startup. Repeated runs and matched machine load are needed to establish regression significance.'
$target=[IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
[IO.File]::WriteAllText($target,($rows -join [Environment]::NewLine),[Text.UTF8Encoding]::new($false))
Write-Output "UI_SMOKE_DIFF_OK previous=$($old.Version) current=$($new.Version) count=$($diff.Count)"
$diff | Sort-Object @{Expression={[math]::Abs($_.DeltaMs)};Descending=$true} | Select-Object -First 15 | ForEach-Object {
    Write-Output "UI_SMOKE_DIFF $($_.Metric) $($_.PreviousMs)ms -> $($_.CurrentMs)ms delta=$($_.DeltaMs)ms trend=$($_.Trend)"
}
