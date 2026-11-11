using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed class ServiceCatalogService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<ServiceInfo>? _cache;
    private static DateTime _cacheAt;
    private static Task<IReadOnlyList<ServiceInfo>>? _flight;
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static int _generation;

    private static readonly string[] RecommendedTargetNames =
        ["CscService", "DiagTrack", "MapsBroker", "StorSvc"];

    // The Recommended service page only displays these four start modes.
    // Enumerating and materializing hundreds of Windows services to show four
    // labels wastes registry handles/CPU, even when the full catalog is cached.
    internal static Dictionary<string, int> ReadRecommendedStartTypes()
    {
        var found = new Dictionary<string, int>(RecommendedTargetNames.Length, StringComparer.OrdinalIgnoreCase);
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root == null) return found;
        foreach (string name in RecommendedTargetNames)
        {
            using RegistryKey? key = root.OpenSubKey(name);
            if (key == null) continue;
            int type = Convert.ToInt32(key.GetValue("Type", 0));
            if ((type & 0x10) == 0 && (type & 0x20) == 0) continue;
            found.Add(name, Convert.ToInt32(key.GetValue("Start", 3)));
        }
        return found;
    }

    // Same-process A/B against the still-supported full catalog provider.
    // Both paths only READ the Windows Services registry tree.
    internal static async Task RunRecommendedStartModesSmokeAsync()
    {
        const int runs = 5;
        var fullMs = new double[runs];
        var targetMs = new double[runs];
        long fullBytes = 0, targetBytes = 0;
        for (int i = 0; i < runs; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long before = GC.GetTotalAllocatedBytes(precise: false);
            var all = await new ServiceCatalogService().GetAsync(forceRefresh: true)
                .ConfigureAwait(false);
            watch.Stop();
            fullMs[i] = watch.Elapsed.TotalMilliseconds;
            fullBytes += GC.GetTotalAllocatedBytes(precise: false) - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            var targets = ReadRecommendedStartTypes();
            watch.Stop();
            targetMs[i] = watch.Elapsed.TotalMilliseconds;
            targetBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            foreach (string name in RecommendedTargetNames)
            {
                var fromCatalog = all.FirstOrDefault(x =>
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                bool found = targets.TryGetValue(name, out int actual);
                if ((fromCatalog != null) != found ||
                    (found && fromCatalog!.StartType != actual))
                    throw new InvalidOperationException(
                        $"Recommended service {name} does not match full registry scan.");
            }
        }
        Array.Sort(fullMs);
        Array.Sort(targetMs);
        LogService.Info("performance",
            $"SERVICE_RECOMMENDED_FAST_READ_SMOKE_OK targets={RecommendedTargetNames.Length} " +
            $"runs={runs} fullCatalogMedianMs={fullMs[2]:F2} targetedMedianMs={targetMs[2]:F2} " +
            $"fullAllocatedBytesAllThreads={fullBytes} targetAllocatedBytesCurrentThread={targetBytes} " +
            $"matching=true readOnly=true");
    }

    public static IReadOnlyList<ServiceInfo>? TryGetFreshSnapshot()
    {
        lock (CacheLock) return _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(5) ? _cache : null;
    }

    public async Task<IReadOnlyList<ServiceInfo>> GetAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        Task<IReadOnlyList<ServiceInfo>> work;
        lock (CacheLock)
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(5)) return _cache;
            if (_flight != null && !_flight.IsCompleted)
            {
                work = _flight;
            }
            else
            {
                if (forceRefresh) _generation++;
                int generation = _generation;
                var created = ScanAsync(generation);
                _flight = work = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (CacheLock)
                        if (ReferenceEquals(_flight, created))
                            _flight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return await work.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ServiceInfo>> ScanAsync(int generation)
    {
        await ScanGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(CancellationToken.None, generation)).ConfigureAwait(false);
        }
        finally
        {
            ScanGate.Release();
        }
    }
    private static IReadOnlyList<ServiceInfo> Scan(CancellationToken token, int generation)
    {
        token.ThrowIfCancellationRequested();
        var list = new List<ServiceInfo>(512);
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services");
        if (root == null) return list;
        foreach (string name in root.GetSubKeyNames())
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey? key = root.OpenSubKey(name);
            if (key == null) continue;
            int type = Convert.ToInt32(key.GetValue("Type", 0));
            if ((type & 0x10) == 0 && (type & 0x20) == 0) continue;
            int startType = Convert.ToInt32(key.GetValue("Start", 3));
            string displayName = key.GetValue("DisplayName")?.ToString() ?? name;
            string imagePath = key.GetValue("ImagePath")?.ToString() ?? string.Empty;
            list.Add(new ServiceInfo(name, displayName, startType, imagePath));
        }
        list.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName, b.DisplayName));
        IReadOnlyList<ServiceInfo> result = list.ToArray();
        lock (CacheLock)
        {
            if (generation == _generation)
            {
                _cache = result;
                _cacheAt = DateTime.UtcNow;
            }
        }
        return result;
    }

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _generation++;
            _cache = null;
            _cacheAt = default;
            _flight = null;
        }
    }
}
