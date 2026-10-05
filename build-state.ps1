$script:SpankyRoot=Split-Path -Parent $MyInvocation.MyCommand.Path
$script:SpankySrc=Join-Path $script:SpankyRoot 'src\SpankyhoOptimizer'
$script:SpankyCache=Join-Path $script:SpankyRoot 'artifacts\.cache'
New-Item $script:SpankyCache -ItemType Directory -Force | Out-Null

function Test-SpankyFullBuildFresh {
  $state=Join-Path $script:SpankyCache 'full-source.fp'
  if(-not(Test-Path $state)){return $false}
  # Correctness first: always compare the content fingerprint.
  # Length/timestamp metadata alone is not authoritative because some editors/tools can preserve them.
  return ((Get-Content $state -Raw).Trim() -eq (Get-SpankyFingerprint))
}

function Test-SpankyStructuralFresh {
  $state=Join-Path $script:SpankyCache 'full-structural.fp'
  if(-not(Test-Path $state)){return $false}
  return ((Get-Content $state -Raw).Trim() -eq (Get-SpankyFingerprint -Structural))
}
function Save-SpankyStructuralState {
  Set-Content (Join-Path $script:SpankyCache 'full-structural.fp') (Get-SpankyFingerprint -Structural) -NoNewline
}

function Test-SpankyXamlReuseSafe {
  try {
    $intermediate=@(
      Get-ChildItem (Join-Path $script:SpankySrc 'obj\Debug') -Recurse -File -Filter 'SpankyhoOptimizer.dll' -ErrorAction SilentlyContinue |
      Where-Object {$_.DirectoryName -match '\\intermediatexaml$'} |
      Sort-Object LastWriteTimeUtc -Descending
    )
    if($intermediate.Count -eq 0){return $false}
    $eventPattern='\b(Click|Loaded|Unloaded|SelectionChanged|Toggled|Checked|Unchecked|TextChanged|KeyDown|KeyUp|PointerPressed|PointerReleased|PointerMoved|Tapped|DoubleTapped|RightTapped|Holding|SizeChanged)\s*=\s*"'
    foreach($xaml in Get-ChildItem $script:SpankySrc -Recurse -File -Filter '*.xaml' -ErrorAction SilentlyContinue){
      $text=[IO.File]::ReadAllText($xaml.FullName)
      if($text -match '\{x:Bind\b'){return $false}
      if($text -match $eventPattern){return $false}
      if($text -match 'xmlns:\w+\s*=\s*"using:SpankyhoOptimizer(?:\.|")'){return $false}
    }
    return $true
  } catch { return $false }
}

function Test-SpankyRunnableFresh {
  $exe=Join-Path $script:SpankySrc 'bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
  $pending=Join-Path $script:SpankyCache 'fast-only.pending'
  return (Test-Path $exe) -and -not(Test-Path $pending) -and (Test-SpankyFullBuildFresh)
}

function Save-SpankyTestState([switch]$Full){
  $fp=Get-SpankyFingerprint
  Set-Content (Join-Path $script:SpankyCache 'test-quick.fp') $fp -NoNewline
  if($Full){Set-Content (Join-Path $script:SpankyCache 'test-full.fp') $fp -NoNewline}
}
function Test-SpankyTestFresh([switch]$Full){
  $name=$(if($Full){'test-full.fp'}else{'test-quick.fp'})
  $state=Join-Path $script:SpankyCache $name
  if(-not(Test-Path $state)){return $false}
  return ((Get-Content $state -Raw).Trim() -eq (Get-SpankyFingerprint))
}
function Save-SpankyGateState {
  Set-Content (Join-Path $script:SpankyCache 'full-gate.fp') (Get-SpankyFingerprint) -NoNewline
}
function Test-SpankyGateFresh {
  $state=Join-Path $script:SpankyCache 'full-gate.fp'
  if(-not(Test-Path $state)){return $false}
  return ((Get-Content $state -Raw).Trim() -eq (Get-SpankyFingerprint))
}

