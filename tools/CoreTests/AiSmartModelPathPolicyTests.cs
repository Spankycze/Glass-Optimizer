using SpankyhoOptimizer.Services;

public sealed class AiSmartModelPathPolicyTests
{
    [Fact]
    public void Writable_model_path_is_user_writable_local_appdata()
    {
        string local = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(AiSmartModelPathPolicy.WritablePath);

        Assert.StartsWith(local, path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FanAgent", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_model_path_remains_programdata_for_migration()
    {
        string common = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        Assert.StartsWith(
            common,
            Path.GetFullPath(AiSmartModelPathPolicy.LegacyPath),
            StringComparison.OrdinalIgnoreCase);
    }
}
