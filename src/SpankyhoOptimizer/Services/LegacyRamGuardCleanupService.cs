using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static class LegacyRamGuardCleanupService
{
    private const string LegacyTask = @"\RAM Guard Win11 Admin Engine";

    public static void CleanupBestEffort()
    {
        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (!process.ProcessName.Contains("RAM_Guard", StringComparison.OrdinalIgnoreCase) &&
                        !process.ProcessName.Contains("RAM Guard", StringComparison.OrdinalIgnoreCase))
                        continue;

                    process.Kill(true);
                    process.WaitForExit(1200);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch { }

        CleanupRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run");
        CleanupRunKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run");
        CleanupRunKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");

        try
        {
            TaskSchedulerComService.DeleteIfExists(LegacyTask);
        }
        catch (Exception ex)
        {
            LogService.Error("cleanup", "Legacy RAM Guard task cleanup failed", ex);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DeleteTree(Path.Combine(localAppData, "RAM Guard"));
        DeleteTree(Path.Combine(localAppData, "RAMGuard"));

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        DeleteFile(Path.Combine(desktop, "RAM Guard.lnk"));
        DeleteFile(Path.Combine(desktop, "RAM_Guard.lnk"));

        LogService.Info("cleanup", "LEGACY_RAM_GUARD_CLEANUP completed");
    }

    private static void CleanupRunKey(RegistryKey hive, string path)
    {
        try
        {
            using RegistryKey? run = hive.OpenSubKey(path, writable: true);
            if (run == null) return;

            foreach (string name in run.GetValueNames())
            {
                string value = run.GetValue(name)?.ToString() ?? string.Empty;
                if (name.Contains("RAM Guard", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("RAM_Guard", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("RAM Guard", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("RAM_Guard", StringComparison.OrdinalIgnoreCase))
                {
                    try { run.DeleteValue(name, false); } catch { }
                }
            }
        }
        catch { }
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            LogService.Error("cleanup", $"Legacy folder cleanup failed path={path}", ex);
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
