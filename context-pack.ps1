param([Parameter(Mandatory=$true,Position=0)][string]$Task)
$ErrorActionPreference='Stop'
$changed=@()
if(Test-Path (Join-Path $PSScriptRoot '.git')){
  try { $changed=@(& git -C $PSScriptRoot diff --name-only HEAD 2>$null | Where-Object { $_ }) } catch {}
}
$args=@('pack',$Task,'--repo',$PSScriptRoot,'--compression-profile','max','--format','context-only')
if($changed.Count -gt 0){ $args += '--changed'; $args += $changed }
& 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\redcon.exe' @args
exit $LASTEXITCODE
