using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Increases the frequency of safe measured background experiments and
/// favors candidates with repeatably demonstrated A/B/A benefit.
/// All actual Windows changes still use the existing allowlist, readback,
/// write-ahead journal, and three-sample A/B/A proof.
/// </summary>
internal static class AiMaintenanceOpportunityPolicy
{
    internal const int StandardObservationSeconds = 180;
    internal const double MinimumProvenBenefit = 0.20;
    internal static readonly TimeSpan ProvenBenefitLifetime = TimeSpan.FromDays(7);

    internal static int ObservationSeconds(int configuredSeconds, string kind)
    {
        // A zero/negative duration would let an A/B/A trial commit without any
        // quiet observation window. Treat corrupt/imported values as default.
        if (configuredSeconds <= 0)
            configuredSeconds = StandardObservationSeconds;
        // Respect any explicit user-defined duration, including conservative
        // values. Shorter built-in defaults only for reversible service/task
        // A/B/A after independent stable quiet background samples.
        if (configuredSeconds != StandardObservationSeconds)
            return configuredSeconds;
        // Decode accepts older case-insensitive service/task Kind values.
        if (string.Equals(kind, "service", StringComparison.OrdinalIgnoreCase))
            return 120;
        if (string.Equals(kind, "task", StringComparison.OrdinalIgnoreCase))
            return 90;
        return configuredSeconds;
    }

    internal static int CandidateObservationSeconds(
        int configuredSeconds, string kind,
        AiSystemRuntimeItemState state, DateTime nowUtc)
    {
        int usual = ObservationSeconds(configuredSeconds, kind);
        if (configuredSeconds != StandardObservationSeconds ||
            !string.Equals(kind, state.Kind, StringComparison.OrdinalIgnoreCase) ||
            !HasRecentProvenBenefit(state, nowUtc))
            return usual;

        // Learn from the age of the most recent proven A/B/A result. Repeat a
        // very fresh successful trial sooner; gradually approach the normal
        // observation duration as that learned evidence ages. This adapts
        // time-to-next useful experiment without changing the actual three
        // A/B/A measurements or user-configured observation durations.
        TimeSpan proofAge = nowUtc - state.LastBenefitVerifiedUtc!.Value;
        bool service = string.Equals(kind, "service", StringComparison.OrdinalIgnoreCase);
        if (service)
            return proofAge > TimeSpan.FromDays(3) ? 105 :
                   proofAge > TimeSpan.FromDays(1) ? 90 : 75;
        if (string.Equals(kind, "task", StringComparison.OrdinalIgnoreCase))
            return proofAge > TimeSpan.FromDays(3) ? 80 :
                   proofAge > TimeSpan.FromDays(1) ? 70 : 60;
        return usual;
    }

    // Runtime authorization and the displayed opportunity reason MUST share
    // the Engine 2 decision. The generic context permission alone is not
    // sufficient: it can remain true during non-idle background workloads.
    internal static bool AllowsMeasuredTrial(
        AiSharedDecisionContext context, AiEngine2Decision decision) =>
        decision.Phase == AiEngine2Phase.EligibleForMeasuredTrial &&
        context.AllowBackgroundMutation &&
        context.Confidence >= 72 &&
        context.RealIdle &&
        !context.GameActive &&
        AiSharedWorkloadPolicy.AllowsBackgroundAutomation(context.Workload);

    // A truthful reason for not attempting a Windows optimization. Unlike a
    // bare CPU threshold, it mirrors the actual opportunity gates.
    internal static string WindowStatus(
        bool enabled, bool backgroundWorkload, bool quietCpuComparable,
        bool smoothnessAllowed, bool decisionAllowed, bool pendingAba,
        bool stablePressure, bool telemetryFresh = true,
        bool recoveryPending = false, bool realIdle = true,
        string? engineReason = null)
    {
        if (!enabled) return "OFF: AI disabled";
        if (recoveryPending) return "RECOVER: interrupted AI changes";
        if (pendingAba) return "VERIFY: current A/B/A trial";
        if (!telemetryFresh) return "WAIT: fresh CPU/workload telemetry";
        if (!backgroundWorkload) return "ACTIVE: foreground monitoring; changes protected";
        if (!quietCpuComparable) return "WAIT: quiet CPU evidence";
        if (!smoothnessAllowed) return "WAIT: memory/CPU/disk recovery";
        // Distinguish actual user activity and lost AI evidence from an
        // arbitrary confidence threshold without allocating new status text.
        if (!realIdle) return "ACTIVE: background monitoring; changes protected";
        if (!decisionAllowed)
        {
            if (engineReason == "another experiment active")
                return "WAIT: another performance experiment";
            if (engineReason is "stale decision context" or
                "invalid decision metadata" or "invalid system telemetry")
                return "WAIT: fresh trustworthy AI decision";
            if (engineReason == "current AI self telemetry unavailable")
                return "WAIT: AI self-health telemetry";
            return "WAIT: workload confidence";
        }
        if (!stablePressure) return "WAIT: stable 2-minute baseline";
        return "READY: safe measurable background candidate scan";
    }

