param(
 [string]$Exe = 'C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe',
 [string]$Label = 'baseline-894',
 [string]$Output = 'V:\ChatGPT Projects\Spankyho Optimizer\artifacts\memory-profile-894\multi-section-baseline.json',
 [int]$Cycles = 8,
 [int]$WarmupCycles = 0,
 [int]$WarmupIdleMs = 0,
 [int]$PauseMs = 105,
 [switch]$ProfileSections,
 [switch]$ProfileManagedHeap,
 [int]$IdleTailMs = 0,
 [int]$IdleSampleMs = 1000,
 [string]$SectionsCsv = ''
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
$names=@('Přehled','Systémové tweaky','Power Plan','Internet / Síť','GPU / Display','Úložiště / NVMe','Nastavení','USB / HID','Po spuštění','UI / Explorer','Themes','Windows Update')
if($SectionsCsv){
  $selected=@($SectionsCsv.Split(',') | ForEach-Object {$_.Trim()} | Where-Object {$_})
  if($selected.Count -lt 2 -or @($selected | Select-Object -Unique).Count -ne $selected.Count){
    throw 'SectionsCsv must contain at least two distinct sidebar button names'
  }
  foreach($section in $selected){if($names -cnotcontains $section){throw ('Unknown sidebar section: '+$section)}}
  $names=$selected
}
if(@(Get-Process -Name SpankyhoOptimizer -ErrorAction SilentlyContinue).Count){throw 'Other Optimizer process running'}
$pr=Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
try{
  for($i=0;$i -lt 80;$i++){Start-Sleep -Milliseconds 100;$pr.Refresh();if($pr.HasExited){throw 'App exited early'};if($pr.MainWindowHandle){break}}
  if(!$pr.MainWindowHandle){throw 'App main window missing'}
  $root=[System.Windows.Automation.AutomationElement]::RootElement
  $cond=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$pr.Id)
  $win=$null
  for($i=0;$i -lt 40;$i++){$win=$root.FindFirst([System.Windows.Automation.TreeScope]::Children,$cond);if($win){break};Start-Sleep -Milliseconds 100}
  if(!$win){throw 'UIAutomation window missing'}
  $nav=@{}
  $rawView=[System.Windows.Automation.TreeWalker]::RawViewWalker
  $buttonCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)
  for($attempt=0;$attempt -lt 80;$attempt++){
    $nav.Clear()
    $list=$win.FindAll([System.Windows.Automation.TreeScope]::Descendants,$buttonCondition)
    foreach($button in $list){
      $buttonName=$button.Current.Name
      if($names -ccontains $buttonName){
        $nav[$buttonName]=$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
      }
    }
    if($attempt -eq 0){Write-Host ('UIA_DIAG requested='+$names.Count+' matched='+$nav.Count+' keys='+($nav.Keys -join '|'))}
    if($nav.Count -eq $names.Count){break}
    Start-Sleep -Milliseconds 100
  }
  if($nav.Count -ne $names.Count){
    $sessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $appSession=$pr.SessionId
    $ownDesktop=[Environment]::UserInteractive
    Write-Warning ('UIA_SESSION_DIAG runnerSession='+$sessionId+' appSession='+$appSession+' userInteractive='+$ownDesktop)
    $availableNames=@($list | ForEach-Object {$_.Current.Name} | Where-Object {$_} | Select-Object -Unique)
    $allElements=$win.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    $windowClass=$win.Current.ClassName
    $rawChildren=@()
    $rawChild=$rawView.GetFirstChild($win)
    while($null -ne $rawChild -and $rawChildren.Count -lt 30){
      $rawChildren+=($rawChild.Current.ControlType.ProgrammaticName+':'+$rawChild.Current.Name+':'+$rawChild.Current.ClassName)
      $rawChild=$rawView.GetNextSibling($rawChild)
    }
    Write-Warning ('UIA_RAW_CHILDREN '+($rawChildren -join ' | '))
    $bridge=$win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'Microsoft.UI.Content.DesktopChildSiteBridge'))
    if($null -ne $bridge){
      $bridgeButtons=$bridge.FindAll([System.Windows.Automation.TreeScope]::Descendants,$buttonCondition)
      $bridgeNodes=$bridge.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
      $bridgeRawFirst=$rawView.GetFirstChild($bridge)
      Write-Warning ('UIA_BRIDGE_DIAG buttons='+$bridgeButtons.Count+' descendants='+$bridgeNodes.Count+' rawFirst='+$(if($bridgeRawFirst){$bridgeRawFirst.Current.ControlType.ProgrammaticName+':'+$bridgeRawFirst.Current.ClassName}else{'none'}))
    }
    $windowName=$win.Current.Name
    $treeKinds=@($allElements | ForEach-Object { $_.Current.ControlType.ProgrammaticName+':'+$_.Current.Name } | Select-Object -First 12)
    Write-Warning ('UIA_TREE_NODES '+($treeKinds -join ' | '))
    Write-Warning ('UIA_TREE_DIAG window='+$windowName+' class='+$windowClass+' descendants='+$allElements.Count+' buttons='+$list.Count+' pid='+$pr.Id)
    $diagnostic=if($list.Count -eq 0 -and $allElements.Count -le 5){
      'UIA XAML content unavailable (outer WinUI bridge only): descendants='+$allElements.Count+'; buttons=0; check XAML accessibility provider before benchmarking navigation'
    }else{
      'Sidebar incomplete after 8s: matched='+$nav.Count+'/'+$names.Count+'; buttonsFound='+$list.Count+'; available='+($availableNames -join ' | ')
    }
    Write-Warning $diagnostic
    throw $diagnostic
  }
  if($WarmupCycles -lt 0 -or $WarmupCycles -gt 30){throw 'Invalid warmup cycle count'}
  for($warm=0;$warm -lt $WarmupCycles;$warm++){
    foreach($name in $names){$nav[$name].Invoke();Start-Sleep -Milliseconds $PauseMs}
  }
  if($WarmupIdleMs -lt 0 -or $WarmupIdleMs -gt 60000){throw 'WarmupIdleMs must be 0..60000'}
  if($WarmupIdleMs -gt 0){Start-Sleep -Milliseconds $WarmupIdleMs}
  $samples=@()
  $managedSamples=@()
  $bySection=@{}
  if($ProfileSections){foreach($name in $names){$bySection[$name]=[ordered]@{cpuMs=0.0;wallMs=0.0;count=0;wsDeltaMiB=0.0;privateDeltaMiB=0.0;handlesDelta=0}}}
  $pr.Refresh()
  $startWs=$pr.WorkingSet64;$startPriv=$pr.PrivateMemorySize64
  $startCpu=$pr.TotalProcessorTime.TotalMilliseconds
  $sw=[Diagnostics.Stopwatch]::StartNew()
  for($cycle=1;$cycle -le $Cycles;$cycle++){
    foreach($name in $names){
      if($ProfileSections){$pr.Refresh();$sectionCpuStart=$pr.TotalProcessorTime.TotalMilliseconds;$sectionWsStart=$pr.WorkingSet64;$sectionPrivateStart=$pr.PrivateMemorySize64;$sectionHandleStart=$pr.HandleCount;$sectionTime=[Diagnostics.Stopwatch]::StartNew()}
      $nav[$name].Invoke()
      Start-Sleep -Milliseconds $PauseMs
      if($ProfileSections){
        $sectionTime.Stop();$pr.Refresh()
        $bySection[$name].cpuMs+=$pr.TotalProcessorTime.TotalMilliseconds-$sectionCpuStart
        $bySection[$name].wallMs+=$sectionTime.Elapsed.TotalMilliseconds
        $bySection[$name].wsDeltaMiB+=($pr.WorkingSet64-$sectionWsStart)/1MB
        $bySection[$name].privateDeltaMiB+=($pr.PrivateMemorySize64-$sectionPrivateStart)/1MB
        $bySection[$name].handlesDelta+=$pr.HandleCount-$sectionHandleStart
        $bySection[$name].count++
      }
    }
    $pr.Refresh()
    $sample=[ordered]@{cycle=$cycle;workingSetMiB=[math]::Round($pr.WorkingSet64/1MB,2);privateMiB=[math]::Round($pr.PrivateMemorySize64/1MB,2);handles=$pr.HandleCount;cpuMs=[math]::Round($pr.TotalProcessorTime.TotalMilliseconds-$startCpu,1);elapsedMs=$sw.ElapsedMilliseconds}
    $samples+=$sample
    if($ProfileManagedHeap){
      # Read-only CLR counters; inaccessible runtimes are reported as null.
      $managed=[ordered]@{cycle=$cycle;gcHeapMiB=$null;gen2Count=$null;status='NOT_AVAILABLE'}
      try{
        $counter=Get-Counter -Counter '\\.NET CLR Memory(SpankyhoOptimizer)\\# Bytes in all Heaps' -ErrorAction Stop
        $managed.gcHeapMiB=[math]::Round($counter.CounterSamples[0].CookedValue/1MB,2)
        $managed.status='AVAILABLE'
      }catch{}
      $managedSamples+=$managed
    }
    Write-Host ('CYCLE '+($sample|ConvertTo-Json -Compress))
  }
  $sw.Stop();$pr.Refresh()
  $endCpuMs=$pr.TotalProcessorTime.TotalMilliseconds
  $endWs=$pr.WorkingSet64
  $endPrivate=$pr.PrivateMemorySize64
  $idleCpuMs=$null;$idleFinalWsMiB=$null;$idleFinalPrivateMiB=$null;$idleHandleCount=$null
  $idleSamples=@()
  if($IdleTailMs -gt 0){
    if($IdleSampleMs -lt 200 -or $IdleSampleMs -gt 60000){throw 'IdleSampleMs must be 200..60000'}
    $idleElapsed=0;$previousCpuMs=$endCpuMs
    while($idleElapsed -lt $IdleTailMs){
      $step=[math]::Min($IdleSampleMs,$IdleTailMs-$idleElapsed)
      Start-Sleep -Milliseconds $step
      $idleElapsed+=$step
      $pr.Refresh()
      $nowCpuMs=$pr.TotalProcessorTime.TotalMilliseconds
      $idleSamples+=([ordered]@{
        elapsedMs=$idleElapsed
        cpuDeltaMs=[math]::Round($nowCpuMs-$previousCpuMs,2)
        workingSetMiB=[math]::Round($pr.WorkingSet64/1MB,2)
        privateMiB=[math]::Round($pr.PrivateMemorySize64/1MB,2)
        handles=$pr.HandleCount
      })
      $previousCpuMs=$nowCpuMs
    }
    $idleCpuMs=[math]::Round($previousCpuMs-$endCpuMs,2)
    $idleFinalWsMiB=[math]::Round($pr.WorkingSet64/1MB,2)
    $idleFinalPrivateMiB=[math]::Round($pr.PrivateMemorySize64/1MB,2)
    $idleHandleCount=$pr.HandleCount
  }
  $result=[ordered]@{
    label=$Label;cycles=$Cycles;warmupCycles=$WarmupCycles;warmupIdleMs=$WarmupIdleMs;sections=$names;actions=$Cycles*$names.Count;pauseMs=$PauseMs;profileSections=[bool]$ProfileSections
    baselineWsMiB=[math]::Round($startWs/1MB,2);finalWsMiB=[math]::Round($endWs/1MB,2)
    wsGrowthMiB=[math]::Round(($endWs-$startWs)/1MB,2)
    baselinePrivateMiB=[math]::Round($startPriv/1MB,2);finalPrivateMiB=[math]::Round($endPrivate/1MB,2)
    privateGrowthMiB=[math]::Round(($endPrivate-$startPriv)/1MB,2)
    processCpuMs=[math]::Round($endCpuMs-$startCpu,1);wallMs=$sw.ElapsedMilliseconds;samples=$samples
    sectionCpuProfile=if($ProfileSections){$bySection}else{$null}
    profileManagedHeap=[bool]$ProfileManagedHeap;managedHeapSamples=$managedSamples
    idleTailMs=$IdleTailMs;idleSampleMs=$IdleSampleMs;idleCpuMs=$idleCpuMs
    idleFinalWsMiB=$idleFinalWsMiB;idleFinalPrivateMiB=$idleFinalPrivateMiB;idleHandleCount=$idleHandleCount
    idleSamples=$idleSamples
  }
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Output))|Out-Null
  [IO.File]::WriteAllText($Output,($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
  Write-Host ('BENCH_FINAL '+($result|ConvertTo-Json -Compress -Depth 4))
} finally {
  # Do not mask the original UIAutomation failure with a secondary shutdown error.
  if(!$pr.HasExited){
    try {
      $ok=$pr.CloseMainWindow()
      if(!$ok -or !$pr.WaitForExit(3000)){
        Write-Warning 'Benchmark-owned GUI did not close gracefully; cleaning up only its process.'
        $pr.Kill()
        $null=$pr.WaitForExit(5000)
      }
    }catch{Write-Warning ('Benchmark-owned GUI cleanup failed: '+$_.Exception.Message)}
  }
}
