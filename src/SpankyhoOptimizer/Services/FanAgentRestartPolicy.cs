using System;
using System.IO;
using System.Security;

namespace SpankyhoOptimizer.Services;

public static class FanAgentRestartPolicy
{
    public static bool ShouldRestart(string? payload) =>
        !string.IsNullOrWhiteSpace(payload);

    // Consume the request BEFORE restarting. An unreadable or undeletable marker
    // must not cause a process restart storm on every fan-control iteration.
    public static bool TryConsumeRestartRequest(string path, Action<Exception>? onError = null)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (!ShouldRestart(File.ReadAllText(path))) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            onError?.Invoke(ex);
            return false;
        }
    }
}
