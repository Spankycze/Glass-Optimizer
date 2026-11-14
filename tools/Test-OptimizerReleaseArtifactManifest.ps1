$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$verify=Join-Path $PSScriptRoot 'Verify-OptimizerReleaseArtifactManifest.ps1'
$fixture=Join-Path $root ('artifacts\.cache\release-manifest-fixture-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$installer=Join-Path $fixture 'Spankyho_Optimizer_V16_140_Setup_Compact.exe'
$payload=Join-Path $fixture 'payload.tar.zst'
$manifestPath=Join-Path $fixture 'manifest.json'
function Write-FixtureManifest {
  $m=[ordered]@{
    version='16.140.0'
    installer=[ordered]@{
      file=[IO.Path]::GetFileName($installer)
      sizeMb=[math]::Round(([IO.FileInfo]$installer).Length/1MB,2)
      sha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
      mode='framework-dependent-net10'
    }
    payloadSha256=(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    gates=[ordered]@{manifestIntegrity=$true}
  }
  $m|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $manifestPath -Encoding UTF8
}
function Invoke-Check([string]$version='16.140.0'){
  & $verify -ManifestPath $manifestPath -InstallerPath $installer -PayloadPath $payload -ExpectedVersion $version | Out-Null
}
function Expect-Rejection([string]$name,[string]$failurePart,[string]$version='16.140.0'){
  $rejected=$false
  try {Invoke-Check $version} catch {
    if($_.Exception.Message.Contains($failurePart,[StringComparison]::Ordinal)){$rejected=$true}
    else {throw "MANIFEST_FIXTURE_WRONG_FAILURE $name : $($_.Exception.Message)"}
  }
  if(-not $rejected){throw "MANIFEST_FIXTURE_ACCEPTED_INVALID_$name"}
  Write-Output "MANIFEST_FIXTURE_REJECT_PASS $name"
}
try {
  # Non-zero fractional MiB exercises JSON size parsing on Czech locales.
  [IO.File]::WriteAllBytes($installer, [byte[]]::new(1572864))
  [IO.File]::WriteAllBytes($payload,[Text.Encoding]::UTF8.GetBytes('trusted-zstd-payload-fixture'))
  Write-FixtureManifest
  Invoke-Check
  'MANIFEST_FIXTURE_VALID_PASS'
  Expect-Rejection 'wrong-version' 'RELEASE_MANIFEST_VERSION_MISMATCH' '16.141.0'
  [IO.File]::AppendAllText($payload,'altered')
  Expect-Rejection 'tampered-payload' 'RELEASE_MANIFEST_PAYLOAD_HASH_MISMATCH'
  [IO.File]::WriteAllBytes($payload,[Text.Encoding]::UTF8.GetBytes('trusted-zstd-payload-fixture'))
  [IO.File]::AppendAllText($installer,'altered')
  Expect-Rejection 'tampered-installer' 'RELEASE_MANIFEST_INSTALLER_HASH_MISMATCH'
  # Non-zero fractional MiB exercises JSON size parsing on Czech locales.
  [IO.File]::WriteAllBytes($installer, [byte[]]::new(1572864))
  $original=[IO.File]::ReadAllText($manifestPath)
  $m=$original|ConvertFrom-Json
  $m.installer.file='other.exe'
  $m|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $manifestPath -Encoding UTF8
  Expect-Rejection 'wrong-installer-name' 'RELEASE_MANIFEST_INSTALLER_IDENTITY_MISMATCH'
  $m=$original|ConvertFrom-Json
  $m.installer.sha256='00'
  $m|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $manifestPath -Encoding UTF8
  Expect-Rejection 'bad-hash-format' 'RELEASE_MANIFEST_INSTALLER_HASH_INVALID'
  $m=$original|ConvertFrom-Json
  $m.installer.sizeMb=123
  $m|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $manifestPath -Encoding UTF8
  Expect-Rejection 'wrong-installer-size' 'RELEASE_MANIFEST_INSTALLER_SIZE_MISMATCH'
  $m=$original|ConvertFrom-Json
  $m.gates.manifestIntegrity=$false
  $m|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $manifestPath -Encoding UTF8
  Expect-Rejection 'disabled-integrity-flag' 'RELEASE_MANIFEST_INTEGRITY_FLAG_MISSING'
  [IO.File]::WriteAllText($manifestPath,'{not-json', [Text.UTF8Encoding]::new($false))
  Expect-Rejection 'invalid-json' 'RELEASE_MANIFEST_INVALID_JSON'
  'RELEASE_MANIFEST_FIXTURE_SUITE_PASS total=9 valid=1 rejected=8'
} finally {
  if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
