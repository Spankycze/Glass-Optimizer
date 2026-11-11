using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

// F05: read-only hardware evidence behind the already selected Bluetooth row.
// A disabled/absent adapter is optional, not a faulty Windows optimization.
internal static class BluetoothAdapterHealthService
{
    internal sealed record Adapter(string Name, int ErrorCode);
    internal sealed record Snapshot(bool Available, IReadOnlyList<Adapter> Adapters);

    internal static Snapshot Read()
    {
        try
        {
            var list = new List<Adapter>();
            var scope = new ManagementScope(@"\\.\root\CIMV2");
            scope.Options.Timeout = TimeSpan.FromSeconds(3);
            scope.Connect();
            using var query = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceID,Name,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'"),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false, Rewindable = false });
            using var items = query.Get();
            foreach (ManagementObject item in items)
            {
                using (item)
                {
                    string id = Convert.ToString(item["DeviceID"], CultureInfo.InvariantCulture) ?? "";
                    // Only physical USB/PCI adapters. BTHENUM child devices may
                    // be paired, disconnected or have deliberate stale entries.
                    if (!IsPhysicalAdapterId(id)) continue;
                    int error = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? 0, CultureInfo.InvariantCulture);
                    string name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? "Bluetooth adapter";
                    if (name.Length > 90) name = name[..90];
                    list.Add(new Adapter(name, error));
                    if (list.Count >= 8) break;
                }
            }
            return new Snapshot(true, list);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or
            UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { return new Snapshot(false, []); }
    }

    internal static bool IsPhysicalAdapterId(string? id) =>
        id is not null &&
        (id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) ||
         id.StartsWith("PCI\\VEN_", StringComparison.OrdinalIgnoreCase));

    internal static bool IsActionableDriverError(int code) =>
        code is 10 or 28 or 31 or 43 or 52;

    internal static string RunPolicySmoke()
    {
        if (!IsPhysicalAdapterId("USB\\VID_8087&PID_0029") ||
            !IsPhysicalAdapterId("PCI\\VEN_8086") ||
            IsPhysicalAdapterId("BTHENUM\\DEV_000000000001") ||
            !IsActionableDriverError(43) || !IsActionableDriverError(28) ||
            IsActionableDriverError(0) || IsActionableDriverError(22) ||
            IsActionableDriverError(45))
            throw new InvalidOperationException("Bluetooth physical-adapter classification regressed.");
        return "BLUETOOTH_ADAPTER_POLICY_SMOKE_OK physicalAdapterOnly=true missingOptional=true disabledNotError=true unpluggedNotError=true writes=0";
    }
}
