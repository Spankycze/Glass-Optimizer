$dll='C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.Core.dll'
$a=[Reflection.Assembly]::LoadFrom($dll)
$td=$a.GetType('SpankyhoOptimizer.Models.TweakDefinition')
function New-Tweak([string]$id,[string]$section,[string]$group=''){
  $o=[Activator]::CreateInstance($td)
  foreach($pair in @(@('Id',$id),@('Section',$section),@('Group',$group),@('Name',$id),@('Description',''))){$td.GetProperty($pair[0]).SetValue($o,$pair[1])}
  return $o
}
$p=$a.GetType('SpankyhoOptimizer.Services.AiSystemPerformancePolicy')
[ordered]@{
 Version=(Get-Item $dll).VersionInfo.ProductVersion
 InventoryMeasured=$p.GetMethod('IsMeasuredCandidate').Invoke($null,@((New-Tweak 'sys.inventory' 'system' 'Background')))
 ExplorerMeasured=$p.GetMethod('IsMeasuredCandidate').Invoke($null,@((New-Tweak 'ui.thumbcache' 'ui' 'Explorer')))
 PrivacyMeasured=$p.GetMethod('IsMeasuredCandidate').Invoke($null,@((New-Tweak 'privacy.websearch' 'privacy')))
 GameDvrMeasured=$p.GetMethod('IsMeasuredCandidate').Invoke($null,@((New-Tweak 'sys.gamedvr' 'system')))
 AppCaptureMeasured=$p.GetMethod('IsMeasuredCandidate').Invoke($null,@((New-Tweak 'sys.capture' 'system')))
} | ConvertTo-Json
