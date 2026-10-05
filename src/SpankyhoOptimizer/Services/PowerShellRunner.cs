using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Deliberately limited to external .ps1 tools such as RemoveWindowsAI.
// Internal Optimizer features must use native .NET/WMI/COM backends instead.
public static class PowerShellRunner
{
    public static Task<ProcessResult> RunFileStreamingAsync(string scriptPath, string arguments, TimeSpan timeout,
        CancellationToken cancellationToken, Action<string>? onStdOut, Action<string>? onStdErr)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !System.IO.File.Exists(scriptPath))
            throw new System.IO.FileNotFoundException("PowerShell script nebyl nalezen.", scriptPath);
        if (!scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Povolen je pouze externí .ps1 script.", nameof(scriptPath));
        string escaped = scriptPath.Replace("\"", "\\\"");
        string args = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{escaped}\" {arguments}";
        return ProcessRunner.RunStreamingAsync("powershell.exe", args, timeout, cancellationToken, onStdOut, onStdErr);
    }
}
