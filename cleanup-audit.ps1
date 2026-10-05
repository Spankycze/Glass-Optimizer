param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$archive=Join-Path $root ('_backups\cleanup-'+(Get-Date -Format 'yyyyMMdd'))
$backupPatterns=@('*.console-backup-*','*.before-encoding-fix','*.tmp','*.bak-*')
$candidates=@()
foreach($pat in $backupPatterns){
  $candidates+=@(Get-ChildItem $root -Recurse -File -Filter $pat -ErrorAction SilentlyContinue |
    Where-Object {$_.FullName -notmatch '\\(bin|obj|artifacts)\\'})
}
$candidates=@($candidates|Sort-Object FullName -Unique)
$hashGroups=@(Get-ChildItem $root -File -Filter '*.ps1' |
  ForEach-Object{[pscustomobject]@{file=$_.FullName;hash=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}} |
  Group-Object hash|Where-Object Count -gt 1)
$sourceFiles=Get-ChildItem (Join-Path $root 'src') -Recurse -File -Include *.cs,*.xaml |
  Where-Object {$_.FullName -notmatch '\\(bin|obj|artifacts)\\'}
$versionTagged=@($sourceFiles | Select-String -Pattern '\bV\d{2,4}[A-Za-z0-9_]*\b' -ErrorAction SilentlyContinue)
Write-Output ('CLEANUP_AUDIT backups='+$candidates.Count+
  ' duplicateScriptGroups='+$hashGroups.Count+
  ' versionTaggedSourceRefs='+$versionTagged.Count)
if($hashGroups.Count){
  foreach($g in $hashGroups){Write-Output ('DUPLICATE_SCRIPT '+(($g.Group.file)-join ' | '))}
}
if($versionTagged.Count){
  foreach($hit in $versionTagged){Write-Output ('VERSION_TAGGED_SOURCE '+$hit.Path+':'+$hit.LineNumber+' '+$hit.Line.Trim())}
}
if($Apply -and $candidates.Count){
  New-Item -ItemType Directory -Force -Path $archive|Out-Null
  foreach($f in $candidates){
    $name=($f.FullName.Substring($root.Length).TrimStart('\\') -replace '[\\/:*?"<>|]','_')
    Move-Item $f.FullName (Join-Path $archive $name) -Force
  }
  Write-Output ('CLEANUP_ARCHIVED count='+$candidates.Count+' path='+$archive)
}
$report=[ordered]@{
  generatedAt=(Get-Date).ToString('o')
  backupCandidates=@($candidates.FullName)
  duplicateScriptGroups=$hashGroups.Count
  versionTaggedSourceRefs=$versionTagged.Count
  applied=[bool]$Apply
}
New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\quality')|Out-Null
$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'artifacts\quality\cleanup-latest.json') -Encoding UTF8
Write-Output 'CLEANUP_AUDIT_OK'
