using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static class RustNativeCore
{
    private const string DllName = "Spanky.NativeCore.dll";
    private static bool? _available;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint spanky_native_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spanky_get_active_power_scheme(out Guid guid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spanky_set_active_power_scheme(ref Guid guid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int spanky_change_service_start_type(string name, uint startType, out int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int spanky_query_service_start_type(string name, out uint startType, out int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint spanky_get_present_devices_utf8(nint buffer, nuint capacity);

    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try { _available = spanky_native_version() >= 0x000D0700; }
            catch (DllNotFoundException) { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
            catch (BadImageFormatException) { _available = false; }
            return _available.Value;
        }
    }

    public static uint Version => IsAvailable ? spanky_native_version() : 0;

    public static bool TryGetActivePowerScheme(out Guid guid)
    {
        guid = default;
        if (!IsAvailable) return false;
        try { return spanky_get_active_power_scheme(out guid) == 0; }
        catch { _available = false; return false; }
    }

    public static bool TrySetActivePowerScheme(Guid guid)
    {
        if (!IsAvailable) return false;
        try { return spanky_set_active_power_scheme(ref guid) == 0; }
        catch { _available = false; return false; }
    }

    public static bool TryQueryServiceStartType(string name, out uint startType, out int error)
    {
        startType = 0; error = 0;
        if (!IsAvailable) return false;
        try { return spanky_query_service_start_type(name, out startType, out error) != 0; }
        catch { _available = false; return false; }
    }

    public static bool TryChangeServiceStartType(string name, uint startType, out int error)
    {
        error = 0;
        if (!IsAvailable) return false;
        try { return spanky_change_service_start_type(name, startType, out error) != 0; }
        catch { _available = false; return false; }
    }

    public static IReadOnlyList<DeviceInfo>? TryGetPresentDevices()
    {
        if (!IsAvailable) return null;
        try
        {
            nint required = spanky_get_present_devices_utf8(0, 0);
            if (required <= 1 || required > 8 * 1024 * 1024) return null;
            nint ptr = Marshal.AllocHGlobal((int)required);
            try
            {
                nint written = spanky_get_present_devices_utf8(ptr, (nuint)required);
                if (written < 0) return null;
                byte[] bytes = new byte[(int)written];
                Marshal.Copy(ptr, bytes, 0, bytes.Length);
                string text = Encoding.UTF8.GetString(bytes);
                var list = new List<DeviceInfo>(256);
                foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 3) continue;
                    list.Add(new DeviceInfo(p[0], p[1], p[2], Present: true));
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch
        {
            _available = false;
            return null;
        }
    }
}
