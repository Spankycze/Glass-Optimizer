$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$cache=Join-Path $root 'artifacts\.cache\app-publish'
$payload=Join-Path $root 'installer\payload.zip'
$releaseDir=Join-Path $root 'artifacts\release'
$offline=Get-ChildItem $releaseDir -Filter '*_Offline.exe' -File -ErrorAction SilentlyContinue|Sort-Object LastWriteTime -Descending|Select-Object -First 1
if(-not $offline){$offline=Get-ChildItem (Join-Path $root 'artifacts\.cache') -Filter 'Spankyho_Optimizer_V*_Setup.exe' -File -ErrorAction SilentlyContinue|Sort-Object LastWriteTime -Descending|Select-Object -First 1}
$compact=Get-ChildItem $releaseDir -Filter '*_Compact.exe' -File -ErrorAction SilentlyContinue|Sort-Object LastWriteTime -Descending|Select-Object -First 1
$appBytes=if(Test-Path $cache){[int64]((Get-ChildItem $cache -Recurse -File|Measure-Object Length -Sum).Sum)}else{0}
$payloadBytes=if(Test-Path $payload){(Get-Item $payload).Length}else{0}
$offlineBytes=if($offline){$offline.Length}else{0}
$compactBytes=if($compact){$compact.Length}else{0}
Write-Output ('INSTALLER_SIZE appPublishMB='+[math]::Round($appBytes/1MB,2)+' payloadMB='+[math]::Round($payloadBytes/1MB,2)+' offlineMB='+[math]::Round($offlineBytes/1MB,2)+' compactMB='+[math]::Round($compactBytes/1MB,2))
if($compactBytes -gt 0 -and $offlineBytes -gt 0){Write-Output ('INSTALLER_SIZE compactSavedMB='+[math]::Round(($offlineBytes-$compactBytes)/1MB,2)+' compactSavedPct='+[math]::Round((1-$compactBytes/$offlineBytes)*100,1))}
