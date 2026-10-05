param([ValidateSet('dns','json','version','smoke')][string]$Mode='smoke')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$proj=Join-Path $root 'fuzz\SpankyhoOptimizer.Fuzz\SpankyhoOptimizer.Fuzz.csproj'
dotnet build $proj -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Fuzz harness build failed'}
if($Mode -eq 'smoke'){
  dotnet run -c Release --no-build --project $proj -- --smoke
  if($LASTEXITCODE -ne 0){throw 'Fuzz smoke failed'}
  $corpus=Join-Path $root 'fuzz\regressions'
  dotnet run -c Release --no-build --project $proj -- --regression $corpus
  if($LASTEXITCODE -ne 0){throw 'Fuzz regression corpus failed'}
  Write-Output 'FUZZ_SMOKE_OK'
  return
}
Write-Output ('FUZZ_HARNESS_READY mode='+$Mode+' project='+$proj)
Write-Output 'Use SharpFuzz/libFuzzer/AFL host for long fuzzing; smoke mode is the release gate.'
