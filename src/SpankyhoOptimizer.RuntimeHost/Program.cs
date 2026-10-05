using System;
using System.Diagnostics;
using System.Runtime;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer.RuntimeHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ConfigureLowOverheadProcess();

        bool watchdog = HasArg(args, "--fan-watchdog");
        bool watchdogLoop = HasArg(args, "--fan-watchdog-loop");
        bool hardwareBridge = HasArg(args, "--hardware-telemetry-bridge");
        int sessionId = Process.GetCurrentProcess().SessionId;

        if (!RuntimeHostStartupPolicy.ShouldRunInSession(
                sessionId,
                watchdog,
                watchdogLoop,
                hardwareBridge))
        {
            try
            {
                LogService.Info(
                    "runtime-host",
                    $"RUNTIMEHOST_SESSION0_LEGACY_EXIT session={sessionId} watchdog={watchdog} watchdogLoop={watchdogLoop}");
            }
            catch
            {
            }

            return 0;
        }

        if (RuntimeHostStartupPolicy.ShouldLogVoiceRuntime(
            watchdog,
            watchdogLoop,
            hardwareBridge))
        {
            LogService.Info(
                "voice",
                "VOICE_RUNTIME version=" +
                RuntimeVersionInfo.FromAssembly(typeof(Program).Assembly) +
                " path=" +
                (Environment.ProcessPath ?? string.Empty));
        }

        try
        {
            if (hardwareBridge)
            {
                HardwareTelemetryBridgeService.RunAsync().GetAwaiter().GetResult();
                return 0;
            }

            if (watchdogLoop)
            {
                FanProfileService.RunWatchdogLoopAsync().GetAwaiter().GetResult();
                return 0;
            }

            if (watchdog)
            {
                FanProfileService.EnsureAgentUpdated();
                FanProfileService.RunWatchdogAsync().GetAwaiter().GetResult();
                return 0;
            }

            FanProfileService.RunAgentAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            try { LogService.Error("runtime-host", "Runtime host terminated unexpectedly", ex); } catch { }
            return 1;
        }
    }

    private static void ConfigureLowOverheadProcess()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        try { GCSettings.LatencyMode = GCLatencyMode.Batch; } catch { }
    }

    private static bool HasArg(string[] args, string value)
    {
        foreach (string arg in args)
            if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
