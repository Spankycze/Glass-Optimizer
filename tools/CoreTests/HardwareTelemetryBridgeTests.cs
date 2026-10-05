using SpankyhoOptimizer.Services;

public sealed class HardwareTelemetryBridgeTests
{
    [Fact]
    public void Task_xml_runs_as_system_highest_without_time_limit()
    {
        string xml = HardwareTelemetryBridgeService.BuildTaskXml(
            @"C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.RuntimeHost.exe");

        Assert.Contains("encoding=\"UTF-16\"", xml, StringComparison.Ordinal);
        Assert.Contains("S-1-5-18", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceAccount", xml, StringComparison.Ordinal);
        Assert.Contains("HighestAvailable", xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
        Assert.Contains("--hardware-telemetry-bridge", xml, StringComparison.Ordinal);
        Assert.Contains("MultipleInstancesPolicy>StopExisting", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Appdata_runtime_is_not_a_trusted_elevated_source()
    {
        string userRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpankyhoOptimizer",
            "RuntimeHost",
            "15.4.999",
            "SpankyhoOptimizer.RuntimeHost.exe");

        Assert.False(HardwareTelemetryBridgeService.IsTrustedSourcePath(userRuntime));
    }

    [Fact]
    public void Program_files_runtime_is_a_trusted_elevated_source()
    {
        string protectedRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Spankyho Optimizer",
            "SpankyhoOptimizer.RuntimeHost.exe");

        Assert.True(HardwareTelemetryBridgeService.IsTrustedSourcePath(protectedRuntime));
    }

    [Fact]
    public void Snapshot_is_stored_under_protected_program_files()
    {
        string programFiles = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string snapshot = Path.GetFullPath(HardwareTelemetryBridgeService.SnapshotPath);

        Assert.StartsWith(programFiles, snapshot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compatible_old_snapshot_is_not_current_version_readiness()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.595", true, 45, "Core", "Ryzen",
            10, 2500, 45, HardwareTelemetryBridgeService.CurrentSchemaVersion);

        Assert.True(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.596"));
        Assert.False(HardwareTelemetryBridgeService.IsCurrentVersionSnapshot(
            snapshot, "15.4.596"));
    }

    [Fact]
    public void Current_snapshot_satisfies_current_version_readiness()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.596", true, 45, "Core", "Ryzen",
            10, 2500, 45, HardwareTelemetryBridgeService.CurrentSchemaVersion);

        Assert.True(HardwareTelemetryBridgeService.IsCurrentVersionSnapshot(
            snapshot, "15.4.596"));
    }

    [Fact]
    public void Legacy_594_snapshot_is_accepted_by_595()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.594", true, 45, "Core", "Ryzen",
            10, 2500, 45, null);

        Assert.True(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.595"));
    }

    [Fact]
    public void Legacy_snapshot_before_594_is_rejected()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.593", true, 45, "Core", "Ryzen",
            10, 2500, 45, null);

        Assert.False(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.595"));
    }

    [Fact]
    public void Legacy_594_snapshot_is_rejected_after_two_patch_gap()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.594", true, 45, "Core", "Ryzen",
            10, 2500, 45, null);

        Assert.False(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.597"));
    }

    [Fact]
    public void Schema_v1_snapshot_is_patch_version_independent()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.594", true, 45, "Core", "Ryzen",
            10, 2500, 45, HardwareTelemetryBridgeService.CurrentSchemaVersion);

        Assert.True(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.999"));
    }

    [Fact]
    public void Task_xml_escapes_command_path()
    {
        string xml = HardwareTelemetryBridgeService.BuildTaskXml(
            @"C:\A&B\SpankyhoOptimizer.RuntimeHost.exe");

        Assert.Contains(@"C:\A&amp;B\SpankyhoOptimizer.RuntimeHost.exe", xml, StringComparison.Ordinal);
    }
}
