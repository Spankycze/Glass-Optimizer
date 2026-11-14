$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$src=Join-Path $root 'src\SpankyhoOptimizer'
$files=Get-ChildItem $src -Recurse -File -Include *.cs,*.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$violations=[Collections.Generic.List[string]]::new()

foreach($file in $files){
  $lines=Get-Content -LiteralPath $file.FullName
  for($i=0;$i -lt $lines.Count;$i++){
    $line=$lines[$i]
    $trim=$line.Trim()
    if($trim -match 'Colors\.Black|Brushes\.Black|#(?:FF|DD|CC|AA)000000'){
      $violations.Add(('{0}:{1}: explicit black token' -f $file.FullName,($i+1)))
      continue
    }
    if($line -match 'Background\s*=\s*ThemeColor\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)'){
      $a=[int]$Matches[1]; $r=[int]$Matches[2]; $g=[int]$Matches[3]; $b=[int]$Matches[4]
      if($a -ge 128 -and $r -le 20 -and $g -le 20 -and $b -le 20){
        $violations.Add(('{0}:{1}: opaque near-black Background A={2} RGB={3},{4},{5}' -f $file.FullName,($i+1),$a,$r,$g,$b))
      }
    }
    if($line -match 'Background\s*=\s*ThemeColor\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)'){
      $r=[int]$Matches[1]; $g=[int]$Matches[2]; $b=[int]$Matches[3]
      if($r -le 20 -and $g -le 20 -and $b -le 20){
        $violations.Add(('{0}:{1}: solid near-black Background RGB={2},{3},{4}' -f $file.FullName,($i+1),$r,$g,$b))
      }
    }
    if($line -match 'overlay\.Background\s*=\s*ThemeColor\(\s*(\d+)'){
      $a=[int]$Matches[1]
      if($a -gt 112){
        $violations.Add(('{0}:{1}: modal overlay alpha too high ({2})' -f $file.FullName,($i+1),$a))
      }
    }
  }
}
if($violations.Count -gt 0){
  $violations | ForEach-Object { Write-Host $_ -ForegroundColor Red }
  throw ('Black surface audit failed with {0} violation(s).' -f $violations.Count)
}
Write-Host ('BLACK_SURFACE_AUDIT_OK files={0}' -f $files.Count)
