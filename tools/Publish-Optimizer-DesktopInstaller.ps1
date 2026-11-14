# Publishes one verified Spankyho Optimizer Compact setup to Desktop.
# No process termination, no changes to Windows or application settings.
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [string]$DesktopPath = '',
    [string]$ExpectedSha256 = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
# Accept legacy three-part names and new short public Major_Minor setup
# names. Keep version comparison on all available numeric components.
$pattern = '^(?:Glass_Optimizer_Setup_Compact|Spankyho_Optimizer_V(\d+)_(\d+)(?:_(\d+))?_Setup_Compact)\.exe$'
$source = (Get-Item -LiteralPath $InstallerPath -ErrorAction Stop)
if ($source.PSIsContainer -or $source.Name -cnotmatch $pattern) {
    throw 'DESKTOP_SETUP_INVALID_SOURCE: source must be Compact Setup EXE.'
}
$version = if ($source.BaseName -eq 'Glass_Optimizer_Setup_Compact') {
    [version]::new([int]::MaxValue, [int]::MaxValue, [int]::MaxValue, [int]::MaxValue)
} else {
    [version]::new([int]$Matches[1], [int]$Matches[2], $(if ($Matches[3]) { [int]$Matches[3] } else { 0 }))
}
$expectedName = $source.Name
if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
    $DesktopPath = [Environment]::GetFolderPath('Desktop')
}
if (-not (Test-Path -LiteralPath $DesktopPath -PathType Container)) {
    throw 'DESKTOP_SETUP_PATH_NOT_FOUND'
}

$target = Join-Path $DesktopPath $expectedName
$sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
# The release gate must pin the exact previously verified bytes, not merely
# accept whichever contents happen to be present at the later copy step.
if(-not [string]::IsNullOrEmpty($ExpectedSha256)){
    if($ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
       -not [string]::Equals($sourceHash,$ExpectedSha256,[StringComparison]::OrdinalIgnoreCase)){
        throw 'DESKTOP_SETUP_EXPECTED_HASH_MISMATCH'
    }
}
$installers = @(Get-ChildItem -LiteralPath $DesktopPath -File -ErrorAction Stop |
    Where-Object { $_.Name -cmatch $pattern })
$obsolete = @()
foreach ($entry in $installers) {
    if ($entry.Name -cnotmatch $pattern) { continue }
    $foundVersion = if ($entry.BaseName -eq 'Glass_Optimizer_Setup_Compact') {
        [version]::new([int]::MaxValue, [int]::MaxValue, [int]::MaxValue, [int]::MaxValue)
    } else {
        [version]::new([int]$Matches[1], [int]$Matches[2], $(if ($Matches[3]) { [int]$Matches[3] } else { 0 }))
    }
    if ($foundVersion -gt $version) {
        throw "DESKTOP_SETUP_DOWNGRADE_BLOCKED: $($entry.Name) is newer than $expectedName"
    }
    if (-not [string]::Equals($entry.FullName, $target,
        [StringComparison]::OrdinalIgnoreCase)) {
        $obsolete += $entry
    }
}

if ($DryRun) {
    Write-Output "DESKTOP_SETUP_DRY_RUN version=$version remove=$($obsolete.Count) keep=$target"
    foreach ($entry in $obsolete) {
        Write-Output "DESKTOP_SETUP_WOULD_REMOVE $($entry.FullName)"
    }
    return
}

# Copy + hash-verify into a temporary path before publishing the actual EXE.
# A failed/partial transfer must not destroy the previously verified installer.
$pending = Join-Path $DesktopPath ('.spanky-installer-pending-' +
    [guid]::NewGuid().ToString('N') + '.tmp')
try {
    if ([string]::Equals($source.FullName, $target,
        [StringComparison]::OrdinalIgnoreCase)) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sourceHash) {
            throw 'DESKTOP_SETUP_EXISTING_HASH_MISMATCH'
        }
    } else {
        Copy-Item -LiteralPath $source.FullName -Destination $pending -ErrorAction Stop
        if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $sourceHash) {
            throw 'DESKTOP_SETUP_TRANSFER_HASH_MISMATCH'
        }
        Move-Item -LiteralPath $pending -Destination $target -Force -ErrorAction Stop
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sourceHash) {
        throw 'DESKTOP_SETUP_FINAL_HASH_MISMATCH'
    }

    # Restrict deletion to exact, previous-version Spankyho Compact EXE names.
    # Do not stop/restart any process to unlock a file.
    $removed = 0
    foreach ($entry in $obsolete) {
        Remove-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop
        $removed++
        Write-Output "DESKTOP_SETUP_REMOVED $($entry.Name)"
    }
    $remaining = @(Get-ChildItem -LiteralPath $DesktopPath -File -ErrorAction Stop |
        Where-Object { $_.Name -cmatch $pattern })
    if ($remaining.Count -ne 1 -or -not [string]::Equals(
        $remaining[0].FullName, $target, [StringComparison]::OrdinalIgnoreCase)) {
        throw "DESKTOP_SETUP_CLEANUP_INCOMPLETE: remaining=$($remaining.Count)"
    }
    Write-Output "DESKTOP_SETUP_OK version=$version removed=$removed file=$target sha256=$sourceHash"
}
finally {
    if (Test-Path -LiteralPath $pending) {
        Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
    }
}
