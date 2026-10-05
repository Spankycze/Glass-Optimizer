using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static class GTweakDebloatPostCleanupService
{
    public static async Task ApplyAsync(InstalledAppInfo app, CancellationToken token = default)
    {
        string name = app.PackageName;
        if (name.Equals("MicrosoftWindows.Client.WebExperience", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.WidgetsPlatformRuntime", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.StartExperiencesApp", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(DisableWidgetsReoffer, token);
            return;
        }
        if (name.Equals("Microsoft.Windows.DevHome", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => DeleteKey(@"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\DevHomeUpdate"), token);
            return;
        }
        if (name.Equals("Microsoft.OutlookForWindows", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.Office.Outlook", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => DeleteKey(@"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\OutlookUpdate"), token);
            return;
        }
        if (name.Equals("Microsoft.MSPaint", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(RemovePaint3DContextHandlers, token);
        }
    }

    private static void DisableWidgetsReoffer()
    {
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Dsh", true);
        key.SetValue("AllowNewsAndInterests", 0, RegistryValueKind.DWord);
    }

    private static void DeleteKey(string path)
    {
        Registry.LocalMachine.DeleteSubKeyTree(path, false);
    }
    private static void RemovePaint3DContextHandlers()
    {
        using var baseKey = Registry.ClassesRoot.OpenSubKey("SystemFileAssociations", writable: true);
        if (baseKey == null) return;
        foreach (string sub in baseKey.GetSubKeyNames())
        {
            try
            {
                using var shell = baseKey.OpenSubKey(sub + "\\Shell", writable: true);
                if (shell == null) continue;
                if (shell.GetSubKeyNames().Any(x => x.Equals("3D Print", StringComparison.OrdinalIgnoreCase)))
                    shell.DeleteSubKeyTree("3D Print", false);
            }
            catch (Exception ex)
            {
                LogService.Error("debloat", $"Paint3D context cleanup failed for {sub}", ex);
            }
        }
    }
}