    internal static DateTime? NextQuietEpochStart(
        DateTime? previousStartUtc, Queue<AiBackgroundLoadSample> samples,
        bool comparable, DateTime nowUtc)
    {
        if (!comparable || samples.Count == 0 || nowUtc.Kind != DateTimeKind.Utc)
            return null;
        // The sampler can discard a sequence for a missing interval or a
        // backwards clock; a fresh first sample ends the previous epoch.
        DateTime earliestSampleUtc = samples.Peek().Utc;
        if (earliestSampleUtc.Kind != DateTimeKind.Utc ||
            (previousStartUtc is DateTime previous && previous.Kind != DateTimeKind.Utc) ||
            previousStartUtc is null || previousStartUtc > nowUtc ||
            earliestSampleUtc > nowUtc || earliestSampleUtc < previousStartUtc ||
            (samples.Count == 1 && earliestSampleUtc == nowUtc))
            return nowUtc;
        // Validate the entire short queue: a clock jump need not be on the
        // last sample. Exit as soon as an invalid timestamp is discovered.
        DateTime precedingUtc = earliestSampleUtc;
        bool firstSample = true;
        foreach (AiBackgroundLoadSample sample in samples)
        {
            DateTime sampleUtc = sample.Utc;
            // A repeated timestamp is not a second independent quiet sample.
            // Do not credit its epoch toward a shorter candidate observation.
            if (!double.IsFinite(sample.LoadPercent) || sample.LoadPercent < 0 ||
                sample.LoadPercent > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent ||
                sampleUtc.Kind != DateTimeKind.Utc || sampleUtc > nowUtc ||
                (firstSample ? sampleUtc < precedingUtc :
                    sampleUtc - precedingUtc < AiBackgroundPerformancePolicy.MinimumSampleGap ||
                    sampleUtc - precedingUtc > AiBackgroundPerformancePolicy.MaximumSampleGap))
                return nowUtc;
            precedingUtc = sampleUtc;
            firstSample = false;
        }
        // Imported or paused sampler state must not count as a continuous
        // quiet epoch when its latest measurement is already stale.
        if (nowUtc - precedingUtc > AiBackgroundPerformancePolicy.MaximumSampleGap)
            return nowUtc;
        return previousStartUtc;
    }

    // The first eligible scan already has a stable, time-spaced quiet CPU
    // baseline. Credit at most 60s of that SAME uninterrupted epoch, while
    // requiring at least 30s of fresh per-target eligibility before a write.
    // A prior observation from a different quiet epoch must never carry over.
    internal static DateTime CandidateObservationStart(
        DateTime nowUtc, DateTime quietEpochStartUtc, DateTime? previousStartUtc,
        int configuredSeconds, string kind, AiSystemRuntimeItemState state)
    {
        // Only a verified UTC quiet epoch may credit prior idle time.
        // Local/unspecified or future epochs could shorten eligibility
        // for a service/task on an imported or clock-skewed journal.
        if (nowUtc.Kind != DateTimeKind.Utc ||
            quietEpochStartUtc.Kind != DateTimeKind.Utc ||
            quietEpochStartUtc > nowUtc)
            return nowUtc;
        if (previousStartUtc is DateTime previous &&
            previous.Kind == DateTimeKind.Utc &&
            previous >= quietEpochStartUtc && previous <= nowUtc)
            return previous;

        int requiredSeconds = CandidateObservationSeconds(
            configuredSeconds, kind, state, nowUtc);
        if (configuredSeconds != StandardObservationSeconds || requiredSeconds <= 30)
            return nowUtc;
        double availableQuietSeconds = (nowUtc - quietEpochStartUtc).TotalSeconds;
        double creditSeconds = Math.Min(Math.Min(60, requiredSeconds - 30),
            Math.Max(0, availableQuietSeconds));
        return nowUtc.AddSeconds(-creditSeconds);
    }

    internal static TimeSpan DiscoveryInterval(string kind, double lastScanMs)
    {
        bool service = string.Equals(kind, "service", StringComparison.OrdinalIgnoreCase);
        bool task = string.Equals(kind, "task", StringComparison.OrdinalIgnoreCase);
        if (lastScanMs < 0 || lastScanMs >= 50 || !double.IsFinite(lastScanMs))
            return service ? TimeSpan.FromMinutes(2) :
                   task ? TimeSpan.FromMinutes(3) :
                   TimeSpan.FromMinutes(5);

        // Very cheap measured discovery can run more frequently without
        // relaxing the independent A/B/A, real-idle or workload mutation gates.
        // Never accelerate unknown kinds or scans approaching the CPU budget.
        if (lastScanMs > 0 && lastScanMs <= 5)
            return service ? TimeSpan.FromSeconds(30) :
                   task ? TimeSpan.FromSeconds(45) :
                   TimeSpan.FromMinutes(5);
        return service ? TimeSpan.FromSeconds(60) :
               task ? TimeSpan.FromSeconds(90) :
               TimeSpan.FromMinutes(5);
    }

