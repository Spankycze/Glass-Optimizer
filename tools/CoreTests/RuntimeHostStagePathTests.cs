using SpankyhoOptimizer.Services;

public sealed class RuntimeHostStagePathTests
{
    [Fact]
    public void Versioned_stage_path_is_under_local_app_data()
    {
        string path = RuntimeHostStagePath.GetHostPath(
            @"C:\Users\test\AppData\Local",
            "15.4.567");

        Assert.Equal(
            @"C:\Users\test\AppData\Local\SpankyhoOptimizer\RuntimeHost\15.4.567\SpankyhoOptimizer.RuntimeHost.exe",
            path);
    }
}
