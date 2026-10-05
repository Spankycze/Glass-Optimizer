param(
  [string]$AssemblyPath='',
  [string]$BaselineAssembly='',
  [string]$OutputDir=''
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($AssemblyPath)){
  $AssemblyPath=Join-Path $root 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.dll'
}
if([string]::IsNullOrWhiteSpace($OutputDir)){
  $OutputDir=Join-Path $root ('artifacts\quality\ilspy-'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDir|Out-Null
$ilspy=Get-Command ilspycmd -ErrorAction Stop
$asm=[IO.Path]::GetFullPath($AssemblyPath)
if(-not(Test-Path $asm)){throw 'Assembly missing: '+$asm}

$mainOut=Join-Path $OutputDir 'current'
New-Item -ItemType Directory -Force -Path $mainOut|Out-Null
& $ilspy.Source -p -o $mainOut $asm
if($LASTEXITCODE -ne 0){throw 'ilspy current decompile failed'}

$meta=[ordered]@{
  assembly=$asm
  sha256=(Get-FileHash $asm -Algorithm SHA256).Hash
  size=(Get-Item $asm).Length
  decompiledFiles=@(Get-ChildItem $mainOut -Recurse -File).Count
}
if(-not [string]::IsNullOrWhiteSpace($BaselineAssembly) -and (Test-Path $BaselineAssembly)){
  $base=[IO.Path]::GetFullPath($BaselineAssembly)
  $baseOut=Join-Path $OutputDir 'baseline'
  New-Item -ItemType Directory -Force -Path $baseOut|Out-Null
  & $ilspy.Source -p -o $baseOut $base
  $meta.baselineAssembly=$base
  $meta.baselineSha256=(Get-FileHash $base -Algorithm SHA256).Hash
}
$meta|ConvertTo-Json -Depth 6|Set-Content (Join-Path $OutputDir 'assembly-meta.json') -Encoding UTF8
Write-Output ('ILSPY_VERIFY_OK sha256='+$meta.sha256+' files='+$meta.decompiledFiles+' dir='+$OutputDir)
