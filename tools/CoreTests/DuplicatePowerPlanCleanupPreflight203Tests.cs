using SpankyhoOptimizer.Services;
using Xunit;

namespace CoreTests;

// All /getactivescheme and /list responses are synthetic strings. No
// powercfg.exe, Task Scheduler, real plans or Windows writes are performed.
public sealed class DuplicatePowerPlanCleanupPreflight203Tests
{
    private const string A = "11111111-2222-3333-4444-555555555555";
    private const string B = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string C = "99999999-8888-7777-6666-555555555555";
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    private static string Active(string guid) =>
        "Power Scheme GUID: " + guid + "  (Spankyho Performance)";
    private static string Listed(string guid, string label = "Spankyho Performance") =>
        "Power Scheme GUID: " + guid + "  (" + label + ")";
    private static string Both = Listed(A) + "\r\n" + Listed(B) + "\r\n";
    private static PerformanceProfilesService.DuplicatePowerPlanCleanupPlan Plan(
        string? active = null, string? listed = null,
        bool activeOk = true, bool listOk = true) =>
        PerformanceProfilesService.BuildDuplicatePowerPlanCleanupPlan(
            activeOk, active ?? Active(A), listOk, listed ?? Both);

    [Fact]
    public void AnActiveSpankyhoPlanIsAlwaysPreserved()
    {
        var result = Plan();
        Assert.Equal(A, result.ActiveGuid);
        Assert.Equal(A, result.KeepGuid);
        Assert.Equal(new[] { B }, result.DeleteGuids);
        Assert.DoesNotContain(result.ActiveGuid, result.DeleteGuids);
    }

    [Fact]
    public void ActivePlanAppearingLastInInventoryIsKept()
    {
        var result = Plan(Active(B), Listed(A) + "\n" + Listed(B));
        Assert.Equal(B, result.KeepGuid);
        Assert.Equal(new[] { A }, result.DeleteGuids);
    }

    [Fact]
    public void ActiveSystemPlanOutsideSpankyhoCandidatesIsUntouched()
    {
        var result = Plan(Active(C), Listed(C, "Balanced") + "\n" + Both);
        Assert.Equal(C, result.ActiveGuid);
        Assert.Equal(A, result.KeepGuid);
        Assert.Equal(new[] { B }, result.DeleteGuids);
        Assert.DoesNotContain(C, result.DeleteGuids);
    }

    [Fact]
    public void FailedActiveCommandNeverProducesAnyDeletionTarget()
    {
        Assert.Throws<InvalidOperationException>(
            () => Plan(activeOk: false));
    }

    [Fact]
    public void FailedActiveCommandWithPlausibleStdoutIsStillRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => Plan(Active(A), Both, activeOk: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("powercfg: access denied")]
    [InlineData("GUID: not-a-guid")]
    [InlineData("Power Scheme GUID: 11111111-2222-3333-4444")]
    public void ActiveCommandWithoutValidGuidFailsClosed(string output)
    {
        Assert.Throws<InvalidOperationException>(() => Plan(output, Both));
    }

    [Fact]
    public void EmptyGuidCannotBeAcceptedAsAnActivePlan()
    {
        Assert.Throws<InvalidOperationException>(
            () => Plan(Active(EmptyGuid), Listed(EmptyGuid) + "\n" + Both));
    }

