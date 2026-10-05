using System;

namespace SpankyhoOptimizer.Services;

// A trial owns this lease across ticks, including both reference measurements.
// Host-local coordination prevents service scans or another tuner confounding it.
public static class AiPerformanceExperimentCoordinator
{
    private static readonly object Gate = new();
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(20);
    private static string? _owner;
    private static DateTime _lastTouchUtc = DateTime.MinValue;

    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                ExpireStaleLeaseNoLock(DateTime.UtcNow);
                return _owner != null;
            }
        }
    }

    public static bool TryAcquire(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (Gate)
        {
            DateTime now = DateTime.UtcNow;
            ExpireStaleLeaseNoLock(now);

            if (_owner != null && !string.Equals(_owner, owner, StringComparison.Ordinal))
                return false;

            _owner = owner;
            _lastTouchUtc = now;
            return true;
        }
    }

    public static void Release(string owner)
    {
        lock (Gate)
        {
            if (!string.Equals(_owner, owner, StringComparison.Ordinal))
                return;

            _owner = null;
            _lastTouchUtc = DateTime.MinValue;
        }
    }

    public static bool IsLeaseExpired(DateTime lastTouchUtc, DateTime nowUtc) =>
        lastTouchUtc != DateTime.MinValue &&
        nowUtc >= lastTouchUtc &&
        nowUtc - lastTouchUtc > LeaseTimeout;

    private static void ExpireStaleLeaseNoLock(DateTime nowUtc)
    {
        if (_owner == null || !IsLeaseExpired(_lastTouchUtc, nowUtc))
            return;

        LogService.Info("ai-performance",
            $"AI_EXPERIMENT stale_lease_released owner={_owner} last_touch={_lastTouchUtc:O}");
        _owner = null;
        _lastTouchUtc = DateTime.MinValue;
    }
}
