$dll='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer.Core\bin\Release\net10.0-windows10.0.26100.0\SpankyhoOptimizer.Core.dll'
$a=[Reflection.Assembly]::LoadFrom($dll)
$td=$a.GetType('SpankyhoOptimizer.Models.TweakDefinition')
$obj=[Activator]::CreateInstance($td)
foreach($pair in @(@('Id','ui.thumbcache'),@('Section','ui'),@('Group',''),@('Name',''),@('Description',''))){$td.GetProperty($pair[0]).SetValue($obj,$pair[1])}
$p=$a.GetType('SpankyhoOptimizer.Services.AiSystemPerformancePolicy')
Write-Output ('ID=' + $td.GetProperty('Id').GetValue($obj))
Write-Output ('MEASURED=' + $p.GetMethod('IsMeasuredCandidate').Invoke($null,@($obj)))
Write-Output ('REASON=' + $p.GetMethod('CandidateDeferredReason').Invoke($null,@($obj,'explorer')))
