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

    private static readonly DateTime Now =
        new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CloudSnapshotNeverCarriesAReplayableAbaJournalOrForeignWin()
    {
        var row = new AiSystemRuntimeItemState(
            "task", "Fixture", true, true, "trial",
            Now.AddMinutes(-3), Now.AddHours(2), 2, true, true,
            2, "stopped")
        {
            OriginalTaskNextRunUtc = Now.AddHours(3),
            PendingMutation = AiMaintenanceWriteAheadPolicy.TaskDisable,
            PerformanceVerified = true,
            VerificationStartedUtc = Now.AddMinutes(-4),
            VerificationMemorySamples = 2,
            VerificationMemoryTotalMiB = 16000,
            CandidateMemorySamples = 3,
            CandidateAvailableRamMiB = 8200,
            DriverObservationLastUtc = Now.AddMinutes(-3),
            LastVerifiedBenefitAbsolute = 0.4,
            LastVerifiedBenefitPercent = 20,
            LastVerifiedAvailableRamBenefitMiB = 260,
            LastBenefitVerifiedUtc = Now.AddMinutes(-10)
        };
        var clean = Assert.Single(AiCloudProfilePolicy.SanitizeItems(new[] { row }));
        Assert.False(clean.Managed);
        Assert.False(clean.Sleeping);
        Assert.Empty(clean.PendingMutation);
        Assert.Null(clean.OriginalServiceStart);
        Assert.Null(clean.OriginalTaskEnabled);
        Assert.Null(clean.OriginalTaskNextRunUtc);
        Assert.Null(clean.VerificationStartedUtc);
        Assert.Null(clean.DriverObservationLastUtc);
        Assert.Equal(0, clean.VerificationMemorySamples);
        Assert.Equal(0, clean.VerificationMemoryTotalMiB);
        Assert.Equal(0, clean.CandidateMemorySamples);
        Assert.Null(clean.CandidateAvailableRamMiB);
        Assert.False(clean.PerformanceVerified);
        Assert.Equal(0, clean.LastVerifiedBenefitAbsolute);
        Assert.Equal(0, clean.LastVerifiedBenefitPercent);
        Assert.Null(clean.LastVerifiedAvailableRamBenefitMiB);
        Assert.Null(clean.LastBenefitVerifiedUtc);
        Assert.Equal(2, clean.Rollbacks);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("task")]
    [InlineData("driver")]
    public void ActiveLocalWindowsOwnershipAlwaysWinsOverCloudMemory(string kind)
    {
        var local = new AiSystemRuntimeItemState(
            kind, "Fixture", true, true, "owned", null, null,
            3, true, true, 0, "active")
        {
            PendingMutation = kind == "task"
                ? AiMaintenanceWriteAheadPolicy.TaskDisable
                : kind == "driver"
                    ? AiMaintenanceWriteAheadPolicy.DriverDisable
                    : AiMaintenanceWriteAheadPolicy.ServiceStop,
            VerificationStartedUtc = Now.AddMinutes(-2)
        };
        var remote = local with { Managed = false, Sleeping = false, Rollbacks = 3,
            FailedBenefitTrials = 3 };
        Assert.Null(AiCloudProfilePolicy.MergeLearnedProtection(local, remote, Now));
        Assert.Null(AiCloudProfilePolicy.MergeLearnedProtection(
            local with { Managed = false }, remote, Now));
        Assert.Null(AiCloudProfilePolicy.MergeLearnedProtection(
            local with { Managed = false, Sleeping = false }, remote, Now));
        Assert.Null(AiCloudProfilePolicy.MergeLearnedProtection(
            local with { Managed = false, Sleeping = false,
                PendingMutation = "", VerificationStartedUtc = null }, remote, Now));
    }

    [Fact]
    public void CloudMergesOnlyMoreConservativeNegativeExperience()
    {
        var local = new AiSystemRuntimeItemState(
            "service", "Fixture", false, false, "recent use",
            Now.AddMinutes(-30), Now.AddHours(2), null, null, false, 2, "local")
        {
            FailedBenefitTrials = 1,
            LastVerifiedBenefitAbsolute = 0.6,
            LastVerifiedBenefitPercent = 25,
            LastBenefitVerifiedUtc = Now.AddHours(-1),
            FeatureLastConfirmedUseUtc = Now.AddMinutes(-10)
        };
        var remote = local with { Rollbacks = 3, FailedBenefitTrials = 2,
            LastVerifiedBenefitAbsolute = 12.0,
            LastVerifiedAvailableRamBenefitMiB = 100000 };
        var merged = AiCloudProfilePolicy.MergeLearnedProtection(local, remote, Now);
        Assert.NotNull(merged);
        Assert.Equal(3, merged!.Rollbacks);
        Assert.Equal(2, merged.FailedBenefitTrials);
        Assert.Equal(local.ObservationSinceUtc, merged.ObservationSinceUtc);
        Assert.Equal(local.CooldownUntilUtc, merged.CooldownUntilUtc);
        Assert.Equal(local.FeatureLastConfirmedUseUtc, merged.FeatureLastConfirmedUseUtc);
        Assert.Equal(local.LastVerifiedBenefitAbsolute, merged.LastVerifiedBenefitAbsolute);
        Assert.Equal(local.LastVerifiedBenefitPercent, merged.LastVerifiedBenefitPercent);
        Assert.Equal(local.LastBenefitVerifiedUtc, merged.LastBenefitVerifiedUtc);
        Assert.Null(AiCloudProfilePolicy.MergeLearnedProtection(merged, remote, Now));
    }

    [Fact]
    public void CloudWithoutNegativeEvidenceCannotCreateExperimentOrTrial()
    {
        var remote = new AiSystemRuntimeItemState(
            "driver", "Fixture", true, true, "import",
            null, null, 3, null, true, 0, "attempt")
        {
            PendingMutation = AiMaintenanceWriteAheadPolicy.DriverDisable,
            VerificationStartedUtc = Now.AddMinutes(-4),
            LastVerifiedBenefitAbsolute = 0.6
        };
        Assert.Null(AiCloudProfilePolicy.MergeLearnedProtection(null, remote, Now));
        var learned = AiCloudProfilePolicy.MergeLearnedProtection(
            null, remote with { Rollbacks = 2, FailedBenefitTrials = 5 }, Now);
        Assert.NotNull(learned);
        Assert.False(learned!.Managed);
        Assert.False(learned.Sleeping);
        Assert.Empty(learned.PendingMutation);
        Assert.Null(learned.OriginalServiceStart);
        Assert.Null(learned.VerificationStartedUtc);
        Assert.Equal(2, learned.Rollbacks);
        Assert.Equal(3, learned.FailedBenefitTrials);
        Assert.Equal(0, learned.LastVerifiedBenefitAbsolute);
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
