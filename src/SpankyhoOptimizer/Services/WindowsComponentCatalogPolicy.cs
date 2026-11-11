using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

// Three up-to-date, read-only inventories, shared by AI discovery and the
// manual Služby / Ovladače / Task Scheduler catalogs. Candidate *definitions*
// remain portable and never grant new write permissions. Only registrations
// on THIS Windows installation become newly offered optimization candidates.
// Old journal entries are retained independently for restoration/history.
internal static class WindowsComponentCatalogPolicy
{
    private static readonly Lazy<HashSet<string>?> ServiceRegistrations =
        new(ReadServiceKeys);

    private static HashSet<string>? ReadServiceKeys()
    {
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services", writable: false);
            return root == null ? null :
                new HashSet<string>(root.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; } // Unknown inventory is NOT proof of absence.
    }

    private static bool IsRetired(string name, int build) =>
        build >= 26100 &&
        (name.Equals("AJRouter", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("SharedRealitySvc", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("spectrum", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("perceptionsimulation", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("MixedRealityOpenXRSvc", StringComparison.OrdinalIgnoreCase));

    internal static bool ServiceRegistered(string name)
    {
        if (IsRetired(name, Environment.OSVersion.Version.Build)) return false;
        var known = ServiceRegistrations.Value;
        return known == null || known.Contains(name);
    }

    internal static bool DriverRegistered(string name)
    {
        var known = ServiceRegistrations.Value;
        if (known == null) return true;
        if (!known.Contains(name)) return false;
        // Some Windows services share the Services registry tree. Only
        // type 1/2 are kernel/file-system drivers; do not offer drivers for
        // a same-named user-mode service.
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name, writable: false);
            if (key?.GetValue("Type") is int type)
                return type is 1 or 2;
            return false;
        }
        catch { return true; } // Cannot claim absent if unreadable.
    }

    internal static bool TaskRegistered(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) ||
            !fullPath.StartsWith('\\') || fullPath.Contains("..", StringComparison.Ordinal))
            return false;
        try
        {
            string relative = fullPath.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar);
            // Task Scheduler's registered XML definitions are under System32\Tasks.
            // Missing XML is a discovery hint only; the AI's actual COM batch
            // remains authoritative for exception classification and recovery.
            return File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "Tasks", relative));
        }
        catch { return true; } // Unreadable is not an unregistered task.
    }

    internal static string[] PresentInSnapshot(
        IReadOnlyList<string> definitions, IReadOnlySet<string> registered,
        string? kind = null, int build = 0)
    {
        List<string> result = new(definitions.Count);
        foreach (string name in definitions)
            if (registered.Contains(name) &&
                !(string.Equals(kind, "service", StringComparison.OrdinalIgnoreCase)
                  && IsRetired(name, build)))
                result.Add(name);
        return result.ToArray();
    }

    internal static string RunFunctionalSmoke()
    {
        string[] services = { "DiagTrack", "AJRouter", "Fax", "RemoteRegistry" };
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DiagTrack", "AJRouter", "RemoteRegistry" };
        var newOs = PresentInSnapshot(services, installed, "service", 26300);
        var oldOs = PresentInSnapshot(services, installed, "service", 22621);
        if (!newOs.SequenceEqual(new[] { "DiagTrack", "RemoteRegistry" }) ||
            !oldOs.SequenceEqual(new[] { "DiagTrack", "AJRouter", "RemoteRegistry" }))
            throw new InvalidOperationException("Windows version-aware service discovery failed.");
        string[] tasks = { @"\Microsoft\Windows\Maintenance\WinSAT", @"\Microsoft\Windows\Application Experience\AitAgent" };
        if (!PresentInSnapshot(tasks,
            new HashSet<string> { tasks[0] }, "task", 26300).SequenceEqual(new[] { tasks[0] }))
            throw new InvalidOperationException("Installed scheduled task discovery failed.");
        string[] drivers = { "Beep", "Changer", "Serial" };
        if (!PresentInSnapshot(drivers,
            new HashSet<string> { "Beep", "Serial" }, "driver", 26300)
            .SequenceEqual(new[] { "Beep", "Serial" }))
            throw new InvalidOperationException("Installed driver discovery failed.");
        return "WINDOWS_COMPONENT_CATALOG_SMOKE_OK services=versionAware " +
            "tasks=installedOnly drivers=installedOnly sourceListsUnchanged=true writes=0";
    }
}
