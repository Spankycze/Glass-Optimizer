using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Revalidates the SAME current-FanAgent self-performance sample before
/// allowing autonomous Windows changes. A recent initialization record is
/// not yet a sampled overhead baseline; malformed native counters must never
/// turn a "low" pressure score into evidence of safe capacity.
/// </summary>
internal static class AiCurrentSelfHealthEvidencePolicy
{
    internal static bool HasTrustedOverheadSample(AiSelfOptimizationHealth? health, DateTime nowUtc) =>
        health is { Enabled: true } &&
        health.UpdatedUtc <= nowUtc &&
        nowUtc - health.UpdatedUtc <= TimeSpan.FromMinutes(5) &&
        double.IsFinite(health.ProcessCpuPercent) &&
        health.ProcessCpuPercent is >= 0 and <= 100 &&
        double.IsFinite(health.WorkingSetMb) && health.WorkingSetMb >= 0 &&
        double.IsFinite(health.ManagedMemoryMb) && health.ManagedMemoryMb >= 0 &&
        double.IsFinite(health.PressureScore) &&
        health.PressureScore is >= 0 and <= 10 &&
        health.Threads >= 0 && health.Handles >= 0 &&
        health.MaintenanceIntervalSeconds is >= 45 and <= 240 &&
        !string.IsNullOrWhiteSpace(health.State) &&
        !string.Equals(health.State, "Inicializace", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(health.State, "OFF", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(health.State, "Unknown", StringComparison.OrdinalIgnoreCase);
}
