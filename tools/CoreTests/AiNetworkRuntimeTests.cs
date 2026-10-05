using SpankyhoOptimizer.Services;

[Collection("AI performance experiments")]
public sealed class AiNetworkRuntimeTests
{
    private static AiNetworkProbeResult Probe(double ms, string route = "eth|gw|dns|server", bool valid = true) =>
        new(DateTime.UtcNow, route, valid, 262144, new[] { ms, ms + 1, ms - 1, ms + 2, ms - 2 },
            new[] { 20d, 20d, 20d, 20d, 20d }, 0, 2, 1000, valid ? "" : "Endpoint unavailable");

    [Fact]
    public async Task StableTransferImprovementIsRetainedOnlyAfterReferenceRestore()
    {
        using var fixture = new Fixture(Probe(100), Probe(65), Probe(102));
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        Assert.Equal(new[] { "restricted", "normal", "restricted" }, fixture.Backend.Writes);
        Assert.Equal("restricted", fixture.Backend.Value);
        Assert.True(fixture.Service.Health.Managed);
        await fixture.Service.RestoreAllAsync();
        Assert.Equal("normal", fixture.Backend.Value);
    }

    [Fact]
    public async Task NoStableBenefitRestoresOriginal()
    {
        using var fixture = new Fixture(Probe(100), Probe(95), Probe(100));
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        Assert.Equal(new[] { "restricted", "normal" }, fixture.Backend.Writes);
        Assert.False(fixture.Service.Health.Managed);
        Assert.Equal("normal", fixture.Backend.Value);
    }

    [Fact]
    public async Task TimeDriftDoesNotCountAsImprovement()
    {
        using var fixture = new Fixture(Probe(100), Probe(65), Probe(63));
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        Assert.Equal("normal", fixture.Backend.Value);
        Assert.False(fixture.Service.Health.Managed);
    }

    [Fact]
    public void AmbientTrafficDriftInvalidatesBenefit()
    {
        DateTime now = DateTime.UtcNow;
        AiNetworkProbeResult before = Probe(100) with { AmbientBytesPerSecond = 1000, CapturedUtc = now };
        AiNetworkProbeResult candidate = Probe(65) with { AmbientBytesPerSecond = 30000, CapturedUtc = now };
        AiNetworkProbeResult reference = Probe(102) with { AmbientBytesPerSecond = 1000, CapturedUtc = now };

        Assert.False(AiNetworkRuntimePolicy.StableBenefit(before, candidate, reference, now));
    }

    [Fact]
    public void LearnedNetworkDecisionUsesLongCooldowns()
    {
        Assert.Equal(TimeSpan.FromHours(6), AiNetworkRuntimePolicy.AcceptedRetestDelay);
        Assert.Equal(TimeSpan.FromHours(12), AiNetworkRuntimePolicy.RejectedRetestDelay);
        Assert.Equal(TimeSpan.FromMinutes(45), AiNetworkRuntimePolicy.NoisyRetryDelay);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InvalidOrChangedRouteNeverKeepsCandidate(bool valid, bool changed)
    {
        using var fixture = new Fixture(Probe(100), Probe(65, changed ? "wifi" : "eth|gw|dns|server", valid), Probe(100));
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        Assert.Equal("normal", fixture.Backend.Value);
        Assert.False(fixture.Service.Health.Managed);
    }

    [Fact]
    public async Task UnavailableBaselineNeverMutates()
    {
        using var fixture = new Fixture(Probe(0, valid: false));
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        Assert.Empty(fixture.Backend.Writes);
    }

    [Fact]
    public async Task BusyOrGameContextNeverStartsExperiment()
    {
        using var fixture = new Fixture(Probe(100), Probe(65), Probe(100));
        await fixture.Service.TickAsync(true, false, 80, false, CancellationToken.None);
        await fixture.Service.TickAsync(true, true, 2, true, CancellationToken.None);
        Assert.Empty(fixture.Backend.Writes);
        Assert.Equal(0, fixture.Backend.Probes);
    }

    [Fact]
    public async Task InteractiveWorkloadNeverStartsNetworkExperiment()
    {
        using var fixture = new Fixture(Probe(100), Probe(65), Probe(100));
        await fixture.Service.TickAsync(
            true, true, 2, false, AiWorkloadClass.Interactive, CancellationToken.None);

        Assert.Empty(fixture.Backend.Writes);
        Assert.Equal(0, fixture.Backend.Probes);
    }

    [Fact]
    public async Task StaleMeasurementNeverStartsMutation()
    {
        using var fixture = new Fixture(Probe(100) with { CapturedUtc = DateTime.UtcNow.AddMinutes(-2) });
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        Assert.Empty(fixture.Backend.Writes);
    }

    [Fact]
    public async Task RestorePreservesExternalManualChange()
    {
        using var fixture = new Fixture(Probe(100), Probe(65), Probe(100));
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        fixture.Backend.Value = "disabled";
        await fixture.Service.RestoreAllAsync();
        Assert.Equal("disabled", fixture.Backend.Value);
        Assert.Equal(1, fixture.Service.Health.ManualOverrides);
    }

    [Fact]
    public async Task PendingTransactionRecoversOriginalAcrossServiceRestart()
    {
        using var fixture = new Fixture(Probe(100), Probe(65), Probe(100));
        fixture.Backend.FailAfterWrite = true;
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
        fixture.Backend.FailAfterWrite = false;
        var restarted = new AiNetworkRuntimeService(fixture.Backend, fixture.Path);
        await restarted.RestoreAllAsync();
        Assert.Equal("normal", fixture.Backend.Value);
    }

    [Fact]
    public async Task OriginalIsPersistedBeforeEveryCandidateWrite()
    {
        using var fixture = new Fixture(Probe(100), Probe(65), Probe(100));
        fixture.Backend.BeforeWrite = () => Assert.Contains("\"Original\":\"normal\"", File.ReadAllText(fixture.Path), StringComparison.Ordinal);
        await fixture.Service.TickAsync(true, true, 2, false, CancellationToken.None);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-network-" + Guid.NewGuid().ToString("N") + ".json");
        public readonly FakeBackend Backend;
        public readonly AiNetworkRuntimeService Service;
        public Fixture(params AiNetworkProbeResult[] probes) { Backend = new(probes); Service = new(Backend, Path); }
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }

    private sealed class FakeBackend(AiNetworkProbeResult[] probes) : IAiNetworkRuntimeBackend
    {
        public string Value = "normal";
        public List<string> Writes = new();
        public int Probes;
        public bool FailAfterWrite;
        public Action? BeforeWrite;
        public Task<string> ReadSettingAsync(CancellationToken token) => Task.FromResult(Value);
        public Task ApplySettingAsync(string value, CancellationToken token)
        {
            BeforeWrite?.Invoke();
            Writes.Add(value); Value = value;
            if (FailAfterWrite) throw new IOException("Write response lost");
            return Task.CompletedTask;
        }
        public Task<AiNetworkProbeResult> MeasureAsync(CancellationToken token) => Task.FromResult(probes[Probes++]);
    }
}
