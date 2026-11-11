using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// The sole consumer needs the combined diagnostic text; keep one invocation.
internal sealed record NetworkTransitionState(ProcessResult Combined);

internal static class NetworkTransitionStateService
{
    private static readonly object Gate = new();
    private static Task<NetworkTransitionState>? _inFlight;
    private static NetworkTransitionState? _cache;
    private static DateTime _cacheAt;
    private static int _generation;
    private static int _loadCount;
    private const string PersistentKey = "network-transition-v2";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    internal static int LoadCountForDiagnostics => Volatile.Read(ref _loadCount);

    internal static void Invalidate()
    {
        lock (Gate)
        {
            // Prevent an older in-flight netsh result from recreating the
            // persistent file after this invalidation removes it.
            _generation++;
            _cache = null;
            _cacheAt = default;
            _inFlight = null;
            PersistentHardwareCacheService.Remove(PersistentKey);
        }
    }

    internal static Task<NetworkTransitionState> GetAsync(CancellationToken token)
    {
        lock (Gate)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                return Task.FromResult(_cache);

            if (_inFlight == null)
            {
                // Start the persistent JSON read on a worker as well. The old
                // GetAsync parsed the file synchronously on its caller (often
                // the WinUI dispatcher) before coalescing requests.
                var created = Task.Run(LoadOrRestoreAsync);
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

    private static async Task<NetworkTransitionState> LoadOrRestoreAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int generation;
            lock (Gate)
            {
                if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                    return _cache;
                generation = _generation;
            }

            if (PersistentHardwareCacheService.TryReadMachineStable<NetworkTransitionState>(
                    PersistentKey, CacheTtl, out var persisted) && persisted != null)
            {
                lock (Gate)
                {
                    if (generation == _generation)
                    {
                        _cache = persisted;
                        _cacheAt = DateTime.UtcNow;
                        LogService.Info("performance", "NETWORK_TRANSITION_PERSISTENT_HIT");
                        return persisted;
                    }
                }
                continue;
            }

            var fresh = await LoadAsync(generation).ConfigureAwait(false);
            lock (Gate)
            {
                if (generation == _generation)
                    return fresh;
            }
            // A configuration change invalidated this scan. Its old output
            // must not be returned as a fresh result to a waiting caller.
        }
        throw new InvalidOperationException(
            "Network transition state changed repeatedly while loading; retry the read.");
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
        var diskReaders = Enumerable.Range(0, 8).Select(_ => GetAsync(token)).ToArray();
        var restored = await Task.WhenAll(diskReaders).ConfigureAwait(false);
        persistentSw.Stop();
        var persisted = restored[0];
        if (persisted == null || restored.Any(x => !ReferenceEquals(x, persisted)))
            throw new InvalidOperationException("Network transition concurrent disk reads did not share one snapshot.");
        if (LoadCountForDiagnostics != beforePersistent)
            throw new InvalidOperationException("Network transition persistent cache unexpectedly triggered a fresh load.");
        if (persistentSw.ElapsedMilliseconds > 150)
            throw new InvalidOperationException($"Network transition persistent cache is too slow: {persistentSw.ElapsedMilliseconds} ms.");

        LogService.Info($"NETWORK_TRANSITION_SINGLEFLIGHT_SMOKE_OK loads=1 persistentMs={persistentSw.ElapsedMilliseconds} sharedDiskReaders=8");
    }

    // A single read-only netsh -f invocation preserves native Windows output
    // (including localized labels) without five separate process launches.
    private static readonly string[] ReadOnlyCommands =
    [
        "int teredo show state",
        "int ipv6 isatap show state",
        "int ipv6 6to4 show state",
        "int ipv6 show privacy",
        "int ipv6 show global"
    ];

    private static async Task<ProcessResult> RunBatchedNetshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory = Path.Combine(Path.GetTempPath(),
            "SpankyNetTransition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "read-only.netsh");
            await File.WriteAllLinesAsync(script, ReadOnlyCommands,
                System.Text.Encoding.ASCII, token).ConfigureAwait(false);
            return await ProcessRunner.RunAsync("netsh.exe", "-f \"" + script + "\"",
                TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex)
            {
                LogService.Info("performance", "netsh read-only staging cleanup: " + ex.Message);
            }
        }
    }

    internal static async Task RunBatchParitySmokeAsync(CancellationToken token)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            token.ThrowIfCancellationRequested();
            // Match the OLD implementation precisely: five concurrently
            // launched netsh processes, not five sequential launches.
            var referenceWatch = System.Diagnostics.Stopwatch.StartNew();
            var reference = await Task.WhenAll(ReadOnlyCommands.Select(command =>
                ProcessRunner.RunAsync("netsh.exe", command, TimeSpan.FromSeconds(15), token)))
                .ConfigureAwait(false);
            referenceWatch.Stop();
            var batchWatch = System.Diagnostics.Stopwatch.StartNew();
            var batch = await RunBatchedNetshAsync(token).ConfigureAwait(false);
            batchWatch.Stop();
            if (!batch.Success || string.IsNullOrWhiteSpace(batch.StdOut) ||
                reference.Any(r => !r.Success || string.IsNullOrWhiteSpace(r.StdOut)))
                throw new InvalidOperationException("Read-only network transition netsh status unavailable.");
            // Compare characteristic non-volatile native output lines, not
            // English/Czech labels or dynamically changing timers.
            foreach (var standalone in reference)
            {
                string? marker = standalone.StdOut
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .FirstOrDefault(x => x.Length >= 7 &&
                        !x.Contains("Querying", StringComparison.OrdinalIgnoreCase) &&
                        !x.Contains("Dotaz", StringComparison.OrdinalIgnoreCase) &&
                        !x.Contains("Random Time", StringComparison.OrdinalIgnoreCase) &&
                        !x.StartsWith("---", StringComparison.Ordinal));
                if (marker == null ||
                    !batch.StdOut.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Batched netsh output does not match standalone command: " +
                        marker);
            }
            LogService.Info("performance",
                "NETWORK_TRANSITION_BATCH_PARITY_OK pass=" + (pass + 1) +
                " separateProcesses=5 batchProcesses=1 parallelFiveMs=" +
                referenceWatch.ElapsedMilliseconds + " batchMs=" + batchWatch.ElapsedMilliseconds);
        }
    }

    private static async Task<NetworkTransitionState> LoadAsync(int generation)
    {
        Interlocked.Increment(ref _loadCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = new NetworkTransitionState(await RunBatchedNetshAsync(CancellationToken.None)
            .ConfigureAwait(false));

        // Transient failures must never enter persistent/memory cache: otherwise
        // the diagnostic may incorrectly report the device as healthy for 90s.
        if (state.Combined.Success && !string.IsNullOrWhiteSpace(state.Combined.StdOut))
        {
            lock (Gate)
            {
                if (generation == _generation)
                {
                    // Commit and invalidate use one lock; a completed older
                    // netsh invocation can never recreate an invalidated file.
                    PersistentHardwareCacheService.WriteMachineStable(PersistentKey, state);
                    _cache = state;
                    _cacheAt = DateTime.UtcNow;
                }
            }
        }

        LogService.Info("performance", $"NETWORK_TRANSITION_SNAPSHOT ms={sw.ElapsedMilliseconds}");
        return state;
    }
}
