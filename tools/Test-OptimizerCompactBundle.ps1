param(
  [Parameter(Mandatory=$true)][string]$InstallerPath,
  [Parameter(Mandatory=$true)][string]$ManagedDllPath,
  [Parameter(Mandatory=$true)][string]$ExpectedVersion
)
$ErrorActionPreference='Stop'
$verify=Join-Path $PSScriptRoot 'Verify-OptimizerCompactBundle.ps1'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temp=Join-Path $root ('artifacts\.cache\compact-bundle-tamper-'+[guid]::NewGuid().ToString('N'))
function Expect-IntegrityRejection([string]$path,[string]$version,[string]$marker){
  $rejected=$false
  try{
    & $verify -InstallerPath $path -ManagedDllPath $ManagedDllPath -ExpectedVersion $version|Out-Null
  }catch{
    if($_.Exception.Message.Contains($marker,[StringComparison]::Ordinal)){$rejected=$true}
    else{throw "COMPACT_BUNDLE_UNEXPECTED_REJECTION $($_.Exception.Message)"}
  }
  if(-not $rejected){throw "COMPACT_BUNDLE_TAMPERING_ACCEPTED expected=$marker"}
  Write-Output "COMPACT_BUNDLE_REJECT_PASS $marker"
}
try{
  [IO.Directory]::CreateDirectory($temp)|Out-Null
  $result=@(& $verify -InstallerPath $InstallerPath -ManagedDllPath $ManagedDllPath -ExpectedVersion $ExpectedVersion)
  if($result.Count -ne 1 -or $result[0] -notmatch 'entryOffset=(\d+)'){
    throw 'COMPACT_BUNDLE_VALID_FIXTURE_NO_OFFSET'
  }
  [long]$managedOffset=$Matches[1]
  'COMPACT_BUNDLE_VALID_FIXTURE_PASS'
  Expect-IntegrityRejection $InstallerPath '16.999.0' 'COMPACT_BUNDLE_VERSION_MISMATCH'
  $tampered=Join-Path $temp 'Spankyho_Optimizer_V16_140_Setup_Compact.exe'
  [IO.File]::Copy($InstallerPath,$tampered)
  $stream=[IO.File]::Open($tampered,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  try{
    $stream.Position=$managedOffset+2048
    $original=$stream.ReadByte()
    if($original -lt 0){throw 'COMPACT_BUNDLE_TAMPER_OFFSET_OUT_OF_RANGE'}
    $stream.Position--
    $stream.WriteByte([byte]($original -bxor 1))
    $stream.Flush($true)
  }finally{$stream.Dispose()}
  Expect-IntegrityRejection $tampered $ExpectedVersion 'COMPACT_BUNDLE_MANAGED_DLL_HASH_MISMATCH'
  'COMPACT_BUNDLE_INTEGRITY_FIXTURE_SUITE_PASS valid=1 invalid=2'
}finally{
  if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction Stop}
}
