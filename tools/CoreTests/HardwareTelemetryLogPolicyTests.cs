using SpankyhoOptimizer.Services;

public sealed class HardwareTelemetryLogPolicyTests
{
    [Fact]
    public void First_missing_sensor_event_is_logged()
    {
        Assert.True(HardwareTelemetryLogPolicy.ShouldLogMissingSensor(
            DateTime.UtcNow,
            DateTime.MinValue));
    }

    [Fact]
    public void Repeated_missing_sensor_event_is_throttled()
    {
        DateTime now = DateTime.UtcNow;
        Assert.False(HardwareTelemetryLogPolicy.ShouldLogMissingSensor(
            now,
            now.AddMinutes(-1)));
    }

    [Fact]
    public void Missing_sensor_event_is_logged_again_after_interval()
    {
        DateTime now = DateTime.UtcNow;
        Assert.True(HardwareTelemetryLogPolicy.ShouldLogMissingSensor(
            now,
            now.Subtract(HardwareTelemetryLogPolicy.MissingSensorLogInterval)));
    }
}
