param([switch]$Deep)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
Push-Location $root
try{
  $changed=@(& git diff --name-only --diff-filter=ACMR HEAD -- 2>$null)
  $changed+=@(& git ls-files --others --exclude-standard 2>$null)
}finally{Pop-Location}
$changed=@($changed|Where-Object{
  $_ -and
  $_ -notmatch '(^|[\/])_' -and
  $_ -notmatch '^(artifacts|node_modules|third_party)[\/]' -and
  $_ -notmatch '[\/](bin|obj)[\/]'
}|Sort-Object -Unique)
if(!$changed.Count){Write-Host 'SPANKY_CHANGED_FILES_OK none';exit 0}
$cs=@($changed|Where-Object{$_ -like '*.cs'})
$xaml=@($changed|Where-Object{$_ -like '*.xaml'})
$ps=@($changed|Where-Object{$_ -like '*.ps1'})
if($ps.Count){
  foreach($rel in $ps){
    $p=Join-Path $root $rel
    if(!(Test-Path $p)){continue}
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($p,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw ('PowerShell parse failed: '+$rel+' :: '+(($errors|ForEach-Object Message)-join '; '))}
  }
}
if($cs.Count -or $xaml.Count){
  $app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
  & dotnet build $app -c Release --no-restore -p:TreatWarningsAsErrors=false -v:minimal
  if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
}
if($Deep -and (Test-Path (Join-Path $root 'artifacts\quality\analyzers\extended-analyzers.latest.txt'))){
  & (Join-Path $root 'analyzer-regression-gate.ps1')
  if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
}
Write-Host ('SPANKY_CHANGED_FILES_OK count='+$changed.Count+' cs='+$cs.Count+' xaml='+$xaml.Count+' ps='+$ps.Count)
