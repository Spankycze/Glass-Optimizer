param(
  [string]$Version='',
  [string]$PayloadPath='',
  [string]$OutputDir=''
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$proj=Join-Path $root 'installer\SpankyInstaller.csproj'
if(-not $PayloadPath){$PayloadPath=Join-Path $root 'installer\payload.zip'}
if(-not(Test-Path $PayloadPath)){throw 'payload.zip missing'}
if(-not $OutputDir){$OutputDir=Join-Path $root 'artifacts\release'}
New-Item $OutputDir -ItemType Directory -Force|Out-Null

function Get-PayloadVersion([string]$zipPath){
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
  $tmp=Join-Path ([IO.Path]::GetTempPath()) ('spanky-payload-'+[guid]::NewGuid().ToString('N')+'.exe')
  try{
    $entry=$zip.Entries|Where-Object{$_.FullName -match '(^|/)SpankyhoOptimizer\.exe$'}|Select-Object -First 1
    if(-not $entry){throw 'SpankyhoOptimizer.exe missing in payload.zip'}
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$tmp,$true)
    $raw=(Get-Item $tmp).VersionInfo.ProductVersion
    if($raw -notmatch '^(\d+\.\d+\.\d+)'){throw ('Payload version invalid: '+$raw)}
    return $matches[1]
  }finally{
    $zip.Dispose()
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
  }
}
$payloadVersion=Get-PayloadVersion $PayloadPath
if(-not $Version){$Version=$payloadVersion}
elseif($Version -ne $payloadVersion){throw ('Compact version '+$Version+' does not match payload '+$payloadVersion)}

$targetPayload=Join-Path $root 'installer\payload.zip'
if([IO.Path]::GetFullPath($PayloadPath) -ne [IO.Path]::GetFullPath($targetPayload)){
  Copy-Item $PayloadPath $targetPayload -Force
}
$work=Join-Path $root 'artifacts\_compact-installer'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item $work -ItemType Directory -Force|Out-Null

$args=@(
  'publish',$proj,'-c','Release','-r','win-x64','--self-contained','false','--nologo','--no-restore','-o',$work,
  '-p:SelfContained=false','-p:PublishSingleFile=true','-p:EnableCompressionInSingleFile=false',
  '-p:DebugType=None','-p:DebugSymbols=false',('-p:SpankyVersion='+$Version)
)
$sw=[Diagnostics.Stopwatch]::StartNew()
& dotnet @args | Out-Host
$exit=$LASTEXITCODE
$sw.Stop()
if($exit -ne 0){throw ('Compact installer publish failed exit='+$exit)}

$exe=Get-ChildItem $work -Filter '*.exe' -File|Sort-Object Length -Descending|Select-Object -First 1
if(-not $exe){throw 'Compact installer EXE missing'}
$smoke=Start-Process $exe.FullName -ArgumentList '--release-installer-smoke-bundle' -Wait -PassThru
if($smoke.ExitCode -ne 0){throw ('Compact installer smoke failed exit='+$smoke.ExitCode)}

$name='Spankyho_Optimizer_V'+($Version -replace '\.','_')+'_Setup_Compact.exe'
$final=Join-Path $OutputDir $name
Copy-Item $exe.FullName $final -Force
$mb=[math]::Round((Get-Item $final).Length/1MB,2)
$sha=(Get-FileHash $final -Algorithm SHA256).Hash
Write-Output ('COMPACT_INSTALLER_OK version='+$Version+' sizeMB='+$mb+' ms='+[math]::Round($sw.Elapsed.TotalMilliseconds)+' file='+$final)
Write-Output ('COMPACT_INSTALLER_SHA256 '+$sha)
