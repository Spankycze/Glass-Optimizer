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

public static partial class HardwareTelemetryBridgeService
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
}
