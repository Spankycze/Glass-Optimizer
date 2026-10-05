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

        Assert.Equal(5, samples.Count);
        AiBackgroundPerformancePolicy.AddSample(samples, false, 20, start.AddMinutes(4));
        Assert.Empty(samples);
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
