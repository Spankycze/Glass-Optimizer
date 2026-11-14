using System;
using System.Threading.Tasks;

// Dependency-injected runtime workflow. Tests can simulate every result without
// starting Windows installers, downloading anything, or requesting elevation.
internal static class BootstrapDecision
{
    internal static int ClassifyRuntimeExit(int exitCode) => exitCode switch
    {
        0 => 0,
        1641 or 3010 => 57,
        _ => 53
    };

    internal static async Task<int> EnsureThenLaunchAsync(
        Func<bool> runtimeReady,
        Func<Task<int>> installRuntime,
        Func<Task<int>> launchWpf)
    {
        if (!runtimeReady())
        {
            int status = await installRuntime().ConfigureAwait(false);
            if (status != 0) return status;
            if (!runtimeReady()) return 54;
        }
        return await launchWpf().ConfigureAwait(false);
    }
}
