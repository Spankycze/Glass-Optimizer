param(
  [ValidateSet('auto','build','test')][string]$Mode='auto'
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$w=Get-Command watchexec.exe -ErrorAction SilentlyContinue
if(!$w){throw 'watchexec missing'}
$root=$PSScriptRoot
$cmd=if($Mode -eq 'test'){
  @('powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File','.\build-test.ps1')
}else{
  @('powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File','.\build-fast.ps1')
}
Push-Location $root
try{
  & $w.Source --clear --restart --debounce 500ms --watch $root --exts cs,xaml,csproj,props,targets,rs,ps1 --ignore 'artifacts/**' --ignore 'bin/**' --ignore 'obj/**' --ignore '.git/**' -- @cmd
  exit $LASTEXITCODE
}finally{Pop-Location}
