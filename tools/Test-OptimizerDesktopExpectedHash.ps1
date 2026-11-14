$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$publisher=Join-Path $PSScriptRoot 'Publish-Optimizer-DesktopInstaller.ps1'
$fixture=Join-Path $root ('artifacts\.cache\desktop-publish-fixture-'+[guid]::NewGuid().ToString('N'))
$sourceDir=Join-Path $fixture 'source'
$targetDir=Join-Path $fixture 'desktop'
[IO.Directory]::CreateDirectory($sourceDir)|Out-Null
[IO.Directory]::CreateDirectory($targetDir)|Out-Null
$source=Join-Path $sourceDir 'Spankyho_Optimizer_V16_140_Setup_Compact.exe'
$target=Join-Path $targetDir ([IO.Path]::GetFileName($source))
function Invoke-Publish([string]$hash){
  & $publisher -InstallerPath $source -DesktopPath $targetDir -ExpectedSha256 $hash|Out-Null
}
function Expect-Reject([string]$case,[string]$marker,[string]$hash){
  $rejected=$false
  try{Invoke-Publish $hash}catch{
    if($_.Exception.Message.Contains($marker,[StringComparison]::Ordinal)){$rejected=$true}
    else{throw "DESKTOP_PUBLISH_WRONG_ERROR $case : $($_.Exception.Message)"}
  }
  if(-not $rejected){throw "DESKTOP_PUBLISH_UNSAFE_ACCEPT $case"}
  Write-Output "DESKTOP_PUBLISH_REJECT_PASS $case"
}
try{
  [IO.File]::WriteAllBytes($source,[byte[]]::new(1048576))
  $trusted=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
  Invoke-Publish $trusted
  if(-not(Test-Path -LiteralPath $target) -or
     (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $trusted){
    throw 'DESKTOP_PUBLISH_TRUSTED_COPY_FAIL'
  }
  'DESKTOP_PUBLISH_VALID_PASS'
  Invoke-Publish $trusted
  'DESKTOP_PUBLISH_IDEMPOTENT_PASS'
  Expect-Reject 'wrong-expected-hash' 'DESKTOP_SETUP_EXPECTED_HASH_MISMATCH' ('0'*64)
  [IO.File]::AppendAllText($source,'TAMPERED')
  Expect-Reject 'changed-source-after-manifest' 'DESKTOP_SETUP_EXPECTED_HASH_MISMATCH' $trusted
  if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $trusted){
    throw 'DESKTOP_PUBLISH_TAMPER_CHANGED_VERIFIED_DESTINATION'
  }
  'DESKTOP_PUBLISH_DESTINATION_PRESERVED_PASS'
  [IO.File]::WriteAllBytes($source,[byte[]]::new(1048576))
  $newer=Join-Path $targetDir 'Spankyho_Optimizer_V16_141_Setup_Compact.exe'
  [IO.File]::WriteAllText($newer,'newer-version-stub')
  Expect-Reject 'downgrade' 'DESKTOP_SETUP_DOWNGRADE_BLOCKED' $trusted
  'DESKTOP_PUBLISH_FIXTURE_SUITE_PASS count=6 isolated=true'
}finally{
  if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
