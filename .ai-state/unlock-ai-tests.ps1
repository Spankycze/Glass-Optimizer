$files=@(
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\tools\CoreTests\AiMeasuredPerformanceTests.cs',
'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\tools\CoreTests\AiSecurityHardeningPolicyTests.cs'
)
foreach($p in $files){$f=[IO.FileInfo]$p;$f.IsReadOnly=$false}
