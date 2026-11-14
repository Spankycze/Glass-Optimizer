# Read-only health check. No registration, task start/stop, elevation, or system changes.
$ErrorActionPreference = 'Continue'
$taskName = 'SpankyhoOptimizer-HardwareTelemetryBridge'
$snapshot = 'C:\Program Files\SpankyhoOptimizer\HardwareBridge\hardware-telemetry-bridge.json'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
[pscustomobject]@{
    Check = 'Identity'
    Account = $identity.Name
    Elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} | Format-List

try {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $info = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
    [pscustomobject]@{
        Check = 'Task'
        Name = $task.TaskName
        State = $task.State
        LastRunTime = $info.LastRunTime
        LastTaskResult = $info.LastTaskResult
        Principal = $task.Principal.UserId
        ActionCount = @($task.Actions).Count
        Actions = (@($task.Actions) | ForEach-Object { $_.Execute + ' ' + $_.Arguments }) -join '; '
        BridgeModeConfigured = (@($task.Actions | Where-Object { $_.Arguments -eq '--hardware-telemetry-bridge' }).Count -eq 1)
        SystemPrincipalConfigured = ($task.Principal.UserId -in @('SYSTEM', 'S-1-5-18', 'NT AUTHORITY\SYSTEM'))
        TrustedExecutableConfigured = (@($task.Actions | Where-Object {
            $_.Execute -in @(
                'C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.RuntimeHost.exe',
                'C:\Program Files\SpankyhoOptimizer\SpankyhoOptimizer.RuntimeHost.exe')
        }).Count -eq 1)
    } | Format-List
} catch {
    # Task enumeration can conceal protected entries from a non-elevated user.
    # Never label an inaccessible task as missing without elevated verification.
    $status = if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        'NotFoundOrUnavailable'
    } else {
        'UnverifiedWithoutElevation'
    }
    [pscustomobject]@{Check = 'Task'; Status = $status; Error = $_.Exception.Message} | Format-List
}

try {
    $events = Get-WinEvent -FilterHashtable @{
        LogName = 'Microsoft-Windows-TaskScheduler/Operational'
        StartTime = (Get-Date).AddDays(-1)
    } -MaxEvents 150 -ErrorAction Stop |
        Where-Object { $_.Message -like ('*' + $taskName + '*') } |
        Select-Object -First 10 TimeCreated, Id, LevelDisplayName, Message
    if ($events) { $events | Format-List }
    else { Write-Output 'No related Task Scheduler events among the latest 150 sampled entries; absence does not prove the task never ran.' }
} catch {
    Write-Output ('Task Scheduler event inspection unavailable: ' + $_.Exception.Message)
}

if (Test-Path -LiteralPath $snapshot) {
    $item = Get-Item -LiteralPath $snapshot
    [pscustomobject]@{
        Check = 'Snapshot'
        LastWriteTime = $item.LastWriteTime
        AgeSeconds = [int]((Get-Date) - $item.LastWriteTime).TotalSeconds
        FreshWithin6Seconds = (((Get-Date) - $item.LastWriteTime).TotalSeconds -le 6)
        Bytes = $item.Length
    } | Format-List
} else {
    [pscustomobject]@{Check = 'Snapshot'; Status = 'Missing'} | Format-List
}
