using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiAutomaticMutationGuardTests
{
    private static TweakDefinition T(string id, string section, string group = "") => new()
    {
        Id = id,
        Section = section,
        Group = group,
        Name = id,
        Description = ""
    };

    [Theory]
    [InlineData("ai.sys", "system", "Background")]
    [InlineData("ai.ui", "ui", "Explorer")]
    [InlineData("ai.privacy", "privacy", "Telemetry")]
    [InlineData("ai.defender", "defender", "Security")]
    public void AiOwnedProtectedMutationIsBlocked(string id, string section, string group)
    {
        Assert.True(AiAutomaticMutationGuard.Blocks(T(id, section, group)));
    }

    [Fact]
    public void ManualMutationInProtectedSectionIsNotBlockedByAiGuard()
    {
        Assert.False(AiAutomaticMutationGuard.Blocks(T("sys.manual", "system", "Background")));
    }

    [Fact]
    public void SecurityAutomaticMutationIsDisabledAtServiceBoundary()
    {
        Assert.False(AiSecurityHardeningService.AutomaticMutationAllowed);
    }
}
