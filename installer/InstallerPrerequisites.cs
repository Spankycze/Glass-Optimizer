using System;
using System.IO;
using Microsoft.Win32;

namespace SpankyhoOptimizer.ModernSetup;

// Detect only actual runtime dependencies used by the published, x64,
// framework-dependent main application. Do not confuse .NETCore.App
// with WindowsDesktop.App or download WinAppSDK when bundled self-contained.
internal static class InstallerPrerequisites
{
    internal const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    internal static bool IsSupportedWindows(bool is64Bit, int build) =>
        OperatingSystem.IsWindows() && is64Bit && build >= 19041;

    internal static bool HasDesktopRuntime10(out string? version, string? dotnetRoot = null)
    {
        version = null;
        try
        {
            dotnetRoot ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            var shared = Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(shared)) return false;
            Version? best = null;
            foreach (string dir in Directory.EnumerateDirectories(shared))
            {
                if (Version.TryParse(Path.GetFileName(dir), out var installed) &&
                    installed.Major == 10 && installed.Build >= 0 &&
                    (best == null || installed > best))
                    best = installed;
            }
            if (best == null) return false;
            version = best.ToString();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool IsInstalledVersion(string? raw)
    {
        return Version.TryParse(raw, out var v) && v > new Version(0, 0, 0, 0);
    }

    internal static bool HasWebView2(out string? version)
    {
        version = null;
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId);
                    string? found = key?.GetValue("pv") as string;
                    if (!IsInstalledVersion(found)) continue;
                    version = found;
                    return true;
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (ArgumentException) { }
            }
        }
        return false;
    }
}
