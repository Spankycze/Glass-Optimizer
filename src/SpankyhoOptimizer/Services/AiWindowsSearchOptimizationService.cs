using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

// Autonomous, opt-in service optimization. No GUI switch or scheduled task.
// FanAgent owns the normal tick; a durable snapshot allows AI OFF to restore
// exactly the original Windows service startup/running state.
internal sealed class AiWindowsSearchOptimizationService
{
    private sealed record Ownership(int OriginalStart, bool WasRunning);
    private static readonly string OwnershipPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-wsearch-ownership.json");
    private DateTime _nextCheckUtc = DateTime.MinValue;
    private DateTime _nextPreferenceReadUtc = DateTime.MinValue;
    private bool _userRequestsDisable;
    private long _missingPreferenceSinceStamp;

    // User-scoped consent belongs to HKCU, which the normal signed FanAgent
    // reads under the same Windows account. ProgramData AI config keeps its
    // existing administrator-only ACL; never lower that protection.
    internal bool UserRequestsDisable()
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextPreferenceReadUtc && now >= _nextPreferenceReadUtc.AddMinutes(-2))
            return _userRequestsDisable;
        _nextPreferenceReadUtc = now.AddSeconds(15);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\SpankyhoOptimizer\AI");
            object? preference = key?.GetValue("DisableWindowsSearchIndexing");
            if (preference is int value)
            {
                // Explicit DWORD 0 always restores immediately.
                _missingPreferenceSinceStamp = 0;
                _userRequestsDisable = value == 1;
            }
            else if (preference is null && File.Exists(OwnershipPath))
            {
                // A temporarily absent HKCU value during an upgrade is not
                // an explicit opt-out. Keep ONLY an already-owned service
                // for at most two minutes; persistent absence still restores.
                long stamp = Stopwatch.GetTimestamp();
                if (_missingPreferenceSinceStamp == 0)
                    _missingPreferenceSinceStamp = stamp;
                _userRequestsDisable = Stopwatch.GetElapsedTime(
                    _missingPreferenceSinceStamp, stamp) < TimeSpan.FromMinutes(2);
            }
            else
            {
                _missingPreferenceSinceStamp = 0;
                _userRequestsDisable = false;
            }
        }
        catch
        {
            // An unreadable opt-in is not permission for automatic mutation.
            _userRequestsDisable = false;
        }
        return _userRequestsDisable;
    }

    internal bool IsManaged { get; private set; }
    internal string LastError { get; private set; } = "";

    // Returns an action ONLY when the live Windows service changed.
    internal string? Tick(bool enabled)
    {
        DateTime now = DateTime.UtcNow;
        // AI OFF and explicit opt-out restore must not be delayed by the
        // regular 45-second optimized-state poll.
        if (enabled && now < _nextCheckUtc &&
            now >= _nextCheckUtc.AddMinutes(-2))
            return null;
        _nextCheckUtc = now.AddSeconds(45);
        if (!enabled && !File.Exists(OwnershipPath))
        {
            IsManaged = false;
            LastError = "";
            return null;
        }
        try
        {
            Ownership? owned = null;
            if (File.Exists(OwnershipPath))
            {
                owned = JsonSerializer.Deserialize<Ownership>(File.ReadAllText(OwnershipPath))
                    ?? throw new InvalidDataException("Windows Search ownership journal is empty.");
                if (owned.OriginalStart is not (2 or 3 or 4))
                    throw new InvalidDataException("Windows Search original start mode is invalid.");
            }

            if (!enabled && owned == null)
            {
                IsManaged = false;
                LastError = "";
                return null;
            }

            int? start = ServiceSafetyService.ReadStartType("WSearch");
            if (start is null)
                throw new InvalidOperationException("Windows Search service is missing.");
            using var service = new ServiceController("WSearch");
            service.Refresh();
            if (service.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.Stopped))
                return null;

            if (!enabled)
            {
                // Do not overwrite manual Windows/user changes made after AI's write.
                if (start.Value != 4)
                {
                    File.Delete(OwnershipPath);
                    IsManaged = false;
                    LastError = "";
                    return "Windows Search: external preference preserved";
                }
                if (!NativeCore.ChangeServiceStartType("WSearch", (uint)owned!.OriginalStart, out int restoreError))
                    throw new System.ComponentModel.Win32Exception(restoreError, "Windows Search restore failed");
                if (owned.WasRunning && service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(12));
                }
                service.Refresh();
                if (ServiceSafetyService.ReadStartType("WSearch") != owned.OriginalStart ||
                    (owned.WasRunning && service.Status != ServiceControllerStatus.Running))
                    throw new IOException("Windows Search restore read-back failed.");
                File.Delete(OwnershipPath);
                IsManaged = false;
                LastError = "";
                return "Windows Search: original Windows service state restored";
            }

            if (start.Value == 4 && service.Status == ServiceControllerStatus.Stopped)
            {
                IsManaged = owned != null;
                LastError = "";
                return null;
            }

            if (owned == null)
            {
                if (start.Value is not (2 or 3))
                    throw new InvalidOperationException("Windows Search start mode is not automatic/manual.");
                // Do not stop a nonstandard replacement or a service with dependents.
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\WSearch");
                string image = Environment.ExpandEnvironmentVariables(
                    Convert.ToString(key?.GetValue("ImagePath")) ?? "");
                if (!image.StartsWith(Path.Combine(windows, "System32", "SearchIndexer.exe"),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows Search executable identity differs.");
                if (service.Status == ServiceControllerStatus.Running &&
                    (!service.CanStop || service.DependentServices.Any(
                        dependent => dependent.Status == ServiceControllerStatus.Running)))
                    throw new InvalidOperationException("Windows Search has active dependents.");
                owned = new Ownership(start.Value,
                    service.Status == ServiceControllerStatus.Running);
                // Crash-safe BEFORE the Windows mutation. Never lose the original mode.
                AtomicFileService.WriteText(OwnershipPath, JsonSerializer.Serialize(owned));
            }

            if (service.Status == ServiceControllerStatus.Running)
            {
                if (!service.CanStop || service.DependentServices.Any(
                    dependent => dependent.Status == ServiceControllerStatus.Running))
                    throw new InvalidOperationException("Windows Search cannot be stopped safely.");
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
            }
            if (ServiceSafetyService.ReadStartType("WSearch") != 4 &&
                !NativeCore.ChangeServiceStartType("WSearch", 4, out int error))
                throw new System.ComponentModel.Win32Exception(error, "Windows Search disable failed.");
            service.Refresh();
            if (ServiceSafetyService.ReadStartType("WSearch") != 4 ||
                service.Status != ServiceControllerStatus.Stopped)
                throw new IOException("Windows Search stop/disabled read-back failed.");
            IsManaged = true;
            LastError = "";
            return "Windows Search: SearchIndexer stopped, startup disabled, read-back verified";
        }
        catch (Exception ex)
        {
            IsManaged = false;
            LastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "Automatic Windows Search optimization failed", ex);
            return null;
        }
    }
}
