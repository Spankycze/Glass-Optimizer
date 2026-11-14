# Read-only source of truth for every Spankyho Optimizer continuation.
# Source version, verified desktop release and installed version are distinct.
[CmdletBinding()]
param([switch]$AsJson)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$match = [regex]::Match([IO.File]::ReadAllText($propsPath),
    '<SpankyVersion>(\d+\.\d+\.\d+)</SpankyVersion>')
if (-not $match.Success) { throw 'Current source SpankyVersion not found' }
$sourceVersion = $match.Groups[1].Value
$publicMatch = [regex]::Match([IO.File]::ReadAllText($propsPath),
    '<SpankyPublicVersion>(\d+\.\d+)</SpankyPublicVersion>')
$sourcePublicVersion = ([version]$sourceVersion).ToString(2)
if (-not $publicMatch.Success -or
    $publicMatch.Groups[1].Value -ne $sourcePublicVersion) {
    throw 'Current public version disagrees with internal source assembly version'
}

$checkpointPath = Join-Path $root '.ai-state\BUILD_CHECKPOINT.json'
$checkpoint = if (Test-Path $checkpointPath) {
    Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json
} else { $null }
# A newer Debug/Quick build can overwrite BUILD_CHECKPOINT but must never
# erase the last successful installer identity. The dedicated file is written
# only by successful releases; recover older projects from immutable history.
$releaseCheckpointPath=Join-Path $root '.ai-state\LAST_RELEASE_CHECKPOINT.json'
$releaseCheckpoint=$null
if(Test-Path -LiteralPath $releaseCheckpointPath){
    try { $releaseCheckpoint=Get-Content -LiteralPath $releaseCheckpointPath -Raw | ConvertFrom-Json } catch {}
}
if($null -eq $releaseCheckpoint -and $checkpoint -and $checkpoint.kind -eq 'release'){
    $releaseCheckpoint=$checkpoint
}
if($null -eq $releaseCheckpoint){
    $historyPath=Join-Path $root '.ai-state\build-checkpoint-history.jsonl'
    if(Test-Path -LiteralPath $historyPath){
        foreach($line in @(Get-Content -LiteralPath $historyPath -Tail 200 -ErrorAction SilentlyContinue)){
            try {
                $candidate=$line | ConvertFrom-Json
                if($candidate.kind -eq 'release' -and $candidate.outcome -eq 'OK'){
                    $releaseCheckpoint=$candidate
                }
            }catch{}
        }
    }
}
$checkpointIsRelease = $null -ne $releaseCheckpoint -and
    $releaseCheckpoint.kind -eq 'release' -and $releaseCheckpoint.outcome -eq 'OK' -and
    [bool]([regex]::IsMatch([string]$releaseCheckpoint.builtVersion, '^\d+\.\d+\.\d+$'))
$releaseVersion = if ($checkpointIsRelease) { [string]$releaseCheckpoint.builtVersion } else { $null }

$desktop = 'C:\Users\spnka\Desktop'
$setups = @(Get-ChildItem -LiteralPath $desktop -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -eq 'Glass_Optimizer_Setup_Compact.exe' -or
        $_.Name -like 'Spankyho_Optimizer_V*_Setup_Compact.exe'
    })
$releasePath = if ($checkpointIsRelease) { [string]$releaseCheckpoint.evidence } else { $null }
$expectedFileName = if ($checkpointIsRelease) {
    $fileVersion = if ([version]$releaseVersion -ge [version]'16.115.0') {
        ([version]$releaseVersion).ToString(2)
    } else { $releaseVersion }
    'Glass_Optimizer_Setup_Compact.exe'
} else { '' }
$releaseFileValid = $checkpointIsRelease -and $setups.Count -eq 1 -and
    $setups[0].Name -eq $expectedFileName -and
    $setups[0].FullName -eq $releasePath -and (Test-Path -LiteralPath $releasePath)
$actualHash = if ($releaseFileValid) { (Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash } else { $null }
$resumePath = Join-Path $root 'RESUME.md'
$expectedHash = if (Test-Path $resumePath) {
    [regex]::Match([IO.File]::ReadAllText($resumePath), '"sha256"\s*:\s*"([0-9a-fA-F]{64})"').Groups[1].Value
} else { '' }
$hashMatches = $releaseFileValid -and $expectedHash.Length -eq 64 -and
    $actualHash -eq $expectedHash

$installedPath = 'C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe'
$installedVersion = if (Test-Path -LiteralPath $installedPath) {
    (Get-Item -LiteralPath $installedPath).VersionInfo.FileVersion
} else { $null }
$state = [ordered]@{
    CheckedAt = (Get-Date).ToString('o')
    SourceVersion = $sourceVersion
    SourcePublicVersion = $sourcePublicVersion
    VerifiedReleaseVersion = if ($hashMatches) { $releaseVersion } else { $null }
    VerifiedReleasePublicVersion = if ($hashMatches) { ([version]$releaseVersion).ToString(2) } else { $null }
    ReleaseCheckpointVersion = $releaseVersion
    ReleaseEvidenceStatus = if ($hashMatches) { 'VERIFIED_CHECKPOINT_DESKTOP_HASH' }
        elseif (-not $checkpointIsRelease) { 'NO_SUCCESSFUL_RELEASE_CHECKPOINT' }
        elseif ($setups.Count -ne 1) { 'DESKTOP_INSTALLER_COUNT_MISMATCH' }
        elseif (-not $releaseFileValid) { 'CHECKPOINT_DESKTOP_MISMATCH' }
        else { 'SHA256_NOT_VERIFIED' }
    InstallerPath = if ($releaseFileValid) { $releasePath } else { $null }
    InstallerSha256 = $actualHash
    DesktopInstallerCount = $setups.Count
    InstalledVersion = $installedVersion
    InstalledPublicVersion = if ($installedVersion -and
        [regex]::IsMatch($installedVersion, '^\d+\.\d+')) {
        ([version]$installedVersion).ToString(2)
    } else { $null }
    SourceHasUnreleasedChanges = if ($releaseVersion) { [version]$sourceVersion -gt [version]$releaseVersion } else { $null }
}
if ($AsJson) { [pscustomobject]$state | ConvertTo-Json -Depth 3 }
else {
    'LIVE PROJECT STATE (read-only, not a build):'
    $state.GetEnumerator() | ForEach-Object { '{0}: {1}' -f $_.Key,$_.Value }
    if (-not $hashMatches) { Write-Warning 'Release NOT VERIFIED: investigate checkpoint, desktop setup and SHA before making claims.' }
}
