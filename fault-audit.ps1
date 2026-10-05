param([switch]$IncludeLatency)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$proj=Join-Path $root 'tools\FaultInjectionTests\FaultInjectionTests.csproj'
dotnet build $proj -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'FaultInjectionTests build failed'}
$scenarios=@(
  @{name='process-timeout'; faults='process.run=timeout:5'},
  @{name='dns-read-denied'; faults='dns.read=denied'},
  @{name='dns-timeout'; faults='dns.read=timeout:5'},
  @{name='powershell-timeout'; faults='powershell.run=timeout:5'},
  @{name='adapter-restart-fail'; faults='network.adapter-restart=exception:Injected adapter restart failure'},
  @{name='adapter-timeout'; faults='network.adapter-enumeration=timeout:5'},
  @{name='mtu-probe-timeout'; faults='network.mtu-probe=timeout:5'},
  @{name='pre-cancel'; faults=''},
  @{name='cancellation-race'; faults=''},
  @{name='registry-denied'; faults='registry.read=denied'},
  @{name='wmi-fail'; faults='wmi.query=exception:Injected WMI failure'},
  @{name='service-fail'; faults='service.query=exception:Injected service failure'},
  @{name='external-profile'; faults='dns.read=timeout:5;powershell.run=timeout:5;network.adapter-restart=exception:Injected adapter restart failure;registry.read=denied;wmi.query=exception:Injected WMI failure;service.query=exception:Injected service failure'}
)
if($IncludeLatency){$scenarios+=@{name='process-timeout';faults='process.run=delay:20;process.run=timeout:5'}}
$ok=0
foreach($s in $scenarios){
  $old=$env:SPANKY_FAULTS
  try{
    $env:SPANKY_FAULTS=$s.faults
    dotnet run --project $proj -c Release --no-build -- $s.name
    if($LASTEXITCODE -ne 0){throw ('fault scenario failed: '+$s.name)}
    $ok++
  } finally {$env:SPANKY_FAULTS=$old}
}
Write-Output ('FAULT_AUDIT_OK scenarios='+$ok)