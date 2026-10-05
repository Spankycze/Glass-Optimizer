$p = Get-Process SpankyhoOptimizer -ErrorAction Stop | Select-Object -First 1
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Focus {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
$h = $p.MainWindowHandle
if ($h -eq 0) {
    Start-Sleep -Milliseconds 800
    $p.Refresh()
    $h = $p.MainWindowHandle
}
[Win32Focus]::ShowWindow($h, 9) | Out-Null
[Win32Focus]::SetForegroundWindow($h) | Out-Null
Write-Output "PID=$($p.Id) HWND=$h"
