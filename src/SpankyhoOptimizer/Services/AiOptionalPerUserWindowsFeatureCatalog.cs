using System;
using System.Collections.Generic;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

// Read-only user-session service inventory. Never infer permission to stop a
// Clipboard, notification, account-sync, capture, printer or Bluetooth service.
internal static class AiOptionalPerUserWindowsFeatureCatalog
{
    private static readonly Dictionary<string, string> AreaLookup =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AarSvc"] = "Hlasoví asistenti",
            ["BcastDVRUserService"] = "Nahrávání her",
            ["BluetoothUserService"] = "Bluetooth",
            ["CaptureService"] = "Snímání obrazovky",
            ["cbdhsvc"] = "Schránka",
            ["CDPUserSvc"] = "Synchronizace zařízení",
            ["CloudBackupRestoreSvc"] = "Cloudové zálohování",
            ["DevicePickerUserSvc"] = "Výběr zařízení",
            ["DevicesFlowUserSvc"] = "Připojování zařízení",
            ["MessagingService"] = "Zprávy",
            ["OneSyncSvc"] = "Synchronizace účtů",
            ["PenService"] = "Pero a inkoust",
            ["PimIndexMaintenanceSvc"] = "Osobní údaje",
            ["PrintWorkflowUserSvc"] = "Tiskové funkce",
            ["UnistoreSvc"] = "Uživatelská data",
            ["UserDataSvc"] = "Uživatelská data",
            ["WpnUserService"] = "Oznámení",
        };

    private static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>
        CachedAreaSpanLookup = AreaLookup.GetAlternateLookup<ReadOnlySpan<char>>();

    internal static IReadOnlyDictionary<string, string> Areas => AreaLookup;

    internal static bool TryResolve(string? instanceName, out AiOptionalWindowsFeature feature)
    {
        feature = default;
        if (string.IsNullOrEmpty(instanceName))
            return false;
        int marker = instanceName.LastIndexOf('_');
        if (marker <= 0 || marker >= instanceName.Length - 1)
            return false;
        int suffixLength = instanceName.Length - marker - 1;
        if (suffixLength is < 4 or > 16)
            return false;
        foreach (char c in instanceName.AsSpan(marker + 1))
        {
            if (!((c >= '0' && c <= '9') ||
                  (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        // Cached alternate lookup avoids per-service substring allocations
        // while preserving Windows' case-insensitive service identity.
        if (!CachedAreaSpanLookup.TryGetValue(
                instanceName.AsSpan(0, marker), out string? area))
            return false;
        feature = new AiOptionalWindowsFeature(instanceName, area);
        return true;
    }

    internal static int ObserveRuntimeStates(
        IReadOnlyDictionary<string, ServiceControllerStatus> states,
        ISet<string> automaticCandidates,
        Action<AiOptionalWindowsFeature, ServiceControllerStatus> onObserved)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(automaticCandidates);
        ArgumentNullException.ThrowIfNull(onObserved);
        int count = 0;
        foreach (var item in states)
        {
            if (automaticCandidates.Contains(item.Key) ||
                !TryResolve(item.Key, out var feature))
                continue;
            onObserved(feature, item.Value);
            count++;
        }
        return count;
    }
}
