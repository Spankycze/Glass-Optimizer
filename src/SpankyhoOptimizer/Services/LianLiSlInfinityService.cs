using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Direct USB HID control for the Lian Li UNI FAN SL-Infinity hub (0CF2:A102).
/// No L-Connect runtime dependency. Protocol values are clean-room implemented
/// from public SL-Infinity HID documentation.
/// </summary>
public static class LianLiSlInfinityService
{
    public const ushort VendorId = 0x0CF2;
    public const ushort ProductId = 0xA102;
    public const int PhysicalFanChannels = 4;
    public const int RgbChannels = 8;
    public const int MinimumRpm = 200;
    public const int MaximumRpm = 2100;
    public const int MinimumSafeRpm = 500;
    public const int MinimumSafePercent = 16;

    public sealed record ControllerInfo(bool Present, string? DevicePath, string DisplayName);

    public static ControllerInfo Detect()
    {
        string? path = FindControllerPath();
        return new ControllerInfo(path != null, path, path != null
            ? "Lian Li UNI FAN SL-Infinity"
            : "Lian Li SL-Infinity controller nenalezen");
    }
    public static void SetAllFanSpeed(int percent)
    {
        percent = Math.Clamp(percent, MinimumSafePercent, 100);
        WithControllerRetry(handle =>
        {
            for (int channel = 0; channel < PhysicalFanChannels; channel++)
                WriteFanSpeed(handle, channel, percent);
        });
    }

    public static void SetFanSpeed(int channel, int percent)
    {
        ValidateFanChannel(channel);
        percent = Math.Clamp(percent, MinimumSafePercent, 100);
        WithControllerRetry(handle => WriteFanSpeed(handle, channel, percent));
    }

    public static void SetAllFanRpm(int rpm)
    {
        rpm = Math.Clamp(rpm, MinimumSafeRpm, MaximumRpm);
        int percent = RpmToPercent(rpm);
        SetAllFanSpeed(Math.Clamp(percent, MinimumSafePercent, 100));
    }

    public static void SetFanRpm(int channel, int rpm)
    {
        SetFanRpm(channel, rpm, MaximumRpm);
    }

    public static void SetFanRpm(int channel, int rpm, int calibratedMaxRpm)
    {
        ValidateFanChannel(channel);
        calibratedMaxRpm = Math.Clamp(calibratedMaxRpm, 1200, 2600);
        rpm = Math.Clamp(rpm, MinimumSafeRpm, calibratedMaxRpm);
        int percent = (int)Math.Round((rpm - MinimumRpm) * 100d / Math.Max(1, calibratedMaxRpm - MinimumRpm));
        SetFanSpeed(channel, Math.Clamp(percent, MinimumSafePercent, 100));
    }

    public static int RpmToPercent(int rpm) =>
        Math.Clamp((int)Math.Round((Math.Clamp(rpm, MinimumRpm, MaximumRpm) - MinimumRpm) * 100d /
                                  (MaximumRpm - MinimumRpm)), 0, 100);

    public static int PercentToRpm(int percent) =>
        MinimumRpm + (int)Math.Round((MaximumRpm - MinimumRpm) * Math.Clamp(percent, 0, 100) / 100d);

    public static void SetAllMotherboardPwm()
    {
        WithControllerRetry(handle =>
        {
            for (int channel = 0; channel < PhysicalFanChannels; channel++)
            {
                SendFeatureReport(handle, BuildFanModeReport(channel, motherboardPwm: true));
                System.Threading.Thread.Sleep(120);
            }
        });
    }

    public static void SetMotherboardPwm(int channel)
    {
        ValidateFanChannel(channel);
        WithControllerRetry(handle =>
        {
            SendFeatureReport(handle, BuildFanModeReport(channel, motherboardPwm: true));
            System.Threading.Thread.Sleep(120);
        });
    }

