$root=$PSScriptRoot
$f=Join-Path $root '.smart-learning\stats.json'
if(!(Test-Path $f)){Write-Host 'SMART_LEARNING_EMPTY'; exit 0}
$s=Get-Content -Raw $f | ConvertFrom-Json
$s.PSObject.Properties | ForEach-Object {
  $v=$_.Value
  Write-Host ($_.Name+': budget='+$v.budget+' success='+$v.success+' fail='+$v.fail)
  $v.routes.PSObject.Properties | ForEach-Object {
    $r=$_.Value
    Write-Host ('  '+$_.Name+' success='+$r.success+' fail='+$r.fail+' blocked_until='+$r.blocked_until)
  }
}
