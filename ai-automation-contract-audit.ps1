$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$scope=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AiRuntimeScopePolicy.cs') -Raw
$guard=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AiAutomaticMutationGuard.cs') -Raw
$measured=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AiTweakMeasuredRuntime.cs') -Raw
$security=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AiSecurityHardeningService.cs') -Raw
$manager=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AiSystemResourceManagerService.cs') -Raw
$network=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AiNetworkRuntimeService.cs') -Raw
$power=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AdaptivePowerRuntimeService.cs') -Raw
$fan=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\FanProfileService.cs') -Raw
$ui=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.AiSystemRuntime.cs') -Raw

function Need([bool]$ok,[string]$message){ if(-not $ok){ throw "AI_AUTOMATION_CONTRACT_FAIL $message" } }

Need ($scope.Contains('TweaksEnabled = false') -and $scope.Contains('SecurityEnabled = false')) 'protected scope sanitization missing'
Need ($guard.Contains('System/UI/Explorer/Security are manual-only')) 'hard mutation guard missing'
Need ($measured.Contains('IsProtectedManualSection')) 'measured tweak protected-section guard missing'
Need ($security.Contains('AutomaticMutationAllowed => false')) 'security automatic mutation boundary missing'
Need ($manager.Contains('AiRuntimeScopePolicy.Sanitize(LoadConfig())')) 'runtime config sanitization missing'
Need ($manager.Contains('AiWorkloadClass workload')) 'shared workload context missing'
Need ($manager.Contains('ShouldBackgroundSync(workload)')) 'workload-aware cloud sync missing'
Need ($network.Contains('AiWorkloadClass.Interactive or AiWorkloadClass.Game')) 'network interactive-workload guard missing'
Need ($power.Contains('AiWorkloadStabilizer')) 'power workload hysteresis missing'
Need ($fan.Contains('<RestartOnFailure><Count>3</Count><Interval>PT1M</Interval></RestartOnFailure>')) 'fan restart-on-failure policy invalid'
Need ($fan.Contains('DeleteValue(WatchdogRunValueName')) 'legacy watchdog Run cleanup missing'
Need (-not $fan.Contains('SetValue(WatchdogRunValueName')) 'resident watchdog autostart returned'
Need ($ui.Contains('System/UI/Explorer/Security tweaky zůstávají ruční')) 'manual-only AI UI contract missing'
Need ($ui.Contains('Internet podle naměřeného výkonu')) 'network AI UI missing'
Need ($ui.Contains('"Služby"') -and $ui.Contains('"Scheduled Tasks"')) 'service/task AI UI missing'
Write-Output 'AI_AUTOMATION_CONTRACT_OK'
