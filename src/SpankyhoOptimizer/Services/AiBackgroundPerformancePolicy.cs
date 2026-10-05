using System;
using System.Collections.Generic;
using System.Linq;

namespace SpankyhoOptimizer.Services;

public sealed record AiBackgroundLoadSample(DateTime Utc, double LoadPercent);

public static class AiBackgroundPerformancePolicy
{
    public const int RequiredSamples = 5;
    public const double MaximumQuietLoadPercent = 5.0;
    public static readonly TimeSpan MinimumObservationWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MaximumObservationWindow = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan MinimumSampleGap = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMinutes(5);

    public static bool IsComparableQuietSample(bool gameActive, bool realIdle, double loadPercent) =>
        !gameActive && double.IsFinite(loadPercent) && loadPercent >= 0 &&
        loadPercent <= MaximumQuietLoadPercent && (realIdle || loadPercent <= 3.0);

    public static bool HasStableMeasuredPressure(IReadOnlyCollection<AiBackgroundLoadSample> samples)
    {
        if (samples.Count < RequiredSamples) return false;

        AiBackgroundLoadSample[] ordered = samples
            .Where(x => double.IsFinite(x.LoadPercent))
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

        double[] values = ordered.Select(x => x.LoadPercent).OrderBy(x => x).ToArray();
        double median = values[values.Length / 2];
        double spread = values[^1] - values[0];

        // Require real, repeatable background pressure rather than reacting to noise.
        return median >= 0.75 && spread <= 2.0;
    }

    public static double MedianLoad(IReadOnlyCollection<AiBackgroundLoadSample> samples)
    {
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
        if (!comparable)
        {
            samples.Clear();
            return;
        }

        if (samples.Count > 0)
        {
            TimeSpan gap = utcNow - samples.Last().Utc;
            if (gap < MinimumSampleGap)
                return;
            if (gap > MaximumSampleGap)
                samples.Clear();
        }

        samples.Enqueue(new AiBackgroundLoadSample(utcNow, loadPercent));
        while (samples.Count > RequiredSamples)
            samples.Dequeue();
    }
}
