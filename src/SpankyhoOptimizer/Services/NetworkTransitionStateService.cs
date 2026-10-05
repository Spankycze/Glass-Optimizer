using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed record NetworkTransitionState(
    ProcessResult Teredo,
    ProcessResult Isatap,
    ProcessResult SixToFour,
    ProcessResult Privacy,
    ProcessResult Global);

internal static class NetworkTransitionStateService
{
    private static readonly object Gate = new();
    private static Task<NetworkTransitionState>? _inFlight;
    private static NetworkTransitionState? _cache;
    private static DateTime _cacheAt;
    private static int _generation;
    private static int _loadCount;
    private const string PersistentKey = "network-transition-v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    internal static int LoadCountForDiagnostics => Volatile.Read(ref _loadCount);

    internal static void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        lock (Gate)
        {
            _cache = null;
            _cacheAt = default;
            _inFlight = null;
        }
        PersistentHardwareCacheService.Remove(PersistentKey);
    }

    internal static Task<NetworkTransitionState> GetAsync(CancellationToken token)
    {
        lock (Gate)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                return Task.FromResult(_cache);
        }

        int generation = Volatile.Read(ref _generation);
        if (PersistentHardwareCacheService.TryReadMachineStable<NetworkTransitionState>(
                PersistentKey, CacheTtl, out var persisted) &&
            persisted != null &&
            generation == Volatile.Read(ref _generation))
        {
            lock (Gate)
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _cache = persisted;
                    _cacheAt = DateTime.UtcNow;
                    LogService.Info("performance", "NETWORK_TRANSITION_PERSISTENT_HIT");
                    return Task.FromResult(persisted);
                }
            }
        }

        lock (Gate)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                return Task.FromResult(_cache);

            if (_inFlight == null)
            {
                generation = Volatile.Read(ref _generation);
                var created = LoadAsync(generation);
                _inFlight = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (Gate)
                    {
                        if (ReferenceEquals(_inFlight, created))
                            _inFlight = null;
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            return _inFlight.WaitAsync(token);
        }
    }

    internal static async Task RunSingleFlightSmokeAsync(CancellationToken token)
    {
        Invalidate();
        int before = LoadCountForDiagnostics;
        var calls = new[]
        {
            GetAsync(token),
            GetAsync(token),
            GetAsync(token),
            GetAsync(token)
        };
        var results = await Task.WhenAll(calls).ConfigureAwait(false);
        int loads = LoadCountForDiagnostics - before;
        if (loads != 1)
            throw new InvalidOperationException($"Network transition single-flight failed: loads={loads}.");
        if (results.Any(x => x is null))
            throw new InvalidOperationException("Network transition single-flight returned a null snapshot.");

        lock (Gate)
        {
            _cache = null;
            _cacheAt = default;
            _inFlight = null;
        }

        int beforePersistent = LoadCountForDiagnostics;
        var persistentSw = System.Diagnostics.Stopwatch.StartNew();
        var persisted = await GetAsync(token).ConfigureAwait(false);
        persistentSw.Stop();
        if (persisted == null)
            throw new InvalidOperationException("Network transition persistent cache returned null.");
        if (LoadCountForDiagnostics != beforePersistent)
            throw new InvalidOperationException("Network transition persistent cache unexpectedly triggered a fresh load.");
        if (persistentSw.ElapsedMilliseconds > 150)
            throw new InvalidOperationException($"Network transition persistent cache is too slow: {persistentSw.ElapsedMilliseconds} ms.");

        LogService.Info($"NETWORK_TRANSITION_SINGLEFLIGHT_SMOKE_OK loads=1 persistentMs={persistentSw.ElapsedMilliseconds}");
    }

    private static async Task<NetworkTransitionState> LoadAsync(int generation)
    {
        Interlocked.Increment(ref _loadCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var teredoTask = ProcessRunner.RunAsync("netsh.exe", "int teredo show state", TimeSpan.FromSeconds(15), CancellationToken.None);
        var isatapTask = ProcessRunner.RunAsync("netsh.exe", "int ipv6 isatap show state", TimeSpan.FromSeconds(15), CancellationToken.None);
        var sixToFourTask = ProcessRunner.RunAsync("netsh.exe", "int ipv6 6to4 show state", TimeSpan.FromSeconds(15), CancellationToken.None);
        var privacyTask = ProcessRunner.RunAsync("netsh.exe", "int ipv6 show privacy", TimeSpan.FromSeconds(15), CancellationToken.None);
        var globalTask = ProcessRunner.RunAsync("netsh.exe", "int ipv6 show global", TimeSpan.FromSeconds(15), CancellationToken.None);

        await Task.WhenAll(teredoTask, isatapTask, sixToFourTask, privacyTask, globalTask).ConfigureAwait(false);
        var state = new NetworkTransitionState(
            await teredoTask.ConfigureAwait(false),
            await isatapTask.ConfigureAwait(false),
            await sixToFourTask.ConfigureAwait(false),
            await privacyTask.ConfigureAwait(false),
            await globalTask.ConfigureAwait(false));

        if (generation == Volatile.Read(ref _generation))
        {
            PersistentHardwareCacheService.WriteMachineStable(PersistentKey, state);
            lock (Gate)
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _cache = state;
                    _cacheAt = DateTime.UtcNow;
                }
            }
        }

        LogService.Info("performance", $"NETWORK_TRANSITION_SNAPSHOT ms={sw.ElapsedMilliseconds}");
        return state;
    }
}
