using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

// User-facing geolocation is OFF in both Windows machine and user privacy
// settings. The existing elevated FanAgent may opt out ONLY lfsvc, not the
// network stack, sensors, GPS/USB devices or Windows privacy settings.
// An explicit Allow or unreadable consent always restores our owned change.
internal sealed class AiUnusedLocationOptimizationService
{
    private const string ServiceName = "lfsvc";
    private const string LocationConsent =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
    private sealed record Ownership(int OriginalStart, bool WasRunning,
        bool Applied = false, bool Suppressed = false);
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-unused-location-ownership.json");
    private DateTime _nextCheckUtc = DateTime.MinValue;

    internal bool IsManaged { get; private set; }
    internal string LastError { get; private set; } = "";

    private static bool LocationGloballyDenied()
    {
        // Deny must be positively observed in BOTH hives. Missing keys,
        // registry errors or Allow trigger restoration, never a new disable.
        using var machine = Registry.LocalMachine.OpenSubKey(LocationConsent);
        using var user = Registry.CurrentUser.OpenSubKey(LocationConsent);
        return string.Equals(Convert.ToString(machine?.GetValue("Value")), "Deny",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Convert.ToString(user?.GetValue("Value")), "Deny",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal string? Tick(bool serviceAutomationEnabled)
    {
        DateTime now = DateTime.UtcNow;
        bool journalPresent = File.Exists(JournalPath);
        // Read the privacy permission every tick even when checking the
        // Windows SCM is throttled; permit a quick restore after Allow.
        bool denied = false;
        bool permissionReadable = false;
        try
        {
            denied = LocationGloballyDenied();
            permissionReadable = true;
        }
        catch (Exception ex)
        {
            // An unknown permission must never allow a NEW shutdown.
            LastError = "Location permission could not be read: " +
                        ex.GetBaseException().Message;
        }
        bool enabled = serviceAutomationEnabled && denied && permissionReadable;
        if (enabled && _nextCheckUtc != DateTime.MinValue &&
            now >= _nextCheckUtc.AddMinutes(-2) && now < _nextCheckUtc)
            return null;
        _nextCheckUtc = now.AddSeconds(40);
        if (!enabled && !journalPresent)
        {
            IsManaged = false;
            if (permissionReadable) LastError = "";
            return null;
        }

        try
        {
            Ownership? owner = journalPresent
                ? JsonSerializer.Deserialize<Ownership>(File.ReadAllText(JournalPath))
                    ?? throw new InvalidDataException("Location ownership journal invalid")
                : null;
            if (owner != null && owner.OriginalStart is not (2 or 3))
                throw new InvalidDataException("Location original startup must be Auto or Manual");

            if (owner is { Suppressed: true })
            {
                if (!enabled) File.Delete(JournalPath);
                IsManaged = false;
                LastError = "";
                return null;
            }

            int mode = ServiceSafetyService.ReadStartType(ServiceName)
                ?? throw new InvalidOperationException("Native Windows lfsvc not found");
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            if (service.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.Stopped))
                return null;

            if (!enabled)
            {
                if (owner == null) return null;
                if (mode != 4)
                {
                    // Windows/user changed startup mode while AI owned it.
                    File.Delete(JournalPath);
                    IsManaged = false;
                    LastError = "";
                    return "Geolocation: external startup change preserved";
                }
                if (!NativeCore.ChangeServiceStartType(ServiceName,
                    (uint)owner.OriginalStart, out int restoreError))
                    throw new System.ComponentModel.Win32Exception(
                        restoreError, "Restore original lfsvc startup");
                if (owner.WasRunning && service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running,
                        TimeSpan.FromSeconds(12));
                }
                service.Refresh();
                if (ServiceSafetyService.ReadStartType(ServiceName) != owner.OriginalStart ||
                    (owner.WasRunning && service.Status != ServiceControllerStatus.Running))
                    throw new IOException("Native geolocation restoration readback failed");
                File.Delete(JournalPath);
                IsManaged = false;
                LastError = "";
                return "Geolocation: original service configuration restored";
            }

            if (mode == 4 && service.Status == ServiceControllerStatus.Stopped)
            {
                if (owner is { Applied: false })
                    AtomicFileService.WriteText(JournalPath,
                        JsonSerializer.Serialize(owner with { Applied = true }));
                IsManaged = owner != null;
                LastError = "";
                return null;
            }
            if (owner is { Applied: true } &&
                (mode != 4 || service.Status == ServiceControllerStatus.Running))
            {
                // A deliberate SCM re-enable outranks the AI preference.
                AtomicFileService.WriteText(JournalPath,
                    JsonSerializer.Serialize(owner with { Suppressed = true }));
                IsManaged = false;
                LastError = "";
                return "Geolocation: external manual override respected";
            }
            if (mode == 4)
                return null; // Do not stop an externally started disabled service.

            if (owner == null)
            {
                if (mode is not (2 or 3))
                    return null;
                using var reg = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\lfsvc\Parameters");
                string dll = Environment.ExpandEnvironmentVariables(
                    Convert.ToString(reg?.GetValue("ServiceDll")) ?? "");
                string systemDll = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "lfsvc.dll");
                if (!string.Equals(Path.GetFullPath(dll), systemDll,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("lfsvc native DLL identity mismatch");
                if (service.Status == ServiceControllerStatus.Running &&
                    (!service.CanStop || service.DependentServices.Any(
                        s => s.Status == ServiceControllerStatus.Running)))
                    return null;
                // The observation is a USER preference, not a diagnostic:
                // confirm both Deny registry values immediately before write.
                if (!LocationGloballyDenied())
                    return null;
                owner = new Ownership(mode,
                    service.Status == ServiceControllerStatus.Running);
                AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(owner));
            }

            if (service.Status == ServiceControllerStatus.Running)
            {
                if (!service.CanStop || service.DependentServices.Any(
                    s => s.Status == ServiceControllerStatus.Running))
                    return null;
                if (!LocationGloballyDenied()) return null;
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped,
                    TimeSpan.FromSeconds(12));
            }
            // Check user consent AGAIN before disabling automatic startup.
            if (!LocationGloballyDenied()) return null;
            if (ServiceSafetyService.ReadStartType(ServiceName) != 4 &&
                !NativeCore.ChangeServiceStartType(ServiceName, 4, out int error))
                throw new System.ComponentModel.Win32Exception(error,
                    "Disable user-denied lfsvc startup");
            service.Refresh();
            if (ServiceSafetyService.ReadStartType(ServiceName) != 4 ||
                service.Status != ServiceControllerStatus.Stopped)
                throw new IOException("Native lfsvc disable readback failed");
            AtomicFileService.WriteText(JournalPath,
                JsonSerializer.Serialize(owner with { Applied = true }));
            IsManaged = true;
            LastError = "";
            return "Geolocation: location Deny in both hives; lfsvc Stopped/Disabled";
        }
        catch (Exception ex)
        {
            IsManaged = false;
            LastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "Optional geolocation service optimization failed", ex);
            return null;
        }
    }
}
