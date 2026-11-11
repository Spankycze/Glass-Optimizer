using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Reports strictly confirmed A/B/A results. The improvement refers to the
/// individual controlled candidate-versus-reference trial; never add those
/// deltas together as an alleged whole-machine CPU/FPS saving.
/// </summary>
internal static class AiMeasuredOptimizationPolicy
{
    internal readonly record struct Summary(
        int ActiveVerified,
        int ConfirmedLast7Days,
        double LatestBenefitPp,
        double LatestBenefitPercent,
        string LatestTarget,
        DateTime? LatestVerifiedUtc,
        double? LatestObservedAvailableRamBenefitMiB = null);

    // Existing health totals and A/B/A proof are derived from one journal
    // walk instead of two independent per-agent-tick enumerations.
    internal readonly record struct HealthCounts(
        int Managed, int Sleeping, int Blocked,
        int ServiceManaged, int TaskManaged, int DriverManaged);
    internal readonly record struct HealthEvaluation(Summary Measured, HealthCounts Counts);

    internal static HealthEvaluation EvaluateHealth(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items,
        DateTime nowUtc, string? previouslyFormattedTarget = null) =>
        EvaluateEnumerator(items.GetEnumerator(), nowUtc, true, previouslyFormattedTarget);

    // Dictionary.ValueCollection enumerates via a struct. Calling the
    // IEnumerable overload from the 1-5s FanAgent health tick boxes its
    // enumerator, even if nothing changed. Preserve generic callers.
    internal static Summary Evaluate(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items,
        DateTime nowUtc) => EvaluateEnumerator(items.GetEnumerator(), nowUtc, false).Measured;

    internal static Summary Evaluate(IEnumerable<AiSystemRuntimeItemState> items,
        DateTime nowUtc) => EvaluateEnumerator(items.GetEnumerator(), nowUtc, false).Measured;

    // One historical proof predicate is shared with candidate ranking.
    // A false win must never be hidden in health while being promoted to
    // shorten the next observation window.
    internal static bool CredibleHistoricalCpuProof(AiSystemRuntimeItemState item) =>
        double.IsFinite(item.LastVerifiedBenefitAbsolute) &&
        item.LastVerifiedBenefitAbsolute > 0 &&
        item.LastVerifiedBenefitAbsolute <= AiBackgroundPerformancePolicy.MaximumQuietLoadPercent &&
        double.IsFinite(item.LastVerifiedBenefitPercent) &&
        item.LastVerifiedBenefitPercent >= 0 &&
        item.LastVerifiedBenefitPercent <= 100;

    private static HealthEvaluation EvaluateEnumerator<TEnumerator>(
        TEnumerator enumerator, DateTime nowUtc, bool includeCounts,
        string? previouslyFormattedTarget = null)
        where TEnumerator : IEnumerator<AiSystemRuntimeItemState>
    {
        // Never compare an imported local/unspecified instant to UTC journals.
        // Empty diagnostics are preferable to a fictitious historical win.
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            enumerator.Dispose();
            return new(new Summary(0, 0, 0, 0, string.Empty, null), default);
        }
        // Compute the seven-day window only once per FanAgent health tick,
        // not once per verified journal row. Clamp for synthetic early UTC
        // instants instead of underflowing DateTime.MinValue.
        long nowTicks = nowUtc.Ticks;
        long oldestAllowedProofTicks =
            nowTicks >= TimeSpan.TicksPerDay * 7
                ? nowTicks - TimeSpan.TicksPerDay * 7
                : DateTime.MinValue.Ticks;
        int managed = 0, sleeping = 0, blocked = 0;
        int managedServices = 0, managedTasks = 0, managedDrivers = 0;
        int active = 0;
        int recent = 0;
        DateTime? latestDate = null;
        double latestAbsolute = 0;
        double latestPercent = 0;
        double? latestRam = null;
        string? latestKind = null;
        string? latestId = null;

