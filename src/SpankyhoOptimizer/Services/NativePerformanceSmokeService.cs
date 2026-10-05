using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record NativePerfResult(
    bool RustAvailable,
    double ManagedPowerUs,
    double RustPowerUs,
    double ManagedDevicesMs,
    double RustDevicesMs,
    int ManagedDeviceCount,
    int RustDeviceCount,
    double ManagedServiceUs,
    double RustServiceUs)
{
    public bool PreferRustPower => RustAvailable && RustPowerUs <= ManagedPowerUs * 1.05;
    public bool PreferRustDevices => RustAvailable && RustDeviceCount == ManagedDeviceCount && RustDevicesMs < ManagedDevicesMs * 0.95;
    public bool PreferRustService => RustAvailable && RustServiceUs < ManagedServiceUs * 0.95;
}

public static class NativePerformanceSmokeService
{
    public static Task<NativePerfResult> RunAsync()
    {
        return Task.Run(() => RunCore());
    }
    private static NativePerfResult RunCore()
    {
        for (int i = 0; i < 20; i++) _ = NativeCore.GetActivePowerScheme();
        if (RustNativeCore.IsAvailable)
            for (int i = 0; i < 20; i++) RustNativeCore.TryGetActivePowerScheme(out _);

        const int powerIterations = 2000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < powerIterations; i++) _ = NativeCore.GetActivePowerScheme();
        sw.Stop();
        double managedPowerUs = sw.Elapsed.TotalMilliseconds * 1000d / powerIterations;

        double rustPowerUs = double.PositiveInfinity;
        if (RustNativeCore.IsAvailable)
        {
            sw.Restart();
            for (int i = 0; i < powerIterations; i++)
                if (!RustNativeCore.TryGetActivePowerScheme(out _)) throw new InvalidOperationException("Rust power query failed.");
            sw.Stop();
            rustPowerUs = sw.Elapsed.TotalMilliseconds * 1000d / powerIterations;
        }

        _ = NativeCore.GetPresentDevices();
        sw.Restart();
        var managedDevices = NativeCore.GetPresentDevices();
        sw.Stop();
        double managedDevicesMs = sw.Elapsed.TotalMilliseconds;
        double rustDevicesMs = double.PositiveInfinity;
        var rustDevices = RustNativeCore.IsAvailable ? RustNativeCore.TryGetPresentDevices() : null;
        if (rustDevices != null)
        {
            sw.Restart();
            rustDevices = RustNativeCore.TryGetPresentDevices();
            sw.Stop();
            rustDevicesMs = sw.Elapsed.TotalMilliseconds;
        }

        const int serviceIterations = 500;
        for (int i = 0; i < 20; i++) _ = ServiceSafetyService.ReadStartType("EventLog");
        sw.Restart(); for (int i = 0; i < serviceIterations; i++) _ = ServiceSafetyService.ReadStartType("EventLog"); sw.Stop();
        double managedServiceUs = sw.Elapsed.TotalMilliseconds * 1000d / serviceIterations;
        double rustServiceUs = double.PositiveInfinity;
        if (RustNativeCore.IsAvailable) { for (int i = 0; i < 20; i++) RustNativeCore.TryQueryServiceStartType("EventLog", out _, out _); sw.Restart(); for (int i = 0; i < serviceIterations; i++) if (!RustNativeCore.TryQueryServiceStartType("EventLog", out _, out _)) throw new InvalidOperationException("Rust service query failed."); sw.Stop(); rustServiceUs = sw.Elapsed.TotalMilliseconds * 1000d / serviceIterations; }

        return new NativePerfResult(
            RustNativeCore.IsAvailable,
            managedPowerUs,
            rustPowerUs,
            managedDevicesMs,
            rustDevicesMs,
            managedDevices.Count,
            rustDevices?.Count ?? 0,
            managedServiceUs,
            rustServiceUs);
    }
}
