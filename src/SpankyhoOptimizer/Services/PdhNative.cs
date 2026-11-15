using System;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

// Disk and GPU fallback readers share the same PDH query lifecycle; one
// declaration set keeps both interop shapes identical.
internal static class PdhNative
{
    internal const uint FmtDouble = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);
}
