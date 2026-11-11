using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Independent decisions for the Optimizer's OWN scheduled maintenance and
/// optional cloud uploads. Nothing here creates workload modes, changes
/// adapters/DNS/MTU/TCP/RSS/interrupt moderation or limits browser traffic.
/// </summary>
internal static class AiAutomaticWorkloadSafetyPolicy
{
    internal static int MaintenanceIntervalSeconds(
        int currentSeconds, AiWorkloadClass workload,
        AiSystemSmoothnessHealth? smooth, DateTime nowUtc)
    {
        int seconds = AiSelfOptimizationPolicy.ApplyWorkloadFloor(
            Math.Clamp(currentSeconds, 45, 240), workload);
        if (smooth is null || !AiSystemSmoothnessGuardService.IsFresh(
                smooth.UpdatedUtc, nowUtc, TimeSpan.FromSeconds(90)))
            return Math.Max(seconds, 180);
        return smooth.State switch
        {
            "CPU pressure" or "Single-core pressure" or "Memory pressure" or "Disk pressure" or "Disk latency" =>
                Math.Max(seconds, 240),
            "Gaming" => Math.Max(seconds, 180),
            "Unknown" => Math.Max(seconds, 180),
            _ => seconds
        };
    }

    internal static bool CanUploadOwnCloudProfile(
        AiWorkloadClass workload, AiSystemSmoothnessHealth? smooth, DateTime nowUtc) =>
        AiCloudProfilePolicy.ShouldBackgroundSync(workload) &&
        smooth != null &&
        AiSystemSmoothnessGuardService.IsFresh(
            smooth.UpdatedUtc, nowUtc, TimeSpan.FromSeconds(90)) &&
        smooth.State == "Normal" && !smooth.GameActive;

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var smooth = new AiSystemSmoothnessHealth(
            now.AddSeconds(-15), 8, 40, 12, "Normal", "", false);
        if (MaintenanceIntervalSeconds(60, AiWorkloadClass.Background, smooth, now) != 60 ||
            MaintenanceIntervalSeconds(60, AiWorkloadClass.Interactive, smooth, now) != 120 ||
            MaintenanceIntervalSeconds(60, AiWorkloadClass.Game, smooth, now) != 180 ||
            MaintenanceIntervalSeconds(60, AiWorkloadClass.Background,
                smooth with { State = "Disk latency" }, now) != 240 ||
            MaintenanceIntervalSeconds(60, AiWorkloadClass.Background,
                smooth with { State = "Memory pressure" }, now) != 240 ||
            MaintenanceIntervalSeconds(60, AiWorkloadClass.Background, null, now) != 180 ||
            MaintenanceIntervalSeconds(60, AiWorkloadClass.Background,
                smooth with { UpdatedUtc = now.AddMinutes(-3) }, now) != 180 ||
            !CanUploadOwnCloudProfile(AiWorkloadClass.Background, smooth, now) ||
            CanUploadOwnCloudProfile(AiWorkloadClass.Game, smooth, now) ||
            CanUploadOwnCloudProfile(AiWorkloadClass.Interactive, smooth, now) ||
            CanUploadOwnCloudProfile(AiWorkloadClass.Background,
                smooth with { State = "Disk pressure" }, now) ||
            CanUploadOwnCloudProfile(AiWorkloadClass.Background,
                smooth with { UpdatedUtc = now.AddMinutes(-3) }, now))
            throw new InvalidOperationException(
                "AI automatic workload control allowed intrusive background maintenance.");
        return "AI_AUTOMATIC_WORKLOAD_SMOKE_OK profiles=false " +
               "diskCpuRamPressureDefersOwnWork=true ownCloudTransferOnlyWhenQuiet=true " +
               "networkSettingsUntouched=true writes=0";
    }
}
