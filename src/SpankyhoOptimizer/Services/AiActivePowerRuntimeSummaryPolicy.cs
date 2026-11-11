using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Explain live CPU power tuning separately from service/task A/B/A wins.
/// Does not infer a speedup from an EPP number, or claim ownership of a
/// manually changed Windows power scheme. No native reads or mutations.
/// </summary>
internal static class AiActivePowerRuntimeSummaryPolicy
{
    internal static string Describe(AdaptivePowerRuntimeStatus? power,
        bool aiEnabled, bool powerPlanEnabled, DateTime nowUtc)
    {
        if (!aiEnabled || !powerPlanEnabled)
            return "CPU plán: automatická správa vypnutá";
        if (!AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc) ||
            power == null)
            return "CPU plán: čekám na čerstvé ověřené hodnoty";
        // The on-disk status may be truncated or deserialized with null or
        // unexpected mode values. Never label an unknown mode as ACTIVE.
        if (power.Mode is "Observation (AI off)" or
            "Observation (no Spanky plan)" or "Observation (EPP unverified)")
            return "CPU plán: jen sledování (plán Optimizeru není aktivní)";
        if (power.Mode is not ("Idle" or "Balanced" or "Performance"))
            return "CPU plán: nelze ověřit aktuální režim";
        if (power.Epp is < 0 or > 100 || power.Boost is < 0 or > 4)
            return "CPU plán: nelze ověřit aktuální EPP/Boost";
        if (power.VerifiedPowerSettingTransitionsThisSession < 0 ||
            power.DeferredOrdinaryTransitionEvaluationsThisSession < 0)
            return "CPU plán: nelze ověřit počítadla přepnutí";
        string workload = power.GameActive ? " • detekována hra" :
            power.RealIdle ? " • neaktivní uživatel" : " • aktivní práce";
        return $"CPU plán: aktivní {power.Mode}, EPP {power.Epp}, Boost {power.Boost}" +
               workload +
               $" • skutečná přepnutí EPP od spuštění {power.VerifiedPowerSettingTransitionsThisSession}" +
               $" • odložená běžná rozhodnutí {power.DeferredOrdinaryTransitionEvaluationsThisSession}" +
               " • nikoli prokázaná úspora CPU/RAM" +
               (string.IsNullOrWhiteSpace(power.EppTrialOpportunityStatus)
                   ? "" : "\n" + power.EppTrialOpportunityStatus);
    }

    internal static string RunStatusDescriptionSmoke()
    {
        DateTime now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
        var valid = new AdaptivePowerRuntimeStatus(now, "Balanced", 12, "",
            null, null, null, true, false, 20, 3);
        if (!Describe(valid, true, true, now).StartsWith("CPU plán: aktivní Balanced", StringComparison.Ordinal) ||
            !Describe(valid with { Mode = "Observation (AI off)" }, true, true, now)
                .Contains("jen sledování", StringComparison.Ordinal) ||
            !Describe(valid with { Mode = "invalid" }, true, true, now)
                .Contains("nelze ověřit aktuální režim", StringComparison.Ordinal) ||
            !Describe(valid with { Mode = null! }, true, true, now)
                .Contains("nelze ověřit aktuální režim", StringComparison.Ordinal) ||
            !Describe(valid with { Epp = 101 }, true, true, now)
                .Contains("nelze ověřit aktuální EPP/Boost", StringComparison.Ordinal) ||
            !Describe(valid with { VerifiedPowerSettingTransitionsThisSession = -1 }, true, true, now)
                .Contains("nelze ověřit počítadla", StringComparison.Ordinal) ||
            !Describe(valid with { UpdatedUtc = now.AddSeconds(1) }, true, true, now)
                .Contains("čekám na čerstvé", StringComparison.Ordinal))
            throw new InvalidOperationException("AI Runtime power status could display unverified ACTIVE telemetry.");
        return "AI_POWER_RUNTIME_STATUS_SMOKE_OK validMode=true corruptRejected=true writes=0";
    }
}
