using System;
using System.Diagnostics;

namespace SpankyhoOptimizer.Services;

public static class SafeModeLauncherService
{
    public static void RestartInSafeMode()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Nelze zjistit cestu aplikace.");
        int currentPid = Environment.ProcessId;
        using var started = Process.Start(new ProcessStartInfo(exe, $"--safe-mode --handoff-pid={currentPid}") { UseShellExecute = true });
        if (started == null) throw new InvalidOperationException("Safe Mode instanci se nepodařilo spustit.");
        Environment.Exit(0);
    }
}
