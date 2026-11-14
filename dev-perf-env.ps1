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

$configuredDev=[Environment]::GetEnvironmentVariable('SPANKY_DEV_DRIVE','User')
$useDev=$false
if($configuredDev){
  try{$useDev=Test-Path ($configuredDev+'\')}catch{$useDev=$false}
}
if($useDev){
  $cacheRoot=Join-Path $configuredDev 'SpankyDevCache'
  $env:SPANKY_DEV_DRIVE_ACTIVE=$configuredDev
}else{
  $cacheRoot=Join-Path $env:LOCALAPPDATA 'SpankyDevCache'
  Remove-Item Env:SPANKY_DEV_DRIVE_ACTIVE -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $cacheRoot | Out-Null
if($useDev){
  $env:NPM_CONFIG_CACHE=Join-Path $cacheRoot 'npm'
  $env:UV_CACHE_DIR=Join-Path $cacheRoot 'uv'
  foreach($scopedToolCacheDirectory in @($env:NPM_CONFIG_CACHE,$env:UV_CACHE_DIR)){
    New-Item -ItemType Directory -Force $scopedToolCacheDirectory | Out-Null
  }
}else{
  Remove-Item Env:NPM_CONFIG_CACHE -ErrorAction SilentlyContinue
  Remove-Item Env:UV_CACHE_DIR -ErrorAction SilentlyContinue
}
# Scoped to the invoking build process and its children: do NOT change
# the user's global TEMP/TMP, existing NuGet packages or project output paths.
# `obj` is still referenced by WinUI/XBF release guards on C:.
if($useDev){
  $buildCacheRoot=Join-Path $cacheRoot 'SpankyhoOptimizer'
  $devTemp=Join-Path $buildCacheRoot 'Temp'
  $devHttpCache=Join-Path $buildCacheRoot 'NuGetHttpCache'
  try {
    foreach($devCacheDirectory in @($buildCacheRoot,$devTemp,$devHttpCache)){
      New-Item -ItemType Directory -Path $devCacheDirectory -Force -ErrorAction Stop | Out-Null
    }
    # Relocate only the Core project's MSBuild intermediate files. The WinUI
    # project's obj folder is explicitly used by release XBF/asset guards.
    $devCoreObj=Join-Path $buildCacheRoot 'CoreObj'
    New-Item -ItemType Directory -Path $devCoreObj -Force -ErrorAction Stop | Out-Null
    # Apply path changes only after all target directories are ready.
    $env:TEMP=$devTemp
    $env:TMP=$devTemp
    # HTTP response cache is independent of the 9+ GB package installation
    # store. Keep installed global packages on C: for --no-restore builds.
    $env:NUGET_HTTP_CACHE_PATH=$devHttpCache
    $env:SPANKY_CORE_DEV_OBJ=$devCoreObj
    Write-Host ('DEV_DRIVE_BUILD_CACHE_OK temp='+$devTemp+' nugetHttp='+$devHttpCache+' coreObj='+$devCoreObj+' packages=unchanged')
  }catch{
    Write-Warning ('Dev Drive build cache is not available; retaining inherited paths: '+$_.Exception.Message)
  }
}
# Keep NuGet on its existing package root because current builds use --no-restore.
Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue

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
