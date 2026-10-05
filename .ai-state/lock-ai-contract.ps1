$ErrorActionPreference='Stop'
$files=@(
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\Services\AiSystemPerformancePolicy.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\Services\AiTweakRuntimeService.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\Services\AiSystemResourceManagerService.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\MainWindow.AiSystemRuntime.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\Services\AiSecurityHardeningService.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\Services\FanProfileService.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\tools\CoreTests\AiMeasuredPerformanceTests.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\tools\CoreTests\AiSecurityHardeningPolicyTests.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\tools\CoreTests\FanAgentTaskPolicyTests.cs'
)
foreach($path in $files){
  $f=[IO.FileInfo]$path
  if(-not $f.Exists){throw "Missing lock target: $path"}
  $f.IsReadOnly=$true
}
$files | ForEach-Object { $f=[IO.FileInfo]$_; "$($f.IsReadOnly) $($_)" }
