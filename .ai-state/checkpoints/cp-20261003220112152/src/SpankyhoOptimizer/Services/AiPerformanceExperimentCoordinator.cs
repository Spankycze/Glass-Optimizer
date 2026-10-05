using System;
namespace SpankyhoOptimizer.Services;

// A trial owns this lease across ticks, including both reference measurements.
// Host-local coordination prevents service scans or another tuner confounding it.
public static class AiPerformanceExperimentCoordinator
{
    private static readonly object Gate = new();
    private static string? _owner;
    public static bool IsActive { get { lock (Gate) return _owner != null; } }
    public static bool TryAcquire(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (Gate)
        {
            if (_owner != null && !string.Equals(_owner, owner, StringComparison.Ordinal))
                return false;
            _owner = owner;
            return true;
        }
    }
    public static void Release(string owner)
    {
        lock (Gate)
        {
            if (string.Equals(_owner, owner, StringComparison.Ordinal)) _owner = null;
        }
    }
}
