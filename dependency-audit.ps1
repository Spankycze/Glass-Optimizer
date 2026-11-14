param()
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$outDir=Join-Path $root 'artifacts\quality\dependencies'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$projects=@(
 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj',
 'tools\CoreTests\CoreTests.csproj'
)
$report=[ordered]@{utc=(Get-Date).ToUniversalTime().ToString('o');projects=@();duplicatePackageRefs=@()}
foreach($rel in $projects){
  $p=Join-Path $root $rel
  if(!(Test-Path $p)){continue}
  $out=@(& dotnet list $p package --vulnerable --include-transitive --format json 2>&1)
  if($LASTEXITCODE -ne 0){throw ('dotnet package audit failed: '+$rel+' :: '+(($out|Select-Object -Last 8)-join ' '))}
  $text=$out -join [Environment]::NewLine
  $text|Set-Content (Join-Path $outDir ((Split-Path $rel -Leaf)+'.vulnerable.json')) -Encoding UTF8
  $json=$null
  try{$json=$text|ConvertFrom-Json}catch{}
  $vuln=0
  if($json -and $json.projects){
    foreach($proj in $json.projects){
      foreach($fw in @($proj.frameworks)){
        foreach($pkg in @($fw.topLevelPackages)+@($fw.transitivePackages)){
          if($pkg.vulnerabilities){$vuln+=@($pkg.vulnerabilities).Count}
        }
      }
    }
  }
  $report.projects+=[pscustomobject]@{project=$rel;vulnerabilities=$vuln}
  if($vuln -gt 0){throw ('VULNERABLE_PACKAGE '+$rel+' count='+$vuln)}
}
$refs=@{}
Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.csproj|ForEach-Object{
  $text=[IO.File]::ReadAllText($_.FullName)
  foreach($m in [regex]::Matches($text,'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"')){
    $key=$m.Groups[1].Value;$ver=$m.Groups[2].Value
    if(!$refs.ContainsKey($key)){$refs[$key]=New-Object Collections.Generic.HashSet[string]}
    [void]$refs[$key].Add($ver)
  }
}
foreach($k in $refs.Keys){if($refs[$k].Count -gt 1){$report.duplicatePackageRefs+=[pscustomobject]@{package=$k;versions=@($refs[$k])}}}
$report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $outDir 'latest.json') -Encoding UTF8
if($report.duplicatePackageRefs.Count){throw ('DUPLICATE_PACKAGE_VERSION '+(($report.duplicatePackageRefs|ForEach-Object package)-join ','))}
Write-Host 'SPANKY_DEPENDENCY_AUDIT_OK'
