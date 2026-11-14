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

public static partial class HardwareTelemetryBridgeService
{
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

    private static int _registrationInProgress;

    public static void EnsureRegisteredBestEffort(string runtimeHostPath)
    {
        // Avoid claiming the single-flight gate for an ordinary unelevated
        // caller. Only privileged startup can register the protected task.
        if (!ShouldClaimRegistrationGate(IsCurrentProcessElevated(),
                !string.IsNullOrWhiteSpace(runtimeHostPath) &&
                File.Exists(runtimeHostPath) && IsTrustedSourcePath(runtimeHostPath))) return;
        // App startup and FanProfile startup can race; do not run competing
        // SYSTEM task Query/End/Create/Run sequences within this process.
        if (!TryEnterRegistration()) return;
        try { EnsureRegisteredCore(runtimeHostPath); }
        finally { ExitRegistration(); }
    }

    internal static bool TryEnterRegistration() =>
        Interlocked.CompareExchange(ref _registrationInProgress, 1, 0) == 0;

    internal static void ExitRegistration() => Volatile.Write(ref _registrationInProgress, 0);

    // Verify that a registration attempt is never started after an untrusted
    // call, and that the gate is released for the next legitimate attempt.
    internal static bool ShouldClaimRegistrationGate(bool elevated, bool validHost) =>
        elevated && validHost;

    private static void EnsureRegisteredCore(string runtimeHostPath)
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
            string? current = TryReadRegistrationStamp(() =>
                File.Exists(RegistrationPath) ? File.ReadAllText(RegistrationPath) : string.Empty);
            // An unreadable stamp is not evidence that registration differs.
            // Do not stop or replace a protected task on an I/O/access failure.
            if (current == null) return;

            bool taskCreatedThisCall = false;
            if (ShouldAttemptRegistrationReplacement(current, expected))
            {
                // Never stop or replace a protected task while its current
                // registration cannot be verified. A failed query may mean
                // Access Denied, not that the task is missing.
                ProcessResult existingTask = ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/Query /TN \"{TaskName}\" /FO LIST",
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None,
                    logNonZeroExitAsError: false)
                    .GetAwaiter()
                    .GetResult();
                if (!existingTask.Success)
                {
                    LogService.Error("hardware",
                        "Hardware bridge registration change requires task verification: " +
                        (existingTask.StdErr.Length > 0 ? existingTask.StdErr : existingTask.StdOut));
                    return;
                }