        using (enumerator)
        {
            while (enumerator.MoveNext())
            {
                AiSystemRuntimeItemState item = enumerator.Current;
                // Journal decoding intentionally accepts case-insensitive
            // legacy Kind values. Use the same identity semantics here or
            // valid older A/B/A evidence disappears from health statistics.
            bool service = string.Equals(item.Kind, "service", StringComparison.OrdinalIgnoreCase);
            bool task = string.Equals(item.Kind, "task", StringComparison.OrdinalIgnoreCase);
            if (includeCounts)
            {
                if (item.Managed)
                {
                    managed++;
                    if (service)
                        managedServices++;
                    else if (task)
                        managedTasks++;
                    else if (string.Equals(item.Kind, "driver", StringComparison.OrdinalIgnoreCase))
                        managedDrivers++;
                }
                if (item.Sleeping) sleeping++;
                if (AiOptionalFeatureSummaryPolicy.CountsAsBlocked(item)) blocked++;
            }
            if ((!service && !task) || string.IsNullOrWhiteSpace(item.Id))
                continue;
            // Active ownership and historical seven-day proof can overlap.
            // Resolve their independent admission windows first, then check
            // the same CPU evidence at most once for a qualifying record.
            bool activeCandidate = item.Managed && item.Sleeping && item.PerformanceVerified;
            DateTime? verifiedAt = item.LastBenefitVerifiedUtc;
            bool recentCandidate = verifiedAt is DateTime proofUtc &&
                proofUtc.Kind == DateTimeKind.Utc &&
                proofUtc.Ticks <= nowTicks &&
                proofUtc.Ticks >= oldestAllowedProofTicks;
            if ((!activeCandidate && !recentCandidate) ||
                !CredibleHistoricalCpuProof(item))
                continue;
            if (activeCandidate)
                active++;
            if (!recentCandidate)
                continue;
            DateTime at = verifiedAt!.Value;
            recent++;

            if (latestDate is not DateTime prior || at > prior)
            {
                latestDate = at;
                latestAbsolute = item.LastVerifiedBenefitAbsolute;
                latestPercent = item.LastVerifiedBenefitPercent;
                // Postpone formatting until after finding the newest trial.
                // Multiple verified rows no longer create transient strings.
                // Canonical display is stable across lowercase/uppercase
                // legacy journal formats and avoids spurious health changes.
                latestKind = service ? "service" : "task";
                latestId = item.Id;
                // A passive physical-memory observation is not a promise of
                // process memory reduction or additive total PC savings.
                latestRam = AiPassiveMemoryTrialPolicy.CredibleHistoricalAvailableRamBenefit(
                    item.LastVerifiedAvailableRamBenefitMiB)
                    ? Math.Round(item.LastVerifiedAvailableRamBenefitMiB!.Value, 1) : null;
            }
        }
        }
        string latestTarget = LatestTargetString(
            latestKind, latestId, previouslyFormattedTarget);
        return new(
            new Summary(active, recent, Math.Round(latestAbsolute, 3),
                Math.Round(latestPercent, 1), latestTarget, latestDate, latestRam),
            new HealthCounts(managed, sleeping, blocked,
                managedServices, managedTasks, managedDrivers));
    }

    // Reusing a stable health display target avoids one new string each
    // agent tick with an already-proven A/B/A candidate. A changed kind or
    // identity always forms the new canonical display string.
    private static string LatestTargetString(
        string? kind, string? id, string? previous)
    {
        if (kind is null || id is null)
            return string.Empty;
        if (previous is not null &&
            previous.Length == kind.Length + 1 + id.Length &&
            previous.AsSpan(0, kind.Length).SequenceEqual(kind.AsSpan()) &&
            previous[kind.Length] == ' ' &&
            previous.AsSpan(kind.Length + 1).SequenceEqual(id.AsSpan()))
            return previous;
        return string.Concat(kind, " ", id);
    }

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var service = new AiSystemRuntimeItemState("service", "DiagTrack",
            true, true, "", null, null, 2, null, true, 0, "verified",
            PerformanceVerified: true, LastVerifiedBenefitAbsolute: 0.3,
            LastBenefitVerifiedUtc: now.AddMinutes(-5),
            LastVerifiedBenefitPercent: 15,
            LastVerifiedAvailableRamBenefitMiB: 220);
        var task = new AiSystemRuntimeItemState("task", "Fixture",
            false, false, "", null, null, null, true, false, 0, "restored",
            LastVerifiedBenefitAbsolute: 0.4,
            LastBenefitVerifiedUtc: now.AddHours(-2),
            LastVerifiedBenefitPercent: 22);
        var expired = task with { Id = "Old", LastBenefitVerifiedUtc = now.AddDays(-9) };
        var corrupt = task with { Id = "NaN", LastVerifiedBenefitAbsolute = double.NaN };
        var driver = service with { Kind = "driver", Id = "Driver", LastVerifiedBenefitAbsolute = 90 };
        var blank = service with { Id = " ", LastBenefitVerifiedUtc = now };
        var invalidActive = service with { Id = "Broken", LastVerifiedBenefitAbsolute = double.NaN };
        Summary result = Evaluate(new[] { service, task, expired, corrupt, driver, blank, invalidActive }, now);

        string newJson = System.Text.Json.JsonSerializer.Serialize(service);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(newJson);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(newJson)!.AsObject();
        legacy.Remove("LastVerifiedBenefitPercent");
        legacy.Remove("LastVerifiedAvailableRamBenefitMiB");
        var older = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(
            legacy.ToJsonString());
        if (result.ActiveVerified != 1 ||
            result.ConfirmedLast7Days != 2 ||
            result.LatestTarget != "service DiagTrack" ||
            result.LatestBenefitPp != 0.3 ||
            result.LatestBenefitPercent != 15 ||
            result.LatestObservedAvailableRamBenefitMiB != 220 ||
            roundTrip?.LastVerifiedAvailableRamBenefitMiB != 220 ||
            roundTrip?.LastVerifiedBenefitPercent != 15 ||
            older?.LastVerifiedBenefitPercent != 0 ||
            older?.LastVerifiedAvailableRamBenefitMiB != null ||
            Evaluate(Array.Empty<AiSystemRuntimeItemState>(), now).ConfirmedLast7Days != 0)
            throw new InvalidOperationException(
                "AI measured A/B/A outcome summary or JSON legacy compatibility regressed.");
        return "AI_MEASURED_BENEFIT_SMOKE_OK confirmed7d=2 activeVerified=1 " +
               "latestCpuPp=0.3 latestPercent=15 legacyJson=true " +
               "driverExcluded=true invalidExcluded=true optionalRamMiB=220 " +
               "wholeMachineSavingsNotAdded=true writes=0";
    }
}