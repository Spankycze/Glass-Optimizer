param(
  [string]$Directory='.',
  [switch]$PlanOnly,
  [switch]$ForceNpm
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
  # Bun was faster on both measured ROOT-only JS dev-tool projects. Avoid
  # silently changing the package manager for unrelated nested repositories.
  $packageName=(Get-Content -LiteralPath (Join-Path $dir 'package.json') -Raw | ConvertFrom-Json).name
  $isCanonicalRoot=[string]::Equals($dir,[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)
  $approvedName=$packageName -in @('ram-guard-quality-tools','spankyho-optimizer-graphics-tools')
  $bunCmd=Get-Command bun.cmd -ErrorAction SilentlyContinue
  $useBun=$isCanonicalRoot -and $approvedName -and !$ForceNpm -and $null -ne $bunCmd
  if($PlanOnly){
    Write-Host ('FAST_NODE_INSTALL_PLAN manager='+$(if($useBun){'bun'}else{'npm'})+' canonicalRoot='+$isCanonicalRoot+' package='+$packageName+' scriptsIgnored='+$useBun)
    return
  }
  Push-Location $dir
  try {
    if($useBun){
      $lock=Join-Path $dir 'package-lock.json'
      $before=(Get-FileHash -LiteralPath $lock -Algorithm SHA256).Hash
      & $bunCmd.Source install --no-save --no-progress --ignore-scripts
      if($LASTEXITCODE -ne 0){throw 'BUN_DEV_DEPENDENCY_INSTALL_FAILED no automatic npm retry'}
      if((Get-FileHash -LiteralPath $lock -Algorithm SHA256).Hash -ne $before){throw 'BUN_CHANGED_CANONICAL_PACKAGE_LOCK'}
      if((Test-Path -LiteralPath (Join-Path $dir 'bun.lock')) -or
         (Test-Path -LiteralPath (Join-Path $dir 'bun.lockb'))){throw 'BUN_CREATED_UNEXPECTED_LOCKFILE'}
      Write-Host 'FAST_NODE_INSTALL_OK manager=bun lockUnchanged=true'
      return
    }
    & npm.cmd ci --prefer-offline --no-audit --no-fund
    if($LASTEXITCODE -ne 0){throw 'NPM_CI_FAILED'}
    Write-Host 'FAST_NODE_INSTALL_OK manager=npm'
    return
  } finally { Pop-Location }
}
if(Test-Path (Join-Path $dir 'package.json')){
  Push-Location $dir
  try { & pnpm install --prefer-offline } finally { Pop-Location }
  exit $LASTEXITCODE
}
throw 'No package.json found.'