    internal static bool HasRecentProvenBenefit(AiSystemRuntimeItemState state, DateTime nowUtc) =>
        // A driver has no owned service/task A/B/A verification phase;
        // imported CPU benefit fields must never mark it as a proven win.
        (string.Equals(state.Kind, "service", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(state.Kind, "task", StringComparison.OrdinalIgnoreCase)) &&
        !state.Managed &&
        !state.Sleeping &&
        // Imported timestamps without UTC provenance cannot establish proof;
        // reject them before blocked-reason and demand classification.
        nowUtc.Kind == DateTimeKind.Utc &&
        state.LastBenefitVerifiedUtc is DateTime proofTime &&
        proofTime.Kind == DateTimeKind.Utc &&
        proofTime <= nowUtc &&
        nowUtc - proofTime <= ProvenBenefitLifetime &&
        state.Rollbacks is >= 0 and < 2 &&
        state.FailedBenefitTrials == 0 &&
        // A historical CPU win must not prioritize a presently protected or
        // unavailable Windows resource ahead of valid candidates.
        !string.IsNullOrWhiteSpace(state.Id) &&
        AiMeasuredOptimizationPolicy.CredibleHistoricalCpuProof(state) &&
        state.LastVerifiedBenefitAbsolute >= MinimumProvenBenefit &&
        // Cheap proof checks reject corrupt/imported evidence before
        // blocked-reason classification. The safety gates are unchanged.
        AiRuntimeBlockedSummaryPolicy.Classify(state.BlockedReason) == AiRuntimeBlockedSummaryPolicy.Category.None &&
        // Old pre-percentage journals recorded absolute A/B/A CPU evidence.
        // Preserve their ranking without granting any mutation privileges.
        // A former service win must not jump ahead of other work after a
        // device/OS activation disproves continued optional idleness.
        // Cheap cooldown validation precedes the potentially expensive
        // optional-service demand classifier; eligibility is unchanged.
        (state.CooldownUntilUtc is not DateTime until ||
         (until.Kind == DateTimeKind.Utc && until <= nowUtc)) &&
        (!string.Equals(state.Kind, "service", StringComparison.OrdinalIgnoreCase) ||
         !AiOptionalServiceDemandPolicy.HasRecentDemand(state, nowUtc));

    internal static int[] OrderedIndices(
        string kind, string[] candidateIds, int rotationCursor,
        IReadOnlyDictionary<string, AiSystemRuntimeItemState> states, DateTime nowUtc)
    {
        int count = candidateIds.Length;
        if (count == 0)
            return Array.Empty<int>();
        // A single candidate has no ordering decision: skip owner-key creation,
        // dictionary lookup and the temporary approved-state array.
        if (count == 1)
            return new[] { 0 };
        var order = new int[count];
        FillRotation(order, rotationCursor);
        if (states.Count == 0)
            return order;
        bool serviceKind = string.Equals(kind, "service", StringComparison.OrdinalIgnoreCase);
        if (!serviceKind && !string.Equals(kind, "task", StringComparison.OrdinalIgnoreCase))
            return order;
        // Non-UTC timestamps cannot authorize historical A/B/A proof.
        // Keep the rotating order without probing the journal for candidates.
        if (nowUtc.Kind != DateTimeKind.Utc)
            return order;
        // Cache approved candidate states for the ranking pass, avoiding
        // repeated journal lookups while retaining stable rotation.
        // Allocate the per-candidate evidence snapshot only for a valid proof.
        AiSystemRuntimeItemState?[]? approved = null;
        AiSystemRuntimeItemState? firstApproved = null;
        int firstApprovedIndex = -1;
        // Canonicalize only the two allowlisted kinds, retaining case-insensitive
        // journal matching and avoiding a dynamically allocated kind prefix.
        string ownerPrefix = serviceKind ? "service|" : "task|";
        // Determine the strongest confirmed CPU saving before RAM tie-breaks;
        // no chain of RAM choices may progressively lower the CPU bar.
        double strongestCpu = 0;
        bool hasFailedTrials = false;
        for (int i = 0; i < count; i++)
        {
            string id = candidateIds[i];
            // Blank IDs can never have valid measured proof; avoid creating
            // owner keys or probing the journal for malformed allowlist rows.
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (TryGetCandidateState(states, ownerPrefix, id, out var candidate) &&
                candidate is not null &&
                string.Equals(candidate.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                if (!candidate.Managed && !candidate.Sleeping && candidate.FailedBenefitTrials > 0)
                    hasFailedTrials = true;
                if (!HasRecentProvenBenefit(candidate, nowUtc))
                    continue;
                if (firstApprovedIndex < 0)
                {
                    firstApprovedIndex = i;
                    firstApproved = candidate;
                }
                else
                {
                    if (approved is null)
                    {
                        approved = new AiSystemRuntimeItemState?[count];
                        approved[firstApprovedIndex] = firstApproved;
                    }
                    approved[i] = candidate;
                }
                strongestCpu = Math.Max(strongestCpu, candidate.LastVerifiedBenefitAbsolute);
            }
        }
        if (firstApprovedIndex < 0)
            return hasFailedTrials
                ? RankUnprovenAfterFailedTrials(order, 0, candidateIds, ownerPrefix, kind, states)
                : order;
        if (approved is null)
        {
            // A single proven candidate needs no temporary reference array.
            // Rotation is consecutive modulo count. Compute the location
            // directly instead of scanning every candidate a second time.
            int origin = order[0];
            int singleOffset = firstApprovedIndex >= origin
                ? firstApprovedIndex - origin
                : count - (origin - firstApprovedIndex);
            if (singleOffset > 0)
                (order[0], order[singleOffset]) = (order[singleOffset], order[0]);
            return hasFailedTrials
                ? RankUnprovenAfterFailedTrials(order, 1, candidateIds, ownerPrefix, kind, states)
                : order;
        }
        int preferredOffset = -1;
        double bestBenefit = 0;
        double bestRamMiB = 0;
        int bestRollbacks = int.MaxValue;
        long bestProofAgeTicks = long.MaxValue;
        for (int offset = 0; offset < count; offset++)
        {
            int index = order[offset];
            AiSystemRuntimeItemState? state = approved[index];
            if (state is null)
                continue;
            double cpuBenefit = state.LastVerifiedBenefitAbsolute;
            // Reject weaker CPU evidence before inspecting optional RAM data.
            // RAM can never promote a candidate outside the <=0.05pp CPU band.
            if (cpuBenefit < strongestCpu - 0.05)
                continue;
            double ramMiB = AiPassiveMemoryTrialPolicy.CredibleHistoricalAvailableRamBenefit(
                state.LastVerifiedAvailableRamBenefitMiB)
                ? state.LastVerifiedAvailableRamBenefitMiB!.Value : 0;
            // Learn from experiment outcomes, not just the size of the last
            // isolated CPU/RAM win. Inside the tight 0.05pp CPU band prefer a
            // candidate that has never needed rollback, then the most recent
            // verified instant rather than a rounded day. For near-equal CPU
            // proofs, a one-hour-old win beats a 23-hour-old win even when
            // the older passive RAM observation is larger.
            // HasRecentProvenBenefit established a past UTC instant.
            long proofAgeTicks = (nowUtc - state.LastBenefitVerifiedUtc!.Value).Ticks;
            bool moreUsefulEvidence = preferredOffset < 0 ||
                state.Rollbacks < bestRollbacks ||
                (state.Rollbacks == bestRollbacks &&
                 (proofAgeTicks < bestProofAgeTicks ||
                  (proofAgeTicks == bestProofAgeTicks &&
                   (ramMiB > bestRamMiB ||
                    (ramMiB == bestRamMiB && cpuBenefit > bestBenefit)))));
            if (moreUsefulEvidence)
            {
                bestBenefit = cpuBenefit;
                bestRamMiB = ramMiB;
                bestRollbacks = state.Rollbacks;
                bestProofAgeTicks = proofAgeTicks;
                preferredOffset = offset;
            }
        }

        // One proven win leads; the remaining candidates retain rotating
        // coverage. We never skip or duplicate the unexplored choices.
        if (preferredOffset > 0)
            (order[0], order[preferredOffset]) = (order[preferredOffset], order[0]);
        return hasFailedTrials
            ? RankUnprovenAfterFailedTrials(order, 1, candidateIds, ownerPrefix, kind, states)
            : order;
    }

    // Once a failed candidate's retry cooldown expires it may be explored
    // again, but it shouldn't precede never-failed alternatives. Keep the
    // winning proven candidate (if any) at index zero; rotate equally learned
    // candidates as before, never removing any service/task from discovery.
    // No extra journal scan or allocation for the usual zero-failure case.
    private static int[] RankUnprovenAfterFailedTrials(
        int[] order, int firstUnprovenOffset, string[] candidateIds,
        string ownerPrefix, string kind,
        IReadOnlyDictionary<string, AiSystemRuntimeItemState> states)
    {
        if (order.Length - firstUnprovenOffset < 2) return order;
        Span<byte> failureCounts = order.Length <= 128
            ? stackalloc byte[order.Length]
            : new byte[order.Length];
        for (int offset = firstUnprovenOffset; offset < order.Length; offset++)
        {
            string id = candidateIds[order[offset]];
            if (!string.IsNullOrWhiteSpace(id) &&
                TryGetCandidateState(states, ownerPrefix, id, out var state) &&
                state is not null && !state.Managed && !state.Sleeping &&
                string.Equals(state.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(state.Id, id, StringComparison.OrdinalIgnoreCase))
                failureCounts[offset] = (byte)Math.Clamp(state.FailedBenefitTrials, 0, 3);
        }
        // Stable insertion: zero failures first, then one, two and three.
        // Preserve the original rotating order for equal learning evidence.
        for (int i = firstUnprovenOffset + 1; i < order.Length; i++)
        {
            int current = order[i];
            byte failures = failureCounts[i];
            int j = i;
            while (j > firstUnprovenOffset && failureCounts[j - 1] > failures)
            {
                order[j] = order[j - 1];
                failureCounts[j] = failureCounts[j - 1];
                j--;
            }
            order[j] = current;
            failureCounts[j] = failures;
        }
        return order;
    }

    private static bool TryGetCandidateState(
        IReadOnlyDictionary<string, AiSystemRuntimeItemState> states,
        string ownerPrefix, string id, out AiSystemRuntimeItemState candidate)
    {
        // A bounded stack buffer avoids a per-candidate owner-key allocation.
        // Preserve the dictionary's own equality semantics and keep the
        // fallback for comparers without alternate span lookup support.
        if (id.Length <= 512 - ownerPrefix.Length &&
            states is Dictionary<string, AiSystemRuntimeItemState> dictionary &&
            dictionary.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
        {
            Span<char> key = stackalloc char[ownerPrefix.Length + id.Length];
            ownerPrefix.AsSpan().CopyTo(key);
            id.AsSpan().CopyTo(key[ownerPrefix.Length..]);
            return lookup.TryGetValue(key, out candidate!);
        }
        // Preserve custom dictionaries/comparers and unusually long IDs.
        return states.TryGetValue(string.Concat(ownerPrefix, id), out candidate!);
    }

    private static void FillRotation(int[] order, int rotationCursor)
    {
        // Only called for candidate sets with at least two entries.
        int count = order.Length;
        int index = AiMaintenanceCandidatePolicy.IndexAt(rotationCursor, 0, count);
        for (int offset = 0; offset < count; offset++)
        {
            order[offset] = index;
            if (++index == count)
                index = 0;
        }
    }

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var samples = new Queue<AiBackgroundLoadSample>();
        for (int i = 0; i < 4; i++)
            AiBackgroundPerformancePolicy.AddSample(
                samples, comparable: true, 1.5 + i * 0.05, now.AddSeconds(i * 40));
        // Live four-sample median is allocation-free and matches even/odd semantics.
        if (Math.Abs(AiBackgroundPerformancePolicy.MedianLoad(samples) - 1.575) > 0.00001 ||
            !double.IsNaN(AiBackgroundPerformancePolicy.MedianLoad(new Queue<AiBackgroundLoadSample>())))
            throw new InvalidOperationException("Quiet CPU median calculation regressed.");
        bool stableEarlier = AiBackgroundPerformancePolicy.HasStableMeasuredPressure(samples);
        var less = new Queue<AiBackgroundLoadSample>(samples);
        less.Dequeue();
        if (!stableEarlier || AiBackgroundPerformancePolicy.HasStableMeasuredPressure(less))
            throw new InvalidOperationException("Faster quiet background window lost stable/noisy evidence.");

        // The actual workload classifier may identify stable work done by
        // a background process while the user is not technically idle.
        // Do not reject that 3-5% CPU evidence; NEVER grant this to an
        // interactive, unknown or gaming workload.
        AiWorkloadClass classified = AiWorkloadContextPolicy.Classify(
            false, false, "chrome", "SearchIndexer", 4.2, 4.4);
        if (AiWorkloadContextPolicy.Classify(false, false, null, "SearchIndexer", 38, 42)
                != AiWorkloadClass.Unknown ||
            AiWorkloadContextPolicy.Classify(false, false, null, null, 0, 2)
                != AiWorkloadClass.Unknown ||
            AiWorkloadContextPolicy.Classify(false, true, null, null, 0, 2)
                != AiWorkloadClass.Idle ||
            AiWorkloadContextPolicy.Classify(true, false, null, null, 0, 30)
                != AiWorkloadClass.Game)
            throw new InvalidOperationException("Unknown foreground must not imply idle/background automation.");
        if (AiWorkloadContextPolicy.Classify(false, false, "chrome", "SearchIndexer", double.NaN, 4)
                != AiWorkloadClass.Unknown ||
            AiWorkloadContextPolicy.Classify(false, false, "chrome", "SearchIndexer", 5, double.NaN)
                != AiWorkloadClass.Unknown ||
            AiWorkloadContextPolicy.Classify(false, false, "chrome", "SearchIndexer", -1, 4)
                != AiWorkloadClass.Unknown ||
            AiWorkloadContextPolicy.Classify(false, false, "chrome", "SearchIndexer", 5, 101)
                != AiWorkloadClass.Unknown)
            throw new InvalidOperationException("Invalid CPU telemetry permitted AI background trial.");
        if (AiWorkloadContextPolicy.Classify(false, false, "Editor.exe-helper", "Editor-helper", 4, 4)
                != AiWorkloadClass.Background ||
            AiWorkloadContextPolicy.Classify(false, false, "Editor.EXE", "Editor", 4, 4)
                != AiWorkloadClass.Interactive)
            throw new InvalidOperationException("Process identity normalization confused embedded .exe with suffix.");
        AiSharedDecisionContext unknown = AiSharedDecisionContextService.Update(
            AiWorkloadClass.Unknown, false, false, 40, null, now);
        if (unknown.Confidence != 0 || unknown.AllowBackgroundMutation ||
            unknown.Reason != "unknown workload")
            throw new InvalidOperationException("Unknown AI workload reported actionable confidence.");
        var backgroundEvidence = new Queue<AiBackgroundLoadSample>();
        for (int i = 0; i < 4; i++)
        {
            double cpu = 3.8 + i * 0.1;
            AiBackgroundPerformancePolicy.AddSample(backgroundEvidence,
                AiBackgroundPerformancePolicy.IsComparableQuietSample(
                    false, false, cpu, classified),
                cpu, now.AddSeconds(i * 40));
        }
        if (classified != AiWorkloadClass.Background ||
            !AiBackgroundPerformancePolicy.HasStableMeasuredPressure(backgroundEvidence) ||
            !AiBackgroundPerformancePolicy.IsComparableQuietSample(
                false, false, 4.5, AiWorkloadClass.Background) ||
            AiBackgroundPerformancePolicy.IsComparableQuietSample(false, false, 4.5) ||
            AiBackgroundPerformancePolicy.IsComparableQuietSample(
                false, false, 4.5, AiWorkloadClass.Interactive) ||
            AiBackgroundPerformancePolicy.IsComparableQuietSample(
                false, false, 4.5, AiWorkloadClass.Unknown) ||
            AiBackgroundPerformancePolicy.IsComparableQuietSample(
                true, false, 4.5, AiWorkloadClass.Background) ||
            AiBackgroundPerformancePolicy.IsComparableQuietSample(
                false, false, 5.1, AiWorkloadClass.Background) ||
            AiBackgroundPerformancePolicy.IsComparableQuietSample(
                false, false, double.NaN, AiWorkloadClass.Background) ||
            WindowStatus(true, true, true, true, true, false, true)
                != "READY: safe measurable background candidate scan" ||
            WindowStatus(true, true, true, true, true, false, true, false)
                != "WAIT: fresh CPU/workload telemetry" ||
            WindowStatus(false, true, true, true, true, false, true, false)
                != "OFF: AI disabled" ||
            !WindowStatus(true, false, true, true, true, false, true).StartsWith("ACTIVE:", StringComparison.Ordinal) ||
            !WindowStatus(true, true, true, false, true, false, true).Contains("recovery") ||
            !WindowStatus(true, true, true, true, true, false, false).Contains("baseline") ||
            !WindowStatus(true, true, true, true, true, true, true).StartsWith("VERIFY:", StringComparison.Ordinal) ||
            !WindowStatus(false, true, true, true, true, false, true).StartsWith("OFF:", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Workload-aware 3-5% background opportunities or truthful gate reasons regressed.");

        var noise = new Queue<AiBackgroundLoadSample>();
        foreach ((int seconds, double load) in new[] { (0, 0.8), (40, 0.9), (80, 1.0), (120, 3.4) })
            AiBackgroundPerformancePolicy.AddSample(noise, true, load, now.AddSeconds(seconds));
        if (AiBackgroundPerformancePolicy.HasStableMeasuredPressure(noise) ||
            ObservationSeconds(180, "service") != 120 ||
            ObservationSeconds(180, "task") != 90 ||
            ObservationSeconds(180, "driver") != 180 ||
            ObservationSeconds(240, "task") != 240 ||
            DiscoveryInterval("service", 5) != TimeSpan.FromSeconds(30) ||
            DiscoveryInterval("task", 5) != TimeSpan.FromSeconds(45) ||
            DiscoveryInterval("service", 10) != TimeSpan.FromSeconds(60) ||
            DiscoveryInterval("task", 10) != TimeSpan.FromSeconds(90) ||
            DiscoveryInterval("task", 70) != TimeSpan.FromMinutes(3) ||
            DiscoveryInterval("service", -1) != TimeSpan.FromMinutes(2) ||
            DiscoveryInterval("task", double.NaN) != TimeSpan.FromMinutes(3))
            throw new InvalidOperationException("AI opportunity policy exceeded conservative scan limits.");

        string[] ids = { "A", "B", "C" };
        var oldWin = new AiSystemRuntimeItemState("service", "B", false, false, "", null,
            null, null, null, false, 0, "verified",
            LastVerifiedBenefitAbsolute: 0.35, LastBenefitVerifiedUtc: now.AddDays(-1));
        var states = new Dictionary<string, AiSystemRuntimeItemState> {
            ["service|B"] = oldWin
        };
        if (OrderedIndices("service", Array.Empty<string>(), 0, states, now).Length != 0)
            throw new InvalidOperationException("Empty candidates incorrectly scheduled.");
        int[] mismatched = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState> {
                ["service|B"] = oldWin with { Kind = "task" }
            }, now);
        if (mismatched[0] != 2)
            throw new InvalidOperationException("Wrong-kind historic trial was prioritized.");
        int[] prioritized = OrderedIndices("service", ids, 2, states, now);
        var memoryWin = oldWin with { Id = "A", LastVerifiedBenefitAbsolute = 0.33,
            LastVerifiedAvailableRamBenefitMiB = 220 };
        var tieStates = new Dictionary<string, AiSystemRuntimeItemState>(states)
        {
            ["service|A"] = memoryWin
        };
        int[] memoryTie = OrderedIndices("service", ids, 2, tieStates, now);
        int[] chainedRam = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState>(tieStates)
            {
                ["service|C"] = memoryWin with {
                    Id = "C", LastVerifiedBenefitAbsolute = 0.29,
                    LastVerifiedAvailableRamBenefitMiB = 900 }
            }, now);
        int[] staleRam = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState>(states)
            {
                ["service|A"] = memoryWin with { LastBenefitVerifiedUtc = now.AddDays(-8) }
            }, now);
        int[] weakerCpu = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState>(states)
            {
                ["service|A"] = memoryWin with { LastVerifiedBenefitAbsolute = 0.23 }
            }, now);
        // AI learning: a fresh, repeatable 0.33pp win beats an almost equal
        // but five-day-old 0.35pp win with a much larger RAM observation.
        var newerProof = memoryWin with {
            LastVerifiedBenefitAbsolute = 0.33,
            LastVerifiedAvailableRamBenefitMiB = null,
            LastBenefitVerifiedUtc = now.AddHours(-2)
        };
        var olderBigRam = oldWin with {
            LastBenefitVerifiedUtc = now.AddDays(-5),
            LastVerifiedAvailableRamBenefitMiB = 900
        };
        int[] freshest = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState> {
                ["service|A"] = newerProof, ["service|B"] = olderBigRam
            }, now);
        // Prior recovery is weaker learning evidence than zero rollbacks,
        // even when the absolute CPU savings are nearly identical.
        int[] repeatable = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState> {
                ["service|A"] = memoryWin with { LastVerifiedAvailableRamBenefitMiB = null },
                ["service|B"] = oldWin with { Rollbacks = 1,
                    LastVerifiedAvailableRamBenefitMiB = 900 }
            }, now);
        // A substantial observed CPU difference (>0.05pp) still dominates:
        // do not let recency or a favorable rollback count erase the delta.
        int[] clearCpuWin = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState> {
                ["service|A"] = newerProof with { LastVerifiedBenefitAbsolute = 0.23 },
                ["service|B"] = olderBigRam with { Rollbacks = 1 }
            }, now);
        // Rounded whole days were too coarse: the older 20-hour CPU proof
        // must not outrank the 1-hour proof just because of its larger RAM.
        int[] hourlyFresh = OrderedIndices("service", ids, 2,
            new Dictionary<string, AiSystemRuntimeItemState> {
                ["service|A"] = newerProof with { LastBenefitVerifiedUtc = now.AddHours(-1) },
                ["service|B"] = olderBigRam with { LastBenefitVerifiedUtc = now.AddHours(-20) }
            }, now);
        // Explore never-failed service/task candidates ahead of known
        // no-benefit A/B/A trials after their cooldown, but keep every ID.
        // Original rotating order for cursor 1: B,C,D,A.
        string[] exploreIds = { "A", "B", "C", "D" };
        var failedServiceStates = new Dictionary<string, AiSystemRuntimeItemState> {
            ["service|B"] = oldWin with { FailedBenefitTrials = 2, LastBenefitVerifiedUtc = null },
            ["service|D"] = oldWin with { Id = "D", FailedBenefitTrials = 1,
                LastBenefitVerifiedUtc = null }
        };
        int[] exploreOrder = OrderedIndices("service", exploreIds, 1, failedServiceStates, now);
        int[] exploreTaskOrder = OrderedIndices("task", exploreIds, 1,
            new Dictionary<string, AiSystemRuntimeItemState> {
                ["task|B"] = failedServiceStates["service|B"] with { Kind = "task" },
                ["task|D"] = failedServiceStates["service|D"] with { Kind = "task" }
            }, now);
        int[] equalFailures = OrderedIndices("service", exploreIds, 1,
            new Dictionary<string, AiSystemRuntimeItemState>(failedServiceStates) {
                ["service|B"] = failedServiceStates["service|B"] with { FailedBenefitTrials = 1 }
            }, now);
        int[] winnerAndExplore = OrderedIndices("service", exploreIds, 1,
            new Dictionary<string, AiSystemRuntimeItemState>(failedServiceStates) {
                ["service|C"] = oldWin with { Id = "C",
                    LastBenefitVerifiedUtc = now.AddHours(-1) }
            }, now);
        int[] foreignHistory = OrderedIndices("service", exploreIds, 1,
            new Dictionary<string, AiSystemRuntimeItemState> {
                ["service|B"] = failedServiceStates["service|B"] with { Kind = "task" },
                ["service|D"] = failedServiceStates["service|D"] with { Id = "other" }
            }, now);
        if (hourlyFresh[0] != 0 ||
            !exploreOrder.AsSpan().SequenceEqual(new[] { 2, 0, 3, 1 }) ||
            !exploreTaskOrder.AsSpan().SequenceEqual(new[] { 2, 0, 3, 1 }) ||
            !equalFailures.AsSpan().SequenceEqual(new[] { 2, 0, 1, 3 }) ||
            !winnerAndExplore.AsSpan().SequenceEqual(new[] { 2, 0, 3, 1 }) ||
            !foreignHistory.AsSpan().SequenceEqual(new[] { 1, 2, 3, 0 }) ||
            freshest[0] != 0 || repeatable[0] != 0 || clearCpuWin[0] != 1 ||
            memoryTie[0] != 0 || chainedRam[0] != 0 ||
            staleRam[0] != 1 || weakerCpu[0] != 1 ||
            prioritized.Length != 3 || prioritized[0] != 1 ||
            prioritized[1] == prioritized[2] ||
            !HasRecentProvenBenefit(oldWin, now) ||
            CandidateObservationSeconds(180, "service", oldWin, now) != 75 ||
            CandidateObservationSeconds(180, "task", oldWin with { Kind = "task" }, now) != 60 ||
            CandidateObservationSeconds(180, "service", oldWin with {
                LastBenefitVerifiedUtc = now.AddDays(-2) }, now) != 90 ||
            CandidateObservationSeconds(180, "service", oldWin with {
                LastBenefitVerifiedUtc = now.AddDays(-5) }, now) != 105 ||
            CandidateObservationSeconds(180, "task", oldWin with {
                Kind = "task", LastBenefitVerifiedUtc = now.AddDays(-2) }, now) != 70 ||
            CandidateObservationSeconds(180, "task", oldWin with {
                Kind = "task", LastBenefitVerifiedUtc = now.AddDays(-5) }, now) != 80 ||
            CandidateObservationSeconds(180, "task", oldWin, now) != 90 ||
            CandidateObservationSeconds(240, "task", oldWin, now) != 240 ||
            CandidateObservationSeconds(180, "driver", oldWin, now) != 180 ||
            CandidateObservationSeconds(180, "service",
                oldWin with { LastBenefitVerifiedUtc = now.AddDays(-8) }, now) != 120 ||
            HasRecentProvenBenefit(oldWin with { LastBenefitVerifiedUtc = now.AddDays(-8) }, now) ||
            HasRecentProvenBenefit(oldWin with { FailedBenefitTrials = 2 }, now) ||
            HasRecentProvenBenefit(oldWin with { Id = " " }, now) ||
            HasRecentProvenBenefit(oldWin with { LastVerifiedBenefitAbsolute = double.PositiveInfinity }, now) ||
            HasRecentProvenBenefit(oldWin with { CooldownUntilUtc = now.AddHours(2) }, now) ||
            HasRecentProvenBenefit(oldWin with { CooldownUntilUtc =
                DateTime.SpecifyKind(now.AddHours(-1), DateTimeKind.Unspecified) }, now) ||
            HasRecentProvenBenefit(oldWin with { CooldownUntilUtc =
                DateTime.SpecifyKind(now.AddHours(-1), DateTimeKind.Local) }, now))
            throw new InvalidOperationException(
                $"AI learned benefit priority ignored cooldown or starved targets: " +
                $"memoryTie={memoryTie[0]} chainedRam={chainedRam[0]} staleRam={staleRam[0]} " +
                $"weakerCpu={weakerCpu[0]} prioritized=[{string.Join(',', prioritized)}] " +
                $"serviceObs={CandidateObservationSeconds(180, "service", oldWin, now)} " +
                $"taskObs={CandidateObservationSeconds(180, "task", oldWin with { Kind = "task" }, now)} " +
                $"mismatchTaskObs={CandidateObservationSeconds(180, "task", oldWin, now)}");

        // A stable quiet epoch can contribute limited observation time to
        // a newly seen candidate, but each target needs fresh eligibility.
        DateTime quietStarted = now.AddMinutes(-2);
        var epochSamples = new Queue<AiBackgroundLoadSample>();
        AiBackgroundPerformancePolicy.AddSample(epochSamples, true, 1.3, quietStarted);
        DateTime? epoch = NextQuietEpochStart(null, epochSamples, true, quietStarted);
        AiBackgroundPerformancePolicy.AddSample(epochSamples, true, 1.35,
            quietStarted.AddSeconds(30));
        DateTime? unchanged = NextQuietEpochStart(epoch, epochSamples, true,
            quietStarted.AddSeconds(30));
        var freshEpoch = new Queue<AiBackgroundLoadSample>();
        AiBackgroundPerformancePolicy.AddSample(freshEpoch, true, 1.4, now);
        DateTime? afterGap = NextQuietEpochStart(unchanged, freshEpoch, true, now);
        // Proven task observation must use a task-owned history record; a
        // service win is deliberately ineligible for the shortened task path.
        var provenTask = oldWin with { Kind = "task" };
        DateTime taskReady = CandidateObservationStart(now, quietStarted, null,
            180, "task", provenTask);
        DateTime serviceReady = CandidateObservationStart(now, quietStarted, null,
            180, "service", oldWin);
        if (epoch != quietStarted || unchanged != quietStarted ||
            afterGap != now ||
            NextQuietEpochStart(afterGap, freshEpoch, false, now) != null ||
            NextQuietEpochStart(epoch, epochSamples, true,
                DateTime.SpecifyKind(now, DateTimeKind.Unspecified)) != null ||
            NextQuietEpochStart(DateTime.SpecifyKind(quietStarted, DateTimeKind.Local),
                epochSamples, true, now) != now ||
            CandidateObservationStart(now, quietStarted, null, 180, "service",
                oldWin) != now.AddSeconds(-45) ||
            taskReady != now.AddSeconds(-30) ||
            serviceReady != now.AddSeconds(-45) ||
            CandidateObservationStart(now, quietStarted, taskReady, 180, "task",
                provenTask) != taskReady ||
            CandidateObservationStart(now, quietStarted,
                DateTime.SpecifyKind(taskReady, DateTimeKind.Unspecified), 180,
                "task", provenTask) != now.AddSeconds(-30) ||
            CandidateObservationStart(now, quietStarted,
                DateTime.SpecifyKind(taskReady, DateTimeKind.Local), 180,
                "task", provenTask) != now.AddSeconds(-30) ||
            CandidateObservationStart(now, now.AddSeconds(-10), taskReady, 180,
                "task", provenTask) != now.AddSeconds(-10) ||
            CandidateObservationStart(now, quietStarted, now.AddDays(-1), 180,
                "task", provenTask) != taskReady ||
            CandidateObservationStart(now, quietStarted, null, 240, "task",
                oldWin) != now ||
            CandidateObservationStart(now, now.AddMinutes(1), null, 180,
                "task", oldWin) != now ||
            CandidateObservationStart(now, DateTime.SpecifyKind(quietStarted, DateTimeKind.Unspecified),
                null, 180, "task", provenTask) != now ||
            CandidateObservationStart(now, DateTime.SpecifyKind(quietStarted, DateTimeKind.Local),
                null, 180, "service", oldWin) != now ||
            CandidateObservationStart(DateTime.SpecifyKind(now, DateTimeKind.Unspecified),
                quietStarted, null, 180, "task", provenTask).Kind != DateTimeKind.Unspecified)
            throw new InvalidOperationException(
                "AI quiet-epoch observation credit crossed a gap or skipped fresh eligibility.");

        return "AI_ACTIVE_OPPORTUNITY_SMOKE_OK quietSamples=4 quietWindow=2m " +
            "taskObservation=90s serviceObservation=120s fasterDiscovery=true " +
            "provenBenefitPriority=true comparableRamTieBreaker=true " +
            "freshEvidencePreferred=true hourlyRecency=true rollbackHistoryLearned=true " +
            "failedTrialsDeprioritized=true explorationRotationPreserved=true " +
            "cpuBandPreserved=true adaptiveEvidenceAge1d3d7d=true " +
            "provenRetrialService75sTask60s=true " +
            "quietEpochCreditBounded=true interruptedQuietResets=true " +
            "customSettingsKept=true noRelaxedAba=true writes=0";
    }
}