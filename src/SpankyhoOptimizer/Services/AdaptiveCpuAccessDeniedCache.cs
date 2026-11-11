using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Negative cache ONLY for native Win32 ERROR_ACCESS_DENIED (5) while reading
/// per-process CPU time. A failed query yielded NO CPU reading already; skipping
/// at most the next 3 snapshots avoids repeating the same exception/native
/// OpenProcess attempts. Never suppress current foreground, known games or
/// one of the five trusted AI process candidates.
/// </summary>
internal sealed class AdaptiveCpuAccessDeniedCache
{
    private readonly record struct Entry(
        string Name, int RetryCycle, DateTime DeniedUtc);
    // Pre-size the fixed-capacity cache to avoid resize allocations during scans.
    private readonly Dictionary<int, Entry> _denied = new(256);
    private readonly List<int> _expired = new(256);
    private const int RecheckEveryCycles = 4;
    private const int MaximumEntries = 256;
    private const int MinimumDeniedEntriesForSkipping = 8;
    private int _cycle;
    private DateTime _cycleUtc;
    private int _suppressed;
    private int _accessDenied;

    internal int SuppressedThisCycle => _suppressed;
    internal int AccessDeniedThisCycle => _accessDenied;
    internal int EntryCount => _denied.Count;

    internal void BeginCycle(DateTime nowUtc)
    {
        // Two-second CPU scan means a denied PID is retried within 8 seconds,
        // even if the machine slept or the wall clock moved backwards.
        // The caller supplies its already sampled DateTime.UtcNow.
        bool clockRolledBack = _cycleUtc != default && nowUtc < _cycleUtc;
        _cycleUtc = nowUtc;
        _cycle++;
        _suppressed = 0;
        _accessDenied = 0;
        if (clockRolledBack)
        {
            // Invalid timestamps must never contribute to the >=8 PID gate.
            _denied.Clear();
            return;
        }
        if ((_cycle & 7) != 0 || _denied.Count == 0)
            return;
        _expired.Clear();
        foreach (var pair in _denied)
            if (_cycle >= pair.Value.RetryCycle ||
                nowUtc < pair.Value.DeniedUtc ||
                nowUtc - pair.Value.DeniedUtc >= TimeSpan.FromSeconds(8))
                _expired.Add(pair.Key);
        foreach (int pid in _expired)
            _denied.Remove(pid);
        _expired.Clear();
    }

    internal bool ShouldSkip(int pid, string processName, int foregroundPid)
    {
        // Elevated Optimizer can have just one denied system process while
        // a low-privilege monitoring context has hundreds. Do not pay the
        // negative-cache lookup/name-filter cost or change observation timing
        // at all unless at least eight distinct denied background PIDs exist.
        if (pid <= 0 || pid == foregroundPid ||
            _denied.Count < MinimumDeniedEntriesForSkipping)
            return false;
        if (!_denied.TryGetValue(pid, out Entry entry))
            return false;
        if (string.IsNullOrWhiteSpace(processName) ||
            IsProtectedFromSkipping(processName))
            return false;
        if (!string.Equals(entry.Name, processName,
                StringComparison.OrdinalIgnoreCase) ||
            _cycle >= entry.RetryCycle ||
            _cycleUtc < entry.DeniedUtc ||
            _cycleUtc - entry.DeniedUtc >= TimeSpan.FromSeconds(8))
        {
            _denied.Remove(pid); // New name or retry point: try fresh CPU.
            return false;
        }
        _suppressed++;
        return true;
    }

    internal void RecordDenied(int pid, string name, int foregroundPid)
    {
        _accessDenied++;
        if (pid <= 0 || pid == foregroundPid ||
            string.IsNullOrWhiteSpace(name) ||
            IsProtectedFromSkipping(name))
            return;
        if (_denied.Count >= MaximumEntries && !_denied.ContainsKey(pid))
        {
            // Reclaim expired entries first. A stale denial should never
            // evict a live entry merely because periodic pruning has not run.
            _expired.Clear();
            foreach (var pair in _denied)
                if (_cycle >= pair.Value.RetryCycle ||
                    _cycleUtc < pair.Value.DeniedUtc ||
                    _cycleUtc - pair.Value.DeniedUtc >= TimeSpan.FromSeconds(8))
                    _expired.Add(pair.Key);
            foreach (int expiredPid in _expired)
                _denied.Remove(expiredPid);
            _expired.Clear();
        }
        if (_denied.Count >= MaximumEntries && !_denied.ContainsKey(pid))
        {
            // Evict only the oldest denial. Clearing the whole cache caused
            // a burst of redundant native access-denied calls next tick.
            int oldestPid = 0;
            int oldestCycle = int.MaxValue;
            foreach (var pair in _denied)
            {
                if (pair.Value.RetryCycle >= oldestCycle)
                    continue;
                oldestPid = pair.Key;
                oldestCycle = pair.Value.RetryCycle;
            }
            _denied.Remove(oldestPid);
        }
        _denied[pid] = new Entry(name, _cycle + RecheckEveryCycles, _cycleUtc);
    }

    private static bool IsProtectedFromSkipping(string name) =>
        AiAutomaticProcessPriorityPolicy.IsAllowlisted(name) ||
        AiKnownGameProcessPolicy.IsKnownGame(name);

