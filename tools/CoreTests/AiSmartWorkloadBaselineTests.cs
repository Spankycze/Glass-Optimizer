using System.Text.Json;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiSmartWorkloadBaselineTests
{
    [Fact]
    public void GlobalBaselineCacheInvalidatesOnModelReplacementEvenWithSameValues()
    {
        var service = new AiSmartRuntimeService();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var resolve = typeof(AiSmartRuntimeService).GetMethod("ResolveBaseline", flags,
            null, new[] { typeof(AiWorkloadClass) }, null)!;
        var modelField = typeof(AiSmartRuntimeService).GetField("_model", flags)!;
        var first = resolve.Invoke(service, new object[] { AiWorkloadClass.Unknown });
        var original = (AiSmartRuntimeModel)modelField.GetValue(service)!;
        modelField.SetValue(service, original with { });
        var replacement = resolve.Invoke(service, new object[] { AiWorkloadClass.Unknown });
        Assert.NotSame(first, replacement);
        Assert.Equal(first, replacement);
        Assert.Same(replacement, resolve.Invoke(service, new object[] { AiWorkloadClass.Unknown }));
    }

    [Fact]
    public void GlobalBaselineReusesObjectUntilModelChanges()
    {
        var service = new AiSmartRuntimeService();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var resolve = typeof(AiSmartRuntimeService).GetMethod("ResolveBaseline", flags,
            null, new[] { typeof(AiWorkloadClass) }, null)!;
        var modelField = typeof(AiSmartRuntimeService).GetField("_model", flags)!;
        var first = (AiSmartWorkloadBaseline)resolve.Invoke(service, new object[] { AiWorkloadClass.Unknown })!;
        Assert.Same(first, resolve.Invoke(service, new object[] { AiWorkloadClass.Unknown }));
        var original = (AiSmartRuntimeModel)modelField.GetValue(service)!;
        modelField.SetValue(service, original with { AvgTempC = original.AvgTempC + 1 });
        var changed = (AiSmartWorkloadBaseline)resolve.Invoke(service, new object[] { AiWorkloadClass.Unknown })!;
        Assert.NotSame(first, changed);
        Assert.Equal(first.AvgTempC + 1, changed.AvgTempC);
        Assert.Same(changed, resolve.Invoke(service, new object[] { AiWorkloadClass.Unknown }));
    }

    [Fact]
    public void BaselineLabelReusesUnchangedTextAndInvalidatesAfterLearning()
    {
        var service = new AiSmartRuntimeService();
        var label = typeof(AiSmartRuntimeService).GetMethod("BaselineText",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var modelField = typeof(AiSmartRuntimeService).GetField("_model",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        string first = (string)label.Invoke(service, new object[] { AiWorkloadClass.Unknown })!;
        string cached = (string)label.Invoke(service, new object[] { AiWorkloadClass.Unknown })!;
        Assert.Same(first, cached);

        var current = (AiSmartRuntimeModel)modelField.GetValue(service)!;
        modelField.SetValue(service, current with { AvgTempC = current.AvgTempC + 3 });
        string changed = (string)label.Invoke(service, new object[] { AiWorkloadClass.Unknown })!;
        Assert.NotEqual(first, changed);
        Assert.Same(changed, label.Invoke(service, new object[] { AiWorkloadClass.Unknown }));
    }

    [Theory]
    [InlineData(AiWorkloadClass.Unknown)]
    [InlineData(AiWorkloadClass.Idle)]
    [InlineData(AiWorkloadClass.Background)]
    [InlineData(AiWorkloadClass.Interactive)]
    [InlineData(AiWorkloadClass.Game)]
    public void WorkloadKeyPreservesPersistedEnumNames(AiWorkloadClass workload)
    {
        Assert.Equal(workload.ToString(), AiSmartRuntimeService.WorkloadKey(workload));
    }

    [Fact]
    public void SpecializedBaselineNeedsEnoughFreshSamples()
    {
        DateTime now = new(2026, 10, 4, 3, 30, 0, DateTimeKind.Utc);

        Assert.True(AiSmartRuntimeService.IsWorkloadBaselineEligible(
            20, now.AddHours(-1), now));
        Assert.False(AiSmartRuntimeService.IsWorkloadBaselineEligible(
            19, now.AddHours(-1), now));
        Assert.False(AiSmartRuntimeService.IsWorkloadBaselineEligible(
            100, now.AddDays(-8), now));
        Assert.False(AiSmartRuntimeService.IsWorkloadBaselineEligible(
            100, now.AddMinutes(1), now));
    }

    [Fact]
    public void FirstWorkloadSampleInitializesProfile()
    {
        DateTime now = DateTime.UtcNow;
        AiSmartWorkloadBaseline baseline = AiSmartRuntimeService.LearnWorkloadBaseline(
            null, 52, 60, 35, 1750, 3900, now);

        Assert.Equal(1, baseline.Samples);
        Assert.Equal(52, baseline.AvgTempC);
        Assert.Equal(60, baseline.AvgPowerW);
        Assert.Equal(35, baseline.AvgLoadPercent);
        Assert.Equal(1750, baseline.AvgRpm);
        Assert.Equal(3900, baseline.AvgClockMHz);
        Assert.Equal(now, baseline.UpdatedUtc);
    }

    [Fact]
    public void WarmupProfileLearnsQuicklyThenSlowsDown()
    {
        DateTime now = DateTime.UtcNow;
        var warmup = new AiSmartWorkloadBaseline(
            1, 50, 40, 10, 1600, 3000, now.AddSeconds(-2));

        AiSmartWorkloadBaseline learned = AiSmartRuntimeService.LearnWorkloadBaseline(
            warmup, 60, 50, 20, 1800, 4000, now);

        Assert.Equal(2, learned.Samples);
        Assert.Equal(51.0, learned.AvgTempC, 3);
        Assert.Equal(41.0, learned.AvgPowerW, 3);
        Assert.Equal(11.0, learned.AvgLoadPercent, 3);
        Assert.Equal(1620.0, learned.AvgRpm, 3);
        Assert.Equal(3100.0, learned.AvgClockMHz, 3);

        var stable = learned with { Samples = 50, AvgTempC = 50 };
        AiSmartWorkloadBaseline stableLearned = AiSmartRuntimeService.LearnWorkloadBaseline(
            stable, 60, 50, 20, 1800, 4000, now.AddSeconds(2));

        Assert.Equal(50.25, stableLearned.AvgTempC, 3);
    }

    [Fact]
    public void OldGlobalOnlyModelJsonRemainsCompatible()
    {
        string json = """
            {"Samples":120,"AvgTempC":55,"AvgPowerW":60,"AvgLoadPercent":25,"AvgRpm":1700,"AvgClockMHz":3600,"UpdatedUtc":"2026-10-04T03:00:00Z"}
            """;

        AiSmartRuntimeModel? model = JsonSerializer.Deserialize<AiSmartRuntimeModel>(json);

        Assert.NotNull(model);
        Assert.Equal(120, model!.Samples);
        Assert.Null(model.Workloads);
    }

    [Fact]
    public void CorruptModelIsSanitizedAndInvalidWorkloadsAreDropped()
    {
        DateTime now = new(2026, 10, 4, 4, 0, 0, DateTimeKind.Utc);
        var workloads = new Dictionary<string, AiSmartWorkloadBaseline>
        {
            ["Game"] = new(25, 65, 85, 55, 1900, 4100, now.AddHours(-1)),
            ["Bogus"] = new(25, 60, 70, 40, 1800, 3800, now.AddHours(-1)),
            ["Idle"] = new(25, 45, 30, 5, 1620, 2200, now.AddMinutes(10)),
            ["Background"] = new(25, double.NaN, 40, 15, 1650, 2800, now.AddHours(-1))
        };

        var corrupt = new AiSmartRuntimeModel(
            -5,
            double.NaN,
            999,
            -10,
            9000,
            90000,
            now.AddMinutes(10),
            workloads);

        AiSmartRuntimeModel safe = AiSmartRuntimeService.SanitizeModel(corrupt, now);

        Assert.Equal(0, safe.Samples);
        Assert.Equal(55, safe.AvgTempC);
        Assert.Equal(55, safe.AvgPowerW);
        Assert.Equal(20, safe.AvgLoadPercent);
        Assert.Equal(1620, safe.AvgRpm);
        Assert.Equal(2800, safe.AvgClockMHz);
        Assert.Equal(DateTime.MinValue, safe.UpdatedUtc);
        Assert.NotNull(safe.Workloads);
        Assert.Single(safe.Workloads!);
        Assert.True(safe.Workloads.ContainsKey("Game"));
    }
}
