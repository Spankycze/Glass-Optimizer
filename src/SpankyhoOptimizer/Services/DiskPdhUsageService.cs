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
    private static IntPtr _latencyCounter;
    private static bool _initialized;
    private static bool _unavailable;
    private static long _retryAfterTicks;
    private const int RetryAfterSeconds = 60;

    // Retry transient PDH setup errors once per minute, not on every
    // four-second telemetry frame. An unsupported provider still returns null.
    private static bool RetryDue(long nowTicks, long retryAfterTicks) =>
        retryAfterTicks == 0 || nowTicks >= retryAfterTicks;

    private static bool IsTemporarilyUnavailable()
    {
        if (!_unavailable) return false;
        if (!RetryDue(System.Diagnostics.Stopwatch.GetTimestamp(), _retryAfterTicks))
            return true;
        _unavailable = false;
        return false;
    }

    private static void MarkUnavailable()
    {
        _unavailable = true;
        _retryAfterTicks = System.Diagnostics.Stopwatch.GetTimestamp() +
            System.Diagnostics.Stopwatch.Frequency * RetryAfterSeconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
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
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    // The live Overview only displays disk busy %. Do not format the
    // additional latency PDH counter on every four-second GUI sample.
    // AI smoothness monitoring still calls TryReadWithLatency(), retaining
    // both counters and all existing I/O latency safeguards.
    internal static double? TryRead()
    {
        lock (Gate)
        {
            if (IsTemporarilyUnavailable()) return null;
            if (!_initialized && !TryInitialize()) return null;
            if (PdhCollectQueryData(_query) != ErrorSuccess) return null;
            double? busy = ReadCounter(_counter);
            return busy.HasValue ? Math.Clamp(busy.Value, 0d, 100d) : null;
        }
    }

    // A single PDH collect returns both passive counters. Latency is a
    // diagnostic of observed I/O, never a synthetic speed measurement.
    internal static (double? BusyPercent, double? LatencyMs) TryReadWithLatency()
    {
        lock (Gate)
        {
            if (IsTemporarilyUnavailable()) return (null, null);
            if (!_initialized && !TryInitialize()) return (null, null);
            if (PdhCollectQueryData(_query) != ErrorSuccess) return (null, null);
            double? busy = ReadCounter(_counter);
            double? seconds = _latencyCounter == IntPtr.Zero ? null : ReadCounter(_latencyCounter);
            // Windows exposes Avg. Disk sec/Transfer in seconds.
            // Reject bad provider data; 5 seconds per I/O is not a reliable sample.
            double? latencyMs = seconds is >= 0 and <= 5
                ? seconds.Value * 1000d : null;
            return (busy.HasValue ? Math.Clamp(busy.Value, 0d, 100d) : null, latencyMs);
        }
    }

    private static double? ReadCounter(IntPtr counter)
    {
        if (counter == IntPtr.Zero ||
            PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var value) != ErrorSuccess ||
            value.CStatus > 1 || !double.IsFinite(value.DoubleValue))
            return null;
        return value.DoubleValue;
    }

    private static bool TryInitialize()
    {
        if (_initialized) return true;
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != ErrorSuccess)
        {
            _query = IntPtr.Zero;
            MarkUnavailable();
            return false;
        }
        uint status = PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _counter);
        if (status != ErrorSuccess)
        {
            uint closeStatus = PdhCloseQuery(_query);
            if (closeStatus != ErrorSuccess)
                LogService.Info("performance", $"PDH_DISK_CLOSE_WARN stage=add-counter status=0x{closeStatus:X8}");
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _latencyCounter = IntPtr.Zero;
            MarkUnavailable();
            return false;
        }

        // Optional, same query/no second polling loop. If this counter is missing,
        // keep the existing disk busy measurement without failing initialization.
        if (PdhAddEnglishCounterW(_query,
            @"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer",
            IntPtr.Zero, out _latencyCounter) != ErrorSuccess)
            _latencyCounter = IntPtr.Zero;

        uint collectStatus = PdhCollectQueryData(_query);
        if (collectStatus != ErrorSuccess)
        {
            uint closeStatus = PdhCloseQuery(_query);
            if (closeStatus != ErrorSuccess)
                LogService.Info("performance", $"PDH_DISK_CLOSE_WARN stage=initial-collect status=0x{closeStatus:X8}");
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _latencyCounter = IntPtr.Zero;
            MarkUnavailable();
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
            _latencyCounter = IntPtr.Zero;
            _initialized = false;
            _unavailable = false;
            _retryAfterTicks = 0;
        }
    }

    // Pure read-only policy test. Does not touch Windows PDH, disk devices,
    // counters or the live query used by Overview.
    internal static string RunRetryPolicySmoke()
    {
        long clock = System.Diagnostics.Stopwatch.Frequency;
        long deadline = clock * RetryAfterSeconds;
        if (!RetryDue(0, 0) ||
            RetryDue(deadline - 1, deadline) ||
            !RetryDue(deadline, deadline) ||
            !RetryDue(deadline + clock, deadline))
            throw new InvalidOperationException("PDH disk recovery retry boundary regression.");
        return "DISK_PDH_RECOVERY_POLICY_OK cooldownSeconds=60 " +
            "retryBounded=true providerFallback=null noDiskWrites=true";
    }
}