    public static void SetAllStaticColor(byte red, byte green, byte blue, int brightnessPercent)
    {
        WithControllerRetry(handle =>
        {
            byte brightness = MapBrightness(brightnessPercent);
            for (int channel = 0; channel < RgbChannels; channel++)
                WriteRgbEffect(handle, channel, 0x01, 0x00, 0x00, red, green, blue, brightness);
            SendGlobalRgbCommit(handle);
        });
    }
    public static void SetArgbSync(bool enabled)
    {
        WithControllerRetry(handle =>
        {
            byte[] report = new byte[65];
            report[0] = 0xE0;
            report[1] = 0x10;
            report[2] = 0x61;
            report[3] = enabled ? (byte)1 : (byte)0;
            SendFeatureReport(handle, report);
        });
    }

    public static void SetAllEffect(byte mode, byte speed, byte direction, byte red, byte green, byte blue, int brightnessPercent)
    {
        WithControllerRetry(handle =>
        {
            byte brightness = MapBrightness(brightnessPercent);
            for (int channel = 0; channel < RgbChannels; channel++)
                WriteRgbEffect(handle, channel, mode, speed, direction, red, green, blue, brightness);
            SendGlobalRgbCommit(handle);
        });
    }

    public static void SetChannelEffect(int fanChannel, byte mode, byte speed, byte direction, byte red, byte green, byte blue, int brightnessPercent, int fanCount = 4)
    {
        ValidateFanChannel(fanChannel);
        fanCount = Math.Clamp(fanCount <= 0 ? 4 : fanCount, 1, 4);
        WithControllerRetry(handle =>
        {
            byte brightness = MapBrightness(brightnessPercent);
            int firstRgbChannel = fanChannel * 2;
            WriteRgbEffect(handle, firstRgbChannel, mode, speed, direction, red, green, blue, brightness, fanCount);
            WriteRgbEffect(handle, firstRgbChannel + 1, mode, speed, direction, red, green, blue, brightness, fanCount);
            SendGlobalRgbCommit(handle);
        });
    }

    public static void SetZoneEffect(int fanChannel, int zone, byte mode, byte speed, byte direction,
        byte red, byte green, byte blue, int brightnessPercent, int fanCount = 4)
    {
        ValidateFanChannel(fanChannel);
        if (zone < 0 || zone > 1) throw new ArgumentOutOfRangeException(nameof(zone));
        fanCount = Math.Clamp(fanCount <= 0 ? 4 : fanCount, 1, 4);
        WithControllerRetry(handle =>
        {
            byte brightness = MapBrightness(brightnessPercent);
            int rgbChannel = fanChannel * 2 + zone;
            WriteRgbEffect(handle, rgbChannel, mode, speed, direction, red, green, blue, brightness, fanCount);
            SendGlobalRgbCommit(handle);
        });
    }

    private static void ValidateFanChannel(int channel)
    {
        if (channel < 0 || channel >= PhysicalFanChannels)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Fan channel must be 0-3.");
    }

    private static void WriteFanSpeed(SafeFileHandle handle, int channel, int percent)
    {
        SendFeatureReport(handle, BuildFanModeReport(channel, motherboardPwm: false));
        System.Threading.Thread.Sleep(35);
        SendFeatureReport(handle, BuildFanSpeedReport(channel, percent));
        System.Threading.Thread.Sleep(70);
    }

    private static void WriteRgbEffect(SafeFileHandle handle, int rgbChannel, byte mode, byte speed, byte direction,
        byte red, byte green, byte blue, byte brightness, int fanCount = 4)
    {
        speed = NormalizeEffectSpeed(speed);
        SendFeatureReport(handle, BuildRgbStartReport(rgbChannel, fanCount));
        System.Threading.Thread.Sleep(15);
        WriteOutputReport(handle, BuildRgbColorReport(rgbChannel, red, green, blue, fanCount));
        System.Threading.Thread.Sleep(15);
        SendFeatureReport(handle, BuildRgbCommitReport(rgbChannel, mode, speed, direction, brightness));
        System.Threading.Thread.Sleep(15);
    }

