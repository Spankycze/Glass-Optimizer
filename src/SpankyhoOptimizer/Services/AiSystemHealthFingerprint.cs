using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Allocation-free comparison of the health JSON's observable inputs.
/// Record struct equality preserves value comparison for text and nullable
/// measurement fields. Health serialization stays on actual changes/heartbeat.
/// </summary>
internal readonly record struct AiSystemHealthFingerprint(
    bool Enabled,
    bool ServicesEnabled,
    bool ScheduledTasksEnabled,
    bool OptionalDevicesEnabled,
    int Managed,
    int Sleeping,
    int Blocked,
    int RestoredCount,
    int ManagedServices,
    int ManagedTasks,
    int ManagedDrivers,
    int ActiveVerified,
    int ConfirmedLast7Days,
    double LatestBenefitPp,
    double LatestBenefitPercent,
    string LatestTarget,
    DateTime? LatestVerifiedUtc,
    double? LatestObservedAvailableRamBenefitMiB,
    string LastAction,
    string LastError,
    double RoundedLoad,
    bool GameActive,
    bool RealIdle);
