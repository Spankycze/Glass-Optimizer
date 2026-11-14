param([ValidateSet('repair','ui')][string]$Mode='repair',[int]$Runs=5)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(!(Test-Path $exe)){throw "Missing executable $exe"}
$arg=if($Mode -eq 'repair'){'--repair-scan-parallel-smoke'}else{'--performance-smoke'}
for($i=1;$i -le $Runs;$i++){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $p=Start-Process -FilePath $exe -ArgumentList $arg -PassThru -WindowStyle Hidden
  $peakWs=0L; $peakPrivate=0L; $cpuMs=0d
  try {
    while(-not $p.WaitForExit(40)){
      $p.Refresh()
      try{
        $peakWs=[Math]::Max($peakWs,$p.WorkingSet64)
        $peakPrivate=[Math]::Max($peakPrivate,$p.PrivateMemorySize64)
        $cpuMs=$p.TotalProcessorTime.TotalMilliseconds
      }catch{}
      if($sw.Elapsed.TotalSeconds -gt 150){$p.Kill();throw "Smoke timeout"}
    }
    $p.Refresh()
    try{$cpuMs=$p.TotalProcessorTime.TotalMilliseconds}catch{}
    [pscustomobject]@{Mode=$Mode; Run=$i;Exit=$p.ExitCode;
      WallMs=[Math]::Round($sw.Elapsed.TotalMilliseconds,2);
      CpuMs=[Math]::Round($cpuMs,2);
      PeakWSMB=[Math]::Round($peakWs/1MB,2);
      PeakPrivateMB=[Math]::Round($peakPrivate/1MB,2)
    }|ConvertTo-Json -Compress
  }
  finally{$p.Dispose()}
}