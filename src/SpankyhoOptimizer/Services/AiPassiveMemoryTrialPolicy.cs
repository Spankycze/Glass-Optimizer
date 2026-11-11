using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Passive physical-memory evidence attached to a CPU A/B/A experiment.
/// Free physical RAM is not a process allocation or an OS-wide "RAM saved"
/// counter: report it only when candidate and restored-reference snapshots
/// are comparable, distinct, stable and return near the initial baseline.
/// Never use this signal alone to disable a Windows service or task.
/// </summary>
internal static class AiPassiveMemoryTrialPolicy
{
    internal const int RequiredPhaseSamples = AiMaintenanceFeedbackPolicy.RequiredCandidateSamples;
    internal const double MaximumPhaseSpreadMiB = 64;
    internal const double MaximumBaselineDriftMiB = 128;
    internal const double MinimumObservedDifferenceMiB = 128;
    // Do not keep a CPU-only "win" if the identical A/B/A experiment
    // reliably demonstrates a substantial physical RAM regression.
    internal const double MinimumConfirmedRegressionMiB = 256;
    // An imported "RAM benefit" larger than the service's own admissible
    // physical-memory range is corrupt. This is not a machine-wide saving.
    internal const double MaximumHistoricalAvailableRamBenefitMiB = 16777216;

    internal static bool CredibleHistoricalAvailableRamBenefit(double? value) =>
        value is double available && double.IsFinite(available) &&
        available >= MinimumObservedDifferenceMiB &&
        available <= MaximumHistoricalAvailableRamBenefitMiB;

    internal readonly record struct Evidence(bool Confirmed, double BenefitMiB, string Reason);
    internal readonly record struct Regression(bool Confirmed, double LostMiB, string Reason);

    internal static double? FreshAvailableMiB(
        AiSystemSmoothnessHealth? snapshot, DateTime nowUtc,
        DateTime? previousSampleUtc, DateTime? phaseStartedUtc)
    {
        if (snapshot is null || snapshot.State != "Normal" || snapshot.GameActive ||
            AiAutomaticPriorityTrialPolicy.DangerousAfterApplying(snapshot, nowUtc) ||
            nowUtc.Kind != DateTimeKind.Utc ||
            snapshot.UpdatedUtc.Kind != DateTimeKind.Utc ||
            (previousSampleUtc is DateTime prior && prior.Kind != DateTimeKind.Utc) ||
            (phaseStartedUtc is DateTime phase && phase.Kind != DateTimeKind.Utc) ||
            !double.IsFinite(snapshot.FreeRamGiB) || snapshot.FreeRamGiB <= 0 ||
            snapshot.FreeRamGiB > 16384 ||
            !AiSystemSmoothnessGuardService.IsFresh(
                snapshot.UpdatedUtc, nowUtc, TimeSpan.FromSeconds(45)) ||
            (previousSampleUtc is DateTime last &&
                (last > nowUtc || snapshot.UpdatedUtc - last < TimeSpan.FromSeconds(15))) ||
            // A future-dated snapshot must never be credited after a backwards
            // clock correction, even if a provider marks it fresh.
            snapshot.UpdatedUtc > nowUtc ||
            (phaseStartedUtc is DateTime started &&
                (started > nowUtc || snapshot.UpdatedUtc <= started)))
            return null;
        return snapshot.FreeRamGiB * 1024d;
    }

    internal static Evidence Evaluate(
        double? baselineMiB,
        int candidateCount, double candidateTotalMiB,
        double? candidateMinMiB, double? candidateMaxMiB,
        int referenceCount, double referenceTotalMiB,
        double? referenceMinMiB, double? referenceMaxMiB)
    {
        if (baselineMiB is not double baseline || !Valid(baseline) ||
            candidateCount != RequiredPhaseSamples ||
            referenceCount != RequiredPhaseSamples ||
            !Valid(candidateTotalMiB) || !Valid(referenceTotalMiB) ||
            !ValidRange(candidateMinMiB, candidateMaxMiB) ||
            !ValidRange(referenceMinMiB, referenceMaxMiB))
            return new(false, 0, "missing, duplicate or invalid physical-memory snapshots");

        double candidate = candidateTotalMiB / candidateCount;
        double reference = referenceTotalMiB / referenceCount;
        if (!Valid(candidate) || !Valid(reference) ||
            !SampleMeanConsistent(candidate, candidateCount,
                candidateMinMiB!.Value, candidateMaxMiB!.Value) ||
            !SampleMeanConsistent(reference, referenceCount,
                referenceMinMiB!.Value, referenceMaxMiB!.Value))
            return new(false, 0, "invalid or arithmetically impossible memory-sample mean");

        if (candidateMaxMiB - candidateMinMiB > MaximumPhaseSpreadMiB ||
            referenceMaxMiB - referenceMinMiB > MaximumPhaseSpreadMiB)
            return new(false, 0, "physical-memory samples vary too much");

        if (Math.Abs(reference - baseline) > MaximumBaselineDriftMiB)
            return new(false, 0, "restored reference RAM did not recover");

        double observed = candidate - reference;
        if (observed < MinimumObservedDifferenceMiB)
            return new(false, 0, "physical-memory difference is not conclusive");

        return new(true, Math.Round(observed, 1),
            "observed available physical RAM in this A/B/A trial (not permanently freed RAM)");
    }

