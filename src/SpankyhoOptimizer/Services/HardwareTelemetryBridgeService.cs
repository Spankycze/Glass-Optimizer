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
    int? SchemaVersion = null,
    double? MotherboardTempC = null,
    string? MotherboardSensor = null);

public static class HardwareTelemetryBridgeService
{
    public const string TaskName = @"\SpankyhoOptimizer-HardwareTelemetryBridge";
    internal const int CurrentSchemaVersion = 1;
    private static readonly Version LegacySchemaMinimumVersion = new(15, 4, 594);
    // Assembly identity cannot change inside a running process. Also used by
    // the frequently called cross-process snapshot compatibility reader.
    private static readonly string ProcessAssemblyVersion =
        typeof(HardwareTelemetryBridgeService).Assembly
            .GetName().Version?.ToString(3) ?? "0.0.0";
    private const string MutexName = @"Global\SpankyhoOptimizer.HardwareTelemetryBridge";
    private static readonly string BridgeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "SpankyhoOptimizer",
        "HardwareBridge");

    public static readonly string SnapshotPath =
        Path.Combine(BridgeRoot, "hardware-telemetry-bridge.json");
    // A bridge snapshot is written every two seconds; its atomic temp path never changes.
    private static readonly string SnapshotTempPath = SnapshotPath + ".tmp";
    private const int SnapshotRenameAttempts = 3;
    // Cache method-group delegates; do not allocate a new closure every 2s.
    // The writer avoids redundant CreateDirectory calls on the 2-second loop.
    private static readonly SnapshotDirectoryWriter SnapshotTempWriter = new();
    private static readonly Action<string, string, bool> SnapshotMove = File.Move;
    private static readonly Action<int> SnapshotBackoff = Thread.Sleep;
    private static readonly HardwareTelemetrySnapshotReadCache SnapshotReadCache = new(SnapshotPath);

    public static readonly string RegistrationPath =
        Path.Combine(BridgeRoot, "current.registration");

    public static readonly string TaskXmlPath =
        Path.Combine(BridgeRoot, "hardware-telemetry-bridge-task.xml");

    // Only the scheduled SYSTEM telemetry bridge may write the protected
    // Program Files snapshot. Reject interactive/non-SYSTEM launches BEFORE
    // opening the Global mutex or touching bridge sensor/file resources.
    internal static bool IsAuthorizedBridgePrincipal(SecurityIdentifier? sid) =>
        sid?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;

    private static bool IsCurrentBridgePrincipalAuthorized()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return IsAuthorizedBridgePrincipal(identity.User);
        }
        catch { return false; } // Privilege checks always fail closed.
    }

    // Return an explicit health outcome. The RuntimeHost must exit nonzero on
    // unexpected telemetry failures so Task Scheduler can restart this
    // CPU-only bridge; cancellation is graceful, but mutex startup timeout
    // exits nonzero to let Task Scheduler retry after a delayed handoff.
    // A Windows named Mutex is owned by the OS thread that called WaitOne.
    // An async Task.Delay continuation can move to a different thread, making
    // ReleaseMutex fail and leaving cross-process writer exclusivity unsafe.
    // A dedicated background thread holds the mutex for the entire bridge loop.
    public static Task<bool> RunAsync(CancellationToken token = default) =>
        RunOnBridgeThread(() => RunOnMutexOwnerThread(token));

    private static Task<bool> RunOnBridgeThread(Func<bool> run)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(run()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        {
            Name = "Spanky Hardware Bridge Mutex Owner",
            IsBackground = true
        };
        thread.Start();
        return completion.Task;
    }

    // Isolated test of the *same* worker-thread dispatch used by production.
    // No protected directory, SYSTEM principal, sensors or fan hardware accessed.
    internal static string RunBridgeMutexThreadAffinitySmoke()
    {
        string name = @"Local\SpankyBridgeMutexSmoke-" + Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        int beforeThread = 0, afterThread = 0;
        Task<bool> worker = RunOnBridgeThread(() =>
        {
            using var mutex = new Mutex(false, name);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Bridge thread could not acquire isolated mutex.");
            try
            {
                beforeThread = Environment.CurrentManagedThreadId;
                entered.Set();
                if (!cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("Bridge wait did not wake after cancellation.");
                afterThread = Environment.CurrentManagedThreadId;
                return true;
            }
            finally { mutex.ReleaseMutex(); }
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Synthetic bridge worker did not enter mutex.");
            using var contender = new Mutex(false, name);
            if (contender.WaitOne(0))
            {
                contender.ReleaseMutex();
                throw new InvalidOperationException("Bridge allowed a second mutex owner.");
            }
            cancellation.Cancel();
            if (!worker.GetAwaiter().GetResult() ||
                beforeThread == 0 || beforeThread != afterThread)
                throw new InvalidOperationException("Bridge worker migrated while holding the mutex.");
            if (!contender.WaitOne(TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Bridge mutex was not released after worker stopped.");
            contender.ReleaseMutex();
        }
        finally { cancellation.Cancel(); }
        return $"HARDWARE_BRIDGE_MUTEX_AFFINITY_OK sameThread=true " +
            $"threadId={beforeThread} competingWriterBlocked=true " +
            "cancelSignaled=true mutexReleased=true protectedWrites=0 sensors=0";
    }

    // Synthetic cancellation of mutex contention, with the existing writer
    // held by another dedicated OS thread. No SYSTEM bridge/fan resources.
    internal static string RunBridgeMutexCancellationSmoke()
    {
        string name = @"Local\SpankyBridgeCancelSmoke-" + Guid.NewGuid().ToString("N");
        using var ownerEntered = new ManualResetEventSlim(false);
        using var ownerRelease = new ManualResetEventSlim(false);
        using var waiterStarted = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();

        Task<bool> owner = RunOnBridgeThread(() =>
        {
            using var mutex = new Mutex(false, name);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Synthetic bridge lock owner could not start.");
            try
            {
                ownerEntered.Set();
                return ownerRelease.Wait(TimeSpan.FromSeconds(4));
            }
            finally { mutex.ReleaseMutex(); }
        });
        try
        {
            if (!ownerEntered.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Synthetic bridge mutex owner did not start.");
            // A timed-out replacement must report a failed startup rather
            // than a graceful cancellation: only failure triggers the
            // scheduled SYSTEM bridge's RestartOnFailure recovery policy.
            long timeoutWaitMs = -1;
            Task<bool> timeoutProbe = RunOnBridgeThread(() =>
            {
                using var mutex = new Mutex(false, name);
                var stopwatch = Stopwatch.StartNew();
                bool acquired = TryAcquireOwnerMutex(mutex, CancellationToken.None,
                    TimeSpan.FromMilliseconds(150), out bool cancelled);
                timeoutWaitMs = stopwatch.ElapsedMilliseconds;
                if (acquired) mutex.ReleaseMutex();
                return !acquired && !cancelled;
            });
            if (!timeoutProbe.GetAwaiter().GetResult() || timeoutWaitMs < 100)
                throw new InvalidOperationException(
                    $"Synthetic writer contention did not produce a real mutex timeout: {timeoutWaitMs}ms.");
            using (var cancelledAlready = new CancellationTokenSource())
            {
                cancelledAlready.Cancel();
                using var testMutex = new Mutex(false, name);
                bool unexpected = TryAcquireOwnerMutex(testMutex, cancelledAlready.Token,
                    TimeSpan.FromSeconds(1), out bool cancelled);
                if (unexpected || !cancelled)
                    throw new InvalidOperationException(
                        "Already-cancelled bridge incorrectly acquired a mutex.");
            }
            long waitMs = -1;
            Task<bool> waiter = RunOnBridgeThread(() =>
            {
                using var mutex = new Mutex(false, name);
                waiterStarted.Set();
                var stopwatch = Stopwatch.StartNew();
                bool acquired = TryAcquireOwnerMutex(mutex, cancellation.Token,
                    TimeSpan.FromSeconds(5), out bool cancelled);
                stopwatch.Stop();
                waitMs = stopwatch.ElapsedMilliseconds;
                if (acquired) mutex.ReleaseMutex();
                return !acquired && cancelled;
            });
            if (!waiterStarted.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Synthetic bridge mutex waiter did not start.");
            Thread.Sleep(100); // Prove cancellation interrupts an active wait.
            cancellation.Cancel();
            if (!waiter.Wait(TimeSpan.FromSeconds(2)) ||
                !waiter.GetAwaiter().GetResult() || waitMs > 1500)
                throw new InvalidOperationException(
                    $"Bridge mutex contention cancellation did not wake promptly: {waitMs}ms.");
            // Confirm cancellation did not accidentally claim the lock.
            using (var contender = new Mutex(false, name))
            {
                if (contender.WaitOne(0))
                {
                    contender.ReleaseMutex();
                    throw new InvalidOperationException(
                        "Cancelled mutex contender stole bridge writer ownership.");
                }
            }
            ownerRelease.Set();
            if (!owner.GetAwaiter().GetResult())
                throw new InvalidOperationException("Synthetic lock owner failed to stop.");
            bool reacquired = RunOnBridgeThread(() =>
            {
                using var mutex = new Mutex(false, name);
                bool owns = TryAcquireOwnerMutex(mutex, CancellationToken.None,
                    TimeSpan.FromSeconds(2), out bool cancelled);
                if (owns) mutex.ReleaseMutex();
                return owns && !cancelled;
            }).GetAwaiter().GetResult();
            if (!reacquired)
                throw new InvalidOperationException("Bridge lock not released after handoff.");
            return $"HARDWARE_BRIDGE_MUTEX_CANCEL_OK waitMs={waitMs} timeoutWaitMs={timeoutWaitMs} " +
                "cancelled=true timedOutStartupFailed=true writerRetainedLock=true reacquired=true protectedWrites=0 sensors=0";
        }
        finally
        {
            cancellation.Cancel();
            ownerRelease.Set();
            _ = owner.Wait(TimeSpan.FromSeconds(5));
        }
    }

    // Wait for cross-process writer ownership or cancellation on the *same*
    // thread that will release the mutex. Prevents a five-second cancellation
    // stall when an old bridge instance still owns the writer lock.
    private static bool TryAcquireOwnerMutex(Mutex mutex, CancellationToken token,
        TimeSpan timeout, out bool cancelled)
    {
        cancelled = token.IsCancellationRequested;
        if (cancelled) return false;
        try
        {
            int signaled = WaitHandle.WaitAny(
                new WaitHandle[] { mutex, token.WaitHandle }, timeout);
            if (signaled == 0) return true;
            cancelled = signaled == 1 || token.IsCancellationRequested;
            return false;
        }
        catch (AbandonedMutexException ex) when (ex.MutexIndex == 0)
        {
            // WaitAny has acquired the abandoned mutex for the current OS
            // thread, which still owns and releases it in the normal finally.
            return true;
        }
    }

    private static bool RunOnMutexOwnerThread(CancellationToken token)
    {
        if (!IsCurrentBridgePrincipalAuthorized())
        {
            try
            {
                LogService.Info("hardware",
                    "HARDWARE_BRIDGE_NON_SYSTEM_EXIT protectedWrites=0 sensorReads=0 mutexOpens=0");
            }
            catch { /* Rejection must not depend on optional logging. */ }
            return false;
        }
        using var mutex = new Mutex(false, MutexName);
        // Cancellation can interrupt contention immediately; the intentional
        // cancellation and old-writer handoff are normal exit outcomes.
        bool owns = TryAcquireOwnerMutex(mutex, token,
            TimeSpan.FromSeconds(5), out bool cancelledDuringWait);
        if (!owns)
        {
            if (cancelledDuringWait) return true;
            // A timed-out new writer must exit NONZERO so Task Scheduler's
            // RestartOnFailure can retry once the previous mutex owner ends.
            // Treating this as success silently lost the replacement bridge.
            try { LogService.Error("hardware", "HARDWARE_BRIDGE_MUTEX_START_TIMEOUT retryByScheduler=true"); }
            catch { /* A failed diagnostic must not change the exit status. */ }
            return false;
        }

        bool failed = false;
        int snapshotIoFailures = 0;
        try
        {
            // Cancellation can race with mutex acquisition. Release the lock
            // via this same thread's finally without waking the sensor stack.
            if (token.IsCancellationRequested) return true;
            CpuTemperatureService.ConfigureCpuAndMotherboardMode();
            // Immutable for the lifetime of this elevated background process.
            // Avoid token/principal construction and assembly-version probing
            // on every two-second sensor loop. Registration file still read
            // fresh on every tick so upgraded bridge processes stop promptly.
            string bridgeVersion = CurrentVersion();
            bool bridgeElevated = IsCurrentProcessElevated();
            string? expectedRegistration = ExpectedCurrentRegistration();
            LogService.Info("hardware", "HARDWARE_BRIDGE_READY version=" + bridgeVersion + " mode=cpu-only");
            // Process-only resource diagnostics are unnecessary in this sensor loop.
            while (!token.IsCancellationRequested)
            {
                // Sensor safety remains independent of performance telemetry.
                if (!RegistrationMatchesExpected(expectedRegistration))
                    break;

                CpuTemperatureService.CpuTelemetrySample sample =
                    CpuTemperatureService.ReadTelemetry();

                try
                {
                    WriteSnapshot(sample, bridgeVersion, bridgeElevated);
                    if (snapshotIoFailures > 0)
                    {
                        LogService.Info("hardware",
                            $"HARDWARE_BRIDGE_SNAPSHOT_IO_RECOVERED failures={snapshotIoFailures}");
                        snapshotIoFailures = 0;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A transient filter/antivirus reader can deny an atomic
                    // replace for one tick. Do not permanently kill the
                    // SYSTEM bridge: a restart task may itself be unavailable
                    // to a non-admin session. Continue to probe every 2s.
                    snapshotIoFailures = Math.Min(snapshotIoFailures + 1, 1_000_000);
                    if (snapshotIoFailures is 1 or 5 ||
                        snapshotIoFailures % 30 == 0)
                        LogService.Error("hardware",
                            $"HARDWARE_BRIDGE_SNAPSHOT_IO_RETRY failures={snapshotIoFailures}", ex);
                }
                // 2-second CPU sensor cadence avoids unnecessary native polling and JSON writes.
                // Readers accept telemetry up to 6 seconds old; fan control remains independent.
                // Cancellation-aware blocking wait stays on the mutex owner
                // thread; zero task continuations or thread switches per tick.
                if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failed = true;
            LogService.Error("hardware", "Hardware telemetry bridge failed", ex);
        }
        finally
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException ex)
            {
                failed = true;
                LogService.Error("hardware", "HARDWARE_BRIDGE_MUTEX_RELEASE_FAILED", ex);
            }
            LogService.Info("hardware", "HARDWARE_BRIDGE_STOPPED version=" + CurrentVersion());
        }
        return !failed;
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
            HardwareTelemetryBridgeSnapshot? snapshot = SnapshotReadCache.Read();

            if (snapshot == null ||
                !snapshot.Elevated ||
                !OverviewTemperatureReadingPolicy.IsFresh(
                    snapshot.UpdatedUtc, DateTime.UtcNow, maxAge))
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

    // Only the final rename is retried. Never replace a valid last-good
    // snapshot with a partial document. On persistent failure the temp file
    // stays available for next tick's safe overwrite and another attempt.
    private static int MoveSnapshotWithRetry(string tempPath, string finalPath,
        Action<string, string, bool> move, Action<int> backoff)
    {
        for (int attempt = 1; attempt <= SnapshotRenameAttempts; attempt++)
        {
            try
            {
                move(tempPath, finalPath, true);
                return attempt;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == SnapshotRenameAttempts)
                    throw;
                backoff(attempt * 40);
            }
        }
        throw new InvalidOperationException("Unreachable snapshot move retry state.");
    }

    // Pure file-system workload in a temporary directory. Confirms that the
    // two-second bridge path avoids unnecessary directory syscalls yet repairs
    // an unexpectedly removed directory before writing. Never uses BridgeRoot.
    internal static string RunBridgeDirectoryHotpathSmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "SpankyBridgeFolderSmoke-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "snapshot");
        string tempFile = Path.Combine(folder, "snapshot.tmp");
        Directory.CreateDirectory(root);
        try
        {
            var writer = new SnapshotDirectoryWriter();
            byte[] payload = Encoding.UTF8.GetBytes("{\"status\":\"valid\"}");
            writer.WriteTemp(folder, tempFile, payload);
            for (int i = 0; i < 16; i++)
                writer.WriteTemp(folder, tempFile, payload);
            if (writer.DirectoryPrepareCount != 1 ||
                !File.ReadAllBytes(tempFile).AsSpan().SequenceEqual(payload))
                throw new InvalidOperationException(
                    "Bridge directory cache did not reuse the initial directory preparation.");

            Directory.Delete(folder, recursive: true);
            writer.WriteTemp(folder, tempFile, payload);
            if (writer.DirectoryPrepareCount != 2 ||
                !File.ReadAllBytes(tempFile).AsSpan().SequenceEqual(payload))
                throw new InvalidOperationException(
                    "Bridge directory deletion was not recovered on the next write.");

            string occupiedPath = Path.Combine(root, "occupied");
            File.WriteAllText(occupiedPath, "not a directory");
            bool stageReported = false;
            try
            {
                new SnapshotDirectoryWriter().WriteTemp(
                    Path.Combine(occupiedPath, "child"),
                    Path.Combine(occupiedPath, "child", "snapshot.tmp"), payload);
            }
            catch (IOException ex)
            {
                stageReported = ex.Message.Contains("stage=ensure-directory",
                    StringComparison.Ordinal) && ex.InnerException != null;
            }
            if (!stageReported)
                throw new InvalidOperationException(
                    "Bridge directory failure omitted its actionable IO stage.");

            bool moveStageReported = false;
            try
            {
                CommitSnapshotWithRetry(tempFile, Path.Combine(folder, "snapshot.json"),
                    (_, _, _) => throw new UnauthorizedAccessException("synthetic move denial"),
                    _ => { });
            }
            catch (IOException ex)
            {
                moveStageReported = ex.Message.Contains("stage=atomic-move",
                    StringComparison.Ordinal) && ex.InnerException is UnauthorizedAccessException;
            }
            if (!moveStageReported)
                throw new InvalidOperationException(
                    "Atomic move denial omitted its actionable IO stage and original cause.");

            // Benchmark ONLY the directory preparation operation, not disk
            // writes or CPU sensors, so the comparison is apples-to-apples.
            const int checks = 2500;
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long stopwatchBefore = Stopwatch.GetTimestamp();
            for (int i = 0; i < checks; i++) Directory.CreateDirectory(folder);
            double baselineMs = Stopwatch.GetElapsedTime(stopwatchBefore).TotalMilliseconds;
            long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

            allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            stopwatchBefore = Stopwatch.GetTimestamp();
            for (int i = 0; i < checks; i++) writer.EnsureReady(folder);
            double reusedMs = Stopwatch.GetElapsedTime(stopwatchBefore).TotalMilliseconds;
            long reusedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

            if (writer.DirectoryPrepareCount != 2)
                throw new InvalidOperationException(
                    "Bridge directory cache reentered the filesystem unexpectedly.");

            return $"HARDWARE_BRIDGE_DIRECTORY_REUSE_SMOKE_OK checks={checks} " +
                $"prepareCalls={writer.DirectoryPrepareCount} deletionRecovered=true " +
                $"failureStages=ensure-directory,atomic-move baselineMs={baselineMs:F3} reusedMs={reusedMs:F3} " +
                $"baselineAllocB={baselineBytes} reusedAllocB={reusedBytes} protectedWrites=0";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Isolated diagnostic cleanup is best effort. */ }
        }
    }

    // Real temp-directory atomic rename, injected transient denial and
    // persistent failure. No Program Files, scheduled task, sensor, driver
    // or elevated hardware access. Called in full-smoke only.
    internal static string RunBridgeSnapshotAtomicRetrySmoke()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "SpankyBridgeAtomicSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "snapshot.tmp");
        string destination = Path.Combine(folder, "snapshot.json");
        try
        {
            // Exercise the production UTF-8 serialization contract in an isolated
            // temp directory; never touch the active SYSTEM bridge snapshot.
            var fixture = new HardwareTelemetryBridgeSnapshot(
                new DateTime(2026, 10, 18, 6, 0, 0, DateTimeKind.Utc),
                15572, "15.4.1474", true, 51.2, "CPU Package",
                "AMD Ryzen 7 3700X", 12.3, 4249, 63.2, 1, 39.8, "Motherboard");
            // Run the production mapper against both valid and intentionally
            // non-finite synthetic sensors. Nothing touches the live bridge.
            var cleanSample = new CpuTemperatureService.CpuTelemetrySample(
                51.2, "CPU Package", "AMD Ryzen 7 3700X", 12.3, 4249, 63.2,
                39.8, "Motherboard");
            var cleanMapped = BuildSnapshot(cleanSample, "15.4.1474", true);
            if (cleanMapped.CpuTempC != fixture.CpuTempC ||
                cleanMapped.CpuLoadPercent != fixture.CpuLoadPercent ||
                cleanMapped.PackagePowerW != fixture.PackagePowerW ||
                cleanMapped.MotherboardTempC != fixture.MotherboardTempC)
                throw new InvalidOperationException("Finite bridge telemetry changed during sanitization.");
            var invalidSample = cleanSample with
            {
                Celsius = double.NaN,
                LoadPercent = double.PositiveInfinity,
                EffectiveClockMHz = double.NegativeInfinity,
                PackagePowerW = double.NaN,
                MotherboardTempC = double.PositiveInfinity
            };
            var safe = BuildSnapshot(invalidSample, "15.4.1474", true);
            if (safe.CpuTempC != null || safe.CpuLoadPercent != null ||
                safe.EffectiveClockMHz != null || safe.PackagePowerW != null ||
                safe.MotherboardTempC != null ||
                JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(
                    JsonSerializer.SerializeToUtf8Bytes(safe)) != safe)
                throw new InvalidOperationException("Non-finite bridge telemetry was not safely represented as null.");
            byte[] expectedUtf8 = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fixture));
            File.WriteAllBytes(source, JsonSerializer.SerializeToUtf8Bytes(fixture));
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(expectedUtf8))
                throw new InvalidOperationException("Hardware bridge UTF-8 JSON differs from legacy output.");
            var roundTrip = JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(File.ReadAllBytes(source));
            if (roundTrip != fixture)
                throw new InvalidOperationException("Hardware bridge UTF-8 JSON failed round-trip.");

            File.WriteAllText(destination, "last-good");
            File.WriteAllText(source, "new-snapshot");
            int moves = 0;
            int waitMs = 0;
            int attempt = MoveSnapshotWithRetry(source, destination,
                (from, to, overwrite) =>
                {
                    moves++;
                    if (moves == 1) throw new UnauthorizedAccessException("injected transient denial");
                    if (moves == 2) throw new IOException("injected transient share violation");
                    File.Move(from, to, overwrite);
                }, ms => waitMs += ms);
            if (attempt != 3 || moves != 3 || waitMs != 120 ||
                File.ReadAllText(destination) != "new-snapshot" || File.Exists(source))
                throw new InvalidOperationException("Atomic snapshot retries failed to recover.");

            File.WriteAllText(source, "not-committed");
            int persistentMoves = 0;
            bool persistentErrorObserved = false;
            try
            {
                MoveSnapshotWithRetry(source, destination,
                    (_, _, _) =>
                    {
                        persistentMoves++;
                        throw new UnauthorizedAccessException("injected persistent denial");
                    }, _ => { });
            }
            catch (UnauthorizedAccessException) { persistentErrorObserved = true; }

            if (!persistentErrorObserved || persistentMoves != SnapshotRenameAttempts ||
                File.ReadAllText(destination) != "new-snapshot" ||
                File.ReadAllText(source) != "not-committed")
                throw new InvalidOperationException("Failed atomic snapshot move corrupted last-good data.");

            File.WriteAllText(source, "subsequent-recovered-snapshot");
            int recovery = MoveSnapshotWithRetry(source, destination,
                File.Move, _ => { });
            if (recovery != 1 ||
                File.ReadAllText(destination) != "subsequent-recovered-snapshot" ||
                File.Exists(source))
                throw new InvalidOperationException("A later healthy tick did not recover snapshot replacement.");

            return "HARDWARE_BRIDGE_ATOMIC_RENAME_RECOVERY_SMOKE_OK " +
                "transientDenial=2 attempts=3 backoffMs=120 " +
                "persistentFailureRetainsLastGood=true subsequentRecovery=true protectedWrites=0 utf8RoundTrip=true";
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string? ExpectedCurrentRegistration()
    {
        try
        {
            string? currentPath = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(currentPath)
                ? null : RegistrationValue(currentPath);
        }
        catch { return null; }
    }

    private static bool RegistrationMatchesExpected(string? expectedRegistration)
    {
        try
        {
            if (!File.Exists(RegistrationPath) || expectedRegistration == null)
                return true;
            // Deliberately re-read the registration file each cycle. A newly
            // registered bridge must replace this process without delay.
            string actual = File.ReadAllText(RegistrationPath).Trim();
            return RegistrationMatchesValue(actual, expectedRegistration);
        }
        catch { return true; }
    }

    // Explicit gate keeps privileged task execution fail-closed when Task Scheduler
    // denies visibility or a task cannot be verified. The registration stamp
    // cannot override this requirement.
    internal static string? TryReadRegistrationStamp(Func<string> read)
    {
        try { return read().Trim(); }
        catch (IOException ex)
        {
            LogService.Error("hardware", "Hardware bridge stamp read failed", ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.Error("hardware", "Hardware bridge stamp read access denied", ex);
            return null;
        }
    }

    internal static bool TryWriteRegistrationStamp(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (IOException ex)
        {
            LogService.Error("hardware", "Hardware bridge stamp write failed", ex);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.Error("hardware", "Hardware bridge stamp access denied", ex);
            return false;
        }
    }

    // Null represents an unreadable stamp, not an absent stamp. Never replace
    // a protected SYSTEM task on uncertain local registration evidence.
    internal static bool ShouldAttemptRegistrationReplacement(string? current, string expected) =>
        current != null && !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldStartVerifiedTask(bool querySucceeded) => querySucceeded;

    // A successful Create in this invocation already verified task registration.
    // A pre-existing task must still pass the read-only query before Run.
    internal static bool ShouldQueryBeforeBridgeStart(bool taskCreatedThisCall) =>
        !taskCreatedThisCall;

    // Require both an authoritative task query and a successful stop before
    // replacing any privileged task definition.
    internal static bool ShouldReplaceVerifiedTask(bool querySucceeded, bool stopSucceeded) =>
        querySucceeded && stopSucceeded;

    internal static bool RegistrationMatchesValue(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    // Read-only measured comparison of the original two-second bridge
    // identity checks and caching them once at bridge initialization.
    // Registration is *not* cached: the process still notices upgrades
    // on its very next sample. This test never writes to Program Files.
    internal static string RunBridgeIdentityReuseSmoke()
    {
        const int cycles = 320;
        static (double Ms, long AllocB, int ElevationReads, string Identity)
            Measure(bool cached)
        {
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            long startTicks = Stopwatch.GetTimestamp();
            string identity = string.Empty;
            int elevationReads = 0;
            string? version = cached ? CurrentVersion() : null;
            bool elevated = cached && IsCurrentProcessElevated();
            string? registration = cached ? ExpectedCurrentRegistration() : null;
            if (cached) elevationReads++;
            for (int i = 0; i < cycles; i++)
            {
                // Reproduce the old repeated assembly metadata query in the
                // baseline; the optimized branch uses immutable process data.
                string versionNow = cached ? version! :
                    (typeof(HardwareTelemetryBridgeService).Assembly
                        .GetName().Version?.ToString(3) ?? "0.0.0");
                bool elevatedNow = cached ? elevated : IsCurrentProcessElevated();
                if (!cached) elevationReads++;
                string? registrationNow = cached
                    ? registration : ExpectedCurrentRegistration();
                identity = versionNow + "|" + elevatedNow + "|" + registrationNow;
            }
            return (Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - startBytes,
                elevationReads, identity);
        }
        var before = Measure(false);
        var after = Measure(true);
        if (before.Identity != after.Identity ||
            before.ElevationReads != cycles || after.ElevationReads != 1 ||
            after.AllocB >= before.AllocB)
            throw new InvalidOperationException(
                "Bridge identity/elevation or single-init caching regression.");
        string expected = ExpectedCurrentRegistration() ?? "no-process-path";
        if (!RegistrationMatchesValue(expected, expected.ToUpperInvariant()) ||
            RegistrationMatchesValue(expected + "-replaced", expected))
            throw new InvalidOperationException("Bridge registration mismatch detection regression.");
        return $"HARDWARE_BRIDGE_IDENTITY_REUSE_OK cycles={cycles} " +
            $"beforeMs={before.Ms:F3} afterMs={after.Ms:F3} " +
            $"beforeAllocB={before.AllocB} afterAllocB={after.AllocB} " +
            $"elevationReadsBefore={before.ElevationReads} " +
            $"elevationReadsAfter={after.ElevationReads} " +
            "freshRegistrationCheckEachCycle=true writes=0";
    }

    private static string RegistrationValue(string runtimeHostPath) =>
        CurrentVersion() + "|" + Path.GetFullPath(runtimeHostPath);

    private static string CurrentVersion() => ProcessAssemblyVersion;

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