    [Fact]
    public void FailedListCommandCannotBeUsedEvenIfItHasPlausibleText()
    {
        Assert.Throws<InvalidOperationException>(
            () => Plan(Active(A), Both, listOk: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Power schemes are unavailable")]
    public void SuccessfullyReturnedButUnusableListIsRejected(string listing)
    {
        Assert.Throws<InvalidOperationException>(() => Plan(Active(A), listing));
    }

    [Fact]
    public void ListingWithoutActiveGuidIsIncompleteAndMustBlockDeletion()
    {
        Assert.Throws<InvalidOperationException>(
            () => Plan(Active(C), Both));
    }

    [Fact]
    public void ListingWithOnlyTheWrongPowerSchemeIsNotReliable()
    {
        Assert.Throws<InvalidOperationException>(
            () => Plan(Active(A), Listed(B)));
    }

    [Fact]
    public void OneCandidateHasNothingToDelete()
    {
        var result = Plan(Active(A), Listed(A));
        Assert.Equal(A, result.KeepGuid);
        Assert.Empty(result.DeleteGuids);
    }

    [Fact]
    public void NoSpankyhoCandidatesIsSafeWhenActivePlanIsInInventory()
    {
        var result = Plan(Active(C), Listed(C, "Balanced"));
        Assert.Null(result.KeepGuid);
        Assert.Empty(result.DeleteGuids);
    }

    [Fact]
    public void DuplicatedLinesCannotDoubleDeleteAScheme()
    {
        var result = Plan(Active(A), Both + Listed(B) + "\r\n" + Listed(B));
        Assert.Equal(new[] { B }, result.DeleteGuids);
    }

    [Fact]
    public void MixedCaseGuidsAreNormalizedForPlanIdentity()
    {
        var result = Plan(Active(B.ToUpperInvariant()),
            Listed(B.ToUpperInvariant()) + "\n" + Listed(A));
        Assert.Equal(B, result.ActiveGuid);
        Assert.Equal(B, result.KeepGuid);
        Assert.Equal(new[] { A }, result.DeleteGuids);
    }

    [Fact]
    public void MalformedCandidateLineIsNotMadeIntoDeletionTarget()
    {
        var result = Plan(Active(A), Listed(A) +
            "\nPower Scheme GUID: not-a-guid (Spankyho Performance)\n" + Listed(B));
        Assert.Equal(new[] { B }, result.DeleteGuids);
    }

    [Fact]
    public void ZeroGuidCandidateCannotBeDeleted()
    {
        var result = Plan(Active(A), Listed(A) + "\n" +
            Listed(EmptyGuid) + "\n" + Listed(B));
        Assert.Equal(new[] { B }, result.DeleteGuids);
    }

    [Fact]
    public void ThreeMatchingPowerPlansPreserveTheActiveOne()
    {
        var result = Plan(Active(B), Listed(A) + "\n" + Listed(B) + "\n" + Listed(C));
        Assert.Equal(B, result.KeepGuid);
        Assert.Equal(new[] { A, C }, result.DeleteGuids);
    }

    [Fact]
    public void CaseInsensitivePlanLabelIsRecognized()
    {
        var result = Plan(Active(A),
            Listed(A, "SPANKYHO PERFORMANCE") + "\n" +
            Listed(B, "spankyho performance"));
        Assert.Equal(new[] { B }, result.DeleteGuids);
    }

    [Fact]
    public void CarriageReturnOnlyOutputIsSupported()
    {
        var result = Plan(Active(A), Listed(A) + "\r" + Listed(B));
        Assert.Equal(new[] { B }, result.DeleteGuids);
    }

    [Fact]
    public void ActiveGuidInDifferentDisplayLanguageIsStillRecognized()
    {
        var result = Plan("Aktivní plán: " + A,
            "Schéma GUID: " + A + " (Spankyho Performance)\n" +
            "Schéma GUID: " + B + " (Spankyho Performance)");
        Assert.Equal(A, result.KeepGuid);
        Assert.Single(result.DeleteGuids);
    }

    [Fact]
    public void FreshActiveReadBeforeDeletionAllowsOnlyTheApprovedCandidate()
    {
        var plan = Plan();
        PerformanceProfilesService.EnsureDuplicatePowerPlanDeletionStillSafe(
            plan, B, true, Active(A));
        Assert.Equal(new[] { B }, plan.DeleteGuids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveSchemeSwitchBeforeDeletionBlocksEvenAnApprovedTarget(bool switchedToCandidate)
    {
        var plan = Plan();
        string changed = switchedToCandidate ? B : C;
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureDuplicatePowerPlanDeletionStillSafe(
                plan, B, true, Active(changed)));
    }

    [Fact]
    public void FailureToRecheckActiveSchemeBlocksDelete()
    {
        var plan = Plan();
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureDuplicatePowerPlanDeletionStillSafe(
                plan, B, false, Active(A)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Power Scheme GUID missing")]
    [InlineData("Power Scheme GUID: malformed")]
    public void UnparseableFreshActiveReadBlocksDeletion(string output)
    {
        var plan = Plan();
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureDuplicatePowerPlanDeletionStillSafe(
                plan, B, true, output));
    }

    [Theory]
    [InlineData(A)]
    [InlineData(C)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData(EmptyGuid)]
    public void OnlyListedDeletionCandidatesArePermitted(string candidateGuid)
    {
        var plan = Plan();
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureDuplicatePowerPlanDeletionStillSafe(
                plan, candidateGuid, true, Active(A)));
    }

    [Fact]
    public void EachOfMultipleCandidatesCanBeIndependentlyValidated()
    {
        var plan = Plan(Active(A), Listed(A) + "\n" + Listed(B) + "\n" + Listed(C));
        PerformanceProfilesService.EnsureDuplicatePowerPlanDeletionStillSafe(
            plan, B, true, Active(A));
        PerformanceProfilesService.EnsureDuplicatePowerPlanDeletionStillSafe(
            plan, C, true, Active(A));
    }

    [Fact]
    public void CompiledServicePolicySmokeCoversFailedActiveRead()
    {
        Assert.True(PerformanceProfilesService.RunDuplicatePowerPlanPreflight203Smoke());
        Assert.True(PowerPlanGuardService.RunOwnedGuardTask191Smoke());
    }
}
