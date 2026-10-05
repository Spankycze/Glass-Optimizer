using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static class GTweakRegistryExportService
{
    private static readonly string[] Paths =
    {
        @"HKEY_CLASSES_ROOT\*\shellex", @"HKEY_CLASSES_ROOT\CLSID", @"HKEY_CLASSES_ROOT\Wow6432Node\CLSID",
        @"HKEY_CLASSES_ROOT\AppID", @"HKEY_CURRENT_USER\Software", @"HKEY_CURRENT_USER\System",
        @"HKEY_CURRENT_USER\Control Panel", @"HKEY_USERS\.DEFAULT\Control Panel", @"HKEY_USERS\S-1-5-19\Control Panel",
        @"HKEY_USERS\S-1-5-20\Control Panel", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Clients"
    };

    public static async Task ExportAsync(string destination, CancellationToken token)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SpankyhoRegExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var files = new List<string>();
        try
        {
            int index = 0;
            foreach (string path in Paths)
            {
                token.ThrowIfCancellationRequested();
                if (!RegistryKeyExists(path)) continue;
                string temp = Path.Combine(tempRoot, $"part-{index++:D2}.reg");
                var result = await ProcessRunner.RunAsync("reg.exe", $"EXPORT \"{path}\" \"{temp}\" /y", TimeSpan.FromMinutes(3), token);
                if (result.Success && File.Exists(temp)) files.Add(temp);
                else LogService.Info("restore", $"Registry export skipped/failed path={path} code={result.ExitCode}");
            }
            if (files.Count == 0) throw new InvalidOperationException("Nebyla exportována žádná registry větev.");
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await using var writer = new StreamWriter(output, Encoding.Unicode);
            bool first = true;
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                string text = await File.ReadAllTextAsync(file, Encoding.Unicode, token);
                if (!first)
                {
                    int header = text.IndexOf(Environment.NewLine, StringComparison.Ordinal);
                    if (header >= 0) text = text[(header + Environment.NewLine.Length)..];
                }
                await writer.WriteLineAsync(text.AsMemory(), token);
                first = false;
            }
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
            catch (Exception ex) { LogService.Error("restore", "Registry export temp cleanup failed", ex); }
        }
    }
    private static bool RegistryKeyExists(string fullPath)
    {
        int split = fullPath.IndexOf('\\');
        if (split <= 0) return false;
        string hiveName = fullPath[..split];
        string sub = fullPath[(split + 1)..];
        RegistryKey? hive = hiveName switch
        {
            "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKEY_USERS" => Registry.Users,
            "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
            _ => null
        };
        if (hive == null) return false;
        try { using RegistryKey? key = hive.OpenSubKey(sub); return key != null; }
        catch { return false; }
    }
}
