using System;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

internal static class GpuPdhUsageService
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint ErrorSuccess = 0;
    private static readonly object Gate = new();
    private static IntPtr _query;
    private static IntPtr _counter;
    private static IntPtr _buffer;
    private static uint _bufferCapacity;
    private static bool _initialized;
    private static bool _unavailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    internal static double? TryRead()
    {
        // On supported NVIDIA cards, lightweight NVAPI avoids loading
        // NVML while preserving native GPU-domain utilization. If NVAPI
        // becomes unavailable, retain the existing NVML and PDH fallbacks.
        return GpuNvapiUsageService.TryRead() ??
            GpuNvmlUsageService.TryRead() ?? TryReadPdh();
    }

    // Kept intact as an independent fallback when NVML is absent or fails.
    internal static double? TryReadPdh()
    {
        lock (Gate)
        {
            if (_unavailable) return null;
            if (!_initialized && !TryInitialize()) return null;
            if (PdhCollectQueryData(_query) != ErrorSuccess) return null;

            uint bytes = 0, count = 0;
            uint status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref bytes, ref count, IntPtr.Zero);
            if (status != PdhMoreData || bytes == 0 || count == 0) return null;

            if (_buffer == IntPtr.Zero || _bufferCapacity < bytes)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal((int)bytes);
                _bufferCapacity = bytes;
            }

            uint bufferSize = _bufferCapacity;
            status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref bufferSize, ref count, _buffer);
            if (status != ErrorSuccess) return null;

            int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
            double? max = null;
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(IntPtr.Add(_buffer, i * stride));
                string name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (item.Value.CStatus > 1) continue;
                double value = Math.Clamp(item.Value.DoubleValue, 0d, 100d);
                max = max.HasValue ? Math.Max(max.Value, value) : value;
            }
            return max;
        }
    }

    private static bool TryInitialize()
    {
        if (_initialized) return true;
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != ErrorSuccess)
        {
            _unavailable = true;
            return false;
        }

        status = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter);
        if (status != ErrorSuccess)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _unavailable = true;
            return false;
        }

        _ = PdhCollectQueryData(_query);
        _initialized = true;
        return true;
    }

    internal static void Dispose()
    {
        // Consistent lock order with TryRead: NVAPI, NVML, then PDH.
        GpuNvapiUsageService.Dispose();
        GpuNvmlUsageService.Dispose();
        lock (Gate)
        {
            if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _buffer = IntPtr.Zero;
            _bufferCapacity = 0;
            _initialized = false;
        }
    }
}