# Split-build fingerprints
$script:SpankyFingerprintMemo=@{}
$script:SpankyCoreSrc=Join-Path $script:SpankyRoot 'src\SpankyhoOptimizer.Core'
$script:SpankyRuntimeHostSrc=Join-Path $script:SpankyRoot 'src\SpankyhoOptimizer.RuntimeHost'
$script:SpankyVersionProps=Join-Path $script:SpankyRoot 'Directory.Build.props'

function Test-SpankyNativeFresh {
  $crate=Join-Path $script:SpankyRoot 'native\Spanky.NativeCore'
  $dest=Join-Path $script:SpankyRoot 'src\SpankyhoOptimizer\Native\Spanky.NativeCore.dll'
  $state=Join-Path $script:SpankyCache 'native-release.fast.state'
  if(-not(Test-Path $crate) -or -not(Test-Path $dest) -or -not(Test-Path $state)){return $false}
  try{
    $files=[Collections.Generic.List[IO.FileInfo]]::new()
    $dirs=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $dirs.Push([IO.DirectoryInfo]::new($crate))
    while($dirs.Count -gt 0){
      $dir=$dirs.Pop()
      foreach($f in $dir.EnumerateFiles()){ $files.Add($f) }
      foreach($child in $dir.EnumerateDirectories()){
        if($child.Name -eq 'target'){continue}
        $dirs.Push($child)
      }
    }
    $lines=foreach($f in ($files | Sort-Object FullName)){"$($f.FullName)|$($f.Length)|$($f.LastWriteTimeUtc.Ticks)"}
    return ((Get-Content $state -Raw) -eq ($lines -join "`n"))
  } catch { return $false }
}

