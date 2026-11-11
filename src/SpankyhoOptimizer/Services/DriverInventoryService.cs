using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record DriverInventoryItem(string DeviceId, string Name, string DeviceClass, string Category,
    string Manufacturer, string Provider, string Version, DateTime? Date, string InfName, bool Signed, int ErrorCode);

public static class DriverInventoryService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<DriverInventoryItem>? _cache;
    private static DateTime _cacheAt;
    private static Task<IReadOnlyList<DriverInventoryItem>>? _flight;
    private static int _cacheGeneration;
    private static int _flightGeneration = -1;

    public static async Task<IReadOnlyList<DriverInventoryItem>> GetAsync(bool force = false, CancellationToken token = default)
    {
        Task<IReadOnlyList<DriverInventoryItem>> work;
        lock (CacheLock)
        {
            if (!force && _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(3))
                return _cache;
            if (_flight != null && !_flight.IsCompleted && _flightGeneration == _cacheGeneration)
            {
                work = _flight;
            }
            else
            {
                int generation = _cacheGeneration;
                _flightGeneration = generation;
                var created = LoadFreshAsync(generation);
                _flight = work = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (CacheLock)
                        if (ReferenceEquals(_flight, created))
                        {
                            _flight = null;
                            _flightGeneration = -1;
                        }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return await work.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DriverInventoryItem>> LoadFreshAsync(int generation)
    {
        IReadOnlyList<DriverInventoryItem> result = !WorkerExecutionContext.IsWorker
            ? await WorkerClientService.RunAsync<DriverInventoryItem[]>("drivers", null, TimeSpan.FromSeconds(45), CancellationToken.None).ConfigureAwait(false)
            : await Task.Run(() => Scan(CancellationToken.None)).ConfigureAwait(false);
        lock (CacheLock)
        {
            if (generation == _cacheGeneration)
            {
                _cache = result;
                _cacheAt = DateTime.UtcNow;
            }
            else
            {
                LogService.Info("drivers", $"DRIVER_INVENTORY_STALE_RESULT_DROPPED generation={generation} current={_cacheGeneration}");
            }
        }
        return result;
    }

    public static void Invalidate()
    {
        lock (CacheLock)
        {
            _cacheGeneration++;
            _cache = null;
            _cacheAt = default;
            _flight = null;
            _flightGeneration = -1;
        }
    }
    private static DriverInventoryItem[] Scan(CancellationToken token)
    {
        // Independent WMI providers run concurrently; COM objects remain thread-local.
        Task<Dictionary<string, int>> errorTask = Task.Run(() => ReadDeviceErrorCodes(token), token);

        var result = new List<DriverInventoryItem>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned FROM Win32_PnPSignedDriver");
        var driverRows = searcher.Get();
        WmiEnumerationScope.ForEach<ManagementObject>(driverRows, driverRows, token, x =>
        {
            string id = x["DeviceID"]?.ToString() ?? string.Empty;
            string name = x["DeviceName"]?.ToString() ?? id;
            string cls = x["DeviceClass"]?.ToString() ?? "Other";
            string manufacturer = x["Manufacturer"]?.ToString() ?? "Unknown";
            string provider = x["DriverProviderName"]?.ToString() ?? manufacturer;
            string version = x["DriverVersion"]?.ToString() ?? "Unknown";
            string inf = x["InfName"]?.ToString() ?? string.Empty;
            bool signed = Convert.ToBoolean(x["IsSigned"] ?? false, CultureInfo.InvariantCulture);
            DateTime? date = ParseWmiDate(x["DriverDate"]?.ToString());
            result.Add(new DriverInventoryItem(id, name, cls, Classify(cls, name), manufacturer, provider, version, date, inf, signed, 0));
        });
        var errors = errorTask.GetAwaiter().GetResult();
        return result.Select(x => x with { ErrorCode = errors.GetValueOrDefault(x.DeviceId) })
            .OrderBy(x => x.Category).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static Dictionary<string, int> ReadDeviceErrorCodes(CancellationToken token)
    {
        var errors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var entity = new ManagementObjectSearcher("SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity");
        var rows = entity.Get();
        WmiEnumerationScope.ForEach<ManagementObject>(rows, rows, token, x =>
        {
            string id = x["DeviceID"]?.ToString() ?? string.Empty;
            if (id.Length == 0) return;
            errors[id] = Convert.ToInt32(x["ConfigManagerErrorCode"] ?? 0, CultureInfo.InvariantCulture);
        });
        return errors;
    }

    private static DateTime? ParseWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value); } catch { return null; }
    }

    private static string Classify(string cls, string name)
    {
        string x = cls + " " + name;
        if (x.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase)) return "GPU";
        if (x.Contains("NET", StringComparison.OrdinalIgnoreCase)) return "Network";
        if (x.Contains("MEDIA", StringComparison.OrdinalIgnoreCase) || x.Contains("AUDIO", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (x.Contains("USB", StringComparison.OrdinalIgnoreCase) || x.Contains("HID", StringComparison.OrdinalIgnoreCase)) return "USB / HID";
        if (x.Contains("SCSI", StringComparison.OrdinalIgnoreCase) || x.Contains("STORAGE", StringComparison.OrdinalIgnoreCase) || x.Contains("DISK", StringComparison.OrdinalIgnoreCase)) return "Storage";
        if (x.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) || x.Contains("PCI", StringComparison.OrdinalIgnoreCase)) return "Chipset / System";
        return "Other";
    }

    public static async Task<ProcessResult> ExportAsync(string infName, string destination, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(infName)) throw new InvalidOperationException("Zařízení nemá dostupný INF název.");
        return await PrivilegedProcessRunner.RunAsync("pnputil.exe", $"/export-driver \"{infName}\" \"{destination}\"", TimeSpan.FromMinutes(2), token);
    }
}
