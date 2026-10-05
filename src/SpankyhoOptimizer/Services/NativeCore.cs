using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static class NativeCore
{
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;

        public Guid ClassGuid;

        public uint DevInst;

        public nint Reserved;
    }

    private const uint DIGCF_PRESENT = 2u;

    private const uint DIGCF_ALLCLASSES = 4u;

    private const uint SPDRP_DEVICEDESC = 0u;

    private const uint SPDRP_CLASS = 7u;

    private const uint SPDRP_FRIENDLYNAME = 12u;

    private const uint SPDRP_MFG = 11u;

    private const uint SC_MANAGER_CONNECT = 1u;

    private const uint SERVICE_CHANGE_CONFIG = 2u;

    private const uint SERVICE_NO_CHANGE = uint.MaxValue;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(nint root, out nint guid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveSchemeManaged(nint root, ref Guid guid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PowerReadFriendlyName(nint rootPowerKey, ref Guid schemeGuid, nint subGroupGuid, nint settingGuid, byte[]? buffer, ref uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint ptr);

    public static bool HasSystemBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return false;
        return s.BatteryFlag != 128 && s.BatteryFlag != 255;
    }

    public static void SetActivePowerScheme(Guid guid)
    {
        if (RustNativeCore.TrySetActivePowerScheme(guid)) return;
        uint result = PowerSetActiveSchemeManaged(IntPtr.Zero, ref guid);
        if (result != 0) throw new Win32Exception((int)result);
    }

    public static string GetActivePowerSchemeName()
    {
        Guid guid = GetActivePowerScheme();
        uint size = 0;
        _ = PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size < 2 || size > 8192) return guid.ToString("D");
        byte[] buffer = new byte[size];
        uint rc = PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
        if (rc != 0) return guid.ToString("D");
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static Guid GetActivePowerScheme()
    {
        if (RustNativeCore.TryGetActivePowerScheme(out var rustGuid)) return rustGuid;
        uint num = PowerGetActiveScheme(IntPtr.Zero, out var guid);
        if (num != 0 || guid == IntPtr.Zero)
        {
            throw new Win32Exception((int)num);
        }
        try
        {
            return Marshal.PtrToStructure<Guid>(guid);
        }
        finally
        {
            LocalFree(guid);
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(nint classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint memberIndex, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref SP_DEVINFO_DATA data, uint property, out uint type, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref SP_DEVINFO_DATA data, StringBuilder instance, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    private static string Prop(nint set, ref SP_DEVINFO_DATA d, uint prop)
    {
        byte[] array = new byte[4096];
        if (!SetupDiGetDeviceRegistryProperty(set, ref d, prop, out var _, array, (uint)array.Length, out var required))
        {
            return string.Empty;
        }
        return Encoding.Unicode.GetString(array, 0, (int)Math.Min(required, (uint)array.Length)).TrimEnd('\0');
    }

    public static IReadOnlyList<DeviceInfo> GetPresentDevices()
    {
        List<DeviceInfo> list = new List<DeviceInfo>(256);
        nint num = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 6u);
        if (num == new IntPtr(-1))
        {
            return list;
        }
        try
        {
            uint num2 = 0u;
            while (true)
            {
                SP_DEVINFO_DATA data = new SP_DEVINFO_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
                };
                if (!SetupDiEnumDeviceInfo(num, num2, ref data))
                {
                    break;
                }
                StringBuilder stringBuilder = new StringBuilder(1024);
                if (SetupDiGetDeviceInstanceId(num, ref data, stringBuilder, (uint)stringBuilder.Capacity, out var _))
                {
                    string text = Prop(num, ref data, 12u);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        text = Prop(num, ref data, 0u);
                    }
                    string manufacturer = Prop(num, ref data, SPDRP_MFG);
                    int problemCode = 0;
                    if (CM_Get_DevNode_Status(out _, out uint problem, data.DevInst, 0) == 0)
                        problemCode = unchecked((int)problem);
                    list.Add(new DeviceInfo(
                        stringBuilder.ToString(),
                        Prop(num, ref data, SPDRP_CLASS),
                        text,
                        Present: true,
                        string.IsNullOrWhiteSpace(manufacturer) ? "Unknown" : manufacturer,
                        problemCode));
                }
                num2++;
            }
            return list;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(num);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint scm, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup, nint tagId, string? dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(nint handle);

    public static bool ChangeServiceStartType(string name, uint startType, out int error)
    {
        error = 0;
        nint num = OpenSCManager(null, null, 1u);
        if (num == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
            nint num2 = OpenService(num, name, 2u);
            if (num2 == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            try
            {
                bool num3 = ChangeServiceConfig(num2, uint.MaxValue, startType, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null);
                if (!num3)
                {
                    error = Marshal.GetLastWin32Error();
                }
                return num3;
            }
            finally
            {
                CloseServiceHandle(num2);
            }
        }
        finally
        {
            CloseServiceHandle(num);
        }
    }
}
