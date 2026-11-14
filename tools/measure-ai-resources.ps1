param([ValidateRange(1,3600)][int]$Seconds=12)
$ErrorActionPreference='Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SpankyIo {
 [StructLayout(LayoutKind.Sequential)] public struct Counts { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetProcessIoCounters(IntPtr h, out Counts c);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
'@
$procs=Get-Process | Where-Object { $_.ProcessName -match '^SpankyhoOptimizer' }
$ids=@($procs.Id)
$rows=[System.Collections.Generic.List[object]]::new()
for($step=0;$step -le $Seconds;$step++){
 foreach($id in $ids){
  try{
   $p=Get-Process -Id $id -ErrorAction Stop
   $io=[SpankyIo+Counts]::new()
   $native=[SpankyIo]::OpenProcess(0x1000,$false,$id)
   $valid=$native -ne [IntPtr]::Zero -and [SpankyIo]::GetProcessIoCounters($native,[ref]$io)
   if($native -ne [IntPtr]::Zero){[void][SpankyIo]::CloseHandle($native)}
   $rows.Add([pscustomobject]@{second=$step;name=$p.ProcessName;pid=$id;cpuMs=[math]::Round($p.TotalProcessorTime.TotalMilliseconds,2);workingMiB=[math]::Round($p.WorkingSet64/1MB,2);privateMiB=[math]::Round($p.PrivateMemorySize64/1MB,2);handles=$p.HandleCount;threads=$p.Threads.Count;readBytes=$(if($valid){$io.ReadTransferCount}else{$null});writeBytes=$(if($valid){$io.WriteTransferCount}else{$null});readOps=$(if($valid){$io.ReadOperationCount}else{$null});writeOps=$(if($valid){$io.WriteOperationCount}else{$null})})
  }catch{ if($step -eq 0){ Write-Output "SAMPLE_ERROR pid=$id $($_.Exception.Message)" } }
 }
 if($step -lt $Seconds){Start-Sleep -Seconds 1}
}
$stamp=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$path=Join-Path $PSScriptRoot "ai-resource-baseline-$stamp.csv"
$rows | Export-Csv -Path $path -NoTypeInformation -Encoding UTF8
$summary=@()
foreach($group in ($rows|Group-Object pid)){
 $first=$group.Group[0];$last=$group.Group[-1]
 $elapsedSec=($last.second-$first.second)
 $cpuDelta=[math]::Round($last.cpuMs-$first.cpuMs,2)
 $peakWorkingMiB=($group.Group | Measure-Object workingMiB -Maximum).Maximum
 $peakPrivateMiB=($group.Group | Measure-Object privateMiB -Maximum).Maximum
 $peakHandles=($group.Group | Measure-Object handles -Maximum).Maximum
 $peakThreads=($group.Group | Measure-Object threads -Maximum).Maximum
 $exePath=$null; $exeVersion=$null; $startedUtc=$null
 try {
  $processDetails=Get-Process -Id $first.pid -ErrorAction Stop
  $exePath=$processDetails.Path
  if($exePath){$exeVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion}
  $startedUtc=$processDetails.StartTime.ToUniversalTime().ToString('o')
 }catch{}
 $result=[pscustomobject]@{pid=$first.pid;name=$first.name;exePath=$exePath;exeFileVersion=$exeVersion;processStartUtc=$startedUtc;samples=$group.Count;elapsedSec=$elapsedSec;cpuDeltaMs=$cpuDelta;cpuAllCoresPct=$(if($elapsedSec -gt 0){[math]::Round($cpuDelta/(1000*$elapsedSec*[Environment]::ProcessorCount)*100,3)}else{$null});workingStartMiB=$first.workingMiB;workingEndMiB=$last.workingMiB;peakWorkingMiB=$peakWorkingMiB;peakPrivateMiB=$peakPrivateMiB;peakHandles=$peakHandles;peakThreads=$peakThreads;privateStartMiB=$first.privateMiB;privateEndMiB=$last.privateMiB;handlesStart=$first.handles;handlesEnd=$last.handles;threadsStart=$first.threads;threadsEnd=$last.threads;writeBytesDelta=$(if($null -ne $last.writeBytes -and $null -ne $first.writeBytes){[long]($last.writeBytes-$first.writeBytes)}else{$null});readBytesDelta=$(if($null -ne $last.readBytes -and $null -ne $first.readBytes){[long]($last.readBytes-$first.readBytes)}else{$null});writeOpsDelta=$(if($null -ne $last.writeOps -and $null -ne $first.writeOps){[long]($last.writeOps-$first.writeOps)}else{$null});readOpsDelta=$(if($null -ne $last.readOps -and $null -ne $first.readOps){[long]($last.readOps-$first.readOps)}else{$null})}
 $summary += $result
 $result | ConvertTo-Json -Compress
}
$summaryPath=[System.IO.Path]::ChangeExtension($path,'.summary.json')
ConvertTo-Json -InputObject $summary -Depth 5 | Set-Content -Path $summaryPath -Encoding utf8
Write-Output "RESOURCE_BASELINE_CSV=$path"
Write-Output "RESOURCE_SUMMARY_JSON=$summaryPath"
