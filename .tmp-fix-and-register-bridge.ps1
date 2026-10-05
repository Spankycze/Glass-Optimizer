$xml='C:\Program Files\SpankyhoOptimizer\HardwareBridge\hardware-telemetry-bridge-task.xml'
$result='C:\\ProgramData\\SpankyhoOptimizer\\hardware-bridge-register-result.txt'
$raw=[IO.File]::ReadAllText($xml)
$raw=$raw -replace '<LogonType>ServiceAccount</LogonType>',''
$raw=$raw -replace 'encoding="UTF-8"','encoding="UTF-16"'
[IO.File]::WriteAllText($xml,$raw,[Text.Encoding]::Unicode)
$psi=New-Object Diagnostics.ProcessStartInfo
$psi.FileName='schtasks.exe'
$psi.Arguments='/Create /F /TN "\SpankyhoOptimizer-HardwareTelemetryBridge" /XML "'+$xml+'"'
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$p=[Diagnostics.Process]::Start($psi); $so=$p.StandardOutput.ReadToEnd(); $se=$p.StandardError.ReadToEnd(); $p.WaitForExit()
if($p.ExitCode -ne 0){ [IO.File]::WriteAllText($result,('CREATE_EXIT='+$p.ExitCode+[Environment]::NewLine+$so+[Environment]::NewLine+$se)); exit $p.ExitCode }
$psi2=New-Object Diagnostics.ProcessStartInfo
$psi2.FileName='schtasks.exe'
$psi2.Arguments='/Run /TN "\SpankyhoOptimizer-HardwareTelemetryBridge"'
$psi2.UseShellExecute=$false; $psi2.CreateNoWindow=$true; $psi2.RedirectStandardOutput=$true; $psi2.RedirectStandardError=$true
$p2=[Diagnostics.Process]::Start($psi2); $so2=$p2.StandardOutput.ReadToEnd(); $se2=$p2.StandardError.ReadToEnd(); $p2.WaitForExit()
[IO.File]::WriteAllText($result,('CREATE_EXIT='+$p.ExitCode+[Environment]::NewLine+$so+[Environment]::NewLine+$se+[Environment]::NewLine+'RUN_EXIT='+$p2.ExitCode+[Environment]::NewLine+$so2+[Environment]::NewLine+$se2))
exit $p2.ExitCode