using Microsoft.Win32;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiTweakRuntimeTests
{
    private static TweakDefinition Tweak(string id = "test.safe", TweakRisk risk = TweakRisk.Safe, bool restart = false) => new()
    {
        Id = id, Section = "performance", Group = "test", Name = id, Description = "test",
        Recommended = true, Risk = risk, RestartRequired = restart, RequiresAdmin = false,
        RevertMode = "OriginalRegistryValue", RevertAsync = _ => Task.CompletedTask,
        RegistryHive = RegistryHive.CurrentUser, RegistryPath = @"Software\AiRuntimeTest", RegistryValueName = id,
        RegistryEnabledValue = 1
    };
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public AiTweakRuntimeTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private sealed class Backend : IAiTweakRuntimeBackend
    {
        public Dictionary<string, string> Values = new();
        public bool FailVerify;
        public string? FailedProbeId;
        public string AccessDeferral = "";
        public string DeferredReason(TweakDefinition t) => AccessDeferral;
        public Task<string> CaptureAsync(TweakDefinition t, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Values.GetValueOrDefault(t.Id, "missing")); }
        public Task<bool> IsEnabledAsync(TweakDefinition t, CancellationToken ct) => t.Id == FailedProbeId ? throw new InvalidOperationException("Probe failed") : Task.FromResult(Values.GetValueOrDefault(t.Id) == "enabled");
        public Task ApplyAsync(TweakDefinition t, string backupId, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Values[t.Id] = FailVerify ? "bad" : "enabled"; return Task.CompletedTask; }
        public Task RestoreAsync(TweakDefinition t, string backupId, string original, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Values[t.Id] = original; return Task.CompletedTask; }
        public string ExpectedSignature(TweakDefinition t) => "enabled";
    }
    private static AiSystemRuntimeConfig Config(bool enabled = true) => new(Enabled: enabled, ObservationSeconds: 0, ActionCooldownSeconds: 0, TweaksEnabled: true);
    private static string Temp() => Path.Combine(Path.GetTempPath(), "spanky-ai-tests", Guid.NewGuid().ToString("N"), "ownership.json");

    [Fact] public async Task DisableRestoresExactOriginalAcrossRestart()
    {
        var path = Temp(); var b = new Backend(); b.Values["test.safe"] = "original42";
        var defs = new[] { Tweak() };
        var s = new AiTweakRuntimeService(() => defs, b, path);
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Equal("enabled", b.Values["test.safe"]);
        s = new AiTweakRuntimeService(() => defs, b, path);
        await s.TickAsync(Config(false), true, 0, false, default);
        Assert.Equal("original42", b.Values["test.safe"]);
        Assert.Equal(0, s.Health.Managed);
    }
    [Fact] public async Task ManualOverrideSurvivesDisableAndLaterEnable()
    {
        var b = new Backend(); var s = new AiTweakRuntimeService(() => new[] { Tweak() }, b, Temp());
        await s.TickAsync(Config(), true, 0, false, default);
        b.Values["test.safe"] = "manual17";
        await s.TickAsync(Config(false), true, 0, false, default);
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Equal("manual17", b.Values["test.safe"]);
        Assert.Equal(1, s.Health.ManualOverrides);
    }
    [Fact] public async Task FailedVerificationRollsBackAndDoesNotReapply()
    {
        var b = new Backend { FailVerify = true }; var s = new AiTweakRuntimeService(() => new[] { Tweak() }, b, Temp());
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Equal("missing", b.Values["test.safe"]);
        Assert.NotEmpty(s.Health.LastError);
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Equal(0, s.Health.Managed);
    }
    [Fact] public async Task BudgetGameAndCancellationPreventUnboundedMutations()
    {
        var b = new Backend(); var defs = Enumerable.Range(0, 8).Select(i => Tweak("t" + i)).ToArray();
        var s = new AiTweakRuntimeService(() => defs, b, Temp());
        await s.TickAsync(Config(), true, 0, true, default);
        Assert.Empty(b.Values);
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.InRange(b.Values.Count, 1, 3);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.TickAsync(Config(), true, 0, false, cts.Token));
        Assert.InRange(b.Values.Count, 1, 3);
    }
    [Fact] public async Task ProbeFailureIsDeferredAndOtherTargetsContinue()
    {
        var b = new Backend { FailedProbeId = "broken" };
        var s = new AiTweakRuntimeService(() => new[] { Tweak("broken"), Tweak("healthy") }, b, Temp());
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Equal("enabled", b.Values["healthy"]);
        Assert.Contains(s.Health.Items, i => i.Id == "broken" && !i.Eligible && i.Reason.Contains("Probe failed"));
        b.FailedProbeId = null;
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.False(b.Values.ContainsKey("broken"));
    }
    [Fact] public async Task ObservationCooldownAndScopeGateAreHonored()
    {
        var b = new Backend(); var s = new AiTweakRuntimeService(() => new[] { Tweak() }, b, Temp());
        await s.TickAsync(Config() with { ObservationSeconds = 300 }, true, 0, false, default);
        Assert.Empty(b.Values);
        await s.TickAsync(Config() with { TweaksEnabled = false }, true, 0, false, default);
        Assert.Empty(b.Values);
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Equal("enabled", b.Values["test.safe"]);
        await s.TickAsync(Config() with { TweaksEnabled = false }, true, 0, false, default);
        Assert.Equal("missing", b.Values["test.safe"]);
    }
    [Fact] public async Task AdministratorDeferralIsTransient()
    {
        var b = new Backend { AccessDeferral = "Requires administrator" };
        var path = Temp(); var defs = new[] { Tweak() };
        var s = new AiTweakRuntimeService(() => defs, b, path);
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Empty(b.Values);
        Assert.False(s.Health.Items[0].Eligible);
        b.AccessDeferral = "";
        s = new AiTweakRuntimeService(() => defs, b, path);
        await s.TickAsync(Config(), true, 0, false, default);
        Assert.Equal("enabled", b.Values["test.safe"]);
    }
    [Fact] public void RegisteredWindowsCatalogAudit()
    {
        var defs = CatalogService.EnumerateTweakSources().Where(s => s.Section != "browsers")
            .SelectMany(s => s.Factory()).GroupBy(t => t.Id).Select(g => g.First()).ToArray();
        var report = defs.GroupBy(t => t.Section).Select(g => new {
            Section = g.Key, Total = g.Count(), Eligible = g.Count(t => AiTweakRuntimePolicy.DeferredReason(t) == ""),
            Deferred = g.Where(t => AiTweakRuntimePolicy.DeferredReason(t) != "")
                .GroupBy(AiTweakRuntimePolicy.DeferredReason).Select(x => new { Reason = x.Key, Count = x.Count() }).ToArray()
        }).ToArray();
        _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
        Assert.True(defs.Length > 100);
        Assert.Contains(defs, t => t.Section == "system" && AiTweakRuntimePolicy.DeferredReason(t) == "");
        Assert.Contains(defs, t => t.Section == "ui" && AiTweakRuntimePolicy.DeferredReason(t) == "");
        Assert.Contains(defs, t => t.Section == "privacy" && AiTweakRuntimePolicy.DeferredReason(t) == "");
        Assert.DoesNotContain(defs, t => t.Section == "defender" && AiTweakRuntimePolicy.DeferredReason(t) == "");
        Assert.DoesNotContain(defs, t => t.Section == "browsers");
    }
    [Fact] public void PolicyDefersAdvancedRestartAndSensitiveTargets()
    {
        Assert.Empty(AiTweakRuntimePolicy.DeferredReason(Tweak()));
        Assert.NotEmpty(AiTweakRuntimePolicy.DeferredReason(Tweak(risk: TweakRisk.Advanced)));
        Assert.NotEmpty(AiTweakRuntimePolicy.DeferredReason(Tweak(restart: true)));
        var defender = new TweakDefinition { Id = "security", Section = "defender", Group = "", Name = "", Description = "", Recommended = true };
        Assert.NotEmpty(AiTweakRuntimePolicy.DeferredReason(defender));
    }
}