    internal static string RunFunctionalSmoke()
    {
        var cache = new AdaptiveCpuAccessDeniedCache();
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var few = new AdaptiveCpuAccessDeniedCache();
        few.BeginCycle(now);
        few.RecordDenied(6000, "DeniedBackground", 100);
        if (few.ShouldSkip(6000, "DeniedBackground", 100))
            throw new InvalidOperationException(
                "Few permission denials should not activate native CPU cache.");
        cache.BeginCycle(now);
        for (int i = 0; i < MinimumDeniedEntriesForSkipping - 1; i++)
            cache.RecordDenied(9000 + i, "DeniedBackground", 100);
        cache.RecordDenied(6000, "DeniedBackground", 100);
        if (!cache.ShouldSkip(6000, "DeniedBackground", 100) ||
            cache.ShouldSkip(6000, "DifferentProcess", 100) ||
            cache.ShouldSkip(6000, "DeniedBackground", 100))
            throw new InvalidOperationException("PID name-change negative cache invalidation failed.");
        cache.RecordDenied(6000, "DeniedBackground", 100);
        for (int i = 0; i < 3; i++)
        {
            cache.BeginCycle(now.AddSeconds(2 * (i + 1)));
            if (!cache.ShouldSkip(6000, "DeniedBackground", 100))
                throw new InvalidOperationException("Denied PID cache expired too early.");
        }
        cache.BeginCycle(now.AddSeconds(8));
        if (cache.ShouldSkip(6000, "DeniedBackground", 100))
            throw new InvalidOperationException("Denied PID retry was delayed too long.");
        cache.RecordDenied(7000, "AnotherProcess", 100);
        if (cache.ShouldSkip(7000, "AnotherProcess", 7000))
            throw new InvalidOperationException("Foreground CPU should never be skipped.");
        cache.RecordDenied(7100, "SleepyProcess", 100);
        cache.BeginCycle(now.AddHours(1));
        if (cache.ShouldSkip(7100, "SleepyProcess", 100))
            throw new InvalidOperationException("Sleep made negative CPU cache stale.");
        cache.RecordDenied(7100, "SleepyProcess", 100);
        cache.BeginCycle(now.AddSeconds(-1));
        if (cache.EntryCount != 0 ||
            cache.ShouldSkip(7100, "SleepyProcess", 100))
            throw new InvalidOperationException("Backward clock jump retained stale denial.");
        foreach (string name in new[] {
            "OneDrive", "Dropbox", "SearchIndexer",
            "SearchProtocolHost", "SearchFilterHost", "DiabloImmortal",
            "Diablo Immortal", "cs2", "Cyberpunk2077"
        })
        {
            cache.RecordDenied(8000, name, 100);
            if (cache.ShouldSkip(8000, name, 100))
                throw new InvalidOperationException("Trusted candidate/game suppressed: " + name);
        }
        for (int i = 0; i < 1000; i++)
            cache.RecordDenied(i + 10000, "DeniedBackground", 100);
        if (cache.EntryCount > MaximumEntries)
            throw new InvalidOperationException("Negative CPU cache grew unbounded.");
        var fullCache = new AdaptiveCpuAccessDeniedCache();
        fullCache.BeginCycle(now);
        for (int i = 0; i < MaximumEntries; i++)
            fullCache.RecordDenied(20000 + i, "DeniedBackground", 100);
        fullCache.RecordDenied(30000, "DeniedBackground", 100);
        if (!fullCache.ShouldSkip(20255, "DeniedBackground", 100) ||
            !fullCache.ShouldSkip(30000, "DeniedBackground", 100) ||
            fullCache.EntryCount != MaximumEntries)
            throw new InvalidOperationException("Cache capacity eviction removed fresh entries.");
        // Saturation with an expired generation should reclaim stale entries
        // instead of evicting the recently refreshed live PID.
        var mixedCache = new AdaptiveCpuAccessDeniedCache();
        mixedCache.BeginCycle(now);
        for (int i = 0; i < MaximumEntries; i++)
            mixedCache.RecordDenied(40000 + i, "DeniedBackground", 100);
        mixedCache.BeginCycle(now.AddSeconds(6));
        for (int i = 40; i < 48; i++)
            mixedCache.RecordDenied(40000 + i, "DeniedBackground", 100);
        mixedCache.BeginCycle(now.AddSeconds(8));
        mixedCache.RecordDenied(50000, "DeniedBackground", 100);
        if (!mixedCache.ShouldSkip(40042, "DeniedBackground", 100) ||
            mixedCache.EntryCount != 9 ||
            mixedCache.ShouldSkip(40049, "DeniedBackground", 100))
            throw new InvalidOperationException("Expired entries displaced live denial.");
        cache.BeginCycle(now.AddHours(1));
        if (cache.SuppressedThisCycle != 0 || cache.AccessDeniedThisCycle != 0)
            throw new InvalidOperationException("Counters not reset between CPU snapshots.");

        const int probes = 32768;
        cache.RecordDenied(12345, "DeniedBackground", 100);
        // With the >=8 distinct-denial activation policy, a single denied PID
        // after cache expiry must not suppress native CPU reads.
        if (cache.ShouldSkip(12345, "DeniedBackground", 100))
            throw new InvalidOperationException("Single denial activated CPU suppression.");
        for (int i = 0; i < 7; i++)
            cache.RecordDenied(60000 + i, "DeniedBackground", 100);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < probes; i++)
            if (!cache.ShouldSkip(12345, "DeniedBackground", 100))
                throw new InvalidOperationException("Negative CPU cache lost stable PID.");
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (bytes > 128)
            throw new InvalidOperationException("Access denied CPU hot path allocated memory.");
        return $"ADAPTIVE_CPU_ACCESS_DENIED_CACHE_SMOKE_OK protectedForeground=true " +
            $"protectedGames=true protectedAiCandidates=true nameChangeRetry=true " +
            $"retryEveryCycles={RecheckEveryCycles} boundedEntries={MaximumEntries} " +
            $"minDeniedActivation={MinimumDeniedEntriesForSkipping} " +
            "lowDenialCostAvoided=true suspendAndClockRollbackRetry=true " +
            $"hotChecks={probes} hotMs={ms:0.###} hotAllocB={bytes} writes=0";
    }
}
