using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class AiToolCacheService
{
    private sealed record Entry(string Value, DateTime ExpiresUtc);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.Ordinal);

    public static async Task<string> GetOrCreateAsync(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        if (Cache.TryGetValue(key, out Entry? cached) && cached.ExpiresUtc > now)
            return cached.Value;

        string value = await factory(token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(value))
            Cache[key] = new Entry(value, now.Add(ttl));
        return value;
    }

    public static string GetOrCreate(
        string key,
        TimeSpan ttl,
        Func<string> factory)
    {
        DateTime now = DateTime.UtcNow;
        if (Cache.TryGetValue(key, out Entry? cached) && cached.ExpiresUtc > now)
            return cached.Value;

        string value = factory();
        if (!string.IsNullOrWhiteSpace(value))
            Cache[key] = new Entry(value, now.Add(ttl));
        return value;
    }

    public static void Invalidate(string prefix)
    {
        foreach (string key in Cache.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                Cache.TryRemove(key, out _);
    }

    internal static int Count => Cache.Count;
}
