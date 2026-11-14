param(
  [string]$Baseline = 'C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe',
  [string]$Candidate = 'V:\ChatGPT Projects\Spankyho Optimizer\artifacts\memory-profile-893\optimized-app\SpankyhoOptimizer.exe',
  [string]$Report = 'V:\ChatGPT Projects\Spankyho Optimizer\artifacts\memory-profile-893\navigation-ab.json'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$navNames = @('Themes','Nastavení','Internet / Síť','Přehled')
function Measure-One([string]$executable,[string]$variant,[int]$trial) {
  if(@(Get-Process -Name SpankyhoOptimizer -ErrorAction SilentlyContinue).Count -ne 0) {throw "Optimizer already running: cannot isolate $variant"}
  $proc = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -PassThru
  try {
    for($i=0;$i -lt 100;$i++){
      Start-Sleep -Milliseconds 100
      $proc.Refresh()
      if($proc.HasExited) { throw "$variant exited early" }
      if($proc.MainWindowHandle -ne 0) { break }
    }
    if($proc.MainWindowHandle -eq 0) { throw "$variant never opened main window" }
    Start-Sleep -Seconds 1
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$proc.Id)
    $win=$root.FindFirst([System.Windows.Automation.TreeScope]::Children,$cond)
    if(!$win){throw "$variant UI automation window missing"}
    $buttons=$win.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
    $navigation=@{}
    foreach($name in $navNames) {
      foreach($button in $buttons) {
        if($button.Current.Name -eq $name){
          $navigation[$name]=$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
          break
        }
      }
    }
    if($navigation.Count -ne 4){throw "$variant navigation button missing"}
    # Warm all four sections equally to exclude first-touch native WinUI setup.
    for($warm=0;$warm -lt 2;$warm++) {
      foreach($name in $navNames) {$navigation[$name].Invoke();Start-Sleep -Milliseconds 110}
    }
    $proc.Refresh()
    $startWs=$proc.WorkingSet64
    $startPrivate=$proc.PrivateMemorySize64
    $startCpu=$proc.TotalProcessorTime.TotalMilliseconds
    $samples=@()
    $timer=[Diagnostics.Stopwatch]::StartNew()
    for($cycle=1;$cycle -le 12;$cycle++) {
      foreach($name in $navNames){
        $navigation[$name].Invoke()
        Start-Sleep -Milliseconds 110
      }
      if($cycle -in @(1,4,8,12)){
        $proc.Refresh()
        $samples+=@{ cycle=$cycle;wsMiB=[math]::Round($proc.WorkingSet64/1MB,2);privateMiB=[math]::Round($proc.PrivateMemorySize64/1MB,2)}
      }
    }
    $timer.Stop()
    $proc.Refresh()
    $result=[ordered]@{
      variant=$variant; trial=$trial; warmCycles=2; testCycles=12; navigations=48
      initialWsMiB=[math]::Round($startWs/1MB,2)
      finalWsMiB=[math]::Round($proc.WorkingSet64/1MB,2)
      deltaWsMiB=[math]::Round(($proc.WorkingSet64-$startWs)/1MB,2)
      initialPrivateMiB=[math]::Round($startPrivate/1MB,2)
      finalPrivateMiB=[math]::Round($proc.PrivateMemorySize64/1MB,2)
      deltaPrivateMiB=[math]::Round(($proc.PrivateMemorySize64-$startPrivate)/1MB,2)
      cpuMs=[math]::Round($proc.TotalProcessorTime.TotalMilliseconds-$startCpu,2)
      wallMs=$timer.ElapsedMilliseconds
      samples=$samples
    }
    Write-Host ('NAV_BENCH '+($result | ConvertTo-Json -Compress -Depth 4))
    return $result
  }
  finally {
    if(!$proc.HasExited){
      $proc.CloseMainWindow()|Out-Null
      if(!$proc.WaitForExit(8000)){
        throw "Failed to gracefully close the isolated benchmark process $variant"
      }
    }
  }
}
if(!(Test-Path $Baseline) -or !(Test-Path $Candidate)) {throw "Benchmark executable missing"}
$results=@()
$results+=Measure-One $Baseline 'installed-893' 1
$results+=Measure-One $Candidate 'prototype-893' 1
$results+=Measure-One $Baseline 'installed-893' 2
$results+=Measure-One $Candidate 'prototype-893' 2
$json=@{createdAtUtc=[DateTime]::UtcNow.ToString('o');results=$results;notes='Read-only UI navigation; no tweak Apply actions; process working set contains native WinUI/cache; trials are not fully controlled for other background activity.'} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($Report,$json,[Text.UTF8Encoding]::new($false))
'NAV_BENCH_REPORT='+$Report
