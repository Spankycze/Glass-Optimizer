$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'
$buildDir = Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\bin\Release\net10.0-windows10.0.26100.0\win-x64'
$sourceExe = Join-Path $buildDir 'SpankyhoOptimizer.RuntimeHost.exe'

$programDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$legacyProgramData = Join-Path $programDataRoot 'SpankyhoOptimizer'
$legacyVersionFile = Join-Path $legacyProgramData 'fan-agent-version.txt'
$legacyRestartFile = Join-Path $legacyProgramData 'fan-agent-restart.request'

$localAppDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$fanAgentState = Join-Path $localAppDataRoot 'SpankyhoOptimizer\FanAgent'
$versionFile = Join-Path $fanAgentState 'fan-agent-version.txt'
$restartFile = Join-Path $fanAgentState 'fan-agent-restart.request'

dotnet build $project -c Release -r win-x64 --no-restore --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path $sourceExe)) { throw 'RuntimeHost build output missing.' }

$targetVersion = (Get-Item $sourceExe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($targetVersion)) { throw 'RuntimeHost version missing.' }
$stageDir = Join-Path $localAppDataRoot ('SpankyhoOptimizer\RuntimeHost\' + $targetVersion)
$stagedExe = Join-Path $stageDir 'SpankyhoOptimizer.RuntimeHost.exe'

# Stop all user-level RuntimeHost processes first. A legacy elevated host is handled by restart request.
$remaining = @()
foreach ($hostProcess in @(Get-Process -Name 'SpankyhoOptimizer.RuntimeHost' -ErrorAction SilentlyContinue)) {
    try {
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction Stop
        try { $hostProcess.WaitForExit(3000) } catch { }
    } catch {
        $remaining += $hostProcess
    }
}

if ($remaining.Count -gt 0) {
    $runningVersion = if (Test-Path $versionFile) {
        (Get-Content -LiteralPath $versionFile -Raw).Trim()
    } elseif (Test-Path $legacyVersionFile) {
        (Get-Content -LiteralPath $legacyVersionFile -Raw).Trim()
    } else {
        $targetVersion
    }

    $payload = $runningVersion + '|' + [DateTime]::UtcNow.ToString('O')
    New-Item -ItemType Directory -Path $fanAgentState -Force | Out-Null
    Set-Content -LiteralPath $restartFile -Value $payload -Encoding UTF8
    try {
        New-Item -ItemType Directory -Path $legacyProgramData -Force | Out-Null
        Set-Content -LiteralPath $legacyRestartFile -Value $payload -Encoding UTF8
    } catch { }

    $handoffDeadline = [DateTime]::UtcNow.AddSeconds(8)
    while ([DateTime]::UtcNow -lt $handoffDeadline) {
        $alive = @($remaining | Where-Object {
            try { $_.Refresh(); -not $_.HasExited } catch { $false }
        })
        if ($alive.Count -eq 0) { break }
        Start-Sleep -Milliseconds 50
    }
}

# Recreate versioned stage after all replaceable processes are gone.
if (Test-Path $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item -Path (Join-Path $buildDir '*') -Destination $stageDir -Recurse -Force

Remove-Item -LiteralPath $restartFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyRestartFile -Force -ErrorAction SilentlyContinue

Start-Process -FilePath $stagedExe -ArgumentList '--fan-agent'
Start-Process -FilePath $stagedExe -ArgumentList '--fan-watchdog-loop'

$deadline = [DateTime]::UtcNow.AddSeconds(8)
$agentReady = $false
$watchdogReady = $false
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 120
    $runtimeProcesses = @(Get-CimInstance Win32_Process -Filter "Name='SpankyhoOptimizer.RuntimeHost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -eq $stagedExe })

    $agentReady = $runtimeProcesses.CommandLine -match '--fan-agent'
    $watchdogReady = $runtimeProcesses.CommandLine -match '--fan-watchdog-loop'
    if ($agentReady -and $watchdogReady) { break }
}
if (-not ($agentReady -and $watchdogReady)) {
    throw "RuntimeHost staged start failed: agent=$agentReady watchdog=$watchdogReady target=$targetVersion"
}

$stateDeadline = [DateTime]::UtcNow.AddSeconds(8)
$stateReady = $false
while ([DateTime]::UtcNow -lt $stateDeadline) {
    if (Test-Path $versionFile) {
        $liveVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
        if ($liveVersion -eq $targetVersion) {
            $stateReady = $true
            break
        }
    }
    Start-Sleep -Milliseconds 120
}
if (-not $stateReady) {
    throw "RuntimeHost state version did not converge: target=$targetVersion"
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -Path $runKey -Name 'Spankyho Fan Agent' -Value ('"' + $stagedExe + '" --fan-agent') -PropertyType String -Force | Out-Null
New-ItemProperty -Path $runKey -Name 'Spankyho Fan Watchdog' -Value ('"' + $stagedExe + '" --fan-watchdog-loop') -PropertyType String -Force | Out-Null

Start-Sleep -Milliseconds 350
Write-Output ("RUNTIMEHOST_DEPLOY_OK version=" + $targetVersion + " agent=1 watchdog=1 path=" + $stagedExe)
