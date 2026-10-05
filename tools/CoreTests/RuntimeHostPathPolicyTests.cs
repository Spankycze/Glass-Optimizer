using SpankyhoOptimizer.Services;

public sealed class RuntimeHostPathPolicyTests
{
    [Fact]
    public void Main_app_is_never_used_as_runtimehost_fallback()
    {
        string resolved = RuntimeHostPathPolicy.Resolve(
            stagedPath: @"C:\stage\SpankyhoOptimizer.RuntimeHost.exe",
            stagedExists: false,
            candidatePath: @"C:\app\SpankyhoOptimizer.RuntimeHost.exe",
            candidateExists: false,
            currentPath: @"C:\app\SpankyhoOptimizer.exe");

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void Current_runtimehost_process_can_be_fallback()
    {
        string current = @"C:\runtime\SpankyhoOptimizer.RuntimeHost.exe";
        Assert.Equal(
            current,
            RuntimeHostPathPolicy.Resolve("x", false, "y", false, current));
    }
}
