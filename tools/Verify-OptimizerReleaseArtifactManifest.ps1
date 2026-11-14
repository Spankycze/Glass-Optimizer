param(
  [Parameter(Mandatory=$true)][string]$ManifestPath,
  [Parameter(Mandatory=$true)][string]$InstallerPath,
  [Parameter(Mandatory=$true)][string]$PayloadPath,
  [Parameter(Mandatory=$true)][string]$ExpectedVersion
)
$ErrorActionPreference='Stop'

# Verify the actual on-disk artifacts, never the values used to construct the
# manifest in memory. Intended for the release gate before desktop delivery.
foreach($path in @($ManifestPath,$InstallerPath,$PayloadPath)){
  if(-not(Test-Path -LiteralPath $path -PathType Leaf)){
    throw "RELEASE_MANIFEST_MISSING_FILE $path"
  }
}
try {
  $manifest=Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
} catch {
  throw "RELEASE_MANIFEST_INVALID_JSON $($_.Exception.Message)"
}
if([string]::IsNullOrWhiteSpace($ExpectedVersion) -or
   -not [string]::Equals([string]$manifest.version,$ExpectedVersion,[StringComparison]::Ordinal)){
  throw 'RELEASE_MANIFEST_VERSION_MISMATCH'
}
$installerName=[IO.Path]::GetFileName($InstallerPath)
if(-not [string]::Equals([string]$manifest.installer.file,$installerName,[StringComparison]::Ordinal) -or
   -not [string]::Equals([string]$manifest.installer.mode,'framework-dependent-net10',[StringComparison]::Ordinal)){
  throw 'RELEASE_MANIFEST_INSTALLER_IDENTITY_MISMATCH'
}
if($manifest.gates.manifestIntegrity -ne $true){
  throw 'RELEASE_MANIFEST_INTEGRITY_FLAG_MISSING'
}
$installerInfo=Get-Item -LiteralPath $InstallerPath
$sizeMb=[math]::Round($installerInfo.Length/1MB,2)
[double]$parsedSize=0
if(-not [double]::TryParse([string]$manifest.installer.sizeMb,[Globalization.NumberStyles]::Float,
    [Globalization.CultureInfo]::InvariantCulture,[ref]$parsedSize)){
  throw 'RELEASE_MANIFEST_INSTALLER_SIZE_INVALID'
}
if($parsedSize -ne $sizeMb){throw 'RELEASE_MANIFEST_INSTALLER_SIZE_MISMATCH'}

function Assert-ArtifactHash([string]$path,[string]$expected,[string]$kind){
  if($expected -cnotmatch '^[a-fA-F0-9]{64}$'){
    throw "RELEASE_MANIFEST_${kind}_HASH_INVALID"
  }
  $actual=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
  $left=[Convert]::FromHexString($expected)
  $right=[Convert]::FromHexString($actual)
  if(-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($left,$right)){
    throw "RELEASE_MANIFEST_${kind}_HASH_MISMATCH"
  }
}
Assert-ArtifactHash $InstallerPath ([string]$manifest.installer.sha256) 'INSTALLER'
Assert-ArtifactHash $PayloadPath ([string]$manifest.payloadSha256) 'PAYLOAD'
Write-Host "RELEASE_MANIFEST_ARTIFACT_HASHES_OK version=$ExpectedVersion installer=$installerName sizeMb=$sizeMb"
