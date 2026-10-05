using SpankyhoOptimizer.Services;

public sealed class RuntimeVersionInfoTests
{
    [Fact]
    public void Reads_non_empty_version_from_assembly()
    {
        string version = RuntimeVersionInfo.FromAssembly(typeof(RuntimeVersionInfo).Assembly);
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain("0.0.0.0", version);
    }
}
