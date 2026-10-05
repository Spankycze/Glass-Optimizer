$ErrorActionPreference='Stop'
$xml='C:\Program Files\SpankyhoOptimizer\HardwareBridge\hardware-telemetry-bridge-task.xml'
$result='C:\\ProgramData\\SpankyhoOptimizer\\hardware-bridge-register-result.txt'
try {
  $raw=[IO.File]::ReadAllText($xml)
  $raw=$raw -replace 'encoding="UTF-8"','encoding="UTF-16"'
  [IO.File]::WriteAllText($xml,$raw,[Text.Encoding]::Unicode)
  $create=& schtasks.exe /Create /F /TN '\SpankyhoOptimizer-HardwareTelemetryBridge' /XML $xml 2>&1
  $createCode=$LASTEXITCODE
  if($createCode -ne 0){ throw ('CREATE failed code='+$createCode+' output='+($create -join ' ')) }
  $run=& schtasks.exe /Run /TN '\SpankyhoOptimizer-HardwareTelemetryBridge' 2>&1
  $runCode=$LASTEXITCODE
  if($runCode -ne 0){ throw ('RUN failed code='+$runCode+' output='+($run -join ' ')) }
  [IO.File]::WriteAllText($result,('OK'+[Environment]::NewLine+'CREATE='+($create -join ' ')+[Environment]::NewLine+'RUN='+($run -join ' ')))
} catch {
  [IO.File]::WriteAllText($result,('ERROR'+[Environment]::NewLine+$_.Exception.ToString()))
  exit 1
}