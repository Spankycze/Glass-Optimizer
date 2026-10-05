param(
  [string]$BuildDir='.',
  [Parameter(ValueFromRemainingArguments=$true)][string[]]$Args
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$dir=(Resolve-Path (Join-Path $PSScriptRoot $BuildDir)).Path
$ninjaFile=Join-Path $dir 'build.ninja'
if(!(Test-Path $ninjaFile)){
  Write-Host 'NINJA_SKIP=no-build.ninja'
  exit 0
}
if(!$env:SPANKY_NINJA){throw 'Ninja is not available.'}
& $env:SPANKY_NINJA -C $dir @Args
exit $LASTEXITCODE
