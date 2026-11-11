using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

// One explicitly user-authorized, reversible Windows Bluetooth service opt-out.
// Never disable the Intel radio's PnP driver or attached HID/audio peripherals.
// Runs on the existing elevated FanAgent tick, without an added UI/scheduler.
internal sealed class AiUnusedBluetoothOptimizationService
{
    private const string ServiceName = "bthserv";
    private sealed record Ownership(int OriginalStart, bool WasRunning,
        bool Applied = false, bool Suppressed = false);

    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-unused-bluetooth-ownership.json");
    private DateTime _nextCheckUtc = DateTime.MinValue;
    private long _missingPreferenceSinceStamp;
    private bool _optedIn;
    private DateTime _nextPreferenceUtc = DateTime.MinValue;

    internal bool IsManaged { get; private set; }
    internal string LastError { get; private set; } = "";

    internal bool UserRequestsDisable()
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextPreferenceUtc && now >= _nextPreferenceUtc.AddMinutes(-2))
            return _optedIn;
        _nextPreferenceUtc = now.AddSeconds(15);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\SpankyhoOptimizer\AI");
            object? value = key?.GetValue("DisableUnusedBluetooth");
            if (value is int choice)
            {
                // Explicit DWORD0 means restore on the next FanAgent pass.
                _missingPreferenceSinceStamp = 0;
                _optedIn = choice == 1;
            }
            else if (value is null && File.Exists(JournalPath))
            {
                // Preserve an already-owned state for a short upgrade gap;
                // no missing registry value can introduce a new mutation.
                long stamp = Stopwatch.GetTimestamp();
                if (_missingPreferenceSinceStamp == 0)
                    _missingPreferenceSinceStamp = stamp;
                _optedIn = Stopwatch.GetElapsedTime(
                    _missingPreferenceSinceStamp, stamp) < TimeSpan.FromMinutes(2);
            }
            else
            {
                _missingPreferenceSinceStamp = 0;
                _optedIn = false;
            }
        }
        catch
        {
            // If HKCU is inaccessible, do not introduce new service writes.
            _optedIn = File.Exists(JournalPath) && _optedIn;
        }
        return _optedIn;
    }

    internal string? Tick(bool enabled)
    {
        DateTime now = DateTime.UtcNow;
        if (enabled && now < _nextCheckUtc && now >= _nextCheckUtc.AddMinutes(-2))
            return null;
        _nextCheckUtc = now.AddSeconds(45);
        if (!enabled && !File.Exists(JournalPath))
        {
            IsManaged = false;
            LastError = "";
            return null;
        }

        try
        {
            Ownership? owner = File.Exists(JournalPath)
                ? JsonSerializer.Deserialize<Ownership>(File.ReadAllText(JournalPath))
                    ?? throw new InvalidDataException("Bluetooth service ownership journal is invalid.")
                : null;
            if (owner != null && owner.OriginalStart is not (2 or 3))
                throw new InvalidDataException("Bluetooth original start type is invalid.");

            // Once the user or Windows has explicitly re-enabled Bluetooth,
            // keep a suppression record rather than forgetting ownership and
            // disabling it again 45 seconds later. Clearing preference to 0
            // clears the suppression so a future explicit opt-in can retry.
            if (owner is { Suppressed: true })
            {
                if (!enabled)
                    File.Delete(JournalPath);
                IsManaged = false;
                LastError = "";
                return null;
            }

            int startMode = ServiceSafetyService.ReadStartType(ServiceName)
                ?? throw new InvalidOperationException("Windows Bluetooth Support Service is missing.");
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            if (service.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.Stopped))
                return null;

            if (!enabled)
            {
                if (owner == null) return null;
                // A user's or Windows' explicit SCM override takes precedence.
                if (startMode != 4)
                {
                    File.Delete(JournalPath);
                    IsManaged = false;
                    LastError = "";
                    return "Bluetooth service: external startup setting preserved";
                }
                if (!NativeCore.ChangeServiceStartType(ServiceName,
                    (uint)owner.OriginalStart, out int restoreError))
                    throw new System.ComponentModel.Win32Exception(
                        restoreError, "Bluetooth original startup restore failed");
                if (owner.WasRunning && service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running,
                        TimeSpan.FromSeconds(12));
                }
                service.Refresh();
                if (ServiceSafetyService.ReadStartType(ServiceName) != owner.OriginalStart ||
                    (owner.WasRunning && service.Status != ServiceControllerStatus.Running))
                    throw new IOException("Bluetooth service restore read-back failed");
                File.Delete(JournalPath);
                IsManaged = false;
                LastError = "";
                return "Bluetooth service: prior state restored";
            }

            if (startMode == 4 && service.Status == ServiceControllerStatus.Stopped)
            {
                if (owner is { Applied: false })
                    AtomicFileService.WriteText(JournalPath,
                        JsonSerializer.Serialize(owner with { Applied = true }));
                IsManaged = owner != null;
                LastError = "";
                return null;
            }
            // A confirmed previous AI mutation was manually overridden.
            // An unapplied write-ahead intent, however, may be retried after
            // a process crash without claiming an external override.
            if (owner is { Applied: true } && startMode != 4)
            {
                AtomicFileService.WriteText(JournalPath,
                    JsonSerializer.Serialize(owner with { Suppressed = true }));
                IsManaged = false;
                LastError = "";
                return "Bluetooth service: external override respected";
            }
            if (startMode == 4 && service.Status == ServiceControllerStatus.Running)
            {
                // It was started externally with Disabled startup; preserve it
                // without fighting the current user's active Bluetooth usage.
                if (owner is { Applied: true })
                    AtomicFileService.WriteText(JournalPath,
                        JsonSerializer.Serialize(owner with { Suppressed = true }));
                IsManaged = false;
                LastError = "";
                return null;
            }

            if (owner == null)
            {
                if (startMode is not (2 or 3))
                    throw new InvalidOperationException("Nonstandard Bluetooth start mode.");

                // Only the authentic built-in bthserv DLL can be managed.
                using var registry = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\bthserv\Parameters");
                string image = Environment.ExpandEnvironmentVariables(
                    Convert.ToString(registry?.GetValue("ServiceDll")) ?? "");
                string expected = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "bthserv.dll");
                if (!string.Equals(Path.GetFullPath(image), expected,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bluetooth service DLL identity differs.");

                if (HasActiveBluetoothPeripherals())
                {
                    LastError = "Active Bluetooth peripheral found; service unchanged";
                    return null;
                }
                if (service.Status == ServiceControllerStatus.Running &&
                    (!service.CanStop || service.DependentServices.Any(
                        item => item.Status == ServiceControllerStatus.Running)))
                    throw new InvalidOperationException("Bluetooth service has active dependents.");
                owner = new Ownership(startMode,
                    service.Status == ServiceControllerStatus.Running);

                // Write-ahead owner journal before touching the native SCM.
                AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(owner));
            }

            if (service.Status == ServiceControllerStatus.Running)
            {
                if (!service.CanStop || service.DependentServices.Any(
                    item => item.Status == ServiceControllerStatus.Running))
                    throw new InvalidOperationException("Bluetooth service is being used.");
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped,
                    TimeSpan.FromSeconds(12));
            }
            if (ServiceSafetyService.ReadStartType(ServiceName) != 4 &&
                !NativeCore.ChangeServiceStartType(ServiceName, 4, out int error))
                throw new System.ComponentModel.Win32Exception(error,
                    "Bluetooth automatic startup disable failed");
            service.Refresh();
            if (ServiceSafetyService.ReadStartType(ServiceName) != 4 ||
                service.Status != ServiceControllerStatus.Stopped)
                throw new IOException("Bluetooth stop/disabled read-back failed");
            AtomicFileService.WriteText(JournalPath,
                JsonSerializer.Serialize(owner with { Applied = true }));
            IsManaged = true;
            LastError = "";
            return "Bluetooth Support Service stopped, Disabled startup verified";
        }
        catch (Exception ex)
        {
            IsManaged = false;
            LastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "Bluetooth optional service optimization failed", ex);
            return null;
        }
    }

    // Inspect REAL connected/permitted PnP children, not the always-present
    // Intel radio itself or old disconnected pairings. Failure fails closed.
    private static bool HasActiveBluetoothPeripherals()
    {
        var scope = new ManagementScope(@"\\.\root\CIMV2");
        scope.Options.Timeout = TimeSpan.FromSeconds(3);
        scope.Connect();
        using var query = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT DeviceID,ConfigManagerErrorCode FROM Win32_PnPEntity " +
            "WHERE DeviceID LIKE 'BTHENUM%' OR DeviceID LIKE 'BTHLEDEVICE%' " +
            "OR DeviceID LIKE 'BTHHFENUM%' OR DeviceID LIKE 'BTHHID%'"),
            new System.Management.EnumerationOptions {
                Timeout = TimeSpan.FromSeconds(3),
                ReturnImmediately = false, Rewindable = false
            });
        using var devices = query.Get();
        foreach (ManagementObject device in devices)
        {
            using (device)
            {
                if (Convert.ToInt32(device["ConfigManagerErrorCode"] ?? -1) == 0)
                    return true;
            }
        }
        return false;
    }
}
