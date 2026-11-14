using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class FanProfileService
{
    private static void EnsureWatchdogLoopRunning(string exe)
    {
        try
        {
            _ = ProcessRunner.StartDetached(
                exe,
                "--fan-watchdog-loop",
                useShellExecute: false);
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan watchdog loop start failed", ex);
        }
    }

    public static void StartAgentIfNeeded()
    {
        try
        {
            if (!LianLiSlInfinityService.Detect().Present)
            {
                StartAiAgentIfNeeded();
                return;
            }
            if (IsHeartbeatFresh(TimeSpan.FromSeconds(20)))
                return;

            string exe = ResolveRuntimeHostPath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

            _ = ProcessRunner.StartDetached(
                exe,
                "--fan-agent",
                useShellExecute: false);
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent start failed", ex);
        }
    }

    public static void StartAiAgentIfNeeded()
    {
        try
        {
            string exe = ResolveRuntimeHostPath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            _ = ProcessRunner.StartDetached(exe, "--ai-agent", useShellExecute: false);
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI agent start failed", ex);
        }
    }

    // Watchdog checks this small protocol repeatedly while preserving fan
    // control liveness. Never allocate from an untrusted/unbounded heartbeat.
    // The agent emits PID|UTC-O (about 32 bytes) every ten seconds.
    private const int MaxHeartbeatBytes = 96;

    private static bool TryParseHeartbeatPayload(string payload, out int pid,
        out DateTime reportedUtc)
    {
        pid = 0;
        reportedUtc = default;
        int separator = payload.IndexOf('|');
        if (separator <= 0 || separator >= payload.Length - 1 ||
            payload.IndexOf('|', separator + 1) >= 0 ||
            !int.TryParse(payload.AsSpan(0, separator), NumberStyles.None,
                CultureInfo.InvariantCulture, out pid) || pid <= 0)
            return false;
        return DateTime.TryParseExact(payload.AsSpan(separator + 1), "O",
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out reportedUtc) &&
            reportedUtc.Kind == DateTimeKind.Utc && reportedUtc.Ticks > 0;
    }

    private static bool TryReadHeartbeatPid(string path, out int pid,
        out DateTime reportedUtc)
    {
        pid = 0;
        reportedUtc = default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > MaxHeartbeatBytes)
            return false;
        // Read at most limit+1 regardless of changes after Length.
        Span<byte> bytes = stackalloc byte[MaxHeartbeatBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = stream.Read(bytes[count..]);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > MaxHeartbeatBytes) return false;
        for (int i = 0; i < count; i++)
            if (bytes[i] < 0x20 || bytes[i] > 0x7e) return false;
        return TryParseHeartbeatPayload(
            System.Text.Encoding.ASCII.GetString(bytes[..count]),
            out pid, out reportedUtc);
    }

    private static bool IsHeartbeatEvidenceFresh(DateTime fileWriteUtc,
        DateTime reportedUtc, DateTime nowUtc, TimeSpan maxAge) =>
        FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(fileWriteUtc, nowUtc, maxAge) &&
        FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(reportedUtc, nowUtc, maxAge) &&
        // A touched file must not refresh an old payload. Allow minor clock
        // and filesystem timestamp granularity differences.
        reportedUtc <= fileWriteUtc.AddSeconds(2);

    // Synthetic file input only; no live heartbeat, HID access, fan changes,
    // watchdog activation or user process termination.
    internal static string RunFanHeartbeatInputSmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-heartbeat-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "heartbeat.txt");
            string valid = $"12345|{new DateTime(2026, 10, 26, 21, 0, 0, DateTimeKind.Utc):O}";
            File.WriteAllText(file, valid);
            DateTime fixtureUtc = new(2026, 10, 26, 21, 0, 0, DateTimeKind.Utc);
            if (!TryReadHeartbeatPid(file, out int pid, out DateTime reportedUtc) ||
                pid != 12345 || reportedUtc != fixtureUtc ||
                !IsHeartbeatEvidenceFresh(fixtureUtc.AddSeconds(1), reportedUtc,
                    fixtureUtc.AddSeconds(10), TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Valid fan heartbeat rejected.");
            // An unrelated metadata touch must not make a stale heartbeat
            // appear live, and an embedded future timestamp is not trustworthy.
            if (IsHeartbeatEvidenceFresh(fixtureUtc.AddSeconds(1),
                    fixtureUtc.AddMinutes(-5), fixtureUtc.AddSeconds(10),
                    TimeSpan.FromSeconds(30)) ||
                IsHeartbeatEvidenceFresh(fixtureUtc.AddSeconds(1),
                    fixtureUtc.AddMinutes(2), fixtureUtc.AddSeconds(10),
                    TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Stale or future fan heartbeat was trusted.");
            int rejected = 0;
            foreach (string invalid in new[]
            {
                "12345",
                "0|2026-10-26T21:00:00.0000000Z",
                "-1|2026-10-26T21:00:00.0000000Z",
                "not-a-pid|2026-10-26T21:00:00.0000000Z",
                "12345|not-a-date",
                valid + "|injected",
                valid + "\n",
                new string('7', MaxHeartbeatBytes + 1)
            })
            {
                File.WriteAllText(file, invalid);
                if (TryReadHeartbeatPid(file, out _, out _))
                    throw new InvalidOperationException(
                        "Malformed/unbounded fan heartbeat was trusted.");
                rejected++;
            }
            File.WriteAllBytes(file, new byte[] { 0xFF, 0xFE, 0x00 });
            if (TryReadHeartbeatPid(file, out _, out _))
                throw new InvalidOperationException("Non-ASCII fan heartbeat was trusted.");
            rejected++;
            // The same atomic writer used by the live agent must remain
            // readable without transient in-place partial payloads.
            AtomicFileService.WriteText(file, valid);
            if (!TryReadHeartbeatPid(file, out pid, out reportedUtc) || pid != 12345 ||
                reportedUtc != fixtureUtc)
                throw new InvalidOperationException("Atomic fan heartbeat was rejected.");
            // Reproduce the failure case with an actual isolated file: a
            // recently touched filesystem entry carrying an old agent time.
            File.WriteAllText(file, $"12345|{fixtureUtc.AddMinutes(-5):O}");
            File.SetLastWriteTimeUtc(file, fixtureUtc.AddSeconds(10));
            if (!TryReadHeartbeatPid(file, out pid, out reportedUtc) ||
                pid != 12345 ||
                IsHeartbeatEvidenceFresh(File.GetLastWriteTimeUtc(file), reportedUtc,
                    fixtureUtc.AddSeconds(11), TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Touched stale fan heartbeat was trusted.");

            // Exercise BOTH fan-agent version readers' shared bounded
            // admission using isolated files, never the running agent.
            string versionFile = Path.Combine(root, "fan-agent-version.txt");
            if (!TryReadAgentVersion(versionFile, out string absent) ||
                absent.Length != 0)
                throw new InvalidOperationException("Missing agent version was not first-run safe.");
            File.WriteAllText(versionFile, "16.286.0");
            if (!TryReadAgentVersion(versionFile, out string accepted) ||
                accepted != "16.286.0" ||
                !FanAgentVersionPolicy.ShouldRequestUpdate("16.287.0", accepted) ||
                FanAgentVersionPolicy.ShouldRequestUpdate(accepted, accepted))
                throw new InvalidOperationException("Valid fan-agent version policy regressed.");
            int rejectedVersions = 0;
            foreach (string invalidVersion in new[]
            {
                "", "not-a-version", "16.286.0|restart",
                "16.286.0\n", "016.286.0", "16.286.-1",
                new string('9', MaxAgentVersionBytes + 1)
            })
            {
                File.WriteAllText(versionFile, invalidVersion);
                if (TryReadAgentVersion(versionFile, out _))
                    throw new InvalidOperationException("Malformed fan-agent version was trusted.");
                rejectedVersions++;
            }
            File.WriteAllBytes(versionFile, new byte[] { 0xFF, 0xFE, 0x00 });
            if (TryReadAgentVersion(versionFile, out _))
                throw new InvalidOperationException("Invalid UTF-8 fan-agent version was trusted.");
            rejectedVersions++;
            AtomicFileService.WriteText(versionFile, "16.286.0");
            if (!TryReadAgentVersion(versionFile, out accepted) ||
                accepted != "16.286.0")
                throw new InvalidOperationException("Atomic fan-agent version was rejected.");
            return $"FAN_HEARTBEAT_BOUNDED_INPUT_SMOKE_OK rejected={rejected} maxBytes={MaxHeartbeatBytes} " +
                   $"agentVersionRejected={rejectedVersions} agentVersionMaxBytes={MaxAgentVersionBytes} " +
                   "validUtc=true atomic=true fanWrites=0 realHeartbeatUntouched=true";
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static bool IsHeartbeatFresh(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(HeartbeatPath))
                return false;

            DateTime last = File.GetLastWriteTimeUtc(HeartbeatPath);
            DateTime nowUtc = DateTime.UtcNow;
            if (!FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(last, nowUtc, maxAge))
                return false;

            // A malformed, oversized or stale embedded timestamp is not
            // evidence of a live agent, even when the file metadata is fresh.
            if (!TryReadHeartbeatPid(HeartbeatPath, out int pid, out DateTime reportedUtc) ||
                !IsHeartbeatEvidenceFresh(last, reportedUtc, nowUtc, maxAge))
                return false;

            using Process process = Process.GetProcessById(pid);
            if (process.HasExited ||
                !FanWatchdogLoopPolicy.MatchesHeartbeatProcessStart(
                    process.StartTime.ToUniversalTime(), reportedUtc))
                return false;

            return process.ProcessName.Equals("SpankyhoOptimizer.RuntimeHost", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task RunWatchdogAsync(CancellationToken token = default)
    {
        if (!LianLiSlInfinityService.Detect().Present)
        {
            StartAiAgentIfNeeded();
            return;
        }

        try
        {
            string currentVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            // Invalid version metadata is not a trusted update signal.
            // The ordinary heartbeat fallback still protects fan liveness.
            bool versionTrusted = TryReadAgentVersion(AgentVersionPath,
                out string runningVersion);
            bool needsUpdate = versionTrusted &&
                FanAgentVersionPolicy.ShouldRequestUpdate(currentVersion, runningVersion);

            if (needsUpdate)
                EnsureAgentUpdated();
            else if (IsHeartbeatFresh(TimeSpan.FromSeconds(30)))
                return;

            LogService.Info("lianli", needsUpdate
                ? $"FAN_WATCHDOG update required old={runningVersion} new={currentVersion}"
                : "FAN_WATCHDOG stale heartbeat -> starting agent");
            StartAgentIfNeeded();
            await Task.Delay(1200, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan watchdog failed", ex);
        }
    }

    public static async Task RunWatchdogLoopAsync(CancellationToken token = default)
    {
        if (!LianLiSlInfinityService.Detect().Present)
        {
            StartAiAgentIfNeeded();
            return;
        }

        using var mutex = new Mutex(false, WatchdogLoopMutexName);
        bool owns;
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) return;

        try
        {
            LogService.Info("lianli", "FAN_WATCHDOG_LOOP_READY");
            await Task.Delay(FanWatchdogLoopPolicy.StartupGrace, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                bool heartbeatFresh = IsHeartbeatFresh(TimeSpan.FromSeconds(30));
                if (FanWatchdogLoopPolicy.ShouldRestartAgent(heartbeatFresh))
                {
                    LogService.Info("lianli", "FAN_WATCHDOG_LOOP stale heartbeat -> starting agent");
                    StartAgentIfNeeded();
                }

                await Task.Delay(FanWatchdogLoopPolicy.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan watchdog loop failed", ex);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    private static void WriteHeartbeat(bool force = false)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (!force && !FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                    _lastHeartbeatWriteUtc, now, TimeSpan.FromSeconds(10)))
                return;

            if (!string.IsNullOrWhiteSpace(HeartbeatDirectory))
                Directory.CreateDirectory(HeartbeatDirectory);

            // Same cross-process reader as the health file: the GUI polls this
            // heartbeat, so use the shared atomic writer with its bounded retry.
            AtomicFileService.WriteText(HeartbeatPath, $"{Environment.ProcessId}|{now:O}");
            _lastHeartbeatWriteUtc = now;
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan heartbeat write failed", ex);
        }
    }

    private static void WriteHealthStatus(
        CpuTemperatureService.CpuTelemetrySample telemetry,
        int[]? rpms,
        FanProfile profile,
        string powerMode,
        bool gameActive,
        bool realIdle,
        double loadPercent,
        AiSmartRuntimeDecision ai,
        bool hidOk,
        DateTime? lastHidSuccessUtc,
        string? lastError,
        bool force = false)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (!force && !FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                    _lastHealthWriteUtc, now, TimeSpan.FromSeconds(5)))
                return;

            if (!string.IsNullOrWhiteSpace(HealthDirectory))
                Directory.CreateDirectory(HealthDirectory);

            double? validTemp = telemetry.Celsius is double measured &&
                double.IsFinite(measured) && measured > 5 && measured < 150
                    ? measured : null;
            string version = AgentAssemblyVersion;
            var health = new FanAgentHealth(
                now,
                Environment.ProcessId,
                version,
                hidOk && validTemp.HasValue,
                hidOk,
                lastHidSuccessUtc,
                lastError,
                validTemp,
                // Serialized synchronously below; the four-channel buffer is not retained.
                // Avoid cloning four RPM values on every health snapshot.
                rpms ?? Array.Empty<int>(),
                profile.CpuTempTargetEnabled,
                powerMode,
                gameActive,
                realIdle,
                double.IsFinite(loadPercent) && loadPercent is >= 0 and <= 100
                    ? Math.Round(loadPercent, 1) : -1,
                AiPowerTelemetryEvidencePolicy.HasUsablePower(telemetry.PackagePowerW)
                    ? telemetry.PackagePowerW : null,
                AiPowerTelemetryEvidencePolicy.HasUsableClock(telemetry.EffectiveClockMHz)
                    ? telemetry.EffectiveClockMHz : null,
                ai.Enabled,
                ai.Mode,
                ai.AnomalyScore,
                ai.Confidence,
                ai.PredictedTemp10s,
                ai.Prediction,
                ai.LearnedBaseline,
                ai.LastAction,
                ai.GamePhase);

            // The GUI reads this health JSON from a separate process. Use the
            // shared atomic writer so a transient handle on the destination
            // retries instead of failing the whole periodic write.
            AtomicFileService.WriteText(AgentHealthPath,
                JsonSerializer.Serialize(health,
                    FanAgentCompactJsonContext.Default.FanAgentHealth));
            _lastHealthWriteUtc = now;
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan health status write failed", ex);
        }
    }

    private static void MarkAgentVersionRunning()
    {
        try
        {
            string? directory = Path.GetDirectoryName(AgentVersionPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string version = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            File.WriteAllText(AgentVersionPath, version);
            try { File.Delete(AgentRestartRequestPath); } catch { }
            LogService.Info("lianli", $"FAN_AGENT_VERSION_RUNNING version={version}");
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent version marker failed", ex);
        }
    }

    public static void RequestAgentRestart()
    {
        try
        {
            string? directory = Path.GetDirectoryName(AgentRestartRequestPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string currentVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            File.WriteAllText(AgentRestartRequestPath, $"{currentVersion}|{DateTime.UtcNow:O}");
            LogService.Info("lianli", $"FAN_AGENT_MANUAL_RESTART_REQUESTED version={currentVersion}");
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent restart request failed", ex);
        }
    }

    private static bool HasUpdateRestartRequest() =>
        FanAgentRestartPolicy.TryConsumeRestartRequest(
            AgentRestartRequestPath, ReportRestartRequestReadError);

    private static void ReportRestartRequestReadError(Exception ex)
    {
        DateTime now = DateTime.UtcNow;
        if (!FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                _lastRestartRequestReadErrorUtc, now, TimeSpan.FromMinutes(1))) return;
        _lastRestartRequestReadErrorUtc = now;
        LogService.Error("lianli", "Fan agent restart request cannot be consumed; keeping current agent running", ex);
    }

    public static async Task RunAgentAsync(CancellationToken token = default)
        => await RunAgentCoreAsync(aiOnly: false, token).ConfigureAwait(false);

    public static async Task RunAiAgentAsync(CancellationToken token = default)
        => await RunAgentCoreAsync(aiOnly: true, token).ConfigureAwait(false);
}
