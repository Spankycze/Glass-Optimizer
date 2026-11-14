[CmdletBinding()]
param([string[]]$Files=@(),[switch]$PlanOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$project=Split-Path $root -Leaf
$events=@('WATCHEXEC_WRITTEN_PATH','WATCHEXEC_CREATED_PATH','WATCHEXEC_REMOVED_PATH','WATCHEXEC_RENAMED_PATH')
$raw=(@($events|ForEach-Object {[Environment]::GetEnvironmentVariable($_,'Process')}) -join ';')
# Watchexec 2.7 defaults to emitting NO event paths. File emission is the
# supported, non-deprecated mechanism and records one absolute path per line.
$eventFile=[Environment]::GetEnvironmentVariable('WATCHEXEC_EVENTS_FILE','Process')
$fileEvents=@()
if($eventFile -and (Test-Path -LiteralPath $eventFile -PathType Leaf)){
 foreach($line in @(Get-Content -LiteralPath $eventFile -ErrorAction Stop)){
  if($line -match '^(?:create|remove|rename|modify|other):(.+)$'){$fileEvents+= $Matches[1]}
 }
}
$relative=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($item in @($Files)+@($fileEvents)+@($raw -split '[;\r\n]+')){
 if([string]::IsNullOrWhiteSpace($item)){continue}
 $value=$item.Trim()
 try{
  $full=if([IO.Path]::IsPathRooted($value)){[IO.Path]::GetFullPath($value)}else{[IO.Path]::GetFullPath((Join-Path $root $value))}
  if(!$full.StartsWith(($root.TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){continue}
  $rel=[IO.Path]::GetRelativePath($root,$full).Replace('\','/')
  if($rel -match '(^|/)(?:bin|obj|node_modules|artifacts|generated)/'){continue}
  [void]$relative.Add($rel)
 }catch{continue}
}
$targets=@($relative|Sort-Object)
# Never run a full suite on a missing/unrecognized filesystem event.
# New/deleted files have CREATED/REMOVED vars rather than WRITTEN vars.
if($targets.Count -eq 0){
 Write-Host 'AUTO_WATCH_SKIPPED noSupportedFileChanges=true'
 return
}
if($project -eq 'RAM Guard'){
 $area='Auto'
 $quick=Join-Path $PSScriptRoot 'verify-fast.ps1'
 if($PlanOnly){
  & $quick -Area $area -Files $targets -PlanOnly
  if(-not $?){throw 'AUTO_WATCH_QUICK_PLAN_FAILED'}
  return
 }
}elseif($project -eq 'Spankyho Optimizer'){
 # The compact CoreTests project deliberately compiles just 9 critical suites.
 # Don't run/fail on historical test files that aren't compiled any longer.
 $projectXml=Get-Content -LiteralPath (Join-Path $root 'tools/CoreTests/CoreTests.csproj') -Raw
 $included=@([regex]::Matches($projectXml,'<Compile Include="([^"]+\.cs)"') |
   ForEach-Object {[IO.Path]::GetFileNameWithoutExtension($_.Groups[1].Value)})
 $testPaths=@($targets|Where-Object {$_ -match '^tools/CoreTests/.*\.cs$'})
 $selectedTestPaths=@($testPaths|Where-Object {
   [IO.Path]::GetFileNameWithoutExtension($_) -in $included
 })
 # Core.csproj compiles Models/*.cs and Services/*.cs directly (except
 # UiResourceService). Use its fast compilation for exactly those sources.
 $coreOnly= @($targets|Where-Object {
   $_ -match '^src/SpankyhoOptimizer[.]Core/[^/]+[.]cs$' -or
   ($_ -match '^src/SpankyhoOptimizer/(?:Models|Services)/[^/]+[.]cs$' -and
     $_ -ne 'src/SpankyhoOptimizer/Services/UiResourceService.cs')
 }).Count -eq $targets.Count
 if($testPaths.Count -eq $targets.Count -and $selectedTestPaths.Count -eq 0){
  Write-Host 'AUTO_WATCH_SKIPPED oldTestFileNotInCompactSuite=true'
  return
 }
 if($PlanOnly){
  $isPower=@($targets|Where-Object {$_ -match '(^|/)PerformanceProfilesService[.]cs$'}).Count -gt 0
  $gate=if($isPower -or $selectedTestPaths.Count -gt 0){'FocusedCoreTests'}elseif($coreOnly){'CoreCompile'}else{'Quality43'}
  'AUTO_WATCH_TEST_PLAN project=Optimizer files='+$targets.Count+' suite='+$gate+' excludedTestFiles='+($testPaths.Count-$selectedTestPaths.Count)
  foreach($file in $targets){'AUTO_WATCH_CHANGED '+$file}
  return
 }
}else{throw 'AUTO_WATCH_UNKNOWN_PROJECT'}
# Watchexec can report removed/renamed paths. An absent ACTIVE source is not
# verified merely because an unrelated quality suite happens to pass.
# Keep the excluded legacy CoreTests early skip above unchanged.
foreach($file in $targets){
 if(!(Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)){
  throw ('AUTO_WATCH_ACTIVE_SOURCE_MISSING '+$file)
 }
}
# Strict releases already execute their own complete verification. Avoid
# concurrent background watch checks competing for the same build/test inputs.
# This read-only guard never terminates or modifies the release process.
$releasePattern=if($project -eq 'RAM Guard'){'build-current[.]ps1|publish-current[.]ps1'}else{'build-release[.]ps1'}
$ongoing=@(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" -ErrorAction SilentlyContinue |
 Where-Object {
  $_.ProcessId -ne $PID -and $_.CommandLine -match $releasePattern -and
  $_.CommandLine -like ('*'+$root+'*')
 })
if($ongoing.Count -gt 0){
 Write-Host ('AUTO_WATCH_SKIPPED strictReleaseActive=true count='+$ongoing.Count)
 return
}
$state=if($project -eq 'RAM Guard'){Join-Path $root 'artifacts/.cache'}else{Join-Path $root '.ai-state'}
[IO.Directory]::CreateDirectory($state)|Out-Null
# Content-based no-op suppression: only a PREVIOUSLY PASSING, byte-identical
# source set may skip work. A real source change always runs relevant tests.
$stampFile=Join-Path $state 'dev-auto-watch-success-fingerprint.json'
$canFingerprint=$true
$stampLines=@('source-watch-v1',$project)
foreach($file in $targets){
 $absolute=Join-Path $root $file
 if(!(Test-Path -LiteralPath $absolute -PathType Leaf)){$canFingerprint=$false;break}
 $stampLines+=($file+'='+((Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash))
}
if($canFingerprint){
 $deps=@('devtools/auto-watch-check.ps1')
 if($project -eq 'RAM Guard'){
  $deps+=@('devtools/verify-fast.ps1','devtools/quick-bun-batch.test.cjs')
 }else{
  # Hash only inputs of the verification mode actually selected for this
  # change. A normal Core compile shouldn't hash 9 unrelated test sources.
  $deps+=@('Directory.Build.props','src/SpankyhoOptimizer.Core/SpankyhoOptimizer.Core.csproj')
  $powerSource=@($targets|Where-Object {$_ -match '(^|/)PerformanceProfilesService\.cs$'}).Count -gt 0
  if($powerSource -or $selectedTestPaths.Count -gt 0){
   $deps+=@('tools/CoreTests/CoreTests.csproj')
   # The same compact test sources that actually run are dependencies.
   $source=[xml](Get-Content -LiteralPath (Join-Path $root 'tools/CoreTests/CoreTests.csproj') -Raw)
   $deps+=@($source.SelectNodes('//Compile[@Include]')|ForEach-Object {'tools/CoreTests/'+$_.Include})
  }elseif(!$coreOnly){
   $deps+=@('tools/Spankyho.QualityTests/Spankyho.QualityTests.csproj')
   # The quality suite has just three source files; exclude obj/bin files.
   $deps+=@(Get-ChildItem -LiteralPath (Join-Path $root 'tools/Spankyho.QualityTests') -File -Filter '*.cs' |
    ForEach-Object {'tools/Spankyho.QualityTests/'+$_.Name})
  }
 }
 foreach($dep in $deps){
  $absolute=Join-Path $root $dep
  if(!(Test-Path -LiteralPath $absolute -PathType Leaf)){$canFingerprint=$false;break}
  $stampLines+=($dep+'='+((Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash))
 }
}
$fingerprint=''
if($canFingerprint){
 $payload=($stampLines -join [Environment]::NewLine)
 $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($payload)))
 if(Test-Path -LiteralPath $stampFile -PathType Leaf){
  try{
   $prior=Get-Content -LiteralPath $stampFile -Raw|ConvertFrom-Json
   if($prior.schema -eq 1 -and $prior.fingerprint -ceq $fingerprint -and $prior.passed -eq $true){
    Write-Host ('AUTO_WATCH_SKIPPED contentUnchanged=true files='+$targets.Count)
    return
   }
  }catch{Write-Verbose 'Watch cache unreadable; verifying rather than skipping'}
 }
}
$log=Join-Path $state 'dev-auto-watch-last-test.log'
$sw=[Diagnostics.Stopwatch]::StartNew()
Push-Location $root
try{
 if($project -eq 'RAM Guard'){
  $out=& $quick -Area Auto -Files $targets *>&1 |Out-String
  $pass=$?
 }else{
  # Recheck only the appropriate fast suite on each edit; the full CoreTests
  # collection is too costly for every source-save event. Full strict release
  # tests continue unchanged.
  $verification='Tests'
  $powerChange=@($targets|Where-Object {$_ -match '(^|/)PerformanceProfilesService\.cs$'}).Count -gt 0
  $testClasses=@($selectedTestPaths|
    ForEach-Object {[IO.Path]::GetFileNameWithoutExtension($_)}|Sort-Object -Unique)
  if($powerChange){
   $filter='FullyQualifiedName~PowerPlanInventoryRowSafety207Tests|FullyQualifiedName~DuplicatePowerPlanCleanupPreflight203Tests|FullyQualifiedName~PerformanceApplyPowerPreflight204Tests'
   $out=& dotnet.exe test (Join-Path $root 'tools/CoreTests/CoreTests.csproj') -c Release --no-restore --nologo -v:q --filter $filter *>&1|Out-String
   $pass=($LASTEXITCODE -eq 0)
  }elseif($testClasses.Count -gt 0){
   $filter=(@($testClasses|ForEach-Object {'FullyQualifiedName~'+$_}) -join '|')
   $out=& dotnet.exe test (Join-Path $root 'tools/CoreTests/CoreTests.csproj') -c Release --no-restore --nologo -v:q --filter $filter *>&1|Out-String
   $pass=($LASTEXITCODE -eq 0) -and $out -notmatch '(?i)No test matches|Žádný test'
  }elseif($coreOnly){
   # Ordinary Core edit: compiling all Core sources is enough for the
   # every-save gate (warm 0.38s vs Quality tests 4.8s measured). Full
   # Quality43 and release checks remain in their original workflows.
   $verification='CompileOnly'
   $out=& dotnet.exe build (Join-Path $root 'src/SpankyhoOptimizer.Core/SpankyhoOptimizer.Core.csproj') -c Release --no-restore --nologo -v:q *>&1|Out-String
   $pass=($LASTEXITCODE -eq 0)
  }else{
   # Other application service changes retain the existing Quality suite.
   $out=& dotnet.exe test (Join-Path $root 'tools/Spankyho.QualityTests/Spankyho.QualityTests.csproj') -c Release --no-restore --nologo -v:q *>&1|Out-String
   $pass=($LASTEXITCODE -eq 0)
  }
  # A test command that matched zero cases must never produce green.
  $pass=$pass -and ($verification -eq 'CompileOnly' -or $out -match '(?:Celkem:|Total:)\s*[1-9][0-9]*')
 }
 # Verify the exact input snapshot before publishing a green result.
 # A file saved while tests run must never report the NEW content as checked.
 $stale=$false
 if($pass -and $canFingerprint -and $fingerprint){
  $after=@('source-watch-v1',$project)
  try{
   foreach($file in @($targets)+@($deps)){
    $absolute=Join-Path $root $file
    if(!(Test-Path -LiteralPath $absolute -PathType Leaf)){$stale=$true;break}
    $after+=($file+'='+((Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash))
   }
   if(!$stale){
    $actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($after -join [Environment]::NewLine))))
    $stale=($actual -cne $fingerprint)
   }
  }catch{$stale=$true}
 }
 $sw.Stop()
 $success=$pass -and !$stale -and $out -notmatch '(?m)^Exception:'
 $lines=@($out -split "[\r\n]+"|Select-Object -Last 65)
 $checkType=if($project -eq 'RAM Guard'){'Fixtures'}else{$verification}
 $header='AUTO_WATCH_RESULT project='+$project+' pass='+$success+' check='+$checkType+' ms='+[Math]::Round($sw.Elapsed.TotalMilliseconds)+' files='+$targets.Count+' utc='+[DateTimeOffset]::UtcNow.ToString('o')
 [IO.File]::WriteAllLines($log,[string[]](@($header)+$lines),[Text.UTF8Encoding]::new($false))
 Write-Host $header
 if(-not $success){throw ('AUTO_WATCH_TEST_FAILED inspect='+$log)}
 # Commit the no-op cache only AFTER a genuine passing test run. Updating
 # this ignored state file never triggers the source watcher.
 if($canFingerprint -and $fingerprint){
  $temp=Join-Path $state ('watch-success-'+[guid]::NewGuid().ToString('N')+'.tmp')
  try{
   $record=@{schema=1;fingerprint=$fingerprint;passed=$true;files=$targets.Count;at=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Compress
   [IO.File]::WriteAllText($temp,$record,[Text.UTF8Encoding]::new($false))
   [IO.File]::Move($temp,$stampFile,$true)
  }catch{Write-Warning ('AUTO_WATCH_FINGERPRINT_CACHE_NONFATAL '+$_.Exception.Message)}
  finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
 }
}finally{Pop-Location}
