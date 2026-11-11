using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record UsbHidDeviceInfo(
    string DeviceId,
    string Name,
    string PnpClass,
    string Manufacturer,
    string Status,
    int ErrorCode,
    bool IsControllerOrHub);

public sealed record UsbHidPowerSnapshot(
    bool? SelectiveSuspendDisabled,
    int RegistryDeviceParameters,
    int RegistryPowerSavingEnabled)
{
    public int RegistryPowerSavingDisabled => Math.Max(0, RegistryDeviceParameters - RegistryPowerSavingEnabled);
}

public static class UsbHidInventoryService
{
    private static readonly object Gate = new();
    private static IReadOnlyList<UsbHidDeviceInfo>? _cache;
    private static DateTime _cacheAt;
    private static Task<IReadOnlyList<UsbHidDeviceInfo>>? _flight;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static Task<IReadOnlyList<UsbHidDeviceInfo>> GetDevicesAsync(bool force = false, CancellationToken token = default)
    {
        lock (Gate)
        {
            if (!force && _cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                return Task.FromResult(_cache);
            if (!force && _flight != null && !_flight.IsCompleted)
                return _flight;

            _flight = LoadDevicesAsync(token);
            return _flight;
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _cache = null;
            _cacheAt = default;
        }
    }

    private static async Task<IReadOnlyList<UsbHidDeviceInfo>> LoadDevicesAsync(CancellationToken token)
    {
        try
        {
            var hardware = await HardwareSnapshotService.GetAsync(false, token).ConfigureAwait(false);
            var items = await Task.Run(() =>
            {
                try
                {
                    var native = hardware.Devices
                        .Where(x => IsUsbHid(x.InstanceId, x.Class, x.FriendlyName))
                        .Select(x => new UsbHidDeviceInfo(
                            x.InstanceId,
                            string.IsNullOrWhiteSpace(x.FriendlyName) ? x.InstanceId : x.FriendlyName,
                            x.Class,
                            x.Manufacturer,
                            x.ProblemCode == 0 ? "OK" : "Error",
                            x.ProblemCode,
                            IsControllerOrHub(x.Class, x.FriendlyName)))
                        .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First())
                        .OrderByDescending(x => x.IsControllerOrHub)
                        .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();

                    if (native.Length > 0)
                    {
                        LogService.Info("usb", $"USB_HID_PROVIDER provider=SetupAPI devices={native.Length}");
                        return (IReadOnlyList<UsbHidDeviceInfo>)native;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error("usb", "SetupAPI USB/HID inventory failed; falling back to WMI.", ex);
                }

                var result = new List<UsbHidDeviceInfo>();
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID,Name,PNPClass,Manufacturer,Status,ConfigManagerErrorCode FROM Win32_PnPEntity");
                var rows = searcher.Get();
                WmiEnumerationScope.ForEach<ManagementObject>(rows, rows, token, x =>
                {
                    string id = x["DeviceID"]?.ToString() ?? string.Empty;
                    string cls = x["PNPClass"]?.ToString() ?? string.Empty;
                    string name = x["Name"]?.ToString() ?? id;
                    if (!IsUsbHid(id, cls, name)) return;

                    string manufacturer = x["Manufacturer"]?.ToString() ?? "Unknown";
                    string status = x["Status"]?.ToString() ?? "Unknown";
                    int error = Convert.ToInt32(x["ConfigManagerErrorCode"] ?? 0, CultureInfo.InvariantCulture);
                    result.Add(new UsbHidDeviceInfo(
                        id, name, cls, manufacturer, status, error, IsControllerOrHub(cls, name)));
                });

                LogService.Info("usb", $"USB_HID_PROVIDER provider=WMI devices={result.Count}");
                return (IReadOnlyList<UsbHidDeviceInfo>)result
                    .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First())
                    .OrderByDescending(x => x.IsControllerOrHub)
                    .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }, token).ConfigureAwait(false);

            lock (Gate)
            {
                _cache = items;
                _cacheAt = DateTime.UtcNow;
                _flight = null;
            }

            LogService.Info("usb", $"USB_HID_INVENTORY_OK devices={items.Count} controllers={items.Count(x => x.IsControllerOrHub)} errors={items.Count(x => x.ErrorCode != 0)}");
            return items;
        }
        catch
        {
            lock (Gate) _flight = null;
            throw;
        }
    }

    public static async Task<UsbHidPowerSnapshot> GetPowerSnapshotAsync(CancellationToken token = default)
    {
        var powerTask = WindowsActions.GetUsbSelectiveSuspendDisabledAsync(token);
        var registryTask = Task.Run(ReadRegistryPowerState, token);

        bool? disabled = await powerTask.ConfigureAwait(false);
        var registry = await registryTask.ConfigureAwait(false);

        return new UsbHidPowerSnapshot(disabled, registry.total, registry.enabled);
    }

    private static (int total, int enabled) ReadRegistryPowerState()
    {
        int total = 0;
        int enabled = 0;
        foreach (string rootPath in new[]
                 {
                     @"SYSTEM\CurrentControlSet\Enum\USB",
                     @"SYSTEM\CurrentControlSet\Enum\USBSTOR",
                     @"SYSTEM\CurrentControlSet\Enum\HID"
                 })
        {
            using var rootKey = Registry.LocalMachine.OpenSubKey(rootPath);
            if (rootKey == null) continue;
            foreach (string a in rootKey.GetSubKeyNames())
            {
                using var aKey = rootKey.OpenSubKey(a);
                if (aKey == null) continue;
                foreach (string b in aKey.GetSubKeyNames())
                {
                    try
                    {
                        using var dp = Registry.LocalMachine.OpenSubKey(rootPath + "\\" + a + "\\" + b + "\\Device Parameters");
                        if (dp == null) continue;

                        bool hasAny = false;
                        bool anyEnabled = false;
                        foreach (string valueName in new[]
                                 {
                                     "EnhancedPowerManagementEnabled",
                                     "SelectiveSuspendEnabled",
                                     "DeviceSelectiveSuspended"
                                 })
                        {
                            object? raw = dp.GetValue(valueName);
                            if (raw == null) continue;
                            hasAny = true;
                            try
                            {
                                if (Convert.ToInt32(raw, CultureInfo.InvariantCulture) != 0)
                                    anyEnabled = true;
                            }
                            catch { }
                        }

                        if (!hasAny) continue;
                        total++;
                        if (anyEnabled) enabled++;
                    }
                    catch { }
                }
            }
        }

        return (total, enabled);
    }

    private static bool IsUsbHid(string id, string cls, string name)
    {
        if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
            return true;

        return cls.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("HID", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControllerOrHub(string cls, string name)
    {
        string x = cls + " " + name;
        return x.Contains("Host Controller", StringComparison.OrdinalIgnoreCase) ||
               x.Contains("Root Hub", StringComparison.OrdinalIgnoreCase) ||
               x.Contains("USB Hub", StringComparison.OrdinalIgnoreCase) ||
               x.Contains("Generic USB Hub", StringComparison.OrdinalIgnoreCase) ||
               x.Contains("Controller", StringComparison.OrdinalIgnoreCase);
    }
}
