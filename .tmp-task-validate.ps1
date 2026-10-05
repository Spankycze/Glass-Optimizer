$ErrorActionPreference='Continue'
$xml='C:\Program Files\SpankyhoOptimizer\HardwareBridge\hardware-telemetry-bridge-task.xml'
$out='C:\\ProgramData\\SpankyhoOptimizer\\hardware-bridge-task-validate.txt'
$psi=New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName='schtasks.exe'
$psi.Arguments='/Create /F /TN "\SpankyhoOptimizer-HardwareTelemetryBridge" /XML "'+$xml+'"'
$psi.UseShellExecute=$false
$psi.CreateNoWindow=$true
$psi.RedirectStandardOutput=$true
$psi.RedirectStandardError=$true
$p=[Diagnostics.Process]::Start($psi)
$stdout=$p.StandardOutput.ReadToEnd()
$stderr=$p.StandardError.ReadToEnd()
$p.WaitForExit()
[IO.File]::WriteAllText($out,('EXIT='+$p.ExitCode+[Environment]::NewLine+'STDOUT='+$stdout+[Environment]::NewLine+'STDERR='+$stderr))
exit $p.ExitCode