    private static void SendGlobalRgbCommit(SafeFileHandle handle)
    {
        byte[] report = new byte[65];
        report[0] = 0xE0;
        report[1] = 0x60;
        report[2] = 0x00;
        report[3] = 0x01;
        SendFeatureReport(handle, report);
        System.Threading.Thread.Sleep(20);
    }

    private static void WithControllerRetry(Action<SafeFileHandle> action)
    {
        _ = WithControllerRetry(handle =>
        {
            action(handle);
            return true;
        });
    }

    private static T WithControllerRetry<T>(Func<SafeFileHandle, T> action)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using SafeFileHandle handle = OpenController();
                return action(handle);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 2)
                    System.Threading.Thread.Sleep(120 + attempt * 180);
            }
        }

        throw new InvalidOperationException("Lian Li controller nereaguje ani po automatickém reconnectu.", last);
    }

    private static byte[] BuildFanModeReport(int channel, bool motherboardPwm)
    {
        byte[] report = new byte[65];
        report[0] = 0xE0;
        report[1] = 0x10;
        report[2] = 0x62;
        report[3] = (byte)((0x10 << channel) | (motherboardPwm ? (0x01 << channel) : 0));
        return report;
    }

    private static byte[] BuildFanSpeedReport(int channel, int percent)
    {
        byte[] report = new byte[65];
        report[0] = 0xE0;
        report[1] = (byte)(0x20 + channel);
        report[2] = 0x00;
        report[3] = percent == 0
            ? (byte)10
            : (byte)Math.Clamp((int)((250d + 17.5d * percent) / 20d), 0, 255);
        return report;
    }
    private static byte[] BuildRgbStartReport(int channel, int fanCount = 4)
    {
        byte[] report = new byte[65];
        report[0] = 0xE0;
        report[1] = 0x10;
        report[2] = 0x60;
        report[3] = (byte)(1 + channel / 2);
        report[4] = (byte)Math.Clamp(fanCount, 1, 4);
        return report;
    }

    private static byte[] BuildRgbColorReport(int channel, byte red, byte green, byte blue, int fanCount = 4)
    {
        byte[] report = new byte[353];
        report[0] = 0xE0;
        report[1] = (byte)(0x30 + channel);
        fanCount = Math.Clamp(fanCount, 1, 4);
        int ledsPerFan = channel % 2 == 0 ? 8 : 12;
        int totalLeds = fanCount * ledsPerFan;
        for (int led = 0; led < totalLeds; led++)
        {
            int offset = 2 + led * 3;
            report[offset] = red;
            report[offset + 1] = blue; // wire order is R,B,G
            report[offset + 2] = green;
        }
        return report;
    }

    private static byte[] BuildRgbCommitReport(int channel, byte brightness) =>
        BuildRgbCommitReport(channel, 0x01, 0x00, 0x00, brightness);

    private static byte[] BuildRgbCommitReport(int channel, byte mode, byte speed, byte direction, byte brightness)
    {
        byte[] report = new byte[65];
        report[0] = 0xE0;
        report[1] = (byte)(0x10 + channel);
        report[2] = mode;
        report[3] = speed;
        report[4] = direction;
        report[5] = brightness;
        return report;
    }
    private static byte NormalizeEffectSpeed(byte speed)
    {
        return speed switch
        {
            0 => 0x02,
            1 => 0x01,
            2 => 0x00,
            3 => 0xFE,
            _ => speed
        };
    }

    private static byte MapBrightness(int percent)
    {
        if (percent <= 0) return 0x08;
        if (percent <= 25) return 0x03;
        if (percent <= 50) return 0x02;
        if (percent <= 75) return 0x01;
        return 0x00;
    }

    private static SafeFileHandle OpenController()
    {
        string path = FindControllerPath()
            ?? throw new InvalidOperationException("Lian Li SL-Infinity controller 0CF2:A102 nebyl nalezen.");

        SafeFileHandle handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nelze otevřít Lian Li HID controller.");
        return handle;
    }

    private static void WriteOutputReport(SafeFileHandle handle, byte[] report)
    {
        if (!TryGetCaps(handle, out HidCaps caps) || caps.OutputReportByteLength <= 0)
            throw new InvalidOperationException("Lian Li HID rozhraní nemá Output Report.");

        int transportLength = caps.OutputReportByteLength;
        if (report.Length > transportLength)
            throw new InvalidOperationException($"Lian Li Output Report je moc dlouhý ({report.Length}>{transportLength}).");

        byte[] wire = report.Length == transportLength ? report : new byte[transportLength];
        if (!ReferenceEquals(wire, report)) Buffer.BlockCopy(report, 0, wire, 0, report.Length);

        bool ok = WriteFile(handle, wire, wire.Length, out int written, IntPtr.Zero);
        int error = Marshal.GetLastWin32Error();
        if (!ok || written != wire.Length)
            throw new Win32Exception(error, $"Lian Li Output Report selhal ({written}/{wire.Length}, win32={error}).");
    }

    private static void SendFeatureReport(SafeFileHandle handle, byte[] report)
    {
        if (!TryGetCaps(handle, out HidCaps caps) || caps.FeatureReportByteLength <= 0)
            throw new InvalidOperationException("Lian Li HID rozhraní nemá Feature Report.");

        int transportLength = caps.FeatureReportByteLength;
        byte[] wire = new byte[transportLength];
        Buffer.BlockCopy(report, 0, wire, 0, Math.Min(report.Length, wire.Length));
        if (!HidD_SetFeature(handle, wire, wire.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Lian Li Feature Report selhal ({wire.Length} B).");
    }

    public static int[] ReadFanRpms() =>
        WithControllerRetry(ReadFanRpmsCore);

    private static int[] ReadFanRpmsCore(SafeFileHandle handle)
    {
        if (!TryGetCaps(handle, out HidCaps caps) || caps.InputReportByteLength <= 0)
            throw new InvalidOperationException("Lian Li HID rozhraní nemá Input Report.");

        var query = new byte[65];
        query[0] = 0xE0;
        query[1] = 0x50;
        query[2] = 0x00;
        SendFeatureReport(handle, query);
        System.Threading.Thread.Sleep(20);

        byte[] input = new byte[caps.InputReportByteLength];
        input[0] = 0xE0;
        if (!HidD_GetInputReport(handle, input, input.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Lian Li RPM Input Report selhal.");

        int payloadOffset = input.Length >= 9 ? 1 : 0;
        if (input.Length - payloadOffset < 8)
            throw new InvalidOperationException($"Lian Li RPM report je krátký ({input.Length} B).");

        var rpm = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int offset = payloadOffset + i * 2;
            int value = (input[offset] << 8) | input[offset + 1];
            if (value < 0 || value > 6000)
                throw new InvalidOperationException($"Lian Li RPM report obsahuje neplatnou hodnotu {value} na kanálu {i + 1}.");
            rpm[i] = value;
        }

        return rpm;
    }

    private static bool TryGetCaps(SafeFileHandle handle, out HidCaps caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
            return false;
        try
        {
            return HidP_GetCaps(preparsed, out caps) >= 0;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }
    private static string? FindControllerPath()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr info = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (info == InvalidHandleValue) return null;

        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref hidGuid, index, ref iface))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(info, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(info, ref iface, detail, required, out _, IntPtr.Zero))
                        continue;

                    string? devicePath = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (string.IsNullOrWhiteSpace(devicePath)) continue;

                    using SafeFileHandle handle = CreateFile(devicePath, 0, FileShareRead | FileShareWrite,
                        IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attr = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attr)) continue;
                    if (attr.VendorID != VendorId || attr.ProductID != ProductId) continue;
                    if (!TryGetCaps(handle, out HidCaps caps)) continue;
                    if (caps.FeatureReportByteLength > 0 && caps.InputReportByteLength > 0 && caps.OutputReportByteLength > 0)
                        return devicePath;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(info); }
        return null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int bytesToWrite,
        out int bytesWritten, IntPtr overlapped);
}
