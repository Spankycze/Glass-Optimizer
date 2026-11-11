using System;
using System.Collections.Generic;
using System.Linq;

namespace SpankyhoOptimizer.Services;

// Tiny hot-path sample: a value type avoids one managed heap object per
// accepted 20-300s quiet-window observation on the FanAgent tick.
public readonly record struct AiBackgroundLoadSample(DateTime Utc, double LoadPercent);

public static class AiBackgroundPerformancePolicy
{
    public const int RequiredSamples = 4;
    public const double MaximumQuietLoadPercent = 5.0;
    public static readonly TimeSpan MinimumObservationWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumObservationWindow = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan MinimumSampleGap = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMinutes(5);

    // Retain the conservative legacy call for other consumers.
    public static bool IsComparableQuietSample(bool gameActive, bool realIdle, double loadPercent) =>
        IsComparableQuietSample(gameActive, realIdle, loadPercent, AiWorkloadClass.Unknown);

    // Background above 3% is admitted ONLY after separate workload,
    // smoothness, stability, confidence, and live-mutation gates.
    public static bool IsComparableQuietSample(
        bool gameActive, bool realIdle, double loadPercent, AiWorkloadClass workload) =>
        !gameActive && double.IsFinite(loadPercent) && loadPercent >= 0 &&
        loadPercent <= MaximumQuietLoadPercent &&
        (realIdle || loadPercent <= 3.0 || workload == AiWorkloadClass.Background);

    public static bool HasStableMeasuredPressure(IReadOnlyCollection<AiBackgroundLoadSample> samples)
    {
        if (samples.Count < RequiredSamples) return false;

        // The live AI sampler retains exactly five observations. Validate and
        // sort those small value sets on the stack rather than allocating
        // LINQ iterator/sort arrays on every maintenance decision.
        if (samples.Count is >= RequiredSamples and <= 5)
        {
            // The current sampler stores four entries, but legacy callers
            // can supply five: handle both without a LINQ array allocation.
            Span<long> timestampBuffer = stackalloc long[5];
            Span<double> valueBuffer = stackalloc double[5];
            Span<long> timestamps = timestampBuffer[..samples.Count];
            Span<double> values = valueBuffer[..samples.Count];
            int index = 0;
            long precedingTicks = 0;
            bool chronological = true;
            // Queue<T>.Enumerator is a struct; enumerating through the
            // IReadOnlyCollection interface boxes it on every AI evaluation.
            if (samples is Queue<AiBackgroundLoadSample> queue)
            {
                foreach (AiBackgroundLoadSample sample in queue)
                {
                    if (sample.Utc.Kind != DateTimeKind.Utc ||
                        !double.IsFinite(sample.LoadPercent) ||
                        sample.LoadPercent < 0 || sample.LoadPercent > MaximumQuietLoadPercent)
                        return false;
                    long ticks = sample.Utc.Ticks;
                    if (index != 0 && ticks < precedingTicks)
                        chronological = false;
                    precedingTicks = ticks;
                    timestamps[index] = ticks;
                    values[index++] = sample.LoadPercent;
                }
            }
            else
            {
                foreach (AiBackgroundLoadSample sample in samples)
                {
                    if (sample.Utc.Kind != DateTimeKind.Utc ||
                        !double.IsFinite(sample.LoadPercent) ||
                        sample.LoadPercent < 0 || sample.LoadPercent > MaximumQuietLoadPercent)
                        return false;
                    long ticks = sample.Utc.Ticks;
                    if (index != 0 && ticks < precedingTicks)
                        chronological = false;
                    precedingTicks = ticks;
                    timestamps[index] = ticks;
                    values[index++] = sample.LoadPercent;
                }
            }

            // Normal AddSample queues are chronological. Keep the original
            // sorted validation for injected, imported or reordered collections.
            if (!chronological)
                timestamps.Sort();
            long observed = timestamps[^1] - timestamps[0];
            if (observed < MinimumObservationWindow.Ticks ||
                observed > MaximumObservationWindow.Ticks)
                return false;

            for (int i = 1; i < timestamps.Length; i++)
            {
                long gap = timestamps[i] - timestamps[i - 1];
                if (gap < MinimumSampleGap.Ticks || gap > MaximumSampleGap.Ticks)
                    return false;
            }

            if (values.Length == 4)
            {
                // Five compare-swaps select the median pair and spread of
                // the live four-value window without general-purpose sorting.
                double a = values[0], b = values[1], c = values[2], d = values[3];
                if (a > b) (a, b) = (b, a);
                if (c > d) (c, d) = (d, c);
                if (a > c) (a, c) = (c, a);
                if (b > d) (b, d) = (d, b);
                if (b > c) (b, c) = (c, b);
                return (b + c) / 2d >= 0.75 && d - a <= 2.0;
            }
            values.Sort();
            double smallMedian = values.Length % 2 == 0
                ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2d
                : values[values.Length / 2];
            return smallMedian >= 0.75 && values[^1] - values[0] <= 2.0;
        }

        // An invalid measurement must never be silently discarded to turn a
        // larger collection into an apparently stable, trusted observation.
        if (samples.Any(x => x.Utc.Kind != DateTimeKind.Utc || !double.IsFinite(x.LoadPercent)))
            return false;

        AiBackgroundLoadSample[] ordered = samples
            .OrderBy(x => x.Utc)
            .ToArray();

        if (ordered.Length < RequiredSamples ||
            ordered.Any(x => x.LoadPercent < 0 || x.LoadPercent > MaximumQuietLoadPercent))
            return false;

        TimeSpan window = ordered[^1].Utc - ordered[0].Utc;
        if (window < MinimumObservationWindow || window > MaximumObservationWindow)
            return false;

        for (int i = 1; i < ordered.Length; i++)
        {
            TimeSpan gap = ordered[i].Utc - ordered[i - 1].Utc;
            if (gap < MinimumSampleGap || gap > MaximumSampleGap)
                return false;
        }

        double[] orderedLoads = ordered.Select(x => x.LoadPercent).OrderBy(x => x).ToArray();
        double median = orderedLoads.Length % 2 == 0
            ? (orderedLoads[orderedLoads.Length / 2 - 1] + orderedLoads[orderedLoads.Length / 2]) / 2d
            : orderedLoads[orderedLoads.Length / 2];
        double spread = orderedLoads[^1] - orderedLoads[0];

        // Require real, repeatable background pressure rather than reacting to noise.
        return median >= 0.75 && spread <= 2.0;
    }

