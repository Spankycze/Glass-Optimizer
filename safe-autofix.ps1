param()
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
Push-Location $root
try{
  $changed=@(& git diff --name-only --diff-filter=ACMR HEAD -- 2>$null)
  $changed+=@(& git ls-files --others --exclude-standard 2>$null)
}finally{Pop-Location}
$cs=@($changed|Where-Object{$_ -like '*.cs'}|ForEach-Object{Join-Path $root $_}|Where-Object{Test-Path $_})
$ps=@($changed|Where-Object{$_ -like '*.ps1'}|ForEach-Object{Join-Path $root $_}|Where-Object{Test-Path $_})
foreach($p in $ps){
  $lines=[IO.File]::ReadAllLines($p)|ForEach-Object{$_.TrimEnd()}
  [IO.File]::WriteAllLines($p,$lines,[Text.UTF8Encoding]::new($false))
}
if($cs.Count){
  $proj=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
  $args=@('format',$proj,'whitespace','--no-restore','--include')+$cs
  & dotnet @args
  if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
}
Write-Host ('SPANKY_SAFE_AUTOFIX_OK cs='+$cs.Count+' ps='+$ps.Count)
