using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiBackgroundPerformancePolicyTests
{
    private static AiBackgroundLoadSample[] Window(params double[] loads)
    {
        DateTime start = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
        return loads.Select((load, i) =>
            new AiBackgroundLoadSample(start.AddSeconds(i * 45), load)).ToArray();
    }

    [Fact]
    public void RequiresStableMeasuredBackgroundPressureAcrossRealTime()
    {
        Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(Window(0.1, 0.2, 0.2, 0.3, 0.2)));
        Assert.True(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(Window(1.0, 1.1, 1.2, 1.0, 1.3)));
        Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(Window(0.8, 1.0, 2.0, 3.2, 4.8)));
    }

    [Fact]
    public void FastBurstOfSamplesIsNotTreatedAsStablePressure()
    {
        DateTime start = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
        var burst = Enumerable.Range(0, 5)
            .Select(i => new AiBackgroundLoadSample(start.AddSeconds(i * 5), 1.2))
            .ToArray();

        Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(burst));
    }

    [Fact]
    public void NonComparableActivityResetsWindow()
    {
        var samples = new Queue<AiBackgroundLoadSample>();
        DateTime start = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++)
            AiBackgroundPerformancePolicy.AddSample(samples, true, 1.2, start.AddSeconds(i * 45));

        Assert.Equal(AiBackgroundPerformancePolicy.RequiredSamples, samples.Count);
        AiBackgroundPerformancePolicy.AddSample(samples, false, 20, start.AddMinutes(4));
        Assert.Empty(samples);
    }

    [Theory]
    [InlineData(4, 1.075)]
    [InlineData(5, 1.1)]
    [InlineData(8, 1.175)]
    public void Queue_median_matches_expected_value_with_no_repeat_allocation(int count, double expected)
    {
        DateTime origin = new(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyCollection<AiBackgroundLoadSample> queue = new Queue<AiBackgroundLoadSample>(
            Enumerable.Range(0, count).Select(i =>
                new AiBackgroundLoadSample(origin.AddSeconds(i * 45), 1 + i * 0.05)));
        Assert.Equal(expected, AiBackgroundPerformancePolicy.MedianLoad(queue), 6);
        _ = AiBackgroundPerformancePolicy.MedianLoad(queue);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
            _ = AiBackgroundPerformancePolicy.MedianLoad(queue);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 4096, $"Median queue path allocated {bytes} B");
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Invalid_clock_kind_clears_existing_quiet_epoch(DateTimeKind kind)
    {
        DateTime start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var samples = new Queue<AiBackgroundLoadSample>();
        AiBackgroundPerformancePolicy.AddSample(samples, true, 1.2, start);
        Assert.Single(samples);
        AiBackgroundPerformancePolicy.AddSample(samples, true, 1.2,
            DateTime.SpecifyKind(start.AddSeconds(45), kind));
        Assert.Empty(samples);
        AiBackgroundPerformancePolicy.AddSample(samples, true, 1.2, start.AddSeconds(90));
        Assert.Single(samples);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ExistingNonUtcSampleCannotPoisonNewQuietEpoch(DateTimeKind kind)
    {
        DateTime start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var samples = new Queue<AiBackgroundLoadSample>();
        samples.Enqueue(new AiBackgroundLoadSample(DateTime.SpecifyKind(start, kind), 1.2));
        AiBackgroundPerformancePolicy.AddSample(samples, true, 1.1, start.AddSeconds(45));
        Assert.Single(samples);
        Assert.Equal(start.AddSeconds(45), samples.Peek().Utc);
    }

    [Fact]
    public void DuplicateFastSamplesDoNotInflateConfidence()
    {
        var samples = new Queue<AiBackgroundLoadSample>();
        DateTime start = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
        AiBackgroundPerformancePolicy.AddSample(samples, true, 1.2, start);
        AiBackgroundPerformancePolicy.AddSample(samples, true, 1.2, start.AddSeconds(2));
        Assert.Single(samples);
    }

    [Fact]
    public void Clock_rollback_resets_quiet_measurement_epoch_without_freezing_collection()
    {
        DateTime start = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var samples = new Queue<AiBackgroundLoadSample>();
        for (int i = 0; i < 5; i++)
            AiBackgroundPerformancePolicy.AddSample(samples, true, 1.1, start.AddSeconds(i * 45));
        Assert.True(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(samples));

        DateTime rolledBack = start.AddHours(-1);
        AiBackgroundPerformancePolicy.AddSample(samples, true, 1.0, rolledBack);
        Assert.Single(samples);
        Assert.Equal(rolledBack, samples.Peek().Utc);
        Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(samples));

        for (int i = 1; i < 5; i++)
            AiBackgroundPerformancePolicy.AddSample(samples, true, 1.1, rolledBack.AddSeconds(i * 45));
        Assert.Equal(AiBackgroundPerformancePolicy.RequiredSamples, samples.Count);
        Assert.True(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(samples));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    [InlineData(6.0)]
    public void Invalid_comparable_reading_invalidates_entire_pressure_window(double invalid)
    {
        DateTime start = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var samples = new Queue<AiBackgroundLoadSample>();
        for (int i = 0; i < 5; i++)
            AiBackgroundPerformancePolicy.AddSample(samples, true, 1.1, start.AddSeconds(i * 45));
        Assert.True(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(samples));
        AiBackgroundPerformancePolicy.AddSample(samples, true, invalid, start.AddMinutes(4));
        Assert.Empty(samples);
    }

    [Fact]
    public void Non_finite_measurement_in_extended_collection_cannot_be_ignored()
    {
        AiBackgroundLoadSample[] valid = Window(1, 1.1, 1.2, 1.1, 1);
        Assert.True(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(valid));
        Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(
            valid.Append(new AiBackgroundLoadSample(valid[^1].Utc.AddSeconds(45), double.NaN)).ToArray()));
        Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(
            valid.Append(new AiBackgroundLoadSample(valid[^1].Utc.AddSeconds(45), double.PositiveInfinity)).ToArray()));
    }

    [Fact]
    public void Stable_pressure_requires_utc_timestamps_in_both_collection_paths()
    {
        AiBackgroundLoadSample[] baseline = Window(1, 1.1, 1.2, 1.1, 1);
        Assert.True(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(baseline));
        foreach (DateTimeKind kind in new[] { DateTimeKind.Local, DateTimeKind.Unspecified })
        {
            AiBackgroundLoadSample[] altered = (AiBackgroundLoadSample[])baseline.Clone();
            altered[2] = altered[2] with { Utc = DateTime.SpecifyKind(altered[2].Utc, kind) };
            Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(new Queue<AiBackgroundLoadSample>(altered)));
            Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(altered));
            Assert.False(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(
                altered.Append(new AiBackgroundLoadSample(baseline[^1].Utc.AddSeconds(45), 1.1)).ToArray()));
        }
    }

    [Fact]
    public void Stable_pressure_check_has_low_allocation_in_five_sample_hotpath()
    {
        var samples = new Queue<AiBackgroundLoadSample>(Window(1, 1.1, 1.2, 1.1, 1));
        _ = AiBackgroundPerformancePolicy.HasStableMeasuredPressure(samples);
        const int iterations = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            Assert.True(AiBackgroundPerformancePolicy.HasStableMeasuredPressure(samples));
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 120 * iterations, $"Fast-path allocation: {bytes} B for {iterations} checks");
    }

    [Theory]
    [InlineData(false, true, 1.0, true)]
    [InlineData(false, false, 2.0, true)]
    [InlineData(false, false, 4.0, false)]
    [InlineData(true, true, 1.0, false)]
    [InlineData(false, true, 6.0, false)]
    public void QuietSampleClassificationIsConservative(bool game, bool idle, double load, bool expected)
    {
        Assert.Equal(expected, AiBackgroundPerformancePolicy.IsComparableQuietSample(game, idle, load));
    }
}
