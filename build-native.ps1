param([switch]$Debug,[switch]$Force,[switch]$StrictHash)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'dev-perf-env.ps1')
if($env:SPANKY_SCCACHE){$env:RUSTC_WRAPPER=$env:SPANKY_SCCACHE}
$crate=Join-Path $root 'native\Spanky.NativeCore'
$manifest=Join-Path $crate 'Cargo.toml'
$dest=Join-Path $root 'src\SpankyhoOptimizer\Native\Spanky.NativeCore.dll'
$cache=Join-Path $root 'artifacts\.cache'
New-Item $cache -ItemType Directory -Force | Out-Null
$mode=$(if($Debug){'debug'}else{'release'})
$state=Join-Path $cache ('native-'+$mode+$(if($StrictHash){'.strict'}else{'.fast'})+'.state')

function Get-NativeSourceFiles([string]$basePath){
  $files=[Collections.Generic.List[IO.FileInfo]]::new()
  $dirs=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
  $dirs.Push([IO.DirectoryInfo]::new($basePath))
  while($dirs.Count -gt 0){
    $dir=$dirs.Pop()
    foreach($file in $dir.EnumerateFiles()){ $files.Add($file) }
    foreach($child in $dir.EnumerateDirectories()){
      if($child.Name -eq 'target'){continue}
      $dirs.Push($child)
    }
  }
  return $files.ToArray()
}
$inputs=Get-NativeSourceFiles $crate | Sort-Object FullName
if($StrictHash){
  $lines=foreach($f in $inputs){$h=(Get-FileHash $f.FullName -Algorithm SHA256).Hash; "$($f.FullName)|$h"}
} else {
  $lines=foreach($f in $inputs){"$($f.FullName)|$($f.Length)|$($f.LastWriteTimeUtc.Ticks)"}
}
$inputState=$lines -join "`n"
if(-not $Force -and (Test-Path $dest) -and (Test-Path $state) -and ((Get-Content $state -Raw) -eq $inputState)){
  $global:LASTEXITCODE=0
  Write-Host 'NATIVE_CACHE_HIT'
  return
}
$cargo=Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if(-not(Test-Path $cargo)){throw 'Rust cargo not found. Install rustup first.'}
$vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if([string]::IsNullOrWhiteSpace($vs)){throw 'MSVC x64 toolchain not found.'}
$devcmd=Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
$profile=if($Debug){''}else{'--release'}
$cmd='call "'+$devcmd+'" -arch=x64 -host_arch=x64 >nul && "'+$cargo+'" build '+$profile+' --manifest-path "'+$manifest+'"'
$sw=[Diagnostics.Stopwatch]::StartNew()
& "$env:SystemRoot\System32\cmd.exe" /d /s /c $cmd
if($LASTEXITCODE -ne 0){throw "Rust native build failed: $LASTEXITCODE"}
$target=Join-Path $root ('native\Spanky.NativeCore\target\'+$mode+'\spanky_native_core.dll')
if(-not(Test-Path $target)){throw 'Rust native DLL was not produced.'}
Copy-Item $target $dest -Force
Set-Content $state $inputState -NoNewline
$sw.Stop()
$hash=(Get-FileHash $dest -Algorithm SHA256).Hash
$global:LASTEXITCODE=0
Write-Host ('NATIVE_BUILD_OK {0:n2}s size={1}KB sha256={2}' -f $sw.Elapsed.TotalSeconds,[math]::Round((Get-Item $dest).Length/1KB,1),$hash)
