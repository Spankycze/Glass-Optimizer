using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Autonomous Device Manager support, deliberately limited to explicitly
// unnecessary, positively identified optional PnP devices. An absent/Unknown
// device is NOT unused: never disable ghost HID, storage, network or display.
internal sealed class AiUnusedPnpDeviceOptimizationService
{
    private const string AllowedIntelBluetoothUsbPrefix = @"USB\VID_8087&PID_";
    private sealed record Ownership(string InstanceId, bool Applied = false,
        bool Suppressed = false);
    private sealed record Device(string InstanceId, int ProblemCode);

    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-unused-pnp-ownership.json");
    private DateTime _nextProbeUtc = DateTime.MinValue;
    private DateTime _nextConsentUtc = DateTime.MinValue;
    private bool _userConsent;

    internal bool IsManaged { get; private set; }
    internal string LastError { get; private set; } = "";

    private bool UserOptedIn()
    {
        DateTime now = DateTime.UtcNow;
        if (_nextConsentUtc > now && now > _nextConsentUtc.AddMinutes(-2))
            return _userConsent;
        _nextConsentUtc = now.AddSeconds(15);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\SpankyhoOptimizer\AI");
            _userConsent = key?.GetValue("DisableUnusedDevices") is int v && v == 1;
        }
        catch { _userConsent = false; }
        return _userConsent;
    }

    internal async Task<string?> TickAsync(bool aiAllowed, bool bluetoothOwned,
        CancellationToken token)
    {
        bool enabled = aiAllowed && UserOptedIn() && bluetoothOwned;
        DateTime now = DateTime.UtcNow;
        if (enabled && now < _nextProbeUtc && now > _nextProbeUtc.AddMinutes(-4))
            return null;
        _nextProbeUtc = now.AddMinutes(3);
        if (!enabled && !File.Exists(JournalPath))
        {
            IsManaged = false;
            LastError = "";
            return null;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            Ownership? owner = File.Exists(JournalPath)
                ? JsonSerializer.Deserialize<Ownership>(File.ReadAllText(JournalPath))
                    ?? throw new InvalidDataException("PnP ownership journal is unreadable")
                : null;
            if (owner != null && !IsAllowedInstanceId(owner.InstanceId))
                throw new InvalidDataException("PnP journal contains disallowed hardware");

            // Manual re-enable is authoritative until preference is toggled
            // OFF and ON again. Do not fight an explicit Device Manager change.
            if (owner is { Suppressed: true })
            {
                if (!enabled) File.Delete(JournalPath);
                IsManaged = false;
                LastError = "";
                return null;
            }

            if (!enabled)
            {
                if (owner == null) return null;
                Device? oldDevice = ReadOne(owner.InstanceId);
                if (oldDevice?.ProblemCode == 22)
                {
                    await ChangeAsync(owner.InstanceId, enable: true, token);
                    Device? restored = ReadOne(owner.InstanceId);
                    if (restored?.ProblemCode != 0)
                        throw new IOException("Device Manager enable read-back was not OK");
                }
                else if (oldDevice == null || oldDevice.ProblemCode != 0)
                    throw new IOException("Device Manager device unavailable: retain ownership for recovery");
                File.Delete(JournalPath);
                IsManaged = false;
                LastError = "";
                return "Device Manager: original Intel Bluetooth adapter enabled";
            }

            Device? device = owner != null
                ? ReadOne(owner.InstanceId) : FindEligibleBluetoothAdapter();

            if (owner is { Applied: true })
            {
                if (device?.ProblemCode == 22)
                {
                    IsManaged = true;
                    LastError = "";
                    return null;
                }
                if (device?.ProblemCode == 0)
                {
                    AtomicFileService.WriteText(JournalPath,
                        JsonSerializer.Serialize(owner with { Suppressed = true }));
                    IsManaged = false;
                    LastError = "";
                    return "Device Manager: manually re-enabled adapter respected";
                }
                throw new IOException("Owned Device Manager adapter cannot be identified");
            }

            if (device?.ProblemCode == 22)
            {
                // A crash may have occurred after native disable and before
                // WAL Applied=true. Recover only OUR exact pending instance.
                if (owner is { Applied: false })
                {
                    AtomicFileService.WriteText(JournalPath,
                        JsonSerializer.Serialize(owner with { Applied = true }));
                    IsManaged = true;
                    LastError = "";
                    return "Device Manager: interrupted Bluetooth ownership recovered";
                }
                return null; // Already disabled by someone else; never own it.
            }
            if (device?.ProblemCode != 0)
                return null; // No present, normally working allowlisted device.

            if (HasAnyActiveBluetoothChild())
                return null; // Never remove active BT HID/audio/paired connection.

            if (owner == null)
            {
                owner = new Ownership(device.InstanceId);
                AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(owner));
            }

            await ChangeAsync(owner.InstanceId, enable: false, token);
            if (ReadOne(owner.InstanceId)?.ProblemCode != 22)
                throw new IOException("Device Manager disable read-back was not Code 22");
            AtomicFileService.WriteText(JournalPath,
                JsonSerializer.Serialize(owner with { Applied = true }));
            IsManaged = true;
            LastError = "";
            return "Device Manager: unused Intel Bluetooth adapter Disabled (PnP read-back)";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            IsManaged = false;
            LastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "Device Manager optional PnP optimization failed", ex);
            return null;
        }
    }

    private static bool IsAllowedInstanceId(string? id)
    {
        // Support another Intel USB Bluetooth hardware revision after a
        // radio replacement without authorizing an unrelated USB device.
        // PnPClass=Bluetooth and manufacturer=Intel are independently
        // required before any native /disable-device write.
        if (id is not { Length: > 27 and < 256 } ||
            !id.StartsWith(AllowedIntelBluetoothUsbPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            id.Any(char.IsControl) || id.Contains('"'))
            return false;
        int pidStart = AllowedIntelBluetoothUsbPrefix.Length;
        if (id.Length <= pidStart + 5 || id[pidStart + 4] != '\\')
            return false;
        for (int i = pidStart; i < pidStart + 4; i++)
            if (!Uri.IsHexDigit(id[i])) return false;
        return true;
    }

    private static Device? FindEligibleBluetoothAdapter()
    {
        using var search = new ManagementObjectSearcher(
            "SELECT DeviceID,PNPClass,Manufacturer,ConfigManagerErrorCode " +
            "FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'");
        using var results = search.Get();
        var eligible = results.Cast<ManagementObject>().Select(item =>
        {
            using (item)
            {
                string id = Convert.ToString(item["DeviceID"]) ?? "";
                string vendor = Convert.ToString(item["Manufacturer"]) ?? "";
                return IsAllowedInstanceId(id) &&
                    vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                    ? new Device(id, Convert.ToInt32(item["ConfigManagerErrorCode"] ?? -1))
                    : null;
            }
        }).Where(x => x != null).ToArray();
        return eligible.Length == 1 ? eligible[0] : null; // No ambiguous radio.
    }

    private static Device? ReadOne(string instanceId)
    {
        if (!IsAllowedInstanceId(instanceId)) return null;
        using var search = new ManagementObjectSearcher(
            "SELECT DeviceID,PNPClass,Manufacturer,ConfigManagerErrorCode " +
            "FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'");
        using var devices = search.Get();
        foreach (ManagementObject item in devices)
        {
            using (item)
            {
                if (string.Equals(Convert.ToString(item["DeviceID"]), instanceId,
                        StringComparison.OrdinalIgnoreCase) &&
                    (Convert.ToString(item["Manufacturer"]) ?? "").Contains(
                        "Intel", StringComparison.OrdinalIgnoreCase))
                    return new Device(instanceId,
                        Convert.ToInt32(item["ConfigManagerErrorCode"] ?? -1));
            }
        }
        return null;
    }

    private static bool HasAnyActiveBluetoothChild()
    {
        using var query = new ManagementObjectSearcher(
            "SELECT DeviceID,ConfigManagerErrorCode FROM Win32_PnPEntity " +
            "WHERE DeviceID LIKE 'BTHENUM%' OR DeviceID LIKE 'BTHLEDEVICE%' " +
            "OR DeviceID LIKE 'BTHHFENUM%' OR DeviceID LIKE 'BTHHID%'");
        using var results = query.Get();
        foreach (ManagementObject child in results)
        {
            using (child)
                if (Convert.ToInt32(child["ConfigManagerErrorCode"] ?? -1) == 0)
                    return true;
        }
        return false;
    }

    private static async Task ChangeAsync(string instanceId, bool enable,
        CancellationToken token)
    {
        if (!IsAllowedInstanceId(instanceId))
            throw new InvalidOperationException("Device Manager mutation not allowlisted");
        string action = enable ? "/enable-device" : "/disable-device";
        // No /force, remove/uninstall or device-tree/subtree change. PnPUtil
        // runs under the existing elevated FanAgent Windows account.
        var result = await ProcessRunner.RunAsync("pnputil.exe",
            action + " \"" + instanceId + "\"", TimeSpan.FromSeconds(24),
            token).ConfigureAwait(false);
        if (!result.Success)
            throw new IOException("PnPUtil returned " + result.ExitCode + ": " +
                (result.StdErr + " " + result.StdOut).Trim());
    }
}
