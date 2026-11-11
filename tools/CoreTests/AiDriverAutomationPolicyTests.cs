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
    public void DriverHoldIsBoundedAndNeverAppliesToUnmanagedEntries()
    {
        DateTime now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var row = new AiSystemRuntimeItemState("driver", "Fixture", true, true,
            string.Empty, null, null, 3, null, false, 0, "test",
            LastChangedUtc: now.AddHours(-23));
        Assert.False(AiDriverAutomationPolicy.ShouldRelease(row, now));
        Assert.True(AiDriverAutomationPolicy.ShouldRelease(
            row with { LastChangedUtc = now.AddHours(-24) }, now));
        Assert.True(AiDriverAutomationPolicy.ShouldRelease(
            row with { LastChangedUtc = null }, now));
        Assert.False(AiDriverAutomationPolicy.ShouldRelease(row with { Managed = false }, now));
        Assert.False(AiDriverAutomationPolicy.ShouldRelease(row with
        {
            OriginalServiceStart = null, LastChangedUtc = null
        }, now));
        Assert.False(AiDriverAutomationPolicy.ShouldRelease(row with
        {
            OriginalServiceStart = 2, LastChangedUtc = null
        }, now));
        Assert.False(AiDriverAutomationPolicy.ShouldRelease(row with { Kind = "task" }, now));
    }

    [Fact]
    public void AttachedDeviceRecoveryNeverInventsAnOriginalDriverSetting()
    {
        var state = new AiSystemRuntimeItemState("driver", "serenum",
            true, true, "", null, null, 3, null, false, 0, "owned");
        Assert.True(AiDriverAutomationPolicy.CanRestoreAttachedDevice(state, 4, true));
        Assert.False(AiDriverAutomationPolicy.CanRestoreAttachedDevice(
            state with { OriginalServiceStart = null }, 4, true));
        Assert.False(AiDriverAutomationPolicy.CanRestoreAttachedDevice(
            state with { OriginalServiceStart = 2 }, 4, true));
        Assert.False(AiDriverAutomationPolicy.CanRestoreAttachedDevice(
            state with { Managed = false }, 4, true));
        Assert.False(AiDriverAutomationPolicy.CanRestoreAttachedDevice(
            state with { Kind = "service" }, 4, true));
        Assert.False(AiDriverAutomationPolicy.CanRestoreAttachedDevice(state, 3, true));
        Assert.False(AiDriverAutomationPolicy.CanRestoreAttachedDevice(state, 4, false));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void AttachedDeviceRestoreLearnsDemandAndDropsStaleDriverJournal(
        int prior, int learned)
    {
        DateTime now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var candidate = new AiSystemRuntimeItemState(
            "driver", "Fixture", true, true, "", null, null,
            3, null, false, prior, "disabled")
        {
            ObservationSinceUtc = now.AddMinutes(-35),
            DriverObservationLastUtc = now.AddMinutes(-5),
            PendingMutation = AiMaintenanceWriteAheadPolicy.DriverDisable,
            VerificationStartedUtc = now.AddMinutes(-20),
            LastBenefitVerifiedUtc = now.AddHours(-1),
            LastVerifiedBenefitAbsolute = 0.45,
            LastVerifiedAvailableRamBenefitMiB = 128
        };
        Assert.True(AiDriverAutomationPolicy.CanRestoreAttachedDevice(
            candidate, 4, attached: true));
        var released = AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
            AiMaintenanceOwnershipPolicy.Relinquish(
                candidate, now, "device reattached", TimeSpan.FromHours(24)),
            externallyNeeded: true) with
        {
            Rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                candidate.Rollbacks, externallyNeeded: true)
        };
        Assert.Equal(learned, released.Rollbacks);
        Assert.False(released.Managed);
        Assert.False(released.Sleeping);
        Assert.Null(released.OriginalServiceStart);
        Assert.Null(released.ObservationSinceUtc);
        Assert.Null(released.DriverObservationLastUtc);
        Assert.Empty(released.PendingMutation);
        Assert.Null(released.VerificationStartedUtc);
        Assert.Null(released.LastBenefitVerifiedUtc);
        Assert.Null(released.LastVerifiedAvailableRamBenefitMiB);
        Assert.Equal(0, released.LastVerifiedBenefitAbsolute);
        Assert.False(AiMaintenanceWriteAheadPolicy.IsPending(released));
    }

    [Theory]
    [InlineData(3, 4, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 2, false)]
    [InlineData(2, 4, false)]
    [InlineData(1, 4, false)]
    [InlineData(0, 4, false)]
    [InlineData(4, 4, false)]
    public void OnlyDemandStartCanOwnDisabledDriverForRestore(
        int originalStart, int liveStart, bool owned)
    {
        Assert.Equal(owned, AiMaintenanceOwnershipPolicy.DriverStartStillOwned(
            originalStart, liveStart));
    }

    [Theory]
    [InlineData(3, 4, "restore")]
    [InlineData(3, 3, "unchanged")]
    [InlineData(3, 2, "external")]
    [InlineData(2, 4, "external")]
    [InlineData(1, 4, "external")]
    [InlineData(0, 4, "external")]
    [InlineData(4, 4, "external")]
    [InlineData(null, 4, "external")]
    public void CrashJournalNeverRestoresIllegalOriginalDriverStart(
        int? originalStart, int liveStart, string expected)
    {
        Assert.Equal(expected, AiMaintenanceWriteAheadPolicy.DriverRecoveryState(
            originalStart, liveStart));
        Assert.Equal(originalStart == 3 && liveStart == 3,
            AiMaintenanceOwnershipPolicy.DriverAlreadyRestored(
                originalStart, liveStart));
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    [InlineData(3, 2, false)]
    [InlineData(2, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(4, 4, false)]
    [InlineData(null, 3, false)]
    public void OriginalDriverMustBeReadBackBeforeJournalRelease(
        int? originalStart, int actualStart, bool restored)
    {
        Assert.Equal(restored,
            AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                originalStart, actualStart) is null);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void InitialNextBootDisableMustBeReadBackBeforeCommit(
        int liveStart, bool confirmed)
    {
        Assert.Equal(confirmed,
            AiMaintenanceWriteAheadPolicy.DriverDisableReadbackReason(
                liveStart) is null);
    }

    [Fact]
    public void ExternalDriverEnableCannotLeaveOldPendingOwner()
    {
        DateTime now = new(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);
        var held = new AiSystemRuntimeItemState(
            "driver", "Fixture", true, true, "", null, null,
            3, null, false, 1, "hold")
        { PendingMutation = AiMaintenanceWriteAheadPolicy.DriverDisable };
        var yielded = AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
            AiMaintenanceOwnershipPolicy.Relinquish(
                held, now, "external driver enable", TimeSpan.FromHours(12)),
            externallyNeeded: true) with
        {
            Rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                held.Rollbacks, externallyNeeded: true)
        };
        Assert.Equal(2, yielded.Rollbacks);
        Assert.False(yielded.Managed);
        Assert.Null(yielded.OriginalServiceStart);
        Assert.Empty(yielded.PendingMutation);
        Assert.Null(yielded.LastBenefitVerifiedUtc);
    }

    [Theory]
    [InlineData(true, true, 1, 3, false, true, true)]
    [InlineData(false, true, 1, 3, false, true, false)]
    [InlineData(true, false, 1, 3, false, true, false)]
    [InlineData(true, true, 2, 3, false, true, false)]
    [InlineData(true, true, 0, 3, false, true, false)]
    [InlineData(true, true, 1, 0, false, true, false)]
    [InlineData(true, true, 1, 1, false, true, false)]
    [InlineData(true, true, 1, 2, false, true, false)]
    [InlineData(true, true, 1, 4, false, true, false)]
    [InlineData(true, true, 1, 3, true, true, false)]
    [InlineData(true, true, 1, 3, false, false, false)]
    public void FinalDriverMutationRequiresFreshIndependentEvidence(
        bool installed, bool windowsOwned, int serviceType, int currentStart,
        bool attached, bool freshQuiet, bool allowed)
    {
        string? blocked = AiDriverAutomationPolicy.MutationBlockReason(
            installed, windowsOwned, serviceType, currentStart, attached, freshQuiet);
        Assert.Equal(allowed, blocked is null);
    }

    [Theory]
    [InlineData(System.ServiceProcess.ServiceControllerStatus.Stopped, true)]
    [InlineData(System.ServiceProcess.ServiceControllerStatus.Running, false)]
    [InlineData(System.ServiceProcess.ServiceControllerStatus.StartPending, false)]
    [InlineData(System.ServiceProcess.ServiceControllerStatus.StopPending, false)]
    [InlineData(System.ServiceProcess.ServiceControllerStatus.Paused, false)]
    public void LiveKernelDriverMustActuallyBeStopped(
        System.ServiceProcess.ServiceControllerStatus status, bool safe)
    {
        Assert.Equal(safe, AiDriverAutomationPolicy.LiveDriverBlockReason(true, status) is null);
        Assert.NotNull(AiDriverAutomationPolicy.LiveDriverBlockReason(false, status));
    }

    [Fact]
    public void DriverObservationNeedsUninterruptedRecentLiveSamples()
    {
        DateTime now = new(2026, 10, 15, 12, 20, 0, DateTimeKind.Utc);
        DateTime begin = now.AddMinutes(-30);
        DateTime seen = now.AddMinutes(-5);
        Assert.Equal(begin, AiDriverAutomationPolicy.ObservationStart(begin, seen, now));
        Assert.Equal(begin, AiDriverAutomationPolicy.ObservationStart(
            begin, now.AddMinutes(-10), now));
        Assert.Equal(now, AiDriverAutomationPolicy.ObservationStart(
            begin, now.AddMinutes(-11), now));
        Assert.Equal(now, AiDriverAutomationPolicy.ObservationStart(
            begin, null, now));
        Assert.Equal(now, AiDriverAutomationPolicy.ObservationStart(
            null, seen, now));
        Assert.Equal(now, AiDriverAutomationPolicy.ObservationStart(
            now.AddMinutes(1), now, now));
        Assert.Equal(now, AiDriverAutomationPolicy.ObservationStart(
            begin, now.AddMinutes(1), now));
        Assert.Equal(now, AiDriverAutomationPolicy.ObservationStart(
            begin, DateTime.SpecifyKind(seen, DateTimeKind.Unspecified), now));
        // A legacy journal has no DriverObservationLastUtc; it must restart
        // observation rather than inheriting an unverified 30-minute period.
        var legacy = new AiSystemRuntimeItemState("driver", "flpydisk",
            false, false, "", begin, null, 3, null, false, 0, "legacy");
        var decoded = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(
            System.Text.Json.JsonSerializer.Serialize(legacy)
                .Replace("\"DriverObservationLastUtc\":null,", "", StringComparison.Ordinal)
                .Replace(",\"DriverObservationLastUtc\":null", "", StringComparison.Ordinal));
        Assert.NotNull(decoded);
        Assert.Equal(now, AiDriverAutomationPolicy.ObservationStart(
            decoded!.ObservationSinceUtc, decoded.DriverObservationLastUtc, now));
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
