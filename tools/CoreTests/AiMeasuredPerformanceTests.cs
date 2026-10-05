using Microsoft.Win32;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace CoreTests;
[CollectionDefinition("AI performance experiments", DisableParallelization = true)]
public sealed class AiPerformanceExperimentCollection { }

[Collection("AI performance experiments")]
public sealed class AiMeasuredPerformanceTests
{
    private DateTime _clock = DateTime.UnixEpoch;
    private Task Tick(AiTweakRuntimeService s, bool game, AiSystemPerformanceSample sample)
    { _clock = sample.Utc; return s.TickMeasuredAsync(Config(), true, 10, game, sample, default); }
    private static AiSystemPerformanceSample S(int n, double work = 10, double cpu = 10, string key = "work", bool valid = true) =>
        new(DateTime.UnixEpoch.AddSeconds(n * 6), cpu, 4000, 2, work, key, 55, valid, "", "CompatTelRunner", cpu);
    private static AiSystemPerformanceSample[] Window(int start, double work = 10, double cpu = 10, string key = "work") =>
        Enumerable.Range(start, 6).Select(i => S(i, work, cpu, key)).ToArray();

    [Fact] public void RequiresRecoveredReferenceAndSignificantProductivityGain()
    {
        var good = AiSystemPerformancePolicy.Compare(Window(0), Window(7, 8), Window(14));
        Assert.True(good.Accepted, good.Reason);
        Assert.False(AiSystemPerformancePolicy.Compare(Window(0), Window(7, 9.8), Window(14)).Accepted);
        Assert.False(AiSystemPerformancePolicy.Compare(Window(0), Window(7, 8), Window(14, 8)).Accepted);
    }
    [Fact] public void DroppedWorkloadMissingDataAndRegressionNeverAccepted()
    {
        Assert.False(AiSystemPerformancePolicy.Compare(Window(0), Window(7, 8, 4), Window(14)).Accepted);
        Assert.False(AiSystemPerformancePolicy.Compare(Window(0), Window(7, 8, key:"different"), Window(14)).Accepted);
        var missing = Window(7, 8); missing[2] = missing[2] with { Valid = false };
        Assert.False(AiSystemPerformancePolicy.Compare(Window(0), missing, Window(14)).Accepted);
        var memory = Window(7, 8).Select(x => x with { UsedMemoryMb = 6000 }).ToArray();
        Assert.False(AiSystemPerformancePolicy.Compare(Window(0), memory, Window(14)).Accepted);
    }
    private sealed class Backend : IAiTweakRuntimeBackend
    {
        public string Value = "original";
        public Task<string> CaptureAsync(TweakDefinition t, CancellationToken ct) => Task.FromResult(Value);
        public Task<bool> IsEnabledAsync(TweakDefinition t, CancellationToken ct) => Task.FromResult(Value == "enabled");
        public string ExpectedSignature(TweakDefinition t) => "enabled";
        public Task ApplyAsync(TweakDefinition t, string id, CancellationToken ct) { Value = "enabled"; return Task.CompletedTask; }
        public Task RestoreAsync(TweakDefinition t, string id, string original, CancellationToken ct) { Value = original; return Task.CompletedTask; }
    }
    private static TweakDefinition T() => new() { Id="perf.synthetic", Section="performance", Group="", Name="test",Description="",
        Recommended=true, RequiresAdmin=false, RegistryHive=RegistryHive.CurrentUser, RegistryPath="test", RegistryValueName="x",RegistryEnabledValue=1,
        RevertMode="OriginalRegistryValue",RevertAsync=_=>Task.CompletedTask };
    private static string Temp() => Path.Combine(Path.GetTempPath(),"ai-measured-tests",Guid.NewGuid().ToString("N"),"ownership.json");
    private static AiSystemRuntimeConfig Config() => new(ObservationSeconds:0,ActionCooldownSeconds:0,TweaksEnabled:true);

    [Fact] public async Task RestoresBetweenCandidateAndReferenceThenKeepsOnlyProvenGain()
    {
        var b=new Backend(); var s=new AiTweakRuntimeService(()=>new[]{T()},b,Temp(),feedbackRequired:true, utcNow:()=>_clock);
        foreach(var sample in Window(0)) await Tick(s,false,sample);
        Assert.Equal("enabled",b.Value);
        foreach(var sample in Window(7,8)) await Tick(s,false,sample);
        Assert.Equal("original",b.Value);
        foreach(var sample in Window(14)) await Tick(s,false,sample);
        Assert.Equal("enabled",b.Value);
        Assert.Equal(1,s.Health.Accepted);
    }
    [Fact] public async Task RestartAbortsTrialAndManualOverrideIsPreserved()
    {
        var path=Temp();var b=new Backend();
        var s=new AiTweakRuntimeService(()=>new[]{T()},b,path,feedbackRequired:true, utcNow:()=>_clock);
        foreach(var sample in Window(0)) await Tick(s,false,sample);
        Assert.Equal("enabled",b.Value);
        s=new AiTweakRuntimeService(()=>new[]{T()},b,path,feedbackRequired:true, utcNow:()=>_clock);
        await Tick(s,false,S(9));
        Assert.Equal("original",b.Value);
        // Restart recovery history must record interruption, with no new trial in the same tick.
        Assert.Contains("interrupted",s.Health.LastEvidence,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task GameThermalChangeAndInconclusiveRestoreImmediately()
    {
        var b=new Backend();var s=new AiTweakRuntimeService(()=>new[]{T()},b,Temp(),feedbackRequired:true, utcNow:()=>_clock);
        foreach(var sample in Window(0)) await Tick(s,false,sample);
        await Tick(s,true,S(8));
        Assert.Equal("original",b.Value);
        Assert.Equal(0,s.Health.Accepted);
        Assert.NotEmpty(s.Health.LastEvidence);
    }
    [Fact] public void UserExcludedCaptureTweaksStayManual()
    {
        foreach (string id in new[] { "sys.gamedvr", "sys.capture" })
        {
            var tweak = new TweakDefinition { Id=id,Section="system",Group="",Name="",Description="" };
            Assert.Contains("Excluded by user", AiSystemPerformancePolicy.CandidateDeferredReason(tweak,"GameBar|BroadcastDVR"));
        }
    }

    [Fact] public void UiExplorerSearchAndSystemCandidatesAreBlockedByMutationGuard()
    {
        var explorer = new TweakDefinition { Id="ai.ui.thumbcache",Section="ui",Group="Explorer",Name="",Description="" };
        var search = new TweakDefinition { Id="ai.ui.searchfeed",Section="ui",Group="Search",Name="",Description="" };
        var shell = new TweakDefinition { Id="ai.ui.startrecommend",Section="ui",Group="Start",Name="",Description="" };
        var system = new TweakDefinition { Id="ai.sys.inventory",Section="system",Group="Background",Name="",Description="" };
        Assert.True(AiAutomaticMutationGuard.Blocks(explorer));
        Assert.True(AiAutomaticMutationGuard.Blocks(search));
        Assert.True(AiAutomaticMutationGuard.Blocks(shell));
        Assert.True(AiAutomaticMutationGuard.Blocks(system));
    }

    [Fact] public void UnmappedPreferenceTweaksStayManual()
    {
        var preference=new TweakDefinition { Id="ui.extensions",Section="ui",Group="",Name="",Description="" };
        Assert.NotEmpty(AiSystemPerformancePolicy.CandidateDeferredReason(preference,"explorer"));
    }
}
