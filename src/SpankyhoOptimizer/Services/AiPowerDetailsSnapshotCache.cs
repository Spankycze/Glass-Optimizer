using System;

namespace SpankyhoOptimizer.Services;

// Store only short rendered detail strings, never a heavyweight power settings
// graph. This per-window cache does not poll or mutate any Windows setting.
internal sealed record AiPowerDetailsSnapshot(
    string PlanGuid, string ManagedSummary, string SystemSummary,
    DateTime CapturedUtc);

internal sealed class AiPowerDetailsSnapshotCache
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(20);
    private AiPowerDetailsSnapshot? _snapshot;

    internal bool TryGet(string? activePlanGuid, DateTime nowUtc,
        out AiPowerDetailsSnapshot? snapshot)
    {
        snapshot = _snapshot;
        if (snapshot is null || string.IsNullOrWhiteSpace(activePlanGuid))
            return false;
        TimeSpan age = nowUtc - snapshot.CapturedUtc;
        if (age < TimeSpan.Zero || age > Freshness ||
            !string.Equals(snapshot.PlanGuid.Trim(), activePlanGuid.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            snapshot = null;
            return false;
        }
        return true;
    }

    internal void Store(AiPowerDetailsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = string.IsNullOrWhiteSpace(snapshot.PlanGuid) ? null : snapshot;
    }

    internal void Invalidate() => _snapshot = null;
}
