using System;
using System.Collections.Generic;
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
                var created = Task.Run((Func<IReadOnlyList<ServiceInfo>>)(() =>
                {
                    ScanGate.Wait();
                    try { return Scan(CancellationToken.None, generation); }
                    finally { ScanGate.Release(); }
                }));
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
