using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Full OS inventory, not an allowlist-only count. This observes ALL installed
// services, scheduled tasks and kernel drivers; write admission remains separate.
internal sealed class AiSystemInventoryCoverageService
{
    internal sealed record Snapshot(
        DateTime UpdatedUtc,
        int ServicesObserved,
        int ServicesStopped,
        int TasksObserved,
        int TasksDisabled,
        int DriversObserved,
        int DriversStopped,
        double ScanElapsedMs = 0,
        int? PnpDevicesObserved = null,
        int? PnpDevicesPresent = null,
        int? PnpDevicesDisabled = null,
        int? PnpDevicesNotPresent = null,
        int? PnpDevicesOtherProblems = null);
    private static readonly string StatusPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-system-inventory-health.json");
    private long _lastStamp;
    private bool _lastScanFailed;
    internal Snapshot? Current { get; private set; }

    internal async Task TickAsync(bool enabled, bool foregroundPressure, CancellationToken token)
    {
        if (!enabled || foregroundPressure) return;
        long now = Stopwatch.GetTimestamp();
        // Full Windows inventory is read-only and never urgent. Do not let
        // its Task Scheduler COM scan compete with an active game, memory
        // pressure or time-critical AI priority sampling.
        TimeSpan cadence = _lastScanFailed
            ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10);
        if (_lastStamp != 0 &&
            Stopwatch.GetElapsedTime(_lastStamp, now) < cadence)
            return;
        _lastStamp = now;

        ServiceController[] services = Array.Empty<ServiceController>();
        ServiceController[] drivers = Array.Empty<ServiceController>();
        try
        {
            services = ServiceController.GetServices();
            drivers = ServiceController.GetDevices();
            // Reuse the existing Task Scheduler cached inventory and its
            // single-flight COM loader instead of introducing a new scanner.
            var scheduler = new TaskSchedulerService();
            var tasks = TaskSchedulerService.TryGetBasicFreshSnapshot() ??
                await scheduler.GetBasicAsync(forceRefresh: false, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Device Manager is a different object model from kernel-driver
            // SERVICES. Missing/disconnected PnP entries do not indicate that
            // stopping any driver would help performance. Count only what
            // Win32_PnPEntity exposes, not every disconnected ghost node.
            // Failure cannot authorize a device mutation.
            (int? observed, int? present, int? disabled, int? absent,
                int? problems) = ReadPnpInventory(token);
            var snapshot = new Snapshot(
                DateTime.UtcNow,
                services.Length,
                services.Count(s => s.Status == ServiceControllerStatus.Stopped),
                tasks.Count,
                tasks.Count(t => !t.Enabled),
                drivers.Length,
                drivers.Count(s => s.Status == ServiceControllerStatus.Stopped),
                Math.Round(Stopwatch.GetElapsedTime(now).TotalMilliseconds, 1),
                observed, present, disabled, absent, problems);
            AtomicFileService.WriteAllBytesAtomic(StatusPath,
                JsonSerializer.SerializeToUtf8Bytes(snapshot));
            Current = snapshot;
            _lastScanFailed = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _lastScanFailed = true;
            // A failed inventory scan must not remove prior evidence or
            // accidentally authorize an OS mutation from missing telemetry.
            LogService.Error("ai-system", "Full system inventory scan failed", ex);
        }
        finally
        {
            foreach (var service in services) service.Dispose();
            foreach (var driver in drivers) driver.Dispose();
        }
    }

    // Read-only Device Manager coverage. Unlike DeviceManaged (actual AI
    // ownership), these fields merely report what Windows enumerated.
    // Win32_PnPEntity can omit historical ghost HID/USB/storage entries;
    // only enumerated records are counted. Missing rows must NEVER be
    // mistaken for evidence that those devices are unused or safe to disable.
    private static (int?, int?, int?, int?, int?) ReadPnpInventory(
        CancellationToken token)
    {
        try
        {
            using var search = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2"),
                new ObjectQuery(
                    "SELECT DeviceID,Present,ConfigManagerErrorCode FROM Win32_PnPEntity"),
                new System.Management.EnumerationOptions
                {
                    Timeout = TimeSpan.FromSeconds(7),
                    ReturnImmediately = false,
                    Rewindable = false
                });
            using var all = search.Get();
            int observed = 0, present = 0, disabled = 0, absent = 0, problems = 0;
            foreach (ManagementObject item in all)
            {
                using (item)
                {
                    if ((observed & 31) == 0) token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(Convert.ToString(item["DeviceID"])))
                        continue;
                    observed++;
                    bool live = item.Properties["Present"]?.Value is true;
                    int code = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? -1);
                    if (live) present++;
                    else absent++;
                    if (code == 22) disabled++;
                    else if (live && code != 0) problems++;
                }
            }
            return (observed, present, disabled, absent, problems);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Distinguish unavailable inventory from "zero devices".
            LogService.Error("ai-system", "Read-only PnP inventory unavailable", ex);
            return (null, null, null, null, null);
        }
    }

}
