using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record HardwareTelemetryBridgeSnapshot(
    DateTime UpdatedUtc,
    int ProcessId,
    string Version,
    bool Elevated,
    double? CpuTempC,
    string CpuSensor,
    string CpuName,
    double? CpuLoadPercent,
    double? EffectiveClockMHz,
    double? PackagePowerW,
    int? SchemaVersion = null);

public static class HardwareTelemetryBridgeService
{
    public const string TaskName = @"\SpankyhoOptimizer-HardwareTelemetryBridge";
    internal const int CurrentSchemaVersion = 1;
    private static readonly Version LegacySchemaMinimumVersion = new(15, 4, 594);
    private const string MutexName = @"Global\SpankyhoOptimizer.HardwareTelemetryBridge";
    private static readonly string BridgeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "SpankyhoOptimizer",
        "HardwareBridge");

    public static readonly string SnapshotPath =
        Path.Combine(BridgeRoot, "hardware-telemetry-bridge.json");

    public static readonly string RegistrationPath =
        Path.Combine(BridgeRoot, "current.registration");

    public static readonly string TaskXmlPath =
        Path.Combine(BridgeRoot, "hardware-telemetry-bridge-task.xml");

    public static async Task RunAsync(CancellationToken token = default)
    {
        using var mutex = new Mutex(false, MutexName);
        bool owns;
        try { owns = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) return;

        try
        {
            CpuTemperatureService.ConfigureCpuOnlyMode();
            LogService.Info("hardware", "HARDWARE_BRIDGE_READY version=" + CurrentVersion() + " mode=cpu-only");
            while (!token.IsCancellationRequested)
            {
                if (!RegistrationMatchesCurrentProcess())
                    break;

                CpuTemperatureService.CpuTelemetrySample sample =
                    CpuTemperatureService.ReadTelemetry();

                WriteSnapshot(sample);
                await Task.Delay(TimeSpan.FromMilliseconds(1500), token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("hardware", "Hardware telemetry bridge failed", ex);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
            LogService.Info("hardware", "HARDWARE_BRIDGE_STOPPED version=" + CurrentVersion());
        }
    }

    public static CpuTemperatureService.CpuTelemetrySample ReadBestTelemetry()
    {
        HardwareTelemetryBridgeSnapshot? bridge = TryReadFresh(TimeSpan.FromSeconds(6));
        if (bridge != null)
        {
            return new CpuTemperatureService.CpuTelemetrySample(
                bridge.CpuTempC,
                bridge.CpuSensor,
                bridge.CpuName,
                bridge.CpuLoadPercent,
                bridge.EffectiveClockMHz,
                bridge.PackagePowerW);
        }

        try
        {
            return CpuTemperatureService.ReadTelemetry();
        }
        catch
        {
            return new CpuTemperatureService.CpuTelemetrySample(
                null,
                "CPU senzor nedostupný",
                "CPU",
                null,
                null,
                null);
        }
    }

    public static CpuTemperatureService.CpuTemperatureSample ReadBestTemperature()
    {
        CpuTemperatureService.CpuTelemetrySample sample = ReadBestTelemetry();
        return new CpuTemperatureService.CpuTemperatureSample(
            sample.Celsius,
            sample.TemperatureSensor,
            sample.CpuName);
    }

    public static HardwareTelemetryBridgeSnapshot? TryReadFresh(TimeSpan maxAge) =>
        TryReadFreshCore(maxAge, requireCurrentVersion: false);

    public static HardwareTelemetryBridgeSnapshot? TryReadFreshCurrentVersion(TimeSpan maxAge) =>
        TryReadFreshCore(maxAge, requireCurrentVersion: true);

    private static HardwareTelemetryBridgeSnapshot? TryReadFreshCore(
        TimeSpan maxAge,
        bool requireCurrentVersion)
    {
        try
        {
            if (!File.Exists(SnapshotPath))
                return null;

            HardwareTelemetryBridgeSnapshot? snapshot =
                JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(
                    File.ReadAllText(SnapshotPath));

            if (snapshot == null ||
                !snapshot.Elevated ||
                DateTime.UtcNow - snapshot.UpdatedUtc > maxAge)
            {
                return null;
            }

            string currentVersion = CurrentVersion();
            if (requireCurrentVersion)
                return IsCurrentVersionSnapshot(snapshot, currentVersion)
                    ? snapshot
                    : null;

            return IsSnapshotCompatible(snapshot, currentVersion)
                ? snapshot
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsCurrentVersionSnapshot(
        HardwareTelemetryBridgeSnapshot snapshot,
        string currentVersion) =>
        snapshot.Elevated &&
        string.Equals(
            snapshot.Version,
            currentVersion,
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsSnapshotCompatible(
        HardwareTelemetryBridgeSnapshot snapshot,
        string currentVersion)
    {
        if (snapshot.SchemaVersion == CurrentSchemaVersion)
            return true;

        if (snapshot.SchemaVersion is not null and not 0)
            return false;

        if (!Version.TryParse(snapshot.Version, out Version? bridgeVersion) ||
            !Version.TryParse(currentVersion, out Version? appVersion))
        {
            return false;
        }

        if (bridgeVersion < LegacySchemaMinimumVersion ||
            bridgeVersion.Major != appVersion.Major ||
            bridgeVersion.Minor != appVersion.Minor ||
            bridgeVersion > appVersion)
        {
            return false;
        }

        return appVersion.Build - bridgeVersion.Build <= 2;
    }

    public static void EnsureRegisteredBestEffort(string runtimeHostPath)
    {
        try
        {
            if (!IsCurrentProcessElevated() ||
                string.IsNullOrWhiteSpace(runtimeHostPath) ||
                !File.Exists(runtimeHostPath) ||
                !IsTrustedSourcePath(runtimeHostPath))
            {
                return;
            }

            string trustedHostPath = Path.GetFullPath(runtimeHostPath);
            Directory.CreateDirectory(BridgeRoot);
            string expected = RegistrationValue(trustedHostPath);
            string current = File.Exists(RegistrationPath)
                ? File.ReadAllText(RegistrationPath).Trim()
                : string.Empty;

            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                _ = ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/End /TN \"{TaskName}\"",
                    TimeSpan.FromSeconds(4),
                    CancellationToken.None,
                    logNonZeroExitAsError: false)
                    .GetAwaiter()
                    .GetResult();

                File.WriteAllText(
                    TaskXmlPath,
                    BuildTaskXml(trustedHostPath),
                    Encoding.Unicode);
                ProcessResult result = ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/Create /F /TN \"{TaskName}\" /XML \"{TaskXmlPath}\"",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None,
                    logNonZeroExitAsError: false)
                    .GetAwaiter()
                    .GetResult();

                if (!result.Success)
                {
                    LogService.Error("hardware",
                        "Hardware bridge task registration failed: " +
                        (result.StdErr.Length > 0 ? result.StdErr : result.StdOut));
                    return;
                }

                File.WriteAllText(RegistrationPath, expected);
            }

            if (TryReadFreshCurrentVersion(TimeSpan.FromSeconds(6)) == null)
            {
                _ = ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/Run /TN \"{TaskName}\"",
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None,
                    logNonZeroExitAsError: false)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("hardware", "Hardware bridge registration failed", ex);
        }
    }
    internal static string BuildTaskXml(string runtimeHostPath)
    {
        string command = SecurityElement.Escape(Path.GetFullPath(runtimeHostPath))
            ?? runtimeHostPath;

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Spankyho Optimizer elevated hardware telemetry bridge</Description></RegistrationInfo>
  <Triggers><BootTrigger><Enabled>true</Enabled><Delay>PT10S</Delay></BootTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>StopExisting</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>{command}</Command><Arguments>--hardware-telemetry-bridge</Arguments></Exec></Actions>
</Task>
""";
    }

    internal static bool IsTrustedSourcePath(string runtimeHostPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(runtimeHostPath);
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            return roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static void CleanupLegacyStagedCopiesBestEffort()
    {
        try
        {
            if (!Directory.Exists(BridgeRoot))
                return;

            foreach (string directory in Directory.EnumerateDirectories(BridgeRoot))
            {
                string name = Path.GetFileName(directory);
                if (!Version.TryParse(name, out _))
                    continue;

                try { Directory.Delete(directory, recursive: true); }
                catch { }
            }
        }
        catch { }
    }

    private static void WriteSnapshot(
        CpuTemperatureService.CpuTelemetrySample sample)
    {
        Directory.CreateDirectory(BridgeRoot);
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow,
            Environment.ProcessId,
            CurrentVersion(),
            IsCurrentProcessElevated(),
            sample.Celsius,
            sample.TemperatureSensor,
            sample.CpuName,
            sample.LoadPercent,
            sample.EffectiveClockMHz,
            sample.PackagePowerW,
            CurrentSchemaVersion);

        string temp = SnapshotPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
        File.Move(temp, SnapshotPath, true);
    }

    private static bool RegistrationMatchesCurrentProcess()
    {
        try
        {
            if (!File.Exists(RegistrationPath))
                return true;
            string currentPath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentPath))
                return true;

            string expected = File.ReadAllText(RegistrationPath).Trim();
            return string.Equals(
                expected,
                RegistrationValue(currentPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static string RegistrationValue(string runtimeHostPath) =>
        CurrentVersion() + "|" + Path.GetFullPath(runtimeHostPath);

    private static string CurrentVersion() =>
        typeof(HardwareTelemetryBridgeService).Assembly
            .GetName().Version?.ToString(3) ?? "0.0.0";

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
