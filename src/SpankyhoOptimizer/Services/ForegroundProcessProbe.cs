using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

public static class ForegroundProcessProbe
{
    // The adaptive power sampler already owns a complete Process[] snapshot.
    // Read the active PID without opening a second Process handle; the sampler
    // resolves the name from that same snapshot and falls back to GetName() only
    // when a process appeared or disappeared between the two native reads.
    public static int GetProcessId() => GetWindowIdentity().ProcessId;

    // Returns HWND + PID from a single pair of native reads. Switching
    // between two windows owned by the same browser PID invalidates the
    // adaptive CPU snapshot just like switching to another process.
    internal static (nint Handle, int ProcessId) GetWindowIdentity()
    {
        try
        {
            nint hwnd = GetForegroundWindow();
            if (hwnd == 0) return default;
            _ = GetWindowThreadProcessId(hwnd, out uint processId);
            return processId is > 0 and <= int.MaxValue
                ? (hwnd, (int)processId) : default;
        }
        catch { return default; }
    }

    public static string GetName()
    {
        try
        {
            int processId = GetProcessId();
            if (processId == 0) return string.Empty;
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch { return string.Empty; }
    }

    // Read-only Win32 idle evidence. Missing session input telemetry must
    // NEVER be interpreted as user absence; in that case use the normal
    // process scan cadence. Unsigned arithmetic handles TickCount wrap.
    internal static bool IsUserInputIdleFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds >= uint.MaxValue)
            return false;
        try
        {
            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref input) || input.Tick == 0)
                return false;
            uint currentTick = unchecked((uint)Environment.TickCount);
            return AiInputIdleEvidencePolicy.IsConfirmedIdle(
                currentTick, input.Tick, duration);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
            DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size;
        internal uint Tick;
    }

    // Compare the eliminated extra process-handle open with the already owned
    // CPU sampling snapshot. Read-only; no application settings are touched.
    internal static string RunSnapshotReuseSmoke()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            int pid = Environment.ProcessId;
            string? cachedName = null;
            foreach (Process candidate in processes)
                if (candidate.Id == pid) { cachedName = candidate.ProcessName; break; }
            if (string.IsNullOrWhiteSpace(cachedName))
                throw new InvalidOperationException("Current process missing from native snapshot.");
            const int iterations = 192;
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
            {
                using Process extraHandle = Process.GetProcessById(pid);
                if (!string.Equals(extraHandle.ProcessName, cachedName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Process name mismatch.");
            }
            double extraOpenMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long extraOpenBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
            {
                // The optimized path assigned this same name while already
                // enumerating Process[] for EPP workload/game detection.
                string reused = cachedName;
                GC.KeepAlive(reused);
            }
            double reuseMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long reuseBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            return $"ADAPTIVE_FOREGROUND_REUSE_SMOKE_OK cycles={iterations} " +
                $"reopenMs={extraOpenMs:F3} reuseMs={reuseMs:F3} " +
                $"reopenAllocB={extraOpenBytes} reuseAllocB={reuseBytes} " +
                $"snapshotCount={processes.Length} sameName=true";
        }
        finally { ProcessSnapshotCleanup.DisposeAll(processes); }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
