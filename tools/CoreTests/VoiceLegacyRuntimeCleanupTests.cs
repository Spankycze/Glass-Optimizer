using SpankyhoOptimizer.Services;

public sealed class VoiceLegacyRuntimeCleanupTests
{
    [Fact]
    public void Deletes_legacy_whisper_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "spanky-whisper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ggml-small.bin"), "legacy");

        bool deleted = VoiceLegacyRuntimeCleanup.TryDelete(root);

        Assert.True(deleted);
        Assert.False(Directory.Exists(root));
    }
}
