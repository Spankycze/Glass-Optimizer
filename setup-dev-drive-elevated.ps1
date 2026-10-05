$ErrorActionPreference='Stop'
$main=Join-Path $PSScriptRoot 'setup-dev-drive.ps1'
$log=Join-Path $env:USERPROFILE 'Desktop\Spanky Dev Drive Setup.log'
try{
  ('['+(Get-Date -Format o)+'] START') | Set-Content -Encoding utf8 $log
  & $main *>&1 | Tee-Object -FilePath $log -Append
  $code=$LASTEXITCODE
  if($code -eq 3010){
    ('['+(Get-Date -Format o)+'] REBOOT_REQUIRED') | Tee-Object -FilePath $log -Append
    Write-Host ''
    Write-Host 'SPANKY DEV DRIVE: RESTART WINDOWS ONCE' -ForegroundColor Yellow
    Write-Host 'Po restartu bude setup pokracovat automaticky.' -ForegroundColor Yellow
  }elseif($code -ne 0){
    throw ('setup exit='+$code)
  }else{
    ('['+(Get-Date -Format o)+'] SUCCESS') | Tee-Object -FilePath $log -Append
    Write-Host ''
    Write-Host 'SPANKY DEV DRIVE: SUCCESS' -ForegroundColor Green
  }
}catch{
  $_ | Out-String | Tee-Object -FilePath $log -Append | Write-Host
  ('['+(Get-Date -Format o)+'] FAILED') | Tee-Object -FilePath $log -Append
  Write-Host ''
  Write-Host 'SPANKY DEV DRIVE: FAILED' -ForegroundColor Red
}
Write-Host ('Log: '+$log)
Read-Host 'Stiskni Enter pro zavreni'
