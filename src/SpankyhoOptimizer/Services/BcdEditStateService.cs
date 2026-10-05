using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class BcdEditStateService
{
    private static readonly object Sync = new();
    private static string? _snapshot;
    private static DateTime _snapshotAt;
    private static Task<string>? _flight;
    private static int _generation;
    private static int _loadCount;
    private const string PersistentKey = "bcd-current-v1";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UiStaleTtl = TimeSpan.FromHours(1);
    internal static int LoadCountForDiagnostics => Volatile.Read(ref _loadCount);

    public static async Task<string> GetCurrentAsync(CancellationToken token = default)
    {
        Task<string> flight;
        lock (Sync)
        {
            if (_snapshot != null && DateTime.UtcNow - _snapshotAt < Ttl)
                return _snapshot;

            if (PersistentHardwareCacheService.TryReadMachineStable<string>(
                    PersistentKey, Ttl, out var persisted, out var age) &&
                !string.IsNullOrEmpty(persisted))
            {
                _snapshot = persisted;
                _snapshotAt = DateTime.UtcNow - age;
                LogService.Info("performance", $"BCD_PERSISTENT_HIT ageMs={age.TotalMilliseconds:0}");
                return persisted;
            }

            int generation = _generation;
            _flight ??= LoadAsync(generation);
            flight = _flight;
        }
        return await flight.WaitAsync(token).ConfigureAwait(false);
    }

    public static async Task<string> GetCurrentForUiAsync(CancellationToken token = default)
    {
        Task<string>? foregroundFlight = null;
        lock (Sync)
        {
            if (_snapshot != null)
            {
                TimeSpan age = DateTime.UtcNow - _snapshotAt;
                if (age < Ttl)
                    return _snapshot;
                if (age < UiStaleTtl)
                {
                    QueueBackgroundRefreshLocked();
                    LogService.Info("performance", $"BCD_UI_STALE_MEMORY_HIT ageMs={age.TotalMilliseconds:0}");
                    return _snapshot;
                }
            }

            if (PersistentHardwareCacheService.TryReadMachineStable<string>(
                    PersistentKey, UiStaleTtl, out var persisted, out var persistedAge) &&
                !string.IsNullOrEmpty(persisted))
            {
                _snapshot = persisted;
                _snapshotAt = DateTime.UtcNow - persistedAge;
                if (persistedAge >= Ttl)
                    QueueBackgroundRefreshLocked();
                LogService.Info("performance", $"BCD_UI_PERSISTENT_HIT ageMs={persistedAge.TotalMilliseconds:0}");
                return persisted;
            }

            int generation = _generation;
            _flight ??= LoadAsync(generation);
            foregroundFlight = _flight;
        }

        return await foregroundFlight.WaitAsync(token).ConfigureAwait(false);
    }

    private static void QueueBackgroundRefreshLocked()
    {
        if (_flight is { IsCompleted: false }) return;
        int generation = _generation;
        Task<string> created = Task.Run(async () =>
        {
            await Task.Yield();
            return await LoadAsync(generation).ConfigureAwait(false);
        });
        _flight = created;
        _ = created.ContinueWith(
            static t =>
            {
                if (t.IsFaulted)
                {
                    _ = t.Exception;
                    LogService.Info("performance", "BCD_UI_BACKGROUND_REFRESH_FAIL");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<string> LoadAsync(int generation)
    {
        Interlocked.Increment(ref _loadCount);
        try
        {
            var result = await ProcessRunner.RunAsync(
                "bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException((result.StdErr + result.StdOut).Trim());
            string snapshot = result.StdOut + "\n" + result.StdErr;
            lock (Sync)
            {
                if (generation == _generation)
                {
                    _snapshot = snapshot;
                    _snapshotAt = DateTime.UtcNow;
                    PersistentHardwareCacheService.WriteMachineStable(PersistentKey, snapshot);
                }
            }
            return snapshot;
        }
        finally
        {
            lock (Sync)
                if (generation == _generation) _flight = null;
        }
    }

    internal static void ClearMemoryCacheForSmoke()
    {
        lock (Sync)
        {
            _snapshot = null;
            _snapshotAt = default;
            _flight = null;
        }
    }

    public static void Invalidate()
    {
        lock (Sync)
        {
            _generation++;
            _snapshot = null;
            _snapshotAt = default;
            _flight = null;
            PersistentHardwareCacheService.Remove(PersistentKey);
        }
    }
}
