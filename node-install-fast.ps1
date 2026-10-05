param(
  [string]$Directory='.'
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$dir=(Resolve-Path (Join-Path $PSScriptRoot $Directory)).Path
if(Test-Path (Join-Path $dir 'pnpm-lock.yaml')){
  Push-Location $dir
  try { & pnpm install --frozen-lockfile --prefer-offline } finally { Pop-Location }
  exit $LASTEXITCODE
}
if(Test-Path (Join-Path $dir 'package-lock.json')){
  Push-Location $dir
  try { & npm ci --prefer-offline --no-audit --no-fund } finally { Pop-Location }
  exit $LASTEXITCODE
}
if(Test-Path (Join-Path $dir 'package.json')){
  Push-Location $dir
  try { & pnpm install --prefer-offline } finally { Pop-Location }
  exit $LASTEXITCODE
}
throw 'No package.json found.'
