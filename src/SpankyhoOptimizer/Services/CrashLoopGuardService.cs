using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record StartupState(string Version, int ConsecutiveFailures, DateTime LastStartUtc, DateTime? LastCompleteUtc);

public static class CrashLoopGuardService
{
    private static readonly string FilePath = Path.Combine(LogService.Root, "startup-state.json");
    private const string RegistryPath = @"Software\SpankyhoOptimizer\Runtime";

    public static bool SafeMode { get; private set; }
    public static bool ForcedForRun { get; private set; }

    public static void ForceSafeMode()
    {
        ForcedForRun = true;
        SafeMode = true;
        LogService.Info("startup", "Safe mode forced for this run");
    }

    public static bool BeginStartup(string version)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();

        StartupState? previous = ReadRegistryState();
        long registryReadMs = perf.ElapsedMilliseconds;

        bool legacyFallback = previous == null;
        if (previous == null)
            previous = ReadLegacyFile();
        long fallbackMs = perf.ElapsedMilliseconds - registryReadMs;

        int failures = 0;
        if (previous != null && string.Equals(previous.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            bool previousIncomplete = !previous.LastCompleteUtc.HasValue || previous.LastCompleteUtc.Value < previous.LastStartUtc;
            failures = previousIncomplete ? previous.ConsecutiveFailures + 1 : 0;
        }

        SafeMode = ForcedForRun || failures >= 3;

        var state = new StartupState(version, failures, DateTime.UtcNow, previous?.LastCompleteUtc);
        long writeStart = perf.ElapsedMilliseconds;
        bool registryWritten = TryWriteRegistryState(state);
        if (!registryWritten)
        {
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(
                FilePath,
                JsonSerializer.Serialize(state, OptimizerJsonContext.Default.StartupState));
        }
        long writeMs = perf.ElapsedMilliseconds - writeStart;

        StartupDiagnostics.BufferInfo($"CrashLoop failures={failures} safeMode={SafeMode}");
        StartupDiagnostics.BufferInfo(
            $"CRASHLOOP_BEGIN_PHASE registryReadMs={registryReadMs} fallbackMs={fallbackMs} writeMs={writeMs} source={(legacyFallback ? "legacy" : "registry")} totalMs={perf.ElapsedMilliseconds}");
        return SafeMode;
    }

    public static void CompleteStartup(string version)
    {
        var now = DateTime.UtcNow;
        var state = new StartupState(version, 0, now, now);

        bool registryWritten = TryWriteRegistryState(state);
        if (!registryWritten)
        {
            // Registry is the fast authoritative state. If it is unavailable, keep the
            // legacy JSON write synchronous so crash-loop protection still persists.
            WriteLegacyBackup(state, registryWasWritten: false);
            return;
        }

        // The JSON file is compatibility/redundancy only. Queue one tiny ThreadPool item
        // instead of cold-starting Task.Run/SafeTaskService on the interactive startup path.
        _ = System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            WriteLegacyBackup(state, registryWasWritten: true);
            LogService.Info(
                "startup",
                SafeMode
                    ? "CrashLoop state cleared; safe mode remains active for this run"
                    : "CrashLoop state cleared after successful startup");
        });
    }

    private static void WriteLegacyBackup(StartupState state, bool registryWasWritten)
    {
        try
        {
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(
                FilePath,
                JsonSerializer.Serialize(state, OptimizerJsonContext.Default.StartupState));
        }
        catch (Exception ex)
        {
            LogService.Error(
                "startup",
                registryWasWritten
                    ? "CrashLoop JSON backup write failed"
                    : "CrashLoop completion persistence failed",
                ex);
        }
    }

    private static StartupState? ReadRegistryState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key == null) return null;

            string version = key.GetValue("Version")?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(version)) return null;

            int failures = Convert.ToInt32(key.GetValue("ConsecutiveFailures") ?? 0);
            long startBinary = Convert.ToInt64(key.GetValue("LastStartUtc") ?? 0L);
            long completeBinary = Convert.ToInt64(key.GetValue("LastCompleteUtc") ?? 0L);
            if (startBinary == 0) return null;

            DateTime start = DateTime.FromBinary(startBinary);
            DateTime? complete = completeBinary == 0 ? null : DateTime.FromBinary(completeBinary);
            return new StartupState(version, failures, start, complete);
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "CrashLoop registry state read failed", ex);
            return null;
        }
    }

    private static bool TryWriteRegistryState(StartupState state)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key == null) return false;

            key.SetValue("Version", state.Version, RegistryValueKind.String);
            key.SetValue("ConsecutiveFailures", state.ConsecutiveFailures, RegistryValueKind.DWord);
            key.SetValue("LastStartUtc", state.LastStartUtc.ToBinary(), RegistryValueKind.QWord);
            key.SetValue("LastCompleteUtc", state.LastCompleteUtc?.ToBinary() ?? 0L, RegistryValueKind.QWord);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "CrashLoop registry state write failed", ex);
            return false;
        }
    }

    private static StartupState? ReadLegacyFile()
    {
        try
        {
            return SelfHealingJsonService.ReadOrDefault(
                FilePath,
                () => (StartupState?)null,
                OptimizerJsonContext.Default.StartupState,
                "startup-state");
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "CrashLoop state read failed", ex);
            return null;
        }
    }
}
