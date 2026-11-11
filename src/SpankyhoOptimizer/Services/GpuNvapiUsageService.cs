using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

// GPU-only read-only NVAPI telemetry, using the system-installed NVIDIA driver.
// Prefer this to NVML's larger native footprint on supported adapters. Both
// legacy NVML and Windows PDH remain fallback providers if anything fails.
// All unsafe-by-contract unmanaged calls are isolated behind validated ABI
// versions, a stable NVIDIA device handle and a single lock.
internal static class GpuNvapiUsageService
{
    private const int BufferBytes = 256;
    private const int DynamicPstatesBytes = 72; // 2x uint + 8x {uint present,uint percent}
    private const int ThermalSettingsBytes = 68; // 2x uint + 3x {5x int}
    private const int NvapiVersion1 = 0x10000;
    private const int NvapiVersion2 = 0x20000;

    private static readonly object Gate = new();
    private static readonly byte[] EmptyBuffer = new byte[BufferBytes];
    private static bool _probed;
    private static bool _ready;
    private static long _lastProbeTicks;
    private static int _failures;
    private static nint _nativeLibrary;
    private static nint _deviceHandle;
    private static nint _buffer;
    private static NvUnload? _unload;
    private static NvDynamicPstates? _getDynamicPstates;
    private static NvThermalSettings? _getThermalSettings;

    // A driver can recover without requiring an application restart. Cooldown
    // is monotonic and applies to both missing and temporarily failing NVAPI.
    private static bool MayProbe() =>
        !_probed || _lastProbeTicks <= 0 ||
        Stopwatch.GetTimestamp() < _lastProbeTicks ||
        Stopwatch.GetTimestamp() - _lastProbeTicks >= Stopwatch.Frequency * 60L;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NvQueryInterface(uint apiId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvInitialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvUnload();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumPhysicalGpus(nint handles, out uint gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvDynamicPstates(nint gpu, nint info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvThermalSettings(nint gpu, uint sensorIndex, nint info);

    private static bool EnsureReady()
    {
        if (_ready) return true;
        if (!MayProbe()) return false;

        _probed = true;
        _lastProbeTicks = Stopwatch.GetTimestamp();
        try
        {
            string systemDriver = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll");
            _nativeLibrary = NativeLibrary.Load(systemDriver);
            nint export = NativeLibrary.GetExport(_nativeLibrary, "nvapi_QueryInterface");
            NvQueryInterface query = Marshal.GetDelegateForFunctionPointer<NvQueryInterface>(export);

            static T? Resolve<T>(NvQueryInterface query, uint id) where T : Delegate
            {
                nint address = query(id);
                return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
            }

            NvInitialize? initialize = Resolve<NvInitialize>(query, 0x0150E828);
            _unload = Resolve<NvUnload>(query, 0xD22BDD7E);
            NvEnumPhysicalGpus? enumerate = Resolve<NvEnumPhysicalGpus>(query, 0xE5AC921F);
            _getDynamicPstates = Resolve<NvDynamicPstates>(query, 0x60DED2ED);
            _getThermalSettings = Resolve<NvThermalSettings>(query, 0xE3640A56);
            if (initialize == null || enumerate == null ||
                _getDynamicPstates == null || _getThermalSettings == null)
            {
                ResetNative();
                return false;
            }

            if (initialize() != 0)
            {
                ResetNative();
                return false;
            }

            nint list = Marshal.AllocHGlobal(IntPtr.Size * 64);
            try
            {
                Marshal.Copy(new byte[IntPtr.Size * 64], 0, list, IntPtr.Size * 64);
                if (enumerate(list, out uint count) != 0 || count == 0 || count > 64)
                {
                    ResetNative();
                    return false;
                }

                _deviceHandle = Marshal.ReadIntPtr(list);
                if (_deviceHandle == 0)
                {
                    ResetNative();
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(list); }

            _buffer = Marshal.AllocHGlobal(BufferBytes);
            _ready = true;
            _failures = 0;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException or
            FileNotFoundException or UnauthorizedAccessException or
            TypeLoadException or ArgumentException or SEHException)
        {
            ResetNative();
            return false;
        }
    }

    internal static double? TryRead()
    {
        lock (Gate)
        {
            if (!EnsureReady() || _getDynamicPstates == null) return null;
            try
            {
                Marshal.Copy(EmptyBuffer, 0, _buffer, BufferBytes);
                Marshal.WriteInt32(_buffer, DynamicPstatesBytes | NvapiVersion1);
                if (_getDynamicPstates(_deviceHandle, _buffer) == 0 &&
                    (Marshal.ReadInt32(_buffer, 8) & 1) != 0)
                {
                    int percent = Marshal.ReadInt32(_buffer, 12);
                    if (percent is >= 0 and <= 100)
                    {
                        _failures = 0;
                        return percent;
                    }
                }
            }
            catch (Exception ex) when (ex is SEHException or InvalidOperationException)
            {
            }
            // A lost/invalidated native handle must not keep polling forever.
            if (++_failures >= 3)
            {
                ResetNative();
                _lastProbeTicks = Stopwatch.GetTimestamp();
            }
            return null;
        }
    }

    internal static double? TryReadTemperature()
    {
        lock (Gate)
        {
            if (!EnsureReady() || _getThermalSettings == null) return null;
            try
            {
                Marshal.Copy(EmptyBuffer, 0, _buffer, BufferBytes);
                Marshal.WriteInt32(_buffer, ThermalSettingsBytes | NvapiVersion2);
                if (_getThermalSettings(_deviceHandle, 0, _buffer) == 0 &&
                    Marshal.ReadInt32(_buffer, 4) is > 0 and <= 3 &&
                    (Marshal.ReadInt32(_buffer, 24) & 1) != 0) // GPU, not VRAM/PSU
                {
                    int temperature = Marshal.ReadInt32(_buffer, 20);
                    if (temperature is > 5 and < 150)
                        return temperature;
                }
            }
            catch (Exception ex) when (ex is SEHException or InvalidOperationException)
            {
            }
            return null;
        }
    }

    // Only ever accessed under Gate. Dispose is a no-op for unsupported devices.
    private static void ResetNative()
    {
        _ready = false;
        _deviceHandle = 0;
        _getDynamicPstates = null;
        _getThermalSettings = null;
        _failures = 0;
        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
        }
        if (_unload != null)
        {
            try { _ = _unload(); }
            catch (Exception ex) when (ex is SEHException or InvalidOperationException) { }
            _unload = null;
        }
        if (_nativeLibrary != 0)
        {
            NativeLibrary.Free(_nativeLibrary);
            _nativeLibrary = 0;
        }
    }

    internal static void Dispose()
    {
        lock (Gate)
        {
            ResetNative();
            _probed = false;
            _lastProbeTicks = 0;
        }
    }

    // Pure policy contract: ABI offsets and versions taken from NVIDIA's
    // public SDK. No GPU initialization in normal QA smoke.
    internal static string RunInteropPolicySmoke()
    {
        if ((DynamicPstatesBytes | NvapiVersion1) != 0x10048 ||
            (ThermalSettingsBytes | NvapiVersion2) != 0x20044 ||
            BufferBytes < Math.Max(DynamicPstatesBytes, ThermalSettingsBytes))
            throw new InvalidOperationException("NVAPI ABI layout/version policy changed.");
        return "GPU_NVAPI_INTEROP_POLICY_SMOKE_OK pstateV1=72 thermalV2=68 readOnly=true fallback=nvml-pdh";
    }
}
