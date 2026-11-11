using System;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

/// <summary>Read-only PDH peak logical CPU sample. Reuses one native query and
/// an unmanaged buffer; called by the existing 30-second AI smoothness tick.</summary>
internal static class AiLogicalCorePdhSampler
{
    internal readonly record struct Peak(double Percent, string LogicalProcessor);
    private const uint Success = 0;
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private static readonly object Gate = new();
    private static IntPtr _query, _counter, _buffer;
    private static uint _capacity;
    private static bool _initialized, _unavailable;
    private static DateTime _lastInitFailureUtc;

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr data, out IntPtr query);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
        ref uint bytes, ref uint count, IntPtr buffer);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    internal static Peak? TryRead()
    {
        lock (Gate)
        {
            if (_unavailable)
            {
                if (!AiLogicalCorePressurePolicy.CanRetryProvider(
                        _lastInitFailureUtc, DateTime.UtcNow)) return null;
                _unavailable = false;
            }
            if (!_initialized && !Initialize()) return null;
            try
            {
                if (PdhCollectQueryData(_query) != Success) return null;
                uint bytes = 0, count = 0;
                if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref bytes, ref count,
                        IntPtr.Zero) != MoreData || bytes == 0 || count == 0 || bytes > 1024 * 1024)
                    return null;
                if (_capacity < bytes)
                {
                    IntPtr replacement = Marshal.AllocHGlobal(checked((int)bytes));
                    if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                    _buffer = replacement;
                    _capacity = bytes;
                }
                uint available = _capacity;
                if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref available, ref count,
                        _buffer) != Success) return null;
                int stride = Marshal.SizeOf<CounterItem>();
                if (count > 1024 || (ulong)count * (uint)stride > available) return null;
                Peak? peak = null;
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(_buffer, i * stride));
                    if (item.Value.Status > 1 || !double.IsFinite(item.Value.Value) ||
                        item.Value.Value < 0 || item.Value.Value > 100) continue;
                    // Most entries are colder; don't allocate a logical-core name
                    // unless its utilization can replace the current maximum.
                    if (peak.HasValue && item.Value.Value <= peak.Value.Percent) continue;
                    string name = Marshal.PtrToStringUni(item.Name) ?? "";
                    if (AiLogicalCorePressurePolicy.IsLogicalProcessorName(name))
                        peak = new Peak(item.Value.Value, name);
                }
                return peak;
            }
            catch (Exception ex) when (ex is ExternalException or OverflowException or OutOfMemoryException)
            {
                return null; // Never block the existing agent when PDH is unavailable.
            }
        }
    }

    internal static void Dispose()
    {
        lock (Gate)
        {
            if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _buffer = IntPtr.Zero;
            _capacity = 0;
            _initialized = false;
            _unavailable = false;
            _lastInitFailureUtc = default;
        }
    }

    private static bool Initialize()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != Success)
        {
            _lastInitFailureUtc = DateTime.UtcNow;
            _unavailable = true;
            return false;
        }
        if (PdhAddEnglishCounterW(_query, @"\Processor Information(*)\% Processor Time",
                IntPtr.Zero, out _counter) != Success)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _lastInitFailureUtc = DateTime.UtcNow;
            _unavailable = true;
            return false;
        }
        _ = PdhCollectQueryData(_query);
        _initialized = true;
        _lastInitFailureUtc = default;
        return true;
    }
}