function Get-SpankySourceTreeFiles([string]$basePath){
  if(-not(Test-Path $basePath)){return @()}
  $files=[Collections.Generic.List[IO.FileInfo]]::new()
  $dirs=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
  $dirs.Push([IO.DirectoryInfo]::new($basePath))
  while($dirs.Count -gt 0){
    $dir=$dirs.Pop()
    foreach($file in $dir.EnumerateFiles()){ $files.Add($file) }
    foreach($child in $dir.EnumerateDirectories()){
      if($child.Name -in @('bin','obj')){continue}
      $dirs.Push($child)
    }
  }
  return $files.ToArray()
}
function Get-SpankyHashForFiles($files){
  $contentExtensions=@('.cs','.xaml','.csproj','.props','.targets','.manifest','.json','.txt','.xml')
  $lines=$files | Sort-Object FullName | ForEach-Object {
    $base=$(if($_.FullName.StartsWith($script:SpankySrc,[StringComparison]::OrdinalIgnoreCase)){$script:SpankySrc}elseif($_.FullName.StartsWith($script:SpankyCoreSrc,[StringComparison]::OrdinalIgnoreCase)){$script:SpankyCoreSrc}else{$script:SpankyRoot})
    $rel=$_.FullName.Substring($base.Length)
    if($contentExtensions -contains $_.Extension.ToLowerInvariant()){
      $h=[Security.Cryptography.SHA256]::Create(); $st=[IO.File]::OpenRead($_.FullName)
      try{$ch=([BitConverter]::ToString($h.ComputeHash($st))).Replace('-','')}finally{$st.Dispose();$h.Dispose()}
      "$base|$rel|$($_.Length)|$ch"
    } else { "$base|$rel|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
  }
  $bytes=[Text.Encoding]::UTF8.GetBytes(($lines -join "`n")); $sha=[Security.Cryptography.SHA256]::Create()
  try{return (($sha.ComputeHash($bytes)|ForEach-Object{$_.ToString('X2')}) -join '')}finally{$sha.Dispose()}
}
function Get-SpankyRestoreInputHash([string[]]$paths,[string]$versionPropsPath){
  $sha=[Security.Cryptography.SHA256]::Create()
  try{
    $fullVersionProps=[IO.Path]::GetFullPath($versionPropsPath)
    $lines=foreach($p in $paths | Sort-Object){
      if(-not(Test-Path $p -PathType Leaf)){continue}
      $full=[IO.Path]::GetFullPath($p)
      if([string]::Equals($full,$fullVersionProps,[StringComparison]::OrdinalIgnoreCase)){
        $text=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8)
        $normalized=[regex]::Replace(
          $text,
          '<SpankyVersion>[^<]+</SpankyVersion>',
          '<SpankyVersion>__RESTORE_VERSION__</SpankyVersion>',
          [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        "$p|$normalized"
      } else {
        $h=(Get-FileHash $p -Algorithm SHA256).Hash
        "$p|$h"
      }
    }
    $bytes=[Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')
  } finally {$sha.Dispose()}
}

function Get-SpankyFingerprint([switch]$Structural){
  $key=$(if($Structural){'full-structural'}else{'full-source'})
  if($script:SpankyFingerprintMemo.ContainsKey($key)){return $script:SpankyFingerprintMemo[$key]}
  $files=@(Get-SpankySourceTreeFiles $script:SpankySrc)
  if(Test-Path $script:SpankyCoreSrc){$files+=@(Get-SpankySourceTreeFiles $script:SpankyCoreSrc)}
  if(Test-Path $script:SpankyRuntimeHostSrc){$files+=@(Get-SpankySourceTreeFiles $script:SpankyRuntimeHostSrc)}
  if(Test-Path $script:SpankyVersionProps){$files+=@(Get-Item $script:SpankyVersionProps)}
  if($Structural){$files=$files|Where-Object{$_.Extension -in '.xaml','.csproj','.props','.targets','.manifest'}}
  $value=Get-SpankyHashForFiles $files
  $script:SpankyFingerprintMemo[$key]=$value
  return $value
}
function Get-SpankyMetadataFingerprint {
  if($script:SpankyFingerprintMemo.ContainsKey('full-meta')){return $script:SpankyFingerprintMemo['full-meta']}
  $files=@(Get-SpankySourceTreeFiles $script:SpankySrc)
  if(Test-Path $script:SpankyCoreSrc){$files+=@(Get-SpankySourceTreeFiles $script:SpankyCoreSrc)}
  if(Test-Path $script:SpankyRuntimeHostSrc){$files+=@(Get-SpankySourceTreeFiles $script:SpankyRuntimeHostSrc)}
  if(Test-Path $script:SpankyVersionProps){$files+=@(Get-Item $script:SpankyVersionProps)}
  $lines=$files | Sort-Object FullName | ForEach-Object {"$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"}
  $bytes=[Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
  $sha=[Security.Cryptography.SHA256]::Create()
  try{$value=(($sha.ComputeHash($bytes)|ForEach-Object{$_.ToString('X2')}) -join '')}
  finally{$sha.Dispose()}
  $script:SpankyFingerprintMemo['full-meta']=$value
  return $value
}
function Get-SpankyUiFingerprint {
  if($script:SpankyFingerprintMemo.ContainsKey('ui-source')){return $script:SpankyFingerprintMemo['ui-source']}
  # The WinUI project explicitly removes Models/*.cs and Services/*.cs from compilation,
  # except UiResourceService. Those linked sources belong to the Core fingerprint instead.
  $modelsRoot=Join-Path $script:SpankySrc 'Models'
  $servicesRoot=Join-Path $script:SpankySrc 'Services'
  $uiResource=Join-Path $servicesRoot 'UiResourceService.cs'
  $files=Get-SpankySourceTreeFiles $script:SpankySrc | Where-Object {
    if($_.FullName.StartsWith($modelsRoot,[StringComparison]::OrdinalIgnoreCase)){return $false}
    if($_.FullName.StartsWith($servicesRoot,[StringComparison]::OrdinalIgnoreCase)){
      return $_.FullName.Equals($uiResource,[StringComparison]::OrdinalIgnoreCase)
    }
    return $true
  }
  if(Test-Path $script:SpankyVersionProps){$files+=@(Get-Item $script:SpankyVersionProps)}
  $value=Get-SpankyHashForFiles $files
  $script:SpankyFingerprintMemo['ui-source']=$value
  return $value
}
function Get-SpankyCoreFingerprint {
  if($script:SpankyFingerprintMemo.ContainsKey('core-source')){return $script:SpankyFingerprintMemo['core-source']}
  $files=@()
  if(Test-Path $script:SpankyCoreSrc){
    $files+=@(Get-SpankySourceTreeFiles $script:SpankyCoreSrc | Where-Object {
      $_.Extension -in '.cs','.csproj','.props','.targets'
    })
  }

  # SpankyhoOptimizer.Core.csproj links these sources from the WinUI project.
  # They must participate in the Core fingerprint or build-fast can reuse a stale Core DLL.
  $modelsRoot=Join-Path $script:SpankySrc 'Models'
  if(Test-Path $modelsRoot){
    $files+=@(Get-ChildItem $modelsRoot -File -Filter '*.cs')
  }
  $servicesRoot=Join-Path $script:SpankySrc 'Services'
  if(Test-Path $servicesRoot){
    $files+=@(Get-ChildItem $servicesRoot -File -Filter '*.cs' | Where-Object {
      $_.Name -ne 'UiResourceService.cs'
    })
  }

  if(Test-Path $script:SpankyVersionProps){$files+=@(Get-Item $script:SpankyVersionProps)}
  $value=Get-SpankyHashForFiles $files
  $script:SpankyFingerprintMemo['core-source']=$value
  return $value
}
function Save-SpankyFullBuildState {
  Set-Content (Join-Path $script:SpankyCache 'full-source.fp') (Get-SpankyFingerprint) -NoNewline
  Set-Content (Join-Path $script:SpankyCache 'full-source.meta') (Get-SpankyMetadataFingerprint) -NoNewline
  Set-Content (Join-Path $script:SpankyCache 'full-structural.fp') (Get-SpankyFingerprint -Structural) -NoNewline
  Set-Content (Join-Path $script:SpankyCache 'ui-source.fp') (Get-SpankyUiFingerprint) -NoNewline
  Set-Content (Join-Path $script:SpankyCache 'core-source.fp') (Get-SpankyCoreFingerprint) -NoNewline
  Remove-Item (Join-Path $script:SpankyCache 'fast-only.pending') -Force -ErrorAction SilentlyContinue
}
function Test-SpankyUiFresh {
  $state=Join-Path $script:SpankyCache 'ui-source.fp'; if(-not(Test-Path $state)){return $false}
  return ((Get-Content $state -Raw).Trim() -eq (Get-SpankyUiFingerprint))
}
function Test-SpankyCoreFresh {
  $state=Join-Path $script:SpankyCache 'core-source.fp'; if(-not(Test-Path $state)){return $false}
  return ((Get-Content $state -Raw).Trim() -eq (Get-SpankyCoreFingerprint))
}
function Save-SpankyCoreFastState {
  Set-Content (Join-Path $script:SpankyCache 'full-source.fp') (Get-SpankyFingerprint) -NoNewline
  Set-Content (Join-Path $script:SpankyCache 'full-source.meta') (Get-SpankyMetadataFingerprint) -NoNewline
  Set-Content (Join-Path $script:SpankyCache 'core-source.fp') (Get-SpankyCoreFingerprint) -NoNewline
  Set-Content (Join-Path $script:SpankyCache 'ui-source.fp') (Get-SpankyUiFingerprint) -NoNewline
  Remove-Item (Join-Path $script:SpankyCache 'fast-only.pending') -Force -ErrorAction SilentlyContinue
}
