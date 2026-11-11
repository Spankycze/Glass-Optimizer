using System;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

public static class AiDriverAutomationPolicy
{
    public static readonly TimeSpan MinimumObservation = TimeSpan.FromMinutes(30);
    // No indefinitely disabled demand-start driver after an AI experiment.
    public static readonly TimeSpan MaximumDisableHold = TimeSpan.FromHours(24);

    public static bool ShouldRelease(AiSystemRuntimeItemState state, DateTime nowUtc) =>
        state.Managed && state.Sleeping &&
        state.OriginalServiceStart == 3 &&
        string.Equals(state.Kind, "driver", StringComparison.OrdinalIgnoreCase) &&
        (state.LastChangedUtc is not DateTime changed || changed > nowUtc ||
         nowUtc - changed >= MaximumDisableHold);

    public static bool IsEligibleStartType(int start) => start == 3;

    // A filesystem filter Type=2 is not a safe demand-start kernel
    // peripheral experiment. Both discovery and final mutation agree.
    internal static bool IsEligibleDriverType(int serviceType) => serviceType == 1;

    internal static readonly TimeSpan MaximumObservationGap = TimeSpan.FromMinutes(10);

    // Do not credit stale observations across agent restarts, machine sleep,
    // missed scan cycles or backwards system-clock corrections.
    internal static DateTime ObservationStart(
        DateTime? firstUtc, DateTime? lastUtc, DateTime nowUtc) =>
        nowUtc.Kind != DateTimeKind.Utc ||
        firstUtc is not DateTime first || first.Kind != DateTimeKind.Utc ||
        lastUtc is not DateTime last || last.Kind != DateTimeKind.Utc ||
        first > last || last > nowUtc ||
        nowUtc - last > MaximumObservationGap
            ? nowUtc : first;

    internal static string? LiveDriverBlockReason(
        bool discovered, ServiceControllerStatus liveStatus) =>
        !discovered ? "driver not found in live SCM inventory" :
        liveStatus == ServiceControllerStatus.Stopped ? null :
        $"driver SCM state is {liveStatus}; not inactive";

    // Independent readback at the final write boundary: do not trust a
    // previously sampled optional-device registry state as write permission.
    internal static string? MutationBlockReason(
        bool installed, bool windowsOwned, int serviceType,
        int currentStart, bool attachedDevice, bool freshQuietEvidence)
    {
        if (!installed) return "driver disappeared before mutation";
        if (!windowsOwned) return "driver ownership changed before mutation";
        if (!IsEligibleDriverType(serviceType))
            return "only optional kernel drivers are eligible";
        if (currentStart != 3) return "driver start mode changed or is not demand-start";
        if (attachedDevice) return "device attached before driver mutation";
        if (!freshQuietEvidence) return "fresh quiet CPU/RAM/disk evidence unavailable";
        return null;
    }

    // A detected device is grounds for prompt recovery, never permission to
    // invent an unknown original driver start setting from a corrupt journal.
    public static bool CanRestoreAttachedDevice(
        AiSystemRuntimeItemState state, int currentStart, bool attached) =>
        attached && currentStart == 4 && state.Managed && state.Sleeping &&
        state.OriginalServiceStart == 3 &&
        string.Equals(state.Kind, "driver", StringComparison.OrdinalIgnoreCase);

    public static TimeSpan RequiredObservation(int configuredSeconds)
    {
        TimeSpan configured = TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 0, 3600));
        return configured > MinimumObservation ? configured : MinimumObservation;
    }

    public static string IneligibleReason(int start) => start switch
    {
        0 => "boot-start driver: automatic disable forbidden",
        1 => "system-start driver: automatic disable forbidden",
        2 => "auto-start driver: automatic disable forbidden",
        4 => "driver already disabled",
        _ => "unsupported driver start type"
    };
}
