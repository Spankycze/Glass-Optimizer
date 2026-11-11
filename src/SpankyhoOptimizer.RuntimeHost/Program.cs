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
        // Pure, read-only launch probe for comparing framework-dependent
        // publishing strategies. No fan mutex, tasks, registry or HID access.
        if (HasArg(args, "--startup-probe"))
        {
            if (!RuntimeHostStartupPolicy.ShouldRunInSession(1,
                watchdog: false, watchdogLoop: false, hardwareBridge: false))
                return 40;
            return typeof(FanProfileService).Assembly.GetName().Name is null ? 41 : 0;
        }

        ConfigureLowOverheadProcess();

        bool watchdog = HasArg(args, "--fan-watchdog");
        bool watchdogLoop = HasArg(args, "--fan-watchdog-loop");
        bool aiAgent = HasArg(args, "--ai-agent");
        bool hardwareBridge = HasArg(args, "--hardware-telemetry-bridge");
        using var currentProcess = Process.GetCurrentProcess();
        int sessionId = currentProcess.SessionId;

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

        try
        {
            if (hardwareBridge)
            {
                return HardwareTelemetryBridgeService.RunAsync().GetAwaiter().GetResult() ? 0 : 1;
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

            if (aiAgent)
                FanProfileService.RunAiAgentAsync().GetAwaiter().GetResult();
            else
                FanProfileService.RunAgentAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            try { LogService.Error("runtime-host", "Runtime host terminated unexpectedly", ex); } catch { }
            return 1;
        }
        finally
        {
            // Also closes the read-only logical CPU PDH query on normal agent exit.
            AiSystemSmoothnessGuardService.StopSampling();
        }
    }

    private static void ConfigureLowOverheadProcess()
    {
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            currentProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch { }
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
