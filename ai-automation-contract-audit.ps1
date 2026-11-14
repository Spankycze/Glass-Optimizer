$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$svc=Join-Path $root 'src\SpankyhoOptimizer\Services'

# Read the whole type, including its partial files, so contract checks follow splits.
function Read-TypeSource([string]$dir,[string]$name){
  $files=@(Get-ChildItem -Path (Join-Path $dir ($name + '*.cs')) -File | Sort-Object Name)
  if($files.Count -eq 0){ throw "AI_AUTOMATION_CONTRACT_FAIL missing source $name" }
  ($files | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
}

$scope=Get-Content (Join-Path $svc 'AiRuntimeScopePolicy.cs') -Raw
$guard=Get-Content (Join-Path $svc 'AiAutomaticMutationGuard.cs') -Raw
$measured=Get-Content (Join-Path $svc 'AiTweakMeasuredRuntime.cs') -Raw
$security=Get-Content (Join-Path $svc 'AiSecurityHardeningService.cs') -Raw
$manager=Read-TypeSource $svc 'AiSystemResourceManagerService'
$power=Read-TypeSource $svc 'AdaptivePowerRuntimeService'
$fan=Read-TypeSource $svc 'FanProfileService'
$ui=Read-TypeSource (Join-Path $root 'src\SpankyhoOptimizer') 'MainWindow.AiSystemRuntime'

function Need([bool]$ok,[string]$message){ if(-not $ok){ throw "AI_AUTOMATION_CONTRACT_FAIL $message" } }

Need ($scope.Contains('TweaksEnabled = false') -and $scope.Contains('SecurityEnabled = false')) 'protected scope sanitization missing'
Need ($guard.Contains('System/UI/Explorer/Security are manual-only')) 'hard mutation guard missing'
Need ($measured.Contains('IsProtectedManualSection')) 'measured tweak protected-section guard missing'
Need ($security.Contains('AutomaticMutationAllowed => false')) 'security automatic mutation boundary missing'
Need ($manager.Contains('AiRuntimeScopePolicy.Sanitize(LoadConfig())')) 'runtime config sanitization missing'
Need ($manager.Contains('AiWorkloadClass workload')) 'shared workload context missing'
Need ($manager.Contains('ShouldBackgroundSync(workload)')) 'workload-aware cloud sync missing'
Need ($power.Contains('AiWorkloadStabilizer')) 'power workload hysteresis missing'
Need ($fan.Contains('<RestartOnFailure><Count>3</Count><Interval>PT1M</Interval></RestartOnFailure>')) 'fan restart-on-failure policy invalid'
Need ($fan.Contains('DeleteValue(WatchdogRunValueName')) 'legacy watchdog Run cleanup missing'
Need (-not $fan.Contains('SetValue(WatchdogRunValueName')) 'resident watchdog autostart returned'
Need ($ui.Contains('System/UI/Explorer/Security tweaky zůstávají ruční')) 'manual-only AI UI contract missing'
Need (-not $ui.Contains('Internet podle naměřeného výkonu')) 'removed network AI UI returned'
Need ($ui.Contains('"Služby"') -and $ui.Contains('"Plánované úlohy"') -and $ui.Contains('systemConfig.ServicesEnabled') -and $ui.Contains('systemConfig.ScheduledTasksEnabled') -and $ui.Contains('c => c with { ScheduledTasksEnabled = enabled }')) 'service/task AI UI missing'
Write-Output 'AI_AUTOMATION_CONTRACT_OK'
