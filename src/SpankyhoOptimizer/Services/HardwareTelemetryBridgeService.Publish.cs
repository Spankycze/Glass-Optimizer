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
