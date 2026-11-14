# Read-only resource sampling for already-running Spankyho Optimizer processes.
# Does not launch, kill, restart, adjust priorities, change power settings or write files.
param(
    [ValidateRange(2,20)][int]$Samples = 4,
    [ValidateRange(250,10000)][int]$IntervalMs = 1000
)
$ErrorActionPreference = 'Stop'
$frequency = [double][Diagnostics.Stopwatch]::Frequency
$logical = [Math]::Max(1, [Environment]::ProcessorCount)
$rows = [Collections.Generic.List[object]]::new()
$previous = @{}
$lastTick = 0L

for ($i = 0; $i -lt $Samples; $i++) {
    if ($i -gt 0) { Start-Sleep -Milliseconds $IntervalMs }
    $processes = @(Get-Process -Name 'SpankyhoOptimizer*','*FanAgent*' -ErrorAction SilentlyContinue)
    $tick = [Diagnostics.Stopwatch]::GetTimestamp()
    $elapsedMs = if ($lastTick -gt 0) {
        [Math]::Max(1.0, ($tick - $lastTick) * 1000.0 / $frequency)
    } else { 0.0 }
    $next = @{}
    foreach ($proc in $processes) {
        try {
            $pidValue = [int]$proc.Id
            $cpuSeconds = [double]$proc.TotalProcessorTime.TotalSeconds
            $snapshot = @{
                Name = [string]$proc.ProcessName
                Cpu = $cpuSeconds
                RamMiB = [double]$proc.WorkingSet64 / 1MB
                PrivateMiB = [double]$proc.PrivateMemorySize64 / 1MB
                Handles = [int]$proc.HandleCount
                Threads = [int]$proc.Threads.Count
                StartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks
            }
            $next[$pidValue] = $snapshot
            if ($previous.ContainsKey($pidValue) -and $elapsedMs -gt 0) {
                $old = $previous[$pidValue]
                if ($old.StartTicks -eq $snapshot.StartTicks) {
                    $cpuDeltaMs = ($cpuSeconds - $old.Cpu) * 1000
                    if ([double]::IsFinite($cpuDeltaMs) -and $cpuDeltaMs -ge 0) {
                        $rows.Add([pscustomobject]@{
                            Name = $snapshot.Name; PID = $pidValue
                            CpuPercent = [Math]::Round([Math]::Min(100, $cpuDeltaMs / $elapsedMs / $logical * 100),3)
                            RamMiB = [Math]::Round($snapshot.RamMiB,2)
                            PrivateMiB = [Math]::Round($snapshot.PrivateMiB,2)
                            Handles = $snapshot.Handles; Threads = $snapshot.Threads
                        })
                    }
                }
            }
        } catch { # Process exited or denied; never modify or retry it.
        }
    }
    $previous = $next
    $lastTick = $tick
}
Write-Output "AI_RUNTIME_READONLY samples=$Samples intervalMs=$IntervalMs logicalProcessors=$logical windows=$($Samples-1)"
if ($rows.Count -eq 0) {
    Write-Output 'No stable matching running processes available for a CPU sample.'
    return
}
foreach ($group in ($rows | Group-Object Name,PID | Sort-Object Name)) {
    $first = $group.Group[0]
    $cpu = $group.Group | Measure-Object CpuPercent -Average -Maximum
    $last = $group.Group[-1]
    Write-Output ('{0} PID={1} windows={2} cpuAveragePct={3:N3} cpuMaxPct={4:N3} ramMiB={5:N2} privateMiB={6:N2} handles={7} threads={8}' -f
        $first.Name,$first.PID,$group.Count,$cpu.Average,$cpu.Maximum,
        $last.RamMiB,$last.PrivateMiB,$last.Handles,$last.Threads)
}
