using System;

namespace SpankyhoOptimizer.Services;

// Visibility is broader than automated mutation: system security processes are
// observable for diagnostics, but never targets of automatic termination.
internal static class AiEngine2SystemComponentScope
{
    private static readonly string[] ProtectedProcesses =
    {
        "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
        "MpDefenderCoreService", "smartscreen", "Sense", "WdNisSvc",
        "svchost", "services", "lsass", "csrss", "wininit", "winlogon",
        "dwm", "System", "Registry", "TrustedInstaller", "TiWorker",
        "SearchHost", "StartMenuExperienceHost"
    };

    internal static bool IsProtectedProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        ReadOnlySpan<char> value = name.AsSpan();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        foreach (string protectedName in ProtectedProcesses)
            if (value.Equals(protectedName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    internal static bool CanAutomaticallyTerminate(string? processName) => false;
}
