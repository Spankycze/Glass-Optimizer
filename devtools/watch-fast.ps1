[CmdletBinding()]
param([switch]$Run)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$tool=(Get-Command watchexec.exe -ErrorAction Stop).Source
$project=Split-Path $root -Leaf
if($project -ne 'Spankyho Optimizer'){throw 'WATCH_WRONG_PROJECT'}
$watch=@('src/SpankyhoOptimizer.Core','tools/CoreTests','src/SpankyhoOptimizer/Services','src/SpankyhoOptimizer/Models')
$exts='cs'
$command=@('pwsh.exe','-NoProfile','-File',(Join-Path $root 'devtools/auto-watch-check.ps1'))
$task='OPTIMIZER_CORE_TESTS'
$arguments=@('--postpone','--on-busy-update','queue','--debounce','1200ms','--emit-events-to','file','--shell','none')
foreach($path in $watch){
 $full=Join-Path $root $path
 if(!(Test-Path -LiteralPath $full -PathType Container)){throw ('WATCH_MISSING_DIRECTORY '+$path)}
 $arguments+=@('--watch',$full)
}
$arguments+=@('--exts',$exts,'--ignore','**/bin/**','--ignore','**/obj/**','--ignore','**/generated/**','--')
$arguments+=$command
if(!$Run){
 Write-Host ('WATCH_FAST_PLAN task='+$task+' postponed=true queue=true debounceMs=1200')
 foreach($path in $watch){Write-Host ('WATCH_SCOPE '+$path)}
 Write-Host ('WATCH_COMMAND '+($command -join ' '))
 Write-Host 'WATCH_AUTO_ON_NORMAL_DEV_BUILD noManualActivation=true'
 return
}
# Exclusive per-project lease: repeated dev builds cannot start a second watcher.
$state=if($project -eq 'RAM Guard'){Join-Path $root 'artifacts/.cache'}else{Join-Path $root '.ai-state'}
[IO.Directory]::CreateDirectory($state)|Out-Null
$leasePath=Join-Path $state 'dev-auto-watch.lock'
$lease=$null
try{
 $lease=[IO.File]::Open($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}catch [System.IO.IOException]{
 Write-Host 'WATCH_ALREADY_ACTIVE sharedBuilds=true'
 return
}
$log=Join-Path $state 'dev-auto-watch-runner.log'
Push-Location $root
try{
 [IO.File]::WriteAllText($log,('WATCH_STARTED '+[DateTimeOffset]::UtcNow.ToString('o')+' pid='+$PID),[Text.UTF8Encoding]::new($false))
 Write-Host ('WATCH_FAST_START task='+$task+' automatic=true singleton=true')
 # Writes only a small, current diagnostic log; test output is bounded
 # independently by auto-watch-check.ps1.
 & $tool @arguments *>> $log
 if($LASTEXITCODE -ne 0){throw ('WATCH_TOOL_FAILED '+$LASTEXITCODE)}
}finally{
 Pop-Location
 if($null -ne $lease){$lease.Dispose()}
}
