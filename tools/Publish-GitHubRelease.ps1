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
foreach ($asset in @($release.assets)) {
    & gh release delete-asset $Tag $asset.name --repo $Repository --yes
    if ($LASTEXITCODE -ne 0) {
        throw "RELEASE_ASSET_DELETE_FAILED: $($asset.name)"
    }
}

& gh release upload $Tag $InstallerPath $ChangelogPath --repo $Repository --clobber
if ($LASTEXITCODE -ne 0) {
    throw 'RELEASE_ASSET_UPLOAD_FAILED'
}

$expected = @('Glass_Optimizer_Setup_Compact.exe', 'CHANGELOG.md')
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
