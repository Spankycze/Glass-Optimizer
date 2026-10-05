using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class TcpGlobalStateService
{
    private static readonly char[] LineSeparators = { '\r', '\n' };
    private static readonly object Sync = new();
    private static string? _snapshot;
    private static Dictionary<string, string>? _values;
    private static DateTime _snapshotAt;
    private static Task<string>? _snapshotFlight;
    private static int _generation;
    private const string PersistentKey = "tcp-global-state-v1";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan UiStaleTtl = TimeSpan.FromMinutes(10);

    public static async Task<string> GetSnapshotAsync(CancellationToken token = default)
    {
        Task<string> flight;
        lock (Sync)
        {
            if (_snapshot != null && DateTime.UtcNow - _snapshotAt < Ttl)
                return _snapshot;

            int generation = _generation;
            _snapshotFlight ??= LoadSnapshotAsync(generation);
            flight = _snapshotFlight;
        }
        return await flight.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<string> LoadSnapshotAsync(int generation)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("netsh.exe", "int tcp show global", TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException((result.StdErr + result.StdOut).Trim());
            string snapshot = result.StdOut;
            Dictionary<string, string> values = ParseSnapshot(snapshot);
            lock (Sync)
            {
                if (generation == _generation)
                {
                    _snapshot = snapshot;
                    _values = values;
                    _snapshotAt = DateTime.UtcNow;
                    PersistentHardwareCacheService.WriteMachineStable(PersistentKey, snapshot);
                }
            }
            return snapshot;
        }
        finally
        {
            lock (Sync)
            {
                if (generation == _generation)
                    _snapshotFlight = null;
            }
        }
    }

    public static async Task<string?> GetValueAsync(string key, CancellationToken token = default)
    {
        string snapshot = await GetSnapshotAsync(token).ConfigureAwait(false);
        return GetParsedValue(snapshot, key);
    }

    public static async Task<string?> GetValueForUiAsync(string key, CancellationToken token = default)
    {
        string normalized = key.ToLowerInvariant();
        lock (Sync)
        {
            if (_values != null && _snapshot != null)
            {
                TimeSpan age = DateTime.UtcNow - _snapshotAt;
                if (age < Ttl)
                    return _values.TryGetValue(normalized, out string? fresh) ? fresh : null;
                if (age < UiStaleTtl)
                {
                    QueueBackgroundRefreshLocked();
                    return _values.TryGetValue(normalized, out string? stale) ? stale : null;
                }
            }
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<string>(
                PersistentKey, UiStaleTtl, out var persisted, out var persistedAge) &&
            !string.IsNullOrWhiteSpace(persisted))
        {
            Dictionary<string, string> parsed = ParseSnapshot(persisted);
            lock (Sync)
            {
                _snapshot = persisted;
                _values = parsed;
                _snapshotAt = DateTime.UtcNow - persistedAge;
                if (persistedAge >= Ttl)
                    QueueBackgroundRefreshLocked();
            }
            LogService.Info("performance", $"TCP_GLOBAL_UI_PERSISTENT_HIT ageMs={persistedAge.TotalMilliseconds:0}");
            return parsed.TryGetValue(normalized, out string? persistedValue) ? persistedValue : null;
        }

        return await GetValueAsync(key, token).ConfigureAwait(false);
    }

    private static string? GetParsedValue(string snapshot, string key)
    {
        string normalized = key.ToLowerInvariant();
        lock (Sync)
        {
            if (_values != null && _values.TryGetValue(normalized, out string? cached))
                return cached;
        }
        return ParseSnapshot(snapshot).TryGetValue(normalized, out string? value) ? value : null;
    }

    private static void QueueBackgroundRefreshLocked()
    {
        if (_snapshotFlight is { IsCompleted: false }) return;
        int generation = _generation;
        Task<string> created = Task.Run(async () =>
        {
            await Task.Yield();
            return await LoadSnapshotAsync(generation).ConfigureAwait(false);
        });
        _snapshotFlight = created;
        _ = created.ContinueWith(
            static t =>
            {
                if (t.IsFaulted)
                {
                    _ = t.Exception;
                    LogService.Info("performance", "TCP_GLOBAL_UI_BACKGROUND_REFRESH_FAIL");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Dictionary<string, string> ParseSnapshot(string snapshot)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in snapshot.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string key = line[..colon].Trim() switch
            {
                "Receive Window Auto-Tuning Level" => "autotuninglevel",
                "Receive-Side Scaling State" => "rss",
                "ECN Capability" => "ecncapability",
                _ => string.Empty
            };
            if (key.Length == 0) continue;

            string raw = line[(colon + 1)..].Trim();
            int space = raw.IndexOf(' ');
            values[key] = space > 0 ? raw[..space] : raw;
        }

        return values;
    }

    public static void Invalidate()
    {
        lock (Sync)
        {
            _generation++;
            _snapshot = null;
            _values = null;
            _snapshotAt = default;
            _snapshotFlight = null;
            PersistentHardwareCacheService.Remove(PersistentKey);
        }
    }
}
