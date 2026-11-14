param(
  [Parameter(Mandatory=$true)][string]$InstallerPath,
  [Parameter(Mandatory=$true)][string]$ManagedDllPath,
  [Parameter(Mandatory=$true)][string]$ExpectedVersion
)
$ErrorActionPreference='Stop'
foreach($path in @($InstallerPath,$ManagedDllPath)){
  if(-not(Test-Path -LiteralPath $path -PathType Leaf)){
    throw "COMPACT_BUNDLE_INPUT_MISSING $path"
  }
}
$exe=Get-Item -LiteralPath $InstallerPath
$dll=Get-Item -LiteralPath $ManagedDllPath
$actualVersion=$exe.VersionInfo.FileVersion
try {
  $wanted=([version]$ExpectedVersion).ToString(3)
  $found=([version]$actualVersion).ToString(3)
} catch {
  throw 'COMPACT_BUNDLE_VERSION_INVALID'
}
if($wanted -ne $found){throw 'COMPACT_BUNDLE_VERSION_MISMATCH'}

# Use the SAME SDK's published single-file bundle header detector as dotnet
# publish. This reads PE bytes only; it never executes the UAC setup.
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$sdkRoot=Join-Path (Split-Path -Parent $dotnet) 'sdk'
$hostModels=@(Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction Stop |
  ForEach-Object {Get-Item (Join-Path $_.FullName 'Microsoft.NET.HostModel.dll') -ErrorAction SilentlyContinue} |
  Where-Object {$null -ne $_} |
  Sort-Object @{Expression={try{[version]$_.Directory.Name}catch{[version]'0.0'}}} -Descending)
if($hostModels.Count -eq 0){throw 'COMPACT_BUNDLE_HOSTMODEL_SDK_MISSING'}
$hostModel=[Reflection.Assembly]::LoadFrom($hostModels[0].FullName)
$bundler=$hostModel.GetType('Microsoft.NET.HostModel.Bundle.Bundler',$true)
$detect=$bundler.GetMethod('IsBundle',[Reflection.BindingFlags]'Public,Static')
if($null -eq $detect){throw 'COMPACT_BUNDLE_DETECTOR_MISSING'}
$argsArray=[object[]]@($exe.FullName,[long]0)
if(-not $detect.Invoke($null,$argsArray)){throw 'COMPACT_BUNDLE_HEADER_MISSING'}
$manifestOffset=[long]$argsArray[1]

$stream=[IO.File]::Open($exe.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
  if($manifestOffset -le 0 -or $manifestOffset -ge $stream.Length){
    throw 'COMPACT_BUNDLE_MANIFEST_OFFSET_INVALID'
  }
  $stream.Position=$manifestOffset
  $reader=[IO.BinaryReader]::new($stream,[Text.Encoding]::UTF8,$true)
  $major=$reader.ReadUInt32()
  $minor=$reader.ReadUInt32()
  $count=$reader.ReadInt32()
  if($major -ne 6 -or $count -lt 1 -or $count -gt 256){
    throw "COMPACT_BUNDLE_FORMAT_UNSUPPORTED version=$major.$minor count=$count"
  }
  $null=$reader.ReadString() # bundle identifier
  $null=$reader.ReadInt64() # deps offset
  $null=$reader.ReadInt64() # deps length
  $null=$reader.ReadInt64() # runtimeconfig offset
  $null=$reader.ReadInt64() # runtimeconfig length
  $null=$reader.ReadUInt64() # flags
  $expectedName=$dll.Name
  $managedEntry=$null
  for($i=0;$i -lt $count;$i++){
    $offset=$reader.ReadInt64()
    $length=$reader.ReadInt64()
    $compressed=$reader.ReadInt64()
    $type=$reader.ReadByte()
    $name=$reader.ReadString()
    if($offset -lt 0 -or $length -lt 0 -or $offset -gt ($manifestOffset-$length)){
      throw "COMPACT_BUNDLE_ENTRY_OUT_OF_RANGE $name"
    }
    if([string]::Equals($name,$expectedName,[StringComparison]::Ordinal)){
      if($null -ne $managedEntry){throw 'COMPACT_BUNDLE_DUPLICATE_MANAGED_DLL'}
      if($type -ne 1 -or $compressed -ne 0 -or $length -ne $dll.Length){
        throw 'COMPACT_BUNDLE_MANAGED_DLL_ENTRY_INVALID'
      }
      $managedEntry=[pscustomobject]@{Offset=$offset;Length=$length}
    }
  }
  if($null -eq $managedEntry){throw 'COMPACT_BUNDLE_MANAGED_DLL_NOT_FOUND'}
  # Hash exactly the embedded managed assembly's byte range, not the whole EXE.
  $stream.Position=$managedEntry.Offset
  $digest=[Security.Cryptography.IncrementalHash]::CreateHash(
    [Security.Cryptography.HashAlgorithmName]::SHA256)
  try {
    [long]$remaining=$managedEntry.Length
    $buffer=[byte[]]::new(65536)
    while($remaining -gt 0){
      $read=$stream.Read($buffer,0,[int][Math]::Min($remaining,$buffer.Length))
      if($read -le 0){throw 'COMPACT_BUNDLE_MANAGED_DLL_TRUNCATED'}
      $digest.AppendData($buffer,0,$read)
      $remaining-=$read
    }
    $bundleHash=[Convert]::ToHexString($digest.GetHashAndReset())
  } finally {$digest.Dispose()}
  $standaloneHash=(Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash
  if(-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
      [Convert]::FromHexString($bundleHash),
      [Convert]::FromHexString($standaloneHash))){
    throw 'COMPACT_BUNDLE_MANAGED_DLL_HASH_MISMATCH'
  }
  Write-Output "COMPACT_BUNDLE_EMBEDDED_DLL_SHA256_OK version=$found entries=$count entryOffset=$($managedEntry.Offset) sha256=$bundleHash"
} finally {$stream.Dispose()}
