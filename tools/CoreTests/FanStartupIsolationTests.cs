using System;
using System.IO;

public sealed class FanStartupIsolationTests
{
    [Fact]
    public void Fan_agent_startup_does_not_trigger_cleanup_of_the_separate_RAM_Guard_app()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string serviceFile = Path.Combine(directory!.FullName, "src", "SpankyhoOptimizer", "Services", "FanProfileService.cs");
        Assert.True(File.Exists(serviceFile), "Fan agent service source must exist for startup isolation check");
        string source = File.ReadAllText(serviceFile);
        Assert.Contains("public static async Task RunAgentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyRamGuardCleanupService.CleanupBestEffort(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Optimizer_does_not_ship_the_retired_RAM_Guard_termination_and_deletion_service()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string services = Path.Combine(directory!.FullName, "src", "SpankyhoOptimizer", "Services");
        Assert.False(File.Exists(Path.Combine(services, "LegacyRamGuardCleanupService.cs")),
            "Optimizer must not include code that kills or deletes files from the separate RAM Guard application.");
    }
}
