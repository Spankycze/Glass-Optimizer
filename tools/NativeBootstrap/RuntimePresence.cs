using System;
using System.IO;

internal static class RuntimePresence
{
    // A folder name is not proof of a working Desktop Runtime.
    // Check WPF runtime files and at least one matching .NETCore.App major.
    internal static bool HasDesktop10(string root)
    {
        try
        {
            string baseDir = Path.Combine(root, "shared");
            string desktopPath = Path.Combine(baseDir, "Microsoft.WindowsDesktop.App");
            string corePath = Path.Combine(baseDir, "Microsoft.NETCore.App");
            if (!Directory.Exists(desktopPath) || !Directory.Exists(corePath)) return false;

            bool coreReady = false;
            foreach (string path in Directory.EnumerateDirectories(corePath))
            {
                if (Version.TryParse(Path.GetFileName(path), out Version? v) &&
                    v.Major == 10 && v.Build >= 0 &&
                    File.Exists(Path.Combine(path, "System.Private.CoreLib.dll")))
                {
                    coreReady = true;
                    break;
                }
            }
            if (!coreReady) return false;

            foreach (string path in Directory.EnumerateDirectories(desktopPath))
            {
                if (!Version.TryParse(Path.GetFileName(path), out Version? v) ||
                    v.Major != 10 || v.Build < 0) continue;
                if (File.Exists(Path.Combine(path, "PresentationFramework.dll")) &&
                    File.Exists(Path.Combine(path, "Microsoft.WindowsDesktop.App.runtimeconfig.json")) &&
                    File.Exists(Path.Combine(path, "wpfgfx_cor3.dll")))
                    return true;
            }
            return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
