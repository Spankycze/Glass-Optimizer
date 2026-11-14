param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$LogPath = 'C:\ProgramData\SpankyhoOptimizer\release-ui-smoke.log'
)
$ErrorActionPreference='Stop'
if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) { throw "UI smoke log missing: $LogPath" }
$lines=[IO.File]::ReadAllLines((Resolve-Path -LiteralPath $LogPath).Path)
$end=-1
for($i=$lines.Length-1;$i -ge 0;$i--){
    if($lines[$i].Contains('RELEASE_UI_SMOKE_BUNDLE_OK count=21')){$end=$i;break}
}
if($end -lt 0){throw 'No passing UI smoke bundle marker in log'}
$start=-1
for($i=$end-1;$i -ge 0;$i--){
    if($lines[$i] -match 'DRIVERS_UI_SMOKE_OK ms=\d+'){$start=$i;break}
    if($lines[$i].Contains('RELEASE_UI_SMOKE_BUNDLE_FAIL')){throw 'Ambiguous failed UI smoke run before passing marker'}
}
if($start -lt 0){throw 'UI smoke start not found'}
$metrics=@{}
for($i=$start;$i -lt $end;$i++){
    if($lines[$i] -match '\b([A-Z][A-Z0-9_]+_SMOKE_OK) ms=(\d+)\b'){
        $name=$Matches[1]
        if($metrics.ContainsKey($name)){throw "Repeated UI smoke gate: $name"}
        $metrics[$name]=[long]$Matches[2]
    }
}
if($metrics.Count -lt 18){throw "Incomplete UI smoke metrics: $($metrics.Count) (<18)"}
if($lines[$end] -notmatch '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) '){throw 'Missing timestamp'}
$stamp=$Matches[1]
$record=[pscustomobject]@{
    Version=$Version
    CompletedAt=$stamp
    Metrics=$metrics
    PassGateCount=21
}
$target=[IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
[IO.File]::WriteAllText($target,($record|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
Write-Output "UI_SMOKE_CAPTURE_OK version=$Version timestamp=$stamp metrics=$($metrics.Count) file=$target"
