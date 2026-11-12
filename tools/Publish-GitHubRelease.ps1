# Publishes exactly the Compact installer and CHANGELOG.md to a GitHub release.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Repository = 'Spankycze/Glass-Optimizer',
    [string]$InstallerPath = '',
    [string]$ChangelogPath = ''
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $root 'artifacts\release\Glass_Optimizer_Setup_Compact.exe'
}
if ([string]::IsNullOrWhiteSpace($ChangelogPath)) {
    $ChangelogPath = Join-Path $root 'CHANGELOG.md'
}

foreach ($path in @($InstallerPath, $ChangelogPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "RELEASE_FILE_NOT_FOUND: $path"
    }
}
if ([IO.Path]::GetFileName($InstallerPath) -cne 'Glass_Optimizer_Setup_Compact.exe') {
    throw 'RELEASE_INSTALLER_NAME_INVALID'
}
if ([IO.Path]::GetFileName($ChangelogPath) -cne 'CHANGELOG.md') {
    throw 'RELEASE_CHANGELOG_NAME_INVALID'
}

$releaseJson = (& gh release view $Tag --repo $Repository --json assets 2>&1)
if ($LASTEXITCODE -ne 0) {
    # Create a release only when its Git tag exists; don't turn transient API/auth failures into release mutations.
    & gh api "repos/$Repository/git/ref/tags/$Tag" --silent
    if ($LASTEXITCODE -ne 0) {
        throw "RELEASE_LOOKUP_FAILED_OR_TAG_MISSING: $Tag"
    }
    $title = "Glass Optimizer $($Tag.TrimStart('v', 'V'))"
    & gh release create $Tag --repo $Repository --title $title --notes-file $ChangelogPath
    if ($LASTEXITCODE -ne 0) {
        throw "RELEASE_CREATE_FAILED: $Tag"
    }
    $releaseJson = (& gh release view $Tag --repo $Repository --json assets 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "RELEASE_NOT_FOUND: $Tag"
    }
}
$release = $releaseJson | ConvertFrom-Json
# Upload replacements first. A failed transfer must not destroy the working release assets.
& gh release upload $Tag $InstallerPath $ChangelogPath --repo $Repository --clobber
if ($LASTEXITCODE -ne 0) {
    throw 'RELEASE_ASSET_UPLOAD_FAILED'
}

$expected = @('Glass_Optimizer_Setup_Compact.exe', 'CHANGELOG.md')
$verifyJson = (& gh release view $Tag --repo $Repository --json assets 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw 'RELEASE_VERIFY_FAILED'
}
$uploaded = $verifyJson | ConvertFrom-Json
$uploadedNames = @($uploaded.assets | ForEach-Object name)
foreach ($name in $expected) {
    if ($name -notin $uploadedNames) {
        throw "RELEASE_REQUIRED_ASSET_MISSING: $name"
    }
}
foreach ($asset in @($uploaded.assets | Where-Object { $_.name -notin $expected })) {
    & gh release delete-asset $Tag $asset.name --repo $Repository --yes
    if ($LASTEXITCODE -ne 0) {
        throw "RELEASE_ASSET_DELETE_FAILED: $($asset.name)"
    }
}
$verifyJson = (& gh release view $Tag --repo $Repository --json assets 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw 'RELEASE_VERIFY_FAILED'
}
$actual = @((($verifyJson | ConvertFrom-Json).assets | ForEach-Object name) | Sort-Object)
if (($actual -join '|') -ne (($expected | Sort-Object) -join '|')) {
    throw "RELEASE_ASSET_SET_INVALID: $($actual -join ', ')"
}

$hash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
Write-Output "RELEASE_ASSETS_OK tag=$Tag installer=$hash files=$($actual -join ',')"
