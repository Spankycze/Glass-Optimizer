using SpankyhoOptimizer.Services;
using Xunit;

namespace CoreTests;

// Synthetic powercfg stdout only. No power plans, registry, tasks or Windows
// settings are read or modified by these tests.
public sealed class PowerPlanInventoryRowSafety207Tests
{
    private const string A = "11111111-2222-3333-4444-555555555555";
    private const string B = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string C = "99999999-8888-7777-6666-555555555555";
    private static string Row(string id, string label = "Spankyho Performance") =>
        "Power Scheme GUID: " + id + " (" + label + ")";
    private static PerformanceProfilesService.DuplicatePowerPlanCleanupPlan Parse(
        string inventory, string active = A) =>
        PerformanceProfilesService.BuildDuplicatePowerPlanCleanupPlan(
            true, "Power Scheme GUID: " + active, true, inventory);

    [Theory]
    [InlineData("Warning: Spankyho Performance plan GUID: ")]
    [InlineData("Cleanup candidate: ")]
    [InlineData("Power Scheme GUID = ")]
    public void MentionsWithoutCompleteInventoryRowsCannotAdmitDeletion(string prefix)
    {
        string output = Row(A) + "\n" + prefix + B + " (Spankyho Performance)";
        Assert.Empty(Parse(output).DeleteGuids);
    }

    [Theory]
    [InlineData("Power Scheme GUID: " + B + " (Spankyho Performance Extended)")]
    [InlineData("Power Scheme GUID: " + B + " (Not Spankyho Performance)")]
    [InlineData("Power Scheme GUID: " + B + " (Balanced) Spankyho Performance")]
    [InlineData("Power Scheme GUID: " + B + " (Spankyho Performance) diagnostic")]
    [InlineData("Power Scheme GUID: " + B + " (Spankyho Performance) **")]
    public void OnlyExactLabeledAndCompleteRowsCanBeDeleted(string suspect)
    {
        Assert.Empty(Parse(Row(A) + "\n" + suspect).DeleteGuids);
    }

    [Fact]
    public void ActiveGuidOnlyInDiagnosticDoesNotValidateIncompleteInventory()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Parse("Diagnostic active: " + A + "\n" + Row(B)));
    }

    [Fact]
    public void IncompleteActiveRowDoesNotAuthorizeInventory()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Parse("Power Scheme GUID: " + A + " (Balanced) truncated\n" + Row(B)));
    }

    [Fact]
    public void MultipleGuidsOnOneLineAreNotTreatedAsSingleInventoryEntry()
    {
        Assert.Empty(Parse(Row(A) + "\nPower Scheme GUID: " + B +
            " " + C + " (Spankyho Performance)").DeleteGuids);
    }

    [Fact]
    public void ActiveMarkerAndLocalizedPrefixAreStillAccepted()
    {
        var result = Parse(
            "Identifikátor GUID schématu napájení: " + A +
            " (Spankyho Performance) *\r\n" + Row(B));
        Assert.Equal(A, result.KeepGuid);
        Assert.Equal(new[] { B }, result.DeleteGuids);
    }

    [Fact]
    public void ExactLabelIsCaseInsensitiveAndTrimsSurroundingSpaces()
    {
        var result = Parse(Row(A) + "\nPower Scheme GUID: " + B +
            " (  spankyho performance  )");
        Assert.Equal(new[] { B }, result.DeleteGuids);
    }

    [Fact]
    public void ActiveNonPerformanceSchemeIsStillPreserved()
    {
        var result = Parse(Row(A, "Balanced") + "\n" + Row(B), A);
        Assert.Equal(A, result.ActiveGuid);
        Assert.Equal(B, result.KeepGuid);
        Assert.Empty(result.DeleteGuids);
    }

    [Fact]
    public void NestedParenthesesInRealWorldSchemeNameAreAcceptedForActiveInventory()
    {
        var result = Parse(
            Row(A, "CPUDoc Dynamic Power Plan Win11 V2 (Balanced)") + "\n" + Row(B));
        Assert.Equal(A, result.ActiveGuid);
        Assert.Equal(B, result.KeepGuid);
        Assert.Empty(result.DeleteGuids);
    }

    [Fact]
    public void NestedParenthesesCannotMasqueradeAsExactPerformanceLabel()
    {
        var result = Parse(Row(A) + "\n" + Row(B, "Other (Spankyho Performance)"));
        Assert.Equal(A, result.KeepGuid);
        Assert.Empty(result.DeleteGuids);
    }

    [Fact]
    public void ApplyPreflightCannotReuseMisleadingDiagnosticCandidate()
    {
        var result = PerformanceProfilesService.BuildPerformanceApplyPreflight(
            true, "Power Scheme GUID: " + A, true,
            Row(A, "Balanced") + "\nWarning: " + B + " is Spankyho Performance");
        Assert.Equal(A, result.ActiveGuid);
        Assert.Empty(result.ReusableGuids);
    }
}
