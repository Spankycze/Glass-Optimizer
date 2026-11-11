using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class GTweakMemoryPurgeService
{
    private const int SePrivilegeEnabled = 2;
    private const int SystemFileCacheInformation = 0x15;
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TokenPrivilege
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Cache32
    {
        public uint CurrentSize, PeakSize, PageFaultCount, MinimumWorkingSet, MaximumWorkingSet;
        public uint U1, U2, U3, U4;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Cache64
    {
        public long CurrentSize, PeakSize, PageFaultCount, MinimumWorkingSet, MaximumWorkingSet;
        public long U1, U2, U3, U4;
    }
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, ref long luid);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state,
        int length, IntPtr previous, IntPtr returnLength);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int infoClass, IntPtr info, int length);

    public static Task PurgeAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!EnablePrivilege("SeIncreaseQuotaPrivilege"))
            throw new InvalidOperationException("Nelze aktivovat SeIncreaseQuotaPrivilege.");
        PurgeFileSystemCache();
        token.ThrowIfCancellationRequested();
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            throw new InvalidOperationException("Nelze aktivovat SeProfileSingleProcessPrivilege.");
        PurgeStandbyList();
    }, token);

    private static bool EnablePrivilege(string name)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges);
        var state = new TokenPrivilege { Count = 1, Attributes = SePrivilegeEnabled };
        if (!LookupPrivilegeValue(null, name, ref state.Luid))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue " + name);
        if (!AdjustTokenPrivileges(identity.Token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges " + name);
        return true;
    }
    private static void PurgeFileSystemCache()
    {
        GCHandle handle = default;
        try
        {
            if (IntPtr.Size == 8)
            {
                var info = new Cache64 { MinimumWorkingSet = -1, MaximumWorkingSet = -1 };
                handle = GCHandle.Alloc(info, GCHandleType.Pinned);
            }
            else
            {
                var info = new Cache32 { MinimumWorkingSet = uint.MaxValue, MaximumWorkingSet = uint.MaxValue };
                handle = GCHandle.Alloc(info, GCHandleType.Pinned);
            }
            int size = IntPtr.Size == 8 ? Marshal.SizeOf<Cache64>() : Marshal.SizeOf<Cache32>();
            uint status = NtSetSystemInformation(SystemFileCacheInformation, handle.AddrOfPinnedObject(), size);
            if (status != 0) throw new InvalidOperationException($"NtSetSystemInformation(FileCache) NTSTATUS=0x{status:X8}");
        }
        finally { if (handle.IsAllocated) handle.Free(); }
    }

    private static void PurgeStandbyList()
    {
        GCHandle handle = default;
        try
        {
            int value = MemoryPurgeStandbyList;
            handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            uint status = NtSetSystemInformation(SystemMemoryListInformation, handle.AddrOfPinnedObject(), sizeof(int));
            if (status != 0) throw new InvalidOperationException($"NtSetSystemInformation(MemoryList) NTSTATUS=0x{status:X8}");
        }
        finally { if (handle.IsAllocated) handle.Free(); }
    }
}
