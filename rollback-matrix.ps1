param([switch]$NoRestore)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$proj=Join-Path $root 'tools\CoreTests\CoreTests.csproj'
$args=@('test',$proj,'-c','Release','--nologo','--filter','FullyQualifiedName~RollbackMatrixTests')
if($NoRestore){$args+='--no-restore'}
& dotnet @args
$code=$LASTEXITCODE
if($code -ne 0){throw ('Rollback matrix failed exit='+$code)}
Write-Output 'ROLLBACK_MATRIX_OK cases=5'
