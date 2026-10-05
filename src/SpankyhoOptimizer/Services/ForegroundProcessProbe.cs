using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

public static class ForegroundProcessProbe
{
    public static string GetName()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return string.Empty;

            _ = GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
                return string.Empty;

            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