                ProcessResult endResult = ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/End /TN \"{TaskName}\"",
                    TimeSpan.FromSeconds(4),
                    CancellationToken.None,
                    logNonZeroExitAsError: false)
                    .GetAwaiter()
                    .GetResult();
                if (!ShouldReplaceVerifiedTask(existingTask.Success, endResult.Success))
                {
                    LogService.Error("hardware",
                        "Hardware bridge task stop failed; registration left unchanged: " +
                        (endResult.StdErr.Length > 0 ? endResult.StdErr : endResult.StdOut));
                    return;
                }

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

                // Task Scheduler registration already succeeded. A failure to
                // persist our optimization stamp must not prevent starting a
                // freshly registered bridge; next pass can retry the stamp.
                taskCreatedThisCall = true;
                TryWriteRegistrationStamp(() => File.WriteAllText(RegistrationPath, expected));
            }

            if (TryReadFreshCurrentVersion(TimeSpan.FromSeconds(6)) == null)
            {
                // A matching registration stamp only proves our last write;
                // it does not prove the privileged task still exists or is readable.
                // Fail closed on Access Denied/unknown query failures rather than
                // rewriting a SYSTEM task or repeatedly attempting a blind run.
                if (ShouldQueryBeforeBridgeStart(taskCreatedThisCall))
                {
                    ProcessResult queryResult = ProcessRunner.RunAsync(
                        "schtasks.exe",
                        $"/Query /TN \"{TaskName}\" /FO LIST",
                        TimeSpan.FromSeconds(5),
                        CancellationToken.None,
                        logNonZeroExitAsError: false)
                        .GetAwaiter()
                        .GetResult();
                    if (!ShouldStartVerifiedTask(queryResult.Success))
                    {
                        LogService.Error("hardware",
                            "Hardware bridge task cannot be verified: " +
                            (queryResult.StdErr.Length > 0 ? queryResult.StdErr : queryResult.StdOut));
                        return;
                    }
                }

                ProcessResult startResult = ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/Run /TN \"{TaskName}\"",
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None,
                    logNonZeroExitAsError: false)
                    .GetAwaiter()
                    .GetResult();
                if (!startResult.Success)
                    LogService.Error("hardware",
                        "Hardware bridge task start failed: " +
                        (startResult.StdErr.Length > 0 ? startResult.StdErr : startResult.StdOut));
            }
        }
        catch (Exception ex)
        {
            LogService.Error("hardware", "Hardware bridge registration failed", ex);
        }
    }
    internal static string BuildTaskXml(string runtimeHostPath)
    {
        // Protect every caller, not only EnsureRegisteredBestEffort, from
        // generating a privileged scheduled action for an unrelated binary.
        if (!IsTrustedSourcePath(runtimeHostPath))
            throw new ArgumentException(
                "Hardware bridge task requires the installed trusted RuntimeHost.",
                nameof(runtimeHostPath));
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
    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>{command}</Command><Arguments>--hardware-telemetry-bridge</Arguments></Exec></Actions>
</Task>
""";
    }

    // No task creation or elevated sensor access during this regression test.
    internal static string RunBridgeRecoveryTaskSmoke()
    {
        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        string trustedExecutable = Path.Combine(
            programFiles, "Spankyho Optimizer", "SpankyhoOptimizer.RuntimeHost.exe");
        string parent = Path.Combine(programFiles, "Spankyho Optimizer");
        string unrelated = Path.Combine(programFiles, "Unrelated App",
            "SpankyhoOptimizer.RuntimeHost.exe");
        string wrongBinary = Path.Combine(parent, "SpankyhoOptimizer.exe");
        string nested = Path.Combine(parent, "Staging",
            "SpankyhoOptimizer.RuntimeHost.exe");
        string siblingPrefix = Path.Combine(programFiles,
            "Spankyho Optimizer Backup", "SpankyhoOptimizer.RuntimeHost.exe");
        if (!IsTrustedSourcePath(trustedExecutable) ||
            !IsTrustedSourcePath(trustedExecutable.ToUpperInvariant()) ||
            IsTrustedSourcePath(unrelated) ||
            IsTrustedSourcePath(wrongBinary) ||
            IsTrustedSourcePath(nested) ||
            IsTrustedSourcePath(siblingPrefix) ||
            IsTrustedSourcePath(string.Empty) ||
            IsTrustedSourcePath(Path.Combine(Path.GetTempPath(),
                "SpankyhoOptimizer.RuntimeHost.exe")))
            throw new InvalidOperationException(
                "Hardware bridge SYSTEM executable trust boundary regressed.");
        bool rejectedTaskXml = false;
        try { _ = BuildTaskXml(unrelated); }
        catch (ArgumentException) { rejectedTaskXml = true; }
        if (!rejectedTaskXml)
            throw new InvalidOperationException(
                "Hardware bridge generated a SYSTEM task for unrelated executable.");
        string xml = BuildTaskXml(trustedExecutable);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = document.Root ?? throw new InvalidOperationException("Missing task XML root.");
        var settings = root.Element(ns + "Settings")
            ?? throw new InvalidOperationException("Missing telemetry task settings.");
        var recovery = settings.Element(ns + "RestartOnFailure")
            ?? throw new InvalidOperationException("Telemetry task lacks automatic recovery.");
        string? interval = recovery.Element(ns + "Interval")?.Value;
        string? count = recovery.Element(ns + "Count")?.Value;
        string? executionLimit = settings.Element(ns + "ExecutionTimeLimit")?.Value;
        string? account = root.Element(ns + "Principals")?.Element(ns + "Principal")?
            .Element(ns + "UserId")?.Value;
        string? command = root.Element(ns + "Actions")?.Element(ns + "Exec")?
            .Element(ns + "Command")?.Value;
        string? arguments = root.Element(ns + "Actions")?.Element(ns + "Exec")?
            .Element(ns + "Arguments")?.Value;
        bool hasBootTrigger = root.Element(ns + "Triggers")?.Element(ns + "BootTrigger") != null;
        // A highly privileged administrator identity is intentionally not a
        // substitute for the scheduled LOCAL SYSTEM bridge principal.
        bool principalGuardValid = IsAuthorizedBridgePrincipal(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) &&
            !IsAuthorizedBridgePrincipal(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) &&
            !IsAuthorizedBridgePrincipal(null);
        if (!principalGuardValid || count != "3" || interval != "PT1M" || executionLimit != "PT0S" ||
            account != "S-1-5-18" || !hasBootTrigger ||
            !string.Equals(command, trustedExecutable, StringComparison.OrdinalIgnoreCase) ||
            arguments != "--hardware-telemetry-bridge" ||
            settings.Element(ns + "MultipleInstancesPolicy")?.Value != "StopExisting" ||
            typeof(HardwareTelemetryBridgeService).GetMethod(nameof(RunAsync))?.ReturnType != typeof(Task<bool>))
            throw new InvalidOperationException("Hardware bridge recovery/privilege or nonzero-exit policy regression.");
        return "HARDWARE_BRIDGE_RECOVERY_SMOKE_OK restartAttempts=3 interval=PT1M systemOnly=true trustedBinaryOnly=true invalidTaskXmlRejected=true nonSystemWrites=0 cpuOnly=true exitFailurePropagated=true taskChanges=0";
    }

    // This path is embedded in a Task Scheduler XML action which executes as
    // LocalSystem. Merely living *somewhere* under Program Files is not a
    // sufficient trust boundary: accept only this app's installed RuntimeHost.
    internal static bool IsTrustedSourcePath(string runtimeHostPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeHostPath)) return false;
        try
        {
            string fullPath = Path.GetFullPath(runtimeHostPath);
            string[] programFileRoots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string root in programFileRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string approved = Path.GetFullPath(Path.Combine(
                    root, "Spankyho Optimizer", "SpankyhoOptimizer.RuntimeHost.exe"));
                if (string.Equals(fullPath, approved,
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or
            NotSupportedException or PathTooLongException)
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

    // Invalid native sensor readings occasionally surface as NaN/Infinity.
    // System.Text.Json intentionally rejects those numbers; publish a null
    // observation instead of terminating the SYSTEM telemetry bridge.
    private static double? FiniteSensorValue(double? value) =>
        value is double number && double.IsFinite(number) ? number : null;

    private static HardwareTelemetryBridgeSnapshot BuildSnapshot(
        CpuTemperatureService.CpuTelemetrySample sample,
        string bridgeVersion,
        bool bridgeElevated) => new(
            DateTime.UtcNow,
            Environment.ProcessId,
            bridgeVersion,
            bridgeElevated,
            FiniteSensorValue(sample.Celsius),
            sample.TemperatureSensor,
            sample.CpuName,
            FiniteSensorValue(sample.LoadPercent),
            FiniteSensorValue(sample.EffectiveClockMHz),
            FiniteSensorValue(sample.PackagePowerW),
            CurrentSchemaVersion,
            FiniteSensorValue(sample.MotherboardTempC),
            sample.MotherboardSensor);

    // This writer is confined to the single SYSTEM mutex owner. A successful
    // directory prepare is reused across ticks; deletion between ticks is
    // repaired on the same tick, while non-recoverable failures retain their
    // original exception in InnerException with an actionable stage label.
    private sealed class SnapshotDirectoryWriter
    {
        private bool _ready;
        internal int DirectoryPrepareCount { get; private set; }

        internal void EnsureReady(string folder)
        {
            if (_ready) return;
            Directory.CreateDirectory(folder);
            _ready = true;
            DirectoryPrepareCount++;
        }

        internal void WriteTemp(string folder, string path, byte[] bytes)
        {
            string stage = "ensure-directory";
            try
            {
                EnsureReady(folder);
                stage = "write-temp";
                try { File.WriteAllBytes(path, bytes); }
                catch (DirectoryNotFoundException)
                {
                    // A cleaner or updater removed the directory after the
                    // first tick; recreate and retry once without resetting
                    // or altering the last-good committed JSON snapshot.
                    _ready = false;
                    stage = "recreate-directory";
                    EnsureReady(folder);
                    stage = "retry-write-temp";
                    File.WriteAllBytes(path, bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("HARDWARE_BRIDGE_SNAPSHOT_IO stage=" + stage, ex);
            }
        }
    }

    private static void WriteSnapshot(
        CpuTemperatureService.CpuTelemetrySample sample,
        string bridgeVersion,
        bool bridgeElevated)
    {
        var snapshot = BuildSnapshot(sample, bridgeVersion, bridgeElevated);
        // Serialize UTF-8 directly, retaining the existing on-disk payload and
        // last-good atomic rename contract without an intermediate JSON string.
        SnapshotTempWriter.WriteTemp(BridgeRoot, SnapshotTempPath,
            JsonSerializer.SerializeToUtf8Bytes(snapshot));
        CommitSnapshotWithRetry(SnapshotTempPath, SnapshotPath,
            SnapshotMove, SnapshotBackoff);
    }

    private static int CommitSnapshotWithRetry(string tempPath, string finalPath,
        Action<string, string, bool> move, Action<int> backoff)
    {
        try { return MoveSnapshotWithRetry(tempPath, finalPath, move, backoff); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("HARDWARE_BRIDGE_SNAPSHOT_IO stage=atomic-move", ex);
        }
    }
}
