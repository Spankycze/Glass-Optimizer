using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiDriverAutomationPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void OnlyDemandStartDriversAreAutomaticCandidates(int start)
    {
        Assert.False(AiDriverAutomationPolicy.IsEligibleStartType(start));
    }

    [Fact]
    public void DemandStartIsEligible()
    {
        Assert.True(AiDriverAutomationPolicy.IsEligibleStartType(3));
    }

    [Fact]
    public void DriverObservationNeverDropsBelowThirtyMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(30),
            AiDriverAutomationPolicy.RequiredObservation(180));
        Assert.Equal(TimeSpan.FromMinutes(45),
            AiDriverAutomationPolicy.RequiredObservation(2700));
    }
}
