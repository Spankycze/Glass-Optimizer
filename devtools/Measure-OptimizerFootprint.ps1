<#
Read-only Optimizer performance sampler for already-running Windows processes.
Default mode: CPU/RAM/threads/handles (unchanged from the original sampler).
Optional -IncludeWindowResponse: WM_NULL UI-thread message latency; no window
moves, focus changes, process starts, admin rights, or application settings.
Use -WarmupMs 1000 or more when comparing steady-state UI rather than cold
startup. Examples:
  .\devtools\Measure-OptimizerFootprint.ps1 -Samples 20 -IntervalMs 500
  .\devtools\Measure-OptimizerFootprint.ps1 -IncludeWindowResponse -WarmupMs 1000 -Samples 60 -IntervalMs 200
  .\devtools\Measure-OptimizerFootprint.ps1 -IncludeWindowResponse -ProcessId 1234 -WarmupMs 1000 -Samples 60 -IntervalMs 250 -Scenario interactive-drag -ExpectedExecutablePath 'C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe'
The latter targets exactly one process ID during manual drag/resize, avoiding
accidental mixing with isolated smoke/installer processes. Replace 1234 with
the current main GUI process ID. No Explorer or browser restarts needed.
WM_NULL responsiveness is not an FPS measurement and does not prove smooth drag.
Use -Scenario to distinguish a synthetic stress test from real interaction.
Timed-out WM_NULL replies count toward >=16/33/50ms and conservative
lower-bound p95/p99 (ordinary percentiles still describe valid replies only).
Mean CPU is weighted by actual elapsed sample intervals, not by sample count.
Optional -IncludeWindowGeometry records actual HWND rectangle changes (moving vs.
resizing) without moving it; smoke HWND parked at (-32000,-32000) is marked.
Optional -IncludeUiThreadCpu measures Win32 GUI thread CPU separately from
whole-process CPU, as percent of ONE logical core. Both switches require
-IncludeWindowResponse. No mouse/resize automation is performed.
Reports are JSON in V:\SpankyDevCache\OptimizerRuntimeBenchmark.
#>
param(
    [ValidateRange(2,300)][int]$Samples = 12,
    [ValidateRange(200,10000)][int]$IntervalMs = 1000,
    [string]$OutputDirectory = 'V:\SpankyDevCache\OptimizerRuntimeBenchmark',
    [ValidateRange(0,2147483647)][int]$ProcessId = 0,
    [switch]$IncludeWindowResponse,
    [ValidateRange(100,1000)][int]$WindowMessageTimeoutMs = 250,
    [ValidateRange(0,5000)][int]$WarmupMs = 0,
    [ValidateSet('unclassified','synthetic-smoke','interactive-drag','navigation','idle')][string]$Scenario = 'unclassified',
    [string]$ExpectedExecutablePath = '',
    [switch]$IncludeWindowGeometry,
    [switch]$IncludeUiThreadCpu
)
$ErrorActionPreference = 'Stop'
$logicalCores = [Environment]::ProcessorCount
$rows = [Collections.Generic.List[object]]::new()
$previous = @{}
$processVersions = @{}
$processImages = @{}
$windowHandles = @{}
$previousWindowGeometry = @{}
$previousUiThreadCpu = @{}
if (($IncludeWindowGeometry -or $IncludeUiThreadCpu) -and !$IncludeWindowResponse) {
    throw '-IncludeWindowGeometry and -IncludeUiThreadCpu require -IncludeWindowResponse.'
}
if ($IncludeWindowResponse) {
    # WM_NULL is side-effect-free. This is a real Win32 UI-thread message
    # response probe, not an FPS measurement and not a window drag.
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

public static class OptimizerUiResponseProbe {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr process);
    public static string ProcessImagePath(int pid) {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return string.Empty;
        try {
            var path = new StringBuilder(32768);
            int size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : string.Empty;
        } finally { CloseHandle(handle); }
    }
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High;
        public double Milliseconds { get { return (((ulong)High << 32) | Low) / 10000d; } }
    }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inherit, uint threadId);
    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr h, out FileTime created, out FileTime exit, out FileTime kernel, out FileTime user);
    public static object[] GetGeometry(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        WindowRect rect;
        if (!GetWindowRect(hwnd, out rect)) return null;
        return new object[] {rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, IsIconic(hwnd)};
    }
    public static double GetUiThreadCpuMs(IntPtr hwnd, int pid, out uint tid) {
        tid = 0;
        if (!IsValidWindowForProcess(hwnd, pid)) return -1;
        uint owner;
        tid = GetWindowThreadProcessId(hwnd, out owner);
        if (tid == 0 || owner != (uint)pid) return -1;
        IntPtr h = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, tid);
        if (h == IntPtr.Zero) return -1;
        try {
            FileTime created, exit, kernel, user;
            return GetThreadTimes(h, out created, out exit, out kernel, out user)
                ? kernel.Milliseconds + user.Milliseconds : -1;
        } finally { CloseHandle(h); }
    }
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    public static bool IsValidWindowForProcess(IntPtr hwnd, int pid) {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint owner);
        return owner == (uint)pid;
    }

    public static IntPtr FindVisibleWindow(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, data) => {
            if (!IsValidWindowForProcess(hwnd, pid) || !IsWindowVisible(hwnd))
                return true;
            // Prefer the application's unowned main top-level HWND over
            // helper/owned dialogs. Use the first visible HWND only as fallback.
            if (found == IntPtr.Zero) found = hwnd;
            if (GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero) {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static double MeasureMs(IntPtr hwnd, int timeoutMs) {
        if (hwnd == IntPtr.Zero) return -1;
        var sw = Stopwatch.StartNew();
        IntPtr result;
        IntPtr ok = SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero,
            0x0001 | 0x0002, (uint)timeoutMs, out result);
        sw.Stop();
        return ok == IntPtr.Zero
            ? -2
            : Math.Round(sw.Elapsed.TotalMilliseconds, 3);
    }
}
'@
}
# Optional exact image identity pin. Fail closed if the selected PID is not
# the installed/staged executable the caller explicitly intended to measure.
$expectedProcessStartUtc = $null
$normalizedExpectedPath = ''
if (![string]::IsNullOrWhiteSpace($ExpectedExecutablePath)) {
    if (!$IncludeWindowResponse -or $ProcessId -eq 0) {
        throw '-ExpectedExecutablePath requires -IncludeWindowResponse and an exact -ProcessId.'
    }
    $normalizedExpectedPath = [IO.Path]::GetFullPath($ExpectedExecutablePath)
    $actualPath = [OptimizerUiResponseProbe]::ProcessImagePath($ProcessId)
    if (![string]::Equals($normalizedExpectedPath, $actualPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Executable identity mismatch. Expected '$normalizedExpectedPath', actual '$actualPath'."
    }
    $selectedProcess = Get-Process -Id $ProcessId -ErrorAction Stop
    $expectedProcessStartUtc = $selectedProcess.StartTime.ToUniversalTime().ToString('o')
    $selectedProcess.Dispose()
}
function Capture-Sample([int]$step, [double]$elapsedSeconds) {
    $present = @(Get-Process -Name 'SpankyhoOptimizer*' -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessName -in @('SpankyhoOptimizer','SpankyhoOptimizer.RuntimeHost') -and
            ($ProcessId -eq 0 -or $_.Id -eq $ProcessId)
        })
    if ($expectedProcessStartUtc) {
        $selected = @($present | Where-Object { $_.Id -eq $ProcessId })
        if ($selected.Count -ne 1 -or
            $selected[0].StartTime.ToUniversalTime().ToString('o') -ne $expectedProcessStartUtc) {
            throw 'Pinned optimizer process exited or its PID was recycled. Aborting without mixing measurements.'
        }
    }
    foreach ($process in $present) {
        try {
            $process.Refresh()
            $pidKey = "$($process.Id)"
            if (!$processVersions.ContainsKey($pidKey)) {
                $version = $null
                $imagePath = ''
                if ($IncludeWindowResponse) {
                    $imagePath = [OptimizerUiResponseProbe]::ProcessImagePath($process.Id)
                }
                try {
                    if (![string]::IsNullOrWhiteSpace($imagePath)) {
                        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($imagePath).FileVersion
                    } else {
                        $version = $process.MainModule.FileVersionInfo.FileVersion
                    }
                } catch { }
                $processImages[$pidKey] = $(if ($imagePath) { $imagePath } else { 'unavailable' })
                $processVersions[$pidKey] = $(if ($version) { $version } else { 'unavailable' })
            }
            $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
            $corePercent = $null
            $systemPercent = $null
            $intervalCpuSeconds = $null
            $intervalElapsedSeconds = $null
            if ($previous.ContainsKey($pidKey)) {
                $prev = $previous[$pidKey]
                $deltaElapsed = $elapsedSeconds - $prev.elapsed
                $deltaCpu = $cpuSeconds - $prev.cpu
                if ($deltaElapsed -gt 0 -and $deltaCpu -ge 0) {
                    $corePercent = [math]::Round(100*$deltaCpu/$deltaElapsed,2)
                    $systemPercent = [math]::Round($corePercent/$logicalCores,3)
                    $intervalCpuSeconds = $deltaCpu
                    $intervalElapsedSeconds = $deltaElapsed
                }
            }
            $previous[$pidKey] = @{cpu=$cpuSeconds;elapsed=$elapsedSeconds}
            $responseMs = $null
            $responseTimestampUtc = $null
            $uiThreadId = $null
            $uiThreadCorePercent = $null
            $uiThreadCpuDeltaMs = $null
            $uiThreadElapsedSeconds = $null
            $windowX = $null
            $windowY = $null
            $windowWidth = $null
            $windowHeight = $null
            $windowMinimized = $null
            $windowParkedOffscreen = $false
            $windowMoved = $false
            $windowResized = $false
            if ($IncludeWindowResponse -and $process.ProcessName -eq 'SpankyhoOptimizer') {
                # WinUI can recreate its HWND. Validate ownership every
                # sample so a reused Windows handle never measures another app.
                $mainHwnd = $process.MainWindowHandle
                if ([OptimizerUiResponseProbe]::IsValidWindowForProcess($mainHwnd, $process.Id)) {
                    $windowHandles[$pidKey] = $mainHwnd
                } elseif (!$windowHandles.ContainsKey($pidKey) -or
                    ![OptimizerUiResponseProbe]::IsValidWindowForProcess($windowHandles[$pidKey], $process.Id)) {
                    $windowHandles[$pidKey] = [OptimizerUiResponseProbe]::FindVisibleWindow($process.Id)
                }
                $pinnedHwnd = $windowHandles[$pidKey]
                if ($IncludeWindowGeometry) {
                    $geometry = [OptimizerUiResponseProbe]::GetGeometry($pinnedHwnd)
                    if ($null -ne $geometry -and $geometry.Count -eq 5) {
                        $windowX = [int]$geometry[0]
                        $windowY = [int]$geometry[1]
                        $windowWidth = [int]$geometry[2]
                        $windowHeight = [int]$geometry[3]
                        $windowMinimized = [bool]$geometry[4]
                        # WinUI smoke can park its HWND at (-32000,-32000),
                        # which is not a user-controlled drag or an idle desktop window.
                        $windowParkedOffscreen = ($windowX -le -30000 -or $windowY -le -30000)
                        if ($previousWindowGeometry.ContainsKey($pidKey)) {
                            $beforeGeometry = $previousWindowGeometry[$pidKey]
                            if ($beforeGeometry.hwnd -eq $pinnedHwnd.ToInt64() -and
                                !$beforeGeometry.minimized -and !$windowMinimized -and
                                !$beforeGeometry.parkedOffscreen -and !$windowParkedOffscreen) {
                                $windowMoved = ($beforeGeometry.x -ne $windowX -or
                                    $beforeGeometry.y -ne $windowY)
                                $windowResized = ($beforeGeometry.width -ne $windowWidth -or
                                    $beforeGeometry.height -ne $windowHeight)
                            }
                        }
                        $previousWindowGeometry[$pidKey] = @{
                            hwnd=$pinnedHwnd.ToInt64(); x=$windowX; y=$windowY;
                            width=$windowWidth; height=$windowHeight;
                            minimized=$windowMinimized; parkedOffscreen=$windowParkedOffscreen
                        }
                    } else { $previousWindowGeometry.Remove($pidKey) | Out-Null }
                }
                if ($IncludeUiThreadCpu) {
                    [uint32]$nativeTid = 0
                    $uiCpuMs = [OptimizerUiResponseProbe]::GetUiThreadCpuMs(
                        $pinnedHwnd, $process.Id, [ref]$nativeTid)
                    if ($uiCpuMs -ge 0 -and $nativeTid -ne 0) {
                        $uiThreadId = [int]$nativeTid
                        if ($previousUiThreadCpu.ContainsKey($pidKey)) {
                            $lastUi = $previousUiThreadCpu[$pidKey]
                            $threadCpuDelta = $uiCpuMs - $lastUi.cpuMs
                            $threadElapsed = $elapsedSeconds - $lastUi.elapsed
                            if ($lastUi.tid -eq $uiThreadId -and $threadElapsed -gt 0 -and
                                $threadCpuDelta -ge 0) {
                                $uiThreadCpuDeltaMs = $threadCpuDelta
                                $uiThreadElapsedSeconds = $threadElapsed
                                $uiThreadCorePercent = [math]::Round(
                                    100*$threadCpuDelta/($threadElapsed*1000),2)
                            }
                        }
                        $previousUiThreadCpu[$pidKey] = @{
                            tid=$uiThreadId; cpuMs=$uiCpuMs; elapsed=$elapsedSeconds
                        }
                    } else { $previousUiThreadCpu.Remove($pidKey) | Out-Null }
                }
                $responseTimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
                $responseMs = [OptimizerUiResponseProbe]::MeasureMs(
                    $windowHandles[$pidKey], $WindowMessageTimeoutMs)
                if ($responseMs -eq -2 -and
                    ![OptimizerUiResponseProbe]::IsValidWindowForProcess($windowHandles[$pidKey], $process.Id)) {
                    $windowHandles[$pidKey] = [IntPtr]::Zero
                }
            }
            $rows.Add([pscustomobject]@{
                sample = $step
                pid = $process.Id
                process = $process.ProcessName
                fileVersion = $processVersions[$pidKey]
                executablePath = $processImages[$pidKey]
                elapsedSeconds = [math]::Round($elapsedSeconds,3)
                intervalCpuSeconds = $intervalCpuSeconds
                intervalElapsedSeconds = $intervalElapsedSeconds
                corePercent = $corePercent
                systemPercent = $systemPercent
                workingSetMiB = [math]::Round($process.WorkingSet64/1MB,2)
                privateMiB = [math]::Round($process.PrivateMemorySize64/1MB,2)
                threads = $process.Threads.Count
                handles = $process.HandleCount
                responseMs = $responseMs
                responseTimestampUtc = $responseTimestampUtc
                uiThreadId = $uiThreadId
                uiThreadCorePercent = $uiThreadCorePercent
                uiThreadCpuDeltaMs = $uiThreadCpuDeltaMs
                uiThreadElapsedSeconds = $uiThreadElapsedSeconds
                windowX = $windowX
                windowY = $windowY
                windowWidth = $windowWidth
                windowHeight = $windowHeight
                windowMinimized = $windowMinimized
                windowParkedOffscreen = $windowParkedOffscreen
                windowMoved = $windowMoved
                windowResized = $windowResized
            })
        } catch {
            # A privileged/terminated process can become unreadable mid-sample.
            Write-Warning "PID $($process.Id) sample unavailable: $($_.Exception.Message)"
        } finally {
            $process.Dispose()
        }
    }
}
# Cold-start UI initialization can block WM_NULL for 200+ ms even when
# steady-state resizing is responsive. Separate optional warmup explicitly
# so a startup sample does not silently become a steady-state p95 claim.
if ($WarmupMs -gt 0) { Start-Sleep -Milliseconds $WarmupMs }
$clock = [Diagnostics.Stopwatch]::StartNew()
Capture-Sample 0 0.0
for ($i=1; $i -le $Samples; $i++) {
    Start-Sleep -Milliseconds $IntervalMs
    Capture-Sample $i $clock.Elapsed.TotalSeconds
}
$clock.Stop()
$summary = foreach($g in ($rows | Group-Object pid)) {
    $all = @($g.Group)
    $cpu = @($all | Where-Object { $null -ne $_.corePercent })
    $cpuSecondsSum = ($cpu | Measure-Object intervalCpuSeconds -Sum).Sum
    $elapsedSecondsSum = ($cpu | Measure-Object intervalElapsedSeconds -Sum).Sum
    $uiCpu = @($all | Where-Object { $null -ne $_.uiThreadCorePercent })
    $uiCpuMsSum = ($uiCpu | Measure-Object uiThreadCpuDeltaMs -Sum).Sum
    $uiElapsedSecondsSum = ($uiCpu | Measure-Object uiThreadElapsedSeconds -Sum).Sum
    # A WM_NULL timeout means at least WindowMessageTimeoutMs of delay, not
    # an absent data point. This distribution is a conservative lower bound.
    $uiResponseLowerBounds = @($all | Where-Object {
        $null -ne $_.responseMs -and $_.responseMs -ne -1
    } | ForEach-Object {
        if ($_.responseMs -eq -2) { [double]$WindowMessageTimeoutMs }
        else { [double]$_.responseMs }
    } | Sort-Object)
    [pscustomobject]@{
        process = $all[0].process
        fileVersion = $all[0].fileVersion
        executablePath = $all[0].executablePath
        pid = $all[0].pid
        intervals = $cpu.Count
        # Weight by actual interval duration; timeouts make intervals uneven.
        meanCorePercent = if ($elapsedSecondsSum -gt 0) {
            [math]::Round(100*$cpuSecondsSum/$elapsedSecondsSum,2)
        } else { $null }
        maxCorePercent = if ($cpu.Count) {
            [math]::Round(($cpu | Measure-Object corePercent -Maximum).Maximum,2)
        } else { $null }
        minWorkingMiB = ($all | Measure-Object workingSetMiB -Minimum).Minimum
        maxWorkingMiB = ($all | Measure-Object workingSetMiB -Maximum).Maximum
        maxPrivateMiB = ($all | Measure-Object privateMiB -Maximum).Maximum
        maxThreads = ($all | Measure-Object threads -Maximum).Maximum
        maxHandles = ($all | Measure-Object handles -Maximum).Maximum
        uiThreadSamples = $uiCpu.Count
        meanUiThreadCorePercent = if ($uiElapsedSecondsSum -gt 0) {
            [math]::Round(100*$uiCpuMsSum/($uiElapsedSecondsSum*1000),2)
        } else { $null }
        windowMovedSamples = @($all | Where-Object { $_.windowMoved }).Count
        windowResizedSamples = @($all | Where-Object { $_.windowResized }).Count
        windowMinimizedSamples = @($all | Where-Object { $_.windowMinimized -eq $true }).Count
        windowParkedOffscreenSamples = @($all | Where-Object { $_.windowParkedOffscreen }).Count
        uiResponseSamples = @($all | Where-Object { $null -ne $_.responseMs -and $_.responseMs -ge 0 }).Count
        uiMessageTimeouts = @($all | Where-Object { $_.responseMs -eq -2 }).Count
        uiWindowUnavailableSamples = @($all | Where-Object { $_.responseMs -eq -1 }).Count
        uiOver16Ms = @($all | Where-Object { $_.responseMs -eq -2 -or ($null -ne $_.responseMs -and $_.responseMs -ge 16.67) }).Count
        uiOver33Ms = @($all | Where-Object { $_.responseMs -eq -2 -or ($null -ne $_.responseMs -and $_.responseMs -ge 33.33) }).Count
        uiOver50Ms = @($all | Where-Object { $_.responseMs -eq -2 -or ($null -ne $_.responseMs -and $_.responseMs -ge 50) }).Count
        uiResponseP50Ms = $( $valid = @($all | Where-Object { $null -ne $_.responseMs -and $_.responseMs -ge 0 } | Sort-Object responseMs); if ($valid.Count) { $valid[[int][math]::Floor(($valid.Count - 1) * 0.5)].responseMs } else { $null } )
        uiResponseP95Ms = $( $valid = @($all | Where-Object { $null -ne $_.responseMs -and $_.responseMs -ge 0 } | Sort-Object responseMs); if ($valid.Count) { $valid[[int][math]::Ceiling(($valid.Count - 1) * 0.95)].responseMs } else { $null } )
        uiResponseP99Ms = $( $valid = @($all | Where-Object { $null -ne $_.responseMs -and $_.responseMs -ge 0 } | Sort-Object responseMs); if ($valid.Count) { $valid[[int][math]::Ceiling(($valid.Count - 1) * 0.99)].responseMs } else { $null } )
        uiResponseMaxMs = $( $valid = @($all | Where-Object { $null -ne $_.responseMs -and $_.responseMs -ge 0 }); if ($valid.Count) { ($valid | Measure-Object responseMs -Maximum).Maximum } else { $null } )
        uiResponseP95LowerBoundMs = if ($uiResponseLowerBounds.Count) { $uiResponseLowerBounds[[int][math]::Ceiling(($uiResponseLowerBounds.Count - 1)*0.95)] } else { $null }
        uiResponseP99LowerBoundMs = if ($uiResponseLowerBounds.Count) { $uiResponseLowerBounds[[int][math]::Ceiling(($uiResponseLowerBounds.Count - 1)*0.99)] } else { $null }
        uiResponseMaxLowerBoundMs = if ($uiResponseLowerBounds.Count) { $uiResponseLowerBounds[-1] } else { $null }
    }
}
$report = [pscustomobject]@{
    timestampUtc=(Get-Date).ToUniversalTime().ToString('o')
    measuredSeconds=[math]::Round($clock.Elapsed.TotalSeconds,2)
    logicalCores=$logicalCores
    notes='Read-only. CPU corePercent is one logical core; meanCorePercent is interval-time-weighted. WM_NULL delay is not drag/FPS. Ordinary UI percentiles exclude timeouts; LowerBound percentiles include timed-out probes at their timeout floor (actual delay may be higher). No restart or elevation.'
    scenario=$Scenario
    windowProbeEnabled=[bool]$IncludeWindowResponse
    windowGeometryEnabled=[bool]$IncludeWindowGeometry
    uiThreadCpuEnabled=[bool]$IncludeUiThreadCpu
    targetProcessId=$ProcessId
    expectedExecutablePath=$normalizedExpectedPath
    expectedProcessStartUtc=$expectedProcessStartUtc
    windowTimeoutMs=$WindowMessageTimeoutMs
    warmupMs=$WarmupMs
    summary=@($summary)
    samples=@($rows)
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$destination=Join-Path $OutputDirectory $(if ($IncludeWindowResponse) { 'optimizer-ui-response-latest.json' } else { 'optimizer-footprint-latest.json' })
$report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $destination -Encoding UTF8
if ($summary) {
    if ($IncludeWindowResponse) {
        foreach ($s in $summary) {
            'PID={0} version={1} scenario={2} valid={3} timeouts={4} missing={5} p50={6}ms p95Valid={7}ms p99Floor={8}ms over33={9} cpuCoreWeighted={10}% wsPeak={11}MiB privatePeak={12}MiB' -f `
                $s.pid,$s.fileVersion,$Scenario,$s.uiResponseSamples,$s.uiMessageTimeouts,
                $s.uiWindowUnavailableSamples,$s.uiResponseP50Ms,$s.uiResponseP95Ms,
                $s.uiResponseP99LowerBoundMs,$s.uiOver33Ms,$s.meanCorePercent,
                $s.maxWorkingMiB,$s.maxPrivateMiB
            if ($IncludeWindowGeometry -or $IncludeUiThreadCpu) {
                'WINDOW moved={0} resized={1} minimized={2} parkedOffscreen={3} | UI_THREAD samples={4} meanCorePercent={5}' -f `
                    $s.windowMovedSamples,$s.windowResizedSamples,$s.windowMinimizedSamples,`
                    $s.windowParkedOffscreenSamples,$s.uiThreadSamples,$s.meanUiThreadCorePercent
            }
        }
    } else {
        $summary | Format-Table -AutoSize
    }
} else {
    Write-Warning 'No matching running Optimizer processes during sampling.'
}
"FOOTPRINT_REPORT=$destination"
