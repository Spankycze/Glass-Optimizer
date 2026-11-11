using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Isolated, non-visible *owned* HWND native ping smoke. Never activates or
/// sends messages to the user's browser, game, taskbar or desktop windows.
/// Only used by the explicitly invoked UI smoke harness, not on FanAgent ticks.
/// </summary>
internal static class AiForegroundNativeOwnedWindowSmoke
{
    private const uint WmNull = 0;
    private const uint WmQuit = 0x12;
    private const uint SmtoBlock = 1;
    private const uint SmtoAbortIfHung = 2;
    private const int ProbeCount = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X; internal int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Hwnd;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }

    internal static string RunFunctionalSmoke()
    {
        if (!OperatingSystem.IsWindows())
            return "AI_FOREGROUND_NATIVE_OWN_WINDOW_SMOKE_SKIP nonWindows=true";
        using var ready = new ManualResetEventSlim(false);
        nint ownHwnd = 0;
        uint ownThreadId = 0;
        Exception? workerFault = null;
        Thread gui = new(() =>
        {
            try
            {
                ownThreadId = GetCurrentThreadId();
                // HWND_MESSAGE: this invisible STATIC-class child is never
                // foreground, cannot receive keyboard/mouse interaction.
                ownHwnd = CreateWindowExW(
                    0, "STATIC", "SpankyOptimizerOwnedSmoke", 0,
                    0, 0, 0, 0, new nint(-3), 0, 0, 0);
                if (ownHwnd == 0)
                    throw new InvalidOperationException(
                        "Owned message-only HWND was not created (Win32=" +
                        Marshal.GetLastWin32Error() + ")");
                ready.Set();
                while (true)
                {
                    int result = GetMessageW(out var message, 0, 0, 0);
                    if (result <= 0)
                    {
                        if (result < 0)
                            throw new InvalidOperationException("Owned HWND message pump failed.");
                        break;
                    }
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            catch (Exception error)
            {
                workerFault = error;
                ready.Set();
            }
            finally
            {
                if (ownHwnd != 0)
                    DestroyWindow(ownHwnd);
            }
        }) { IsBackground = true, Name = "Optimizer-Owned-Hwnd-Smoke" };

        using Process testProcess = Process.GetCurrentProcess();
        testProcess.Refresh();
        long beforeCpu = testProcess.TotalProcessorTime.Ticks;
        long beforeWorkingSet = testProcess.WorkingSet64;
        int beforeHandles = testProcess.HandleCount;
        int beforeThreads = testProcess.Threads.Count;
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        string resultText = string.Empty;
        bool joined = true;
        try
        {
            gui.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Owned native HWND creation timed out.");
            if (workerFault is not null)
                throw new InvalidOperationException("Owned window smoke thread failed.", workerFault);
            if (ownHwnd == 0 || ownThreadId == 0)
                throw new InvalidOperationException("Owned window identity unavailable.");
            _ = GetWindowThreadProcessId(ownHwnd, out uint pid);
            if (pid != Environment.ProcessId || GetForegroundWindow() == ownHwnd)
                throw new InvalidOperationException("Smoke HWND is not an isolated owned window.");

            double min = double.MaxValue, max = 0, total = 0;
            for (int i = 0; i < ProbeCount; i++)
            {
                long started = Stopwatch.GetTimestamp();
                nint ok = SendMessageTimeoutW(ownHwnd, WmNull, 0, 0,
                    SmtoBlock | SmtoAbortIfHung,
                    AiForegroundWindowResponsePolicy.NativeTimeoutMs, out _);
                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (ok == 0 || ms > AiForegroundWindowResponsePolicy.NativeTimeoutMs)
                    throw new InvalidOperationException(
                        "Owned WM_NULL window failed bounded native roundtrip.");
                min = Math.Min(min, ms);
                max = Math.Max(max, ms);
                total += ms;
            }
            testProcess.Refresh();
            double cpuMs = TimeSpan.FromTicks(
                Math.Max(0, testProcess.TotalProcessorTime.Ticks - beforeCpu))
                .TotalMilliseconds;
            long workingSetDeltaKiB = (testProcess.WorkingSet64 - beforeWorkingSet) / 1024;
            int handleDelta = testProcess.HandleCount - beforeHandles;
            int threadDelta = testProcess.Threads.Count - beforeThreads;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            resultText = "AI_FOREGROUND_NATIVE_OWN_WINDOW_SMOKE_OK " +
                $"ownedWindowOnly=true samples={ProbeCount} " +
                $"minMs={min:0.###} avgMs={total / ProbeCount:0.###} " +
                $"maxMs={max:0.###} testProcessCpuMs={cpuMs:0.###} " +
                $"testThreadAllocB={allocated} testWorkingSetDeltaKiB={workingSetDeltaKiB} " +
                $"testHandleDelta={handleDelta} testThreadDelta={threadDelta} boundedTimeoutMs=" +
                AiForegroundWindowResponsePolicy.NativeTimeoutMs +
                " noUserWindowMessages=true noSettingsWrites=true";
        }
        finally
        {
            if (ownThreadId != 0)
                PostThreadMessageW(ownThreadId, WmQuit, 0, 0);
            if (gui.IsAlive)
                joined = gui.Join(TimeSpan.FromSeconds(3));
        }
        if (!joined)
            throw new InvalidOperationException(
                "Owned GUI smoke worker did not stop after WM_QUIT.");
        return resultText;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint param);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessageW(out NativeMessage msg, nint hwnd, uint min, uint max);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessageW(ref NativeMessage message);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(
        nint hwnd, uint msg, nint wParam, nint lParam, uint flags,
        uint timeoutMs, out nint result);
}
