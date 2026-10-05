using SpankyhoOptimizer.Services;

public sealed class AiSmartRuntimeTelemetryTrustTests
{
    [Fact]
    public void Missing_cpu_temperature_caps_confidence()
    {
        double confidence = AiSmartRuntimeService.AdjustConfidenceForTelemetry(
            100, hidOk: true, temperatureAvailable: false, powerAvailable: true, modelFresh: true);

        Assert.InRange(confidence, 0, 25);
    }

    [Fact]
    public void Full_live_telemetry_preserves_model_confidence()
    {
        double confidence = AiSmartRuntimeService.AdjustConfidenceForTelemetry(
            100, hidOk: true, temperatureAvailable: true, powerAvailable: true, modelFresh: true);

        Assert.Equal(100, confidence);
    }

    [Fact]
    public void Missing_hid_and_power_reduce_confidence()
    {
        double confidence = AiSmartRuntimeService.AdjustConfidenceForTelemetry(
            100, hidOk: false, temperatureAvailable: true, powerAvailable: false, modelFresh: true);

        Assert.True(confidence < 55);
    }

    [Fact]
    public void Missing_cpu_temperature_enters_sensor_fallback_mode()
    {
        var service = new AiSmartRuntimeService();
        var telemetry = new CpuTemperatureService.CpuTelemetrySample(
            null,
            "CPU senzor nedostupný",
            "CPU",
            10,
            null,
            40);

        AiSmartRuntimeDecision decision = service.Tick(
            enabled: true,
            telemetry,
            fanRpms: new[] { 0, 1600, 1600, 0 },
            currentTargetRpm: 1620,
            hidOk: true,
            gameActive: false,
            realIdle: false,
            loadPercent: 10,
            blocker: string.Empty);

        Assert.Equal("SensorFallback", decision.Mode);
        Assert.InRange(decision.Confidence, 0, 25);
        Assert.Contains("CPU teplota nedostupná", decision.Prediction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("modelový odhad", decision.Prediction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Telemetry_recovery_replaces_stale_fallback_action_immediately()
    {
        var service = new AiSmartRuntimeService();
        var missing = new CpuTemperatureService.CpuTelemetrySample(
            null, "CPU senzor nedostupný", "CPU", 10, null, 40);
        var live = new CpuTemperatureService.CpuTelemetrySample(
            46, "Core (Tctl/Tdie)", "AMD Ryzen 7 3700X", 10, 2500, 45);

        AiSmartRuntimeDecision fallback = service.Tick(
            true, missing, new[] { 0, 1600, 1600, 0 }, 1620,
            true, false, false, 10, string.Empty);
        AiSmartRuntimeDecision recovered = service.Tick(
            true, live, new[] { 0, 1600, 1600, 0 }, 1620,
            true, false, false, 10, string.Empty);

        Assert.Equal("CPU telemetry fallback", fallback.LastAction);
        Assert.Equal("CPU telemetry restored", recovered.LastAction);
        Assert.NotEqual("SensorFallback", recovered.Mode);
    }

    [Theory]
    [InlineData(2.0, 2.0, 1.0)]
    [InlineData(2.0, 4.0, 0.5)]
    [InlineData(-3.0, 1.0, -3.0)]
    public void TemperatureTrendIsNormalizedByElapsedTime(double delta, double seconds, double expected)
    {
        Assert.Equal(expected, AiSmartRuntimeService.NormalizeTemperatureTrend(delta, seconds), 3);
    }

    [Fact]
    public void MissingLivePowerNeverRequestsIdleOptimization()
    {
        var service = new AiSmartRuntimeService();
        var telemetry = new CpuTemperatureService.CpuTelemetrySample(
            45, "Core", "AMD Ryzen 7 3700X", 3, 1800, null);

        AiSmartRuntimeDecision decision = service.Tick(
            enabled: true,
            telemetry,
            fanRpms: new[] { 1600, 1600 },
            currentTargetRpm: 1620,
            hidOk: true,
            gameActive: false,
            realIdle: true,
            loadPercent: 3,
            blocker: string.Empty);

        Assert.False(decision.PreferIdle);
    }

    [Fact]
    public void WorkloadTransitionTemporarilyReducesPredictionConfidence()
    {
        Assert.Equal(65, AiSmartRuntimeService.AdjustConfidenceForWorkloadStability(
            100, AiWorkloadClass.Interactive, 0), 3);
        Assert.Equal(78, AiSmartRuntimeService.AdjustConfidenceForWorkloadStability(
            100, AiWorkloadClass.Interactive, 1), 3);
        Assert.Equal(90, AiSmartRuntimeService.AdjustConfidenceForWorkloadStability(
            100, AiWorkloadClass.Interactive, 2), 3);
        Assert.Equal(100, AiSmartRuntimeService.AdjustConfidenceForWorkloadStability(
            100, AiWorkloadClass.Interactive, 3), 3);
    }

    [Fact]
    public void GameKeepsImmediateConfidenceButUnknownIsConservative()
    {
        Assert.Equal(100, AiSmartRuntimeService.AdjustConfidenceForWorkloadStability(
            100, AiWorkloadClass.Game, 0), 3);
        Assert.Equal(75, AiSmartRuntimeService.AdjustConfidenceForWorkloadStability(
            100, AiWorkloadClass.Unknown, 10), 3);
    }
}
