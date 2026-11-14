param([switch]$SkipBlender,[switch]$SkipWindowsSamples,[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$vendor=Join-Path $PSScriptRoot 'vendor'
New-Item -ItemType Directory -Force $vendor | Out-Null
Set-Location $root
if(-not $VerifyOnly){
 # Preserve the canonical npm lockfile; Bun accelerates only DEV dependencies.
 $lockFile=Join-Path $root 'package-lock.json'
 $beforeHash=(Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash
 if(Get-Command bun.cmd -ErrorAction SilentlyContinue){
  & bun.cmd install --no-save --no-progress --ignore-scripts
  if($LASTEXITCODE -ne 0){throw 'Bun development dependency install failed'}
 } else {
  & npm.cmd ci --ignore-scripts --no-audit --no-fund
  if($LASTEXITCODE -ne 0){throw 'npm ci failed'}
 }
 $afterHash=(Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash
 if($afterHash -ne $beforeHash){throw 'Canonical package-lock.json changed during dependency install'}
}
$modules=@('pixelmatch','pngjs','motion','lucide-static','lottie-web','three','@penpot/mcp')
foreach($pkg in $modules){
 $manifest=Join-Path $root ('node_modules\'+$pkg+'\package.json')
 if(!(Test-Path -LiteralPath $manifest)){throw ('Missing graphic package '+$pkg)}
}
if(-not $SkipWindowsSamples){
 $samples=Join-Path $vendor 'WindowsAppSDK-Samples'
 if(!(Test-Path (Join-Path $samples '.git'))){
  if($VerifyOnly){throw 'Windows SDK samples missing'}
  & git clone --depth 1 --filter=blob:none --sparse 'https://github.com/microsoft/WindowsAppSDK-Samples.git' $samples
  if($LASTEXITCODE -ne 0){throw 'Windows samples git clone failed'}
  & git -C $samples sparse-checkout set Samples/Composition Samples/Windowing docs
  if($LASTEXITCODE -ne 0){throw 'Windows sample sparse-checkout failed'}
 }
}
$blenderExe=Join-Path $vendor 'blender-5.2.2-windows-x64\blender.exe'
if(-not $SkipBlender){
 if(!(Test-Path $blenderExe)){
  if($VerifyOnly){throw 'Blender portable missing'}
  $url='https://download.blender.org/release/Blender5.2/'
  $name='blender-5.2.2-windows-x64.zip'
  $zip=Join-Path $vendor $name
  $hashFile=Join-Path $vendor 'blender-5.2.2.sha256'
  if(!(Test-Path $hashFile)){
   & curl.exe -fLsS --retry 3 ($url+'blender-5.2.2.sha256') -o $hashFile
   if($LASTEXITCODE -ne 0){throw 'Failed to download official SHA256 file'}
  }
  $line=@(Get-Content $hashFile|Where-Object{$_ -match [regex]::Escape($name)})[0]
  $expected=($line -split '\s+')[0]
  if($expected -notmatch '^[a-fA-F0-9]{64}$'){throw 'Missing or invalid official hash'}
  if(!(Test-Path $zip)){
   & curl.exe -fLsS --retry 3 ($url+$name) -o $zip
   if($LASTEXITCODE -ne 0){throw 'Failed to download Blender archive'}
  }
  $actual=(Get-FileHash $zip -Algorithm SHA256).Hash
  if($actual -ne $expected){throw 'Blender signature mismatch'}
  Expand-Archive -LiteralPath $zip -DestinationPath $vendor -Force
  if(!(Test-Path $blenderExe)){throw 'Blender binary not found after extracting'}
  Remove-Item -LiteralPath $zip -Force
 }
 & $blenderExe --background --factory-startup --python-expr 'import bpy; print("BLENDER_READY", bpy.app.version_string)'
 if($LASTEXITCODE -ne 0){throw 'Blender smoke test failed'}
}
$jsRunner=Join-Path $root 'devtools/run-js.cmd'
& node.exe (Join-Path $PSScriptRoot 'generate-assets.mjs')
if($LASTEXITCODE -ne 0){throw 'Asset generator failed'}
& $jsRunner (Join-Path $PSScriptRoot 'smoke.mjs')
if($LASTEXITCODE -ne 0){throw 'Graphic toolchain smoke failed'}
'GRAPHICS_SETUP_COMPLETE packages='+$modules.Count+' penpot=installed blender='+(Test-Path $blenderExe)
