param(
  [string]$Version='',
  [string]$PayloadPath='',
  [string]$OutputDir='',
  [switch]$SkipSmoke,
  [switch]$DeliverToDesktop
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if($DeliverToDesktop -and $SkipSmoke){
  throw 'Desktop delivery requires the installer smoke test; -SkipSmoke is incompatible with -DeliverToDesktop.'
}
$proj=Join-Path $root 'installer\SpankyInstaller.csproj'
if(-not $PayloadPath){$PayloadPath=Join-Path $root 'installer\payload.tar.zst'}
$PayloadPath=if([IO.Path]::IsPathFullyQualified($PayloadPath)){
  [IO.Path]::GetFullPath($PayloadPath)
}else{
  [IO.Path]::GetFullPath((Join-Path $root $PayloadPath))
}
if(-not(Test-Path -LiteralPath $PayloadPath -PathType Leaf)){throw ('payload.tar.zst missing: '+$PayloadPath)}
if(-not $OutputDir){$OutputDir=Join-Path $root 'artifacts\release'}
New-Item $OutputDir -ItemType Directory -Force|Out-Null

function Get-PayloadVersion([string]$archivePath){
  # The same managed streaming reader validates file count, sizes and paths.
  # Do not extract an untrusted EXE to obtain the version.
  $packerDir=Join-Path $root 'tools\PayloadPacker'
  $packer=Join-Path $packerDir 'PayloadPacker.csproj'
  $packerDll=Join-Path $packerDir 'bin\Release\net10.0\PayloadPacker.dll'
  # build-release has just run the packer; do not pay for a second MSBuild
  # project evaluation merely to validate the same archive.
  $inputs=@($packer,(Join-Path $packerDir 'Program.cs'))
  $stale=-not(Test-Path -LiteralPath $packerDll)
  if(-not $stale){
    $builtAt=(Get-Item -LiteralPath $packerDll).LastWriteTimeUtc
    $stale=@($inputs | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $builtAt }).Count -gt 0
  }
  if($stale){
    & dotnet build $packer -c Release --nologo --no-restore -v:q | Out-Host
    if($LASTEXITCODE -ne 0){throw 'Zstd payload verifier build failed.'}
  }
  $result=@(& dotnet $packerDll verify $archivePath)
  if($LASTEXITCODE -ne 0){throw ('Zstd payload validation failed: '+($result -join ' '))}
  $line=$result|Where-Object{$_ -match '^ZSTD_VERIFY_OK version=(\d+\.\d+\.\d+)\b'}|Select-Object -Last 1
  if(-not $line){throw 'Zstd payload manifest/version not found'}
  if($line -notmatch '^ZSTD_VERIFY_OK version=(\d+\.\d+\.\d+)\b'){throw 'Bad version in Zstd archive'}
  return $matches[1]
}
$payloadVersion=Get-PayloadVersion $PayloadPath
if(-not $Version){$Version=$payloadVersion}
elseif($Version -ne $payloadVersion){throw ('Compact version '+$Version+' does not match payload '+$payloadVersion)}

