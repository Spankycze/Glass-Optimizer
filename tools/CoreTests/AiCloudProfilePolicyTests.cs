using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiCloudProfilePolicyTests
{
    [Fact]
    public void CloudRestoreCannotReenableProtectedAiScopes()
    {
        var config = new AiSystemRuntimeConfig(
            TweaksEnabled: true,
            SecurityEnabled: true);

        AiSystemRuntimeConfig safe = AiCloudProfilePolicy.SanitizeConfig(config);

        Assert.False(safe.TweaksEnabled);
        Assert.False(safe.SecurityEnabled);
    }

    [Fact]
    public void CloudSnapshotDropsTransientExperimentState()
    {
        var row = new AiSystemRuntimeItemState(
            Kind: "service",
            Id: "SysMain",
            Managed: true,
            Sleeping: true,
            BlockedReason: "",
            ObservationSinceUtc: DateTime.UtcNow,
            CooldownUntilUtc: DateTime.UtcNow.AddMinutes(5),
            OriginalServiceStart: 2,
            OriginalTaskEnabled: null,
            WasRunningBefore: true,
            Rollbacks: 1,
            LastAction: "candidate trial",
            VerificationStartedUtc: DateTime.UtcNow,
            VerificationSamples: 2,
            VerificationLoadTotal: 2.4,
            VerificationPhase: "ReferenceA",
            CandidateLoadPercent: 1.1);

        AiSystemRuntimeItemState safe = AiCloudProfilePolicy.SanitizeItems(new[] { row })[0];

        Assert.False(safe.Managed);
        Assert.False(safe.Sleeping);
        Assert.Null(safe.ObservationSinceUtc);
        Assert.Null(safe.CooldownUntilUtc);
        Assert.Null(safe.OriginalServiceStart);
        Assert.Null(safe.VerificationStartedUtc);
        Assert.Equal(0, safe.VerificationSamples);
        Assert.Equal("", safe.VerificationPhase);
        Assert.Null(safe.CandidateLoadPercent);
        Assert.Equal(1, safe.Rollbacks);
    }

    [Fact]
    public void BackgroundSyncDefersDuringInteractiveAndGameWork()
    {
        Assert.False(AiCloudProfilePolicy.ShouldBackgroundSync(AiWorkloadClass.Interactive));
        Assert.False(AiCloudProfilePolicy.ShouldBackgroundSync(AiWorkloadClass.Game));
        Assert.True(AiCloudProfilePolicy.ShouldBackgroundSync(AiWorkloadClass.Background));
        Assert.True(AiCloudProfilePolicy.ShouldBackgroundSync(AiWorkloadClass.Idle));
        Assert.False(AiCloudProfilePolicy.ShouldBackgroundSync(AiWorkloadClass.Unknown));
    }
}
