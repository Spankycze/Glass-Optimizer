$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$env:DOTNET_CLI_USE_MSBUILD_SERVER='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:NUGET_XMLDOC_MODE='skip'
$env:NPM_CONFIG_PREFER_OFFLINE='true'
$env:NPM_CONFIG_AUDIT='false'
$env:NPM_CONFIG_FUND='false'
$env:PIP_DISABLE_PIP_VERSION_CHECK='1'

# Refresh PATH after winget/npm installs without requiring a new shell.
$machine=[Environment]::GetEnvironmentVariable('Path','Machine')
$user=[Environment]::GetEnvironmentVariable('Path','User')
$env:Path=$machine+';'+$user

$cacheRoot=Join-Path $env:LOCALAPPDATA 'SpankyDevCache'
New-Item -ItemType Directory -Force $cacheRoot | Out-Null

$sccache=Get-Command sccache.exe -ErrorAction SilentlyContinue
if($sccache){
  $env:SCCACHE_DIR=Join-Path $cacheRoot 'sccache'
  $env:SCCACHE_CACHE_SIZE='8G'
  New-Item -ItemType Directory -Force $env:SCCACHE_DIR | Out-Null
  $env:SPANKY_SCCACHE=$sccache.Source
}

$rg=Get-Command rg.exe -ErrorAction SilentlyContinue
if($rg){$env:SPANKY_RG=$rg.Source}
$ninja=Get-Command ninja.exe -ErrorAction SilentlyContinue
if($ninja){$env:SPANKY_NINJA=$ninja.Source}
$uv=Get-Command uv.exe -ErrorAction SilentlyContinue
if($uv){$env:SPANKY_UV=$uv.Source}
$pnpm=Get-Command pnpm.ps1 -ErrorAction SilentlyContinue
if($pnpm){$env:SPANKY_PNPM=$pnpm.Source}