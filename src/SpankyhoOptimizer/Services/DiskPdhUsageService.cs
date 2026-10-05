using System;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

internal static class DiskPdhUsageService
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint ErrorSuccess = 0;
    private static readonly object Gate = new();
    private static IntPtr _query;
    private static IntPtr _counter;
    private static bool _initialized;
    private static bool _unavailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    internal static double? TryRead()
    {
        lock (Gate)
        {
            if (_unavailable) return null;
            if (!_initialized && !TryInitialize()) return null;
            if (PdhCollectQueryData(_query) != ErrorSuccess) return null;
            if (PdhGetFormattedCounterValue(_counter, PdhFmtDouble, out _, out var value) != ErrorSuccess || value.CStatus > 1)
                return null;
            if (double.IsNaN(value.DoubleValue) || double.IsInfinity(value.DoubleValue))
                return null;
            return Math.Clamp(value.DoubleValue, 0d, 100d);
        }
    }

    private static bool TryInitialize()
    {
        if (_initialized) return true;
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != ErrorSuccess)
        {
            _unavailable = true;
            return false;
        }
        uint status = PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _counter);
        if (status != ErrorSuccess)
        {
            uint closeStatus = PdhCloseQuery(_query);
            if (closeStatus != ErrorSuccess)
                LogService.Info("performance", $"PDH_DISK_CLOSE_WARN stage=add-counter status=0x{closeStatus:X8}");
            _query = IntPtr.Zero;
            _unavailable = true;
            return false;
        }

        uint collectStatus = PdhCollectQueryData(_query);
        if (collectStatus != ErrorSuccess)
        {
            uint closeStatus = PdhCloseQuery(_query);
            if (closeStatus != ErrorSuccess)
                LogService.Info("performance", $"PDH_DISK_CLOSE_WARN stage=initial-collect status=0x{closeStatus:X8}");
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _unavailable = true;
            return false;
        }

        _initialized = true;
        return true;
    }

    internal static void Dispose()
    {
        lock (Gate)
        {
            if (_query != IntPtr.Zero)
            {
                uint closeStatus = PdhCloseQuery(_query);
                if (closeStatus != ErrorSuccess)
                    LogService.Info("performance", $"PDH_DISK_CLOSE_WARN stage=dispose status=0x{closeStatus:X8}");
            }
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _initialized = false;
        }
    }
}
