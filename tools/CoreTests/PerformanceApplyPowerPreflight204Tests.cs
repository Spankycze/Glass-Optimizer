using SpankyhoOptimizer.Services;
using Xunit;

namespace CoreTests;

// All command outputs below are synthetic. Never execute powercfg, update
// live power schemes, COM tasks, Windows services, registry or fan control.
public sealed class PerformanceApplyPowerPreflight204Tests
{
    private const string A = "11111111-2222-3333-4444-555555555555";
    private const string B = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string C = "99999999-8888-7777-6666-555555555555";
    private const string Empty = "00000000-0000-0000-0000-000000000000";
    private static string Line(string guid, string name = "Spankyho Performance") =>
        "Power Scheme GUID: " + guid + " (" + name + ")";
    private static string Inventory =>
        Line(C, "Balanced") + "\r\n" + Line(A) + "\r\n" + Line(B);

    private static PerformanceProfilesService.PerformanceApplyPreflight Plan(
        string? active = null, string? list = null, bool activeSuccess = true,
        bool listSuccess = true) =>
        PerformanceProfilesService.BuildPerformanceApplyPreflight(
            activeSuccess, active ?? Line(C, "Balanced"), listSuccess,
            list ?? Inventory);

    [Fact]
    public void ExistingPlansAreReusedAndActivePlanIsRecordedForRollback()
    {
        var result = Plan();
        Assert.Equal(C, result.ActiveGuid);
        Assert.Equal(new[] { A, B }, result.ReusableGuids);
    }

    [Fact]
    public void ActivePerformancePlanIsPreferredOverOlderCandidate()
    {
        var result = Plan(Line(B), Inventory);
        Assert.Equal(B, result.ActiveGuid);
        Assert.Equal(new[] { B, A }, result.ReusableGuids);
    }

    [Fact]
    public void ActivePlanWithNoPerformanceCandidatesPermitsNewPlanCreation()
    {
        var result = Plan(Line(C, "Balanced"), Line(C, "Balanced"));
        Assert.Equal(C, result.ActiveGuid);
        Assert.Empty(result.ReusableGuids);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void FailedPowercfgStatusNeverPermitsProfileMutation(
        bool activeSuccess, bool listSuccess)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Plan(activeSuccess: activeSuccess, listSuccess: listSuccess));
    }

    [Fact]
    public void ContradictoryActiveGuidsFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Plan(active: Line(C, "Balanced") + "\nDiagnostic: " + A));
    }

    [Fact]
    public void DuplicateSameActiveGuidIsUnambiguous()
    {
        var result = Plan(active: Line(C, "Balanced") + "\n" + Line(C, "Balanced"));
        Assert.Equal(C, result.ActiveGuid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("access denied")]
    [InlineData("Power Scheme GUID: bad-guid")]
    [InlineData("Power Scheme GUID: 11111111-2222")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void InvalidActiveReadBlocksApplyBeforeAnyPowerSettingWrites(string result)
    {
        Assert.Throws<InvalidOperationException>(() => Plan(active: result));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Cannot enumerate power schemes")]
    [InlineData("Power Scheme GUID: not-a-guid")]
    public void SuccessfulButIncompleteInventoryCannotAdmitProfileApply(string output)
    {
        Assert.Throws<InvalidOperationException>(() => Plan(list: output));
    }

    [Fact]
    public void ListMissingActualActiveSchemeFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Plan(list: Line(A) + "\n" + Line(B)));
    }

    [Fact]
    public void ZeroGuidInCandidateListIsNotReused()
    {
        var result = Plan(list: Inventory + "\n" + Line(Empty));
        Assert.Equal(new[] { A, B }, result.ReusableGuids);
    }

    [Fact]
    public void RepeatedPerformanceCandidatesAreDeduplicated()
    {
        var result = Plan(list: Inventory + "\n" + Line(A) + "\n" + Line(B));
        Assert.Equal(new[] { A, B }, result.ReusableGuids);
    }

    [Fact]
    public void CandidateIdentifiersAreCanonicalizedCaseInsensitively()
    {
        var result = Plan(list: Line(C, "Balanced") + "\n" +
            Line(A.ToUpperInvariant()) + "\n" + Line(B));
        Assert.Equal(new[] { A, B }, result.ReusableGuids);
    }

    [Fact]
    public void NonPerformancePlansNeverBecomeReuseCandidates()
    {
        var result = Plan(list: Line(C, "Balanced") + "\n" +
            Line(A, "High performance") + "\n" + Line(B));
        Assert.Equal(new[] { B }, result.ReusableGuids);
    }

    [Fact]
    public void LocalizedPowerSchemeOutputIsSupported()
    {
        var result = Plan(active: "Aktivní schéma: " + C,
            list: "Schéma GUID: " + C + " (Vyvážený)\n" +
                  "Schéma GUID: " + A + " (Spankyho Performance)");
        Assert.Equal(C, result.ActiveGuid);
        Assert.Equal(new[] { A }, result.ReusableGuids);
    }

    [Fact]
    public void SuccessfulActivationIsValidatedByGuidRatherThanSubstring()
    {
        PerformanceProfilesService.EnsureAppliedPowerSchemeReadback(
            A, true, Line(A));
        PerformanceProfilesService.EnsureAppliedPowerSchemeReadback(
            A.ToUpperInvariant(), true, Line(A));
    }

    [Fact]
    public void IncorrectActiveSchemeBlocksPostActivationReadback()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureAppliedPowerSchemeReadback(
                A, true, Line(B)));
    }

    [Fact]
    public void AFailedActivationReadbackCannotBeTrustedEvenWithExpectedGuid()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureAppliedPowerSchemeReadback(
                A, false, Line(A)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad output")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MissingOrZeroGuidReadbackCannotConfirmActivation(string output)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureAppliedPowerSchemeReadback(
                A, true, output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData(Empty)]
    public void InvalidTargetCannotBeReportedAsActivated(string guid)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceProfilesService.EnsureAppliedPowerSchemeReadback(
                guid, true, Line(A)));
    }

    [Fact]
    public void CompiledSafeSmokeCoversApplyCommandPreflight()
    {
        Assert.True(PerformanceProfilesService.RunPerformanceApplyPreflight204Smoke());
        Assert.True(PerformanceProfilesService.RunDuplicatePowerPlanPreflight203Smoke());
    }
}
