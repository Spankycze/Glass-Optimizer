using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiProtectedSectionsPolicyTests
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
    [InlineData("ai.sys.inventory", "system", "Background")]
    [InlineData("ai.ui.thumbcache", "ui", "Explorer")]
    [InlineData("ai.privacy.websearch", "privacy", "Search")]
    [InlineData("ai.defender.any", "defender", "Security")]
    public void AiOwnedProtectedSectionsAreHardBlocked(string id, string section, string group)
    {
        Assert.True(AiAutomaticMutationGuard.Blocks(T(id, section, group)));
    }

    [Fact]
    public void ManualSystemUiExplorerSecurityTweaksRemainManualAndUsable()
    {
        Assert.False(AiAutomaticMutationGuard.Blocks(T("sys.inventory", "system", "Background")));
        Assert.False(AiAutomaticMutationGuard.Blocks(T("ui.thumbcache", "ui", "Explorer")));
        Assert.False(AiAutomaticMutationGuard.Blocks(T("privacy.websearch", "privacy", "Search")));
        Assert.False(AiAutomaticMutationGuard.Blocks(T("defender.any", "defender", "Security")));
    }

    [Fact]
    public void SecurityAutomationIsDisabledAtServiceBoundary()
    {
        Assert.False(AiSecurityHardeningService.AutomaticMutationAllowed);
    }

    [Fact]
    public void NonProtectedSyntheticCandidateIsNotBlockedBySafetyGuard()
    {
        Assert.False(AiAutomaticMutationGuard.Blocks(T("ai.perf.synthetic", "performance")));
    }
}