    internal static Regression EvaluateRegression(
        double? baselineMiB,
        int candidateCount, double candidateTotalMiB,
        double? candidateMinMiB, double? candidateMaxMiB,
        int referenceCount, double referenceTotalMiB,
        double? referenceMinMiB, double? referenceMaxMiB)
    {
        // Absence of reliable RAM observations is NOT a measured regression.
        // Reuse the exact independent 3+3 physical snapshot requirements
        // used when reporting a positive RAM benefit.
        if (baselineMiB is not double baseline || !Valid(baseline) ||
            candidateCount != RequiredPhaseSamples ||
            referenceCount != RequiredPhaseSamples ||
            !Valid(candidateTotalMiB) || !Valid(referenceTotalMiB) ||
            !ValidRange(candidateMinMiB, candidateMaxMiB) ||
            !ValidRange(referenceMinMiB, referenceMaxMiB))
            return new(false, 0, "incomplete RAM evidence");

        double candidate = candidateTotalMiB / candidateCount;
        double reference = referenceTotalMiB / referenceCount;
        if (!Valid(candidate) || !Valid(reference) ||
            !SampleMeanConsistent(candidate, candidateCount,
                candidateMinMiB!.Value, candidateMaxMiB!.Value) ||
            !SampleMeanConsistent(reference, referenceCount,
                referenceMinMiB!.Value, referenceMaxMiB!.Value) ||
            candidateMaxMiB - candidateMinMiB > MaximumPhaseSpreadMiB ||
            referenceMaxMiB - referenceMinMiB > MaximumPhaseSpreadMiB ||
            Math.Abs(reference - baseline) > MaximumBaselineDriftMiB)
            return new(false, 0, "unstable or non-restored RAM evidence");

        double lost = reference - candidate;
        if (lost < MinimumConfirmedRegressionMiB)
            return new(false, 0, "no confirmed material RAM regression");

        return new(true, Math.Round(lost, 1),
            "candidate has substantially less available physical RAM than restored reference");
    }

    internal static bool OutweighsVerifiedCpuGain(
        AiMaintenanceFeedbackVerdict cpuVerdict, Regression memoryRegression) =>
        cpuVerdict.Ready && cpuVerdict.Accepted && memoryRegression.Confirmed;

    // An observed minimum and maximum each consume one of N actual samples.
    // Means outside these sharp bounds prove that the phase values were
    // mismatched, duplicated or loaded from a damaged journal.
    internal static bool SampleMeanConsistent(
        double mean, int count, double minimum, double maximum) =>
        count >= 2 && double.IsFinite(mean) &&
        double.IsFinite(minimum) && double.IsFinite(maximum) &&
        minimum > 0 && maximum >= minimum &&
        mean >= (maximum + (count - 1d) * minimum) / count - 0.000001 &&
        mean <= (minimum + (count - 1d) * maximum) / count + 0.000001;

    private static bool Valid(double value) =>
        double.IsFinite(value) && value > 0 && value <= 16777216;

    private static bool ValidRange(double? minimum, double? maximum) =>
        minimum is double min && maximum is double max &&
        Valid(min) && Valid(max) && min <= max;

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new AiSystemSmoothnessHealth(
            now.AddSeconds(-12), 2, 45, 8, "Normal", "", false);
        Evidence good = Evaluate(
            8192, 3, 3 * 8400, 8384, 8416,
            3, 3 * 8180, 8164, 8196);
        if (!good.Confirmed || good.BenefitMiB != 220 ||
            FreshAvailableMiB(snapshot, now, null, now.AddSeconds(-20)) != 8192 ||
            FreshAvailableMiB(snapshot, now, now.AddSeconds(-12), null) != null ||
            FreshAvailableMiB(snapshot, now, null, now.AddSeconds(-10)) != null ||
            FreshAvailableMiB(snapshot with { State = "Memory pressure" }, now, null, null) != null ||
            FreshAvailableMiB(snapshot with { GameActive = true }, now, null, null) != null ||
            FreshAvailableMiB(snapshot with { UpdatedUtc = now.AddMinutes(-2) }, now, null, null) != null ||
            FreshAvailableMiB(snapshot with { FreeRamGiB = double.NaN }, now, null, null) != null ||
            Evaluate(8192, 2, 2 * 8400, 8384, 8416, 3, 3 * 8180, 8164, 8196).Confirmed ||
            Evaluate(8192, 3, 3 * 8400, 8384, 8416, 2, 2 * 8180, 8164, 8196).Confirmed ||
            Evaluate(8192, 3, 3 * 8400, 8200, 8600, 3, 3 * 8180, 8164, 8196).Confirmed ||
            Evaluate(8192, 3, 3 * 8400, 8384, 8416, 3, 3 * 7800, 7784, 7816).Confirmed ||
            Evaluate(8192, 3, 3 * 8240, 8224, 8256, 3, 3 * 8180, 8164, 8196).Confirmed ||
            Evaluate(double.NaN, 3, 3 * 8400, 8384, 8416, 3, 3 * 8180, 8164, 8196).Confirmed)
            throw new InvalidOperationException(
                "Passive RAM evidence accepted missing, stale, noisy or non-restored A/B/A.");
        return "AI_PASSIVE_RAM_ABA_SMOKE_OK candidate3=true restored3=true " +
               "duplicateSnapshotsRejected=true driftRejected=true noiseRejected=true " +
               "cpuVerdictIndependent=true noAdditionalSampler=true writes=0";
    }
}
