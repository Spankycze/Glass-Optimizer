using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

// Optional NVIDIA driver-native GPU usage provider. Never ships or installs
// NVIDIA binaries; other GPUs and NVML driver failures keep the PDH fallback.
internal static class GpuNvmlUsageService
{
    private static readonly object Gate = new();
    private static bool _probed;
    private static bool _ready;
    private static IntPtr _device;
    private static long _lastProbeTicks;
    private static int _consecutiveRateFailures;

    // A failed first NVML init must not lock the user into costly per-engine
    // GPU PDH queries until the whole app restarts. Retry at the existing 4s
    // sample cadence, but no more often than once per minute. Monotonic time
    // makes this resilient to wall-clock correction and sleep/resume.
    internal static bool ShouldProbeAgain(bool probed, bool ready,
        long lastProbeTicks, long nowTicks) =>
        !ready && (!probed || lastProbeTicks <= 0 ||
        nowTicks < lastProbeTicks ||
        nowTicks - lastProbeTicks >= Stopwatch.Frequency * 60L);

    internal static bool ShouldRecoverAfterRateFailures(int consecutiveFailures) =>
        consecutiveFailures >= 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlInit_v2")]
    private static extern int Init();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int GetHandle(uint index, out IntPtr device);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int GetRates(IntPtr device, out NvmlUtilization value);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int GetTemperature(IntPtr device, uint sensorType, out uint celsius);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlShutdown")]
    private static extern int Shutdown();

    private static bool EnsureReady()
    {
        if (!_ready && ShouldProbeAgain(_probed, _ready,
                _lastProbeTicks, Stopwatch.GetTimestamp()))
        {
            _probed = true;
            _lastProbeTicks = Stopwatch.GetTimestamp();
            bool opened = false;
            try
            {
                opened = Init() == 0;
                if (opened && GetHandle(0, out IntPtr device) == 0 &&
                    device != IntPtr.Zero)
                {
                    _device = device;
                    _ready = true;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            finally
            {
                if (opened && !_ready)
                {
                    try { _ = Shutdown(); }
                    catch (DllNotFoundException) { }
                    catch (EntryPointNotFoundException) { }
                }
            }
        }
        return _ready;
    }

    internal static double? TryRead()
    {
        lock (Gate)
        {
            if (!EnsureReady()) return null;
            try
            {
                if (GetRates(_device, out var value) == 0 && value.Gpu <= 100)
                {
                    _consecutiveRateFailures = 0;
                    return value.Gpu;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            // Three consecutive driver read failures indicate a stale native
            // NVML handle. Shut down the optional provider once, then retry
            // initialization after the same 60s cooldown. Until then the
            // unchanged PDH fallback continues to provide GPU utilization.
            if (ShouldRecoverAfterRateFailures(++_consecutiveRateFailures))
            {
                try { _ = Shutdown(); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                catch (BadImageFormatException) { }
                _ready = false;
                _device = IntPtr.Zero;
                _lastProbeTicks = Stopwatch.GetTimestamp();
                _consecutiveRateFailures = 0;
            }
            return null; // No fabricated 0%: use Windows PDH fallback.
        }
    }

    // The live Overview only needs a GPU core temperature every ~16 seconds.
    // Avoid walking all LibreHardwareMonitor GPU sensors on NVIDIA systems.
    // Optional driver errors return null, retaining the original GPU probe.
    internal static double? TryReadTemperature()
    {
        lock (Gate)
        {
            if (!EnsureReady()) return null;
            try
            {
                const uint gpuCoreSensor = 0;
                if (GetTemperature(_device, gpuCoreSensor, out uint celsius) == 0 &&
                    celsius is > 5 and < 150)
                    return celsius;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            return null;
        }
    }

    internal static void Dispose()
    {
        lock (Gate)
        {
            if (_ready)
            {
                try { _ = Shutdown(); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
            _ready = false;
            _device = IntPtr.Zero;
            _probed = false;
            _lastProbeTicks = 0;
            _consecutiveRateFailures = 0;
        }
    }
}
