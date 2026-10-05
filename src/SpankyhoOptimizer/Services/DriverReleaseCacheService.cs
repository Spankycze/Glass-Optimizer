using System;
using System.Collections.Concurrent;

namespace SpankyhoOptimizer.Services;

public static class DriverReleaseCacheService
{
    private sealed record Entry(DateTime CreatedUtc, DriverReleaseInfo? Release);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static bool TryGet(string key, out DriverReleaseInfo? release)
    {
        release = null;
        if (!Cache.TryGetValue(key, out var entry)) return false;
        if (DateTime.UtcNow - entry.CreatedUtc > Ttl)
        {
            Cache.TryRemove(key, out _);
            return false;
        }
        release = entry.Release;
        return true;
    }

    public static DriverReleaseInfo? Set(string key, DriverReleaseInfo? release)
    {
        Cache[key] = new Entry(DateTime.UtcNow, release);
        return release;
    }

    public static void Clear() => Cache.Clear();
}