    public static double MedianLoad(IReadOnlyCollection<AiBackgroundLoadSample> samples)
    {
        if (samples.Count <= 8)
        {
            Span<double> values = stackalloc double[8];
            int count = 0;
            if (samples is Queue<AiBackgroundLoadSample> queue)
            {
                foreach (AiBackgroundLoadSample sample in queue)
                    if (double.IsFinite(sample.LoadPercent))
                        values[count++] = sample.LoadPercent;
            }
            else
            {
                foreach (AiBackgroundLoadSample sample in samples)
                    if (double.IsFinite(sample.LoadPercent))
                        values[count++] = sample.LoadPercent;
            }
            if (count == 0) return double.NaN;
            Span<double> small = values[..count];
            small.Sort();
            return count % 2 == 0 ? (small[count / 2 - 1] + small[count / 2]) / 2d : small[count / 2];
        }
        double[] ordered = samples
            .Select(x => x.LoadPercent)
            .Where(double.IsFinite)
            .OrderBy(x => x)
            .ToArray();
        if (ordered.Length == 0) return double.NaN;
        return ordered.Length % 2 == 0
            ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2d
            : ordered[ordered.Length / 2];
    }

    public static void AddSample(
        Queue<AiBackgroundLoadSample> samples,
        bool comparable,
        double loadPercent,
        DateTime utcNow)
    {
        // A non-finite or out-of-range measurement can never establish
        // quiet background pressure, even if a caller misclassifies it.
        if (!comparable || utcNow.Kind != DateTimeKind.Utc ||
            !double.IsFinite(loadPercent) ||
            loadPercent < 0 || loadPercent > MaximumQuietLoadPercent)
        {
            samples.Clear();
            return;
        }

        if (samples.Count > 0)
        {
            // A restored/injected queue is not automatically trusted. Validate
            // ALL older timestamps, values and gaps, not just the last sample:
            // otherwise corrupt early entries can remain in a new quiet epoch.
            // The ordinary four-value queue stays allocation-free.
            DateTime latestUtc = default;
            bool corrupt = samples.Count > RequiredSamples;
            bool first = true;
            if (!corrupt)
            {
                foreach (AiBackgroundLoadSample sample in samples)
                {
                    if (sample.Utc.Kind != DateTimeKind.Utc ||
                        !double.IsFinite(sample.LoadPercent) ||
                        sample.LoadPercent < 0 || sample.LoadPercent > MaximumQuietLoadPercent ||
                        (!first && (sample.Utc - latestUtc < MinimumSampleGap ||
                                    sample.Utc - latestUtc > MaximumSampleGap)))
                    {
                        corrupt = true;
                        break;
                    }
                    latestUtc = sample.Utc;
                    first = false;
                }
            }
            if (corrupt)
                samples.Clear();
            else
            {
                TimeSpan gap = utcNow - latestUtc;
                // After a clock rollback or missed tick, restart the epoch.
                if (gap < TimeSpan.Zero || gap > MaximumSampleGap)
                    samples.Clear();
                else if (gap < MinimumSampleGap)
                    return;
            }
        }

        // Evict BEFORE appending. Enqueue-then-dequeue temporarily grows the
        // 4-slot Queue<T> to 5 entries and permanently allocates an 8-slot
        // backing array even though we only retain four samples.
        while (samples.Count >= RequiredSamples)
            samples.Dequeue();
        samples.Enqueue(new AiBackgroundLoadSample(utcNow, loadPercent));
    }
}