# Embed the requested payload directly. Never overwrite installer/payload.tar.zst
# just to build a candidate installer; a process crash must not corrupt the
# source/release payload used by other builds.
$selectedPayload=if([IO.Path]::IsPathFullyQualified($PayloadPath)){
  [IO.Path]::GetFullPath($PayloadPath)
}else{
  [IO.Path]::GetFullPath((Join-Path $root $PayloadPath))
}
if(-not(Test-Path -LiteralPath $selectedPayload -PathType Leaf)){
  throw ('Installer payload not found: '+$selectedPayload)
}
# Each build owns a private GUID staging directory. Never delete the
# shared artifacts/_compact-installer directory: another build may be using it.
$work=Join-Path $root ('artifacts\.cache\compact-build-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work)|Out-Null
try {

$assets=Join-Path $root 'installer\obj\project.assets.json'
if(-not(Test-Path $assets)){
  & dotnet restore $proj -r win-x64 --nologo | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Compact installer restore failed.'}
  Write-Output 'COMPACT_INSTALLER_RESTORE_OK'
}

$args=@(
  'publish',$proj,'-c','Release','-r','win-x64','--self-contained','false','--nologo','--no-restore','-o',$work,
  '-p:SelfContained=false','-p:PublishSingleFile=true','-p:EnableCompressionInSingleFile=false',
  '-p:DebugType=None','-p:DebugSymbols=false',('-p:SpankyVersion='+$Version),('-p:SpankyInstallerPayloadPath='+$selectedPayload)
)
$sw=[Diagnostics.Stopwatch]::StartNew()
& dotnet @args | Out-Host
$exit=$LASTEXITCODE
$sw.Stop()
if($exit -ne 0){throw ('Compact installer publish failed exit='+$exit)}

$exe=Get-ChildItem $work -Filter '*.exe' -File|Sort-Object Length -Descending|Select-Object -First 1
if(-not $exe){throw 'Compact installer EXE missing'}
# A valid standalone payload hash does not prove the EXE embedded those bytes.
# Verify the actual embedded managed resource before accepting or delivering
# this candidate, even with -SkipSmoke (which skips only the UI smoke).
$payloadSha256=(Get-FileHash -LiteralPath $selectedPayload -Algorithm SHA256).Hash
# Running the setup EXE itself requires UAC; hash the exact freshly compiled
# managed resource via dotnet instead. The strict release smoke below verifies
# the packaged EXE as well, without an additional elevation prompt.
$managedPublicVersion=([version]$Version).ToString(2)
$managed=Join-Path $root ('installer\bin\Release\net10.0-windows\win-x64\Spankyho_Optimizer_V'+($managedPublicVersion -replace '\.','_')+'_Setup.dll')
if(-not(Test-Path -LiteralPath $managed)){throw 'COMPACT_EMBEDDED_PAYLOAD_MANAGED_DLL_MISSING'}
& dotnet $managed --verify-embedded-payload-sha256 $payloadSha256
if($LASTEXITCODE -ne 0){throw ('COMPACT_EMBEDDED_PAYLOAD_HASH_MISMATCH exit='+$LASTEXITCODE)}
Write-Output 'COMPACT_EMBEDDED_PAYLOAD_HASH_OK compiledResource=true uacPrompt=false'
# Independently inspect the actual Single-File EXE's bundle manifest. The
# EXE must contain the exact compiled installer DLL (byte-for-byte SHA-256),
# not merely an earlier/stale assembly with a matching version number.
$bundleVerifier=Join-Path $root 'tools\Verify-OptimizerCompactBundle.ps1'
& $bundleVerifier -InstallerPath $exe.FullName -ManagedDllPath $managed -ExpectedVersion $Version | Out-Host
if(-not $?){throw 'COMPACT_SINGLEFILE_BUNDLE_INTEGRITY_FAILED'}
Write-Output 'COMPACT_SINGLEFILE_BUNDLE_INTEGRITY_OK'
if(-not $SkipSmoke){
  $smoke=Start-Process $exe.FullName -ArgumentList @('--release-installer-smoke-bundle',$payloadSha256) -Wait -PassThru
  if($smoke.ExitCode -ne 0){throw ('Compact installer smoke failed exit='+$smoke.ExitCode)}
  Write-Output 'COMPACT_INSTALLER_SMOKE_OK'
}

$publicVersion=([version]$Version).ToString(2)
$name='Glass_Optimizer_Setup_Compact.exe'
$final=Join-Path $OutputDir $name
Copy-Item $exe.FullName $final -Force
$mb=[math]::Round((Get-Item $final).Length/1MB,2)
$sha=(Get-FileHash $final -Algorithm SHA256).Hash
Write-Output ('COMPACT_INSTALLER_OK version='+$Version+' sizeMB='+$mb+' ms='+[math]::Round($sw.Elapsed.TotalMilliseconds)+' file='+$final)
Write-Output ('COMPACT_INSTALLER_SHA256 '+$sha)
# Explicit candidate release path: only deliver AFTER packaging and built-in
# installer smoke succeed. Never kill any user process to delete old setups.
if($DeliverToDesktop){
  & (Join-Path $root 'tools\Publish-Optimizer-DesktopInstaller.ps1') -InstallerPath $final -ExpectedSha256 $sha | Out-Host
  if(-not $?){throw 'Desktop compact installer delivery failed.'}
}
} finally {
  # Only files allocated by THIS run may be cleaned, including on failure.
  if(Test-Path -LiteralPath $work -PathType Container){
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop
  }
}
