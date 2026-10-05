using System;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

internal static class NativeTitleBarService
{
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const uint DwmColorNone = 0xFFFFFFFE;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    public static bool TrySetRoundedGlassRegion(IntPtr hwnd, bool rounded, double radius, out int error)
    {
        error = 0;
        if (hwnd == IntPtr.Zero) { error = -1; return false; }
        if (!rounded)
        {
            if (SetWindowRgn(hwnd, IntPtr.Zero, true) != 0) return true;
            error = Marshal.GetLastWin32Error(); return false;
        }
        if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var client))
        { error = Marshal.GetLastWin32Error(); return false; }
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) { error = Marshal.GetLastWin32Error(); return false; }
        int left = origin.X-window.Left, top = origin.Y-window.Top;
        int diameter = Math.Max(2, (int)Math.Round(radius*2));
        IntPtr region = CreateRoundRectRgn(left,top,left+client.Right+1,top+client.Bottom+1,diameter,diameter);
        if (region == IntPtr.Zero) { error = -2; return false; }
        if (SetWindowRgn(hwnd, region, true) != 0) return true; // Successful transfer: Windows owns the HRGN.
        error = Marshal.GetLastWin32Error();
        DeleteObject(region);
        return false;
    }

    public static bool VerifyRoundedGlassRegion(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var client)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) return false;
        IntPtr region = CreateRoundRectRgn(0,0,1,1,1,1);
        if (region == IntPtr.Zero) return false;
        try
        {
            int left = origin.X-window.Left, top = origin.Y-window.Top;
            return GetWindowRgn(hwnd,region) > 0 && !PtInRegion(region,left,top) &&
                PtInRegion(region,left+client.Right/2,top+client.Bottom/2);
        }
        finally { DeleteObject(region); }
    }
    public static bool TryRoundCorners(IntPtr hwnd, out int error)
    {
        if (hwnd == IntPtr.Zero) { error = -1; return false; }
        uint preference = 2; // DWMWCP_ROUND, DWMWA_WINDOW_CORNER_PREFERENCE
        error = DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(uint));
        return error == 0;
    }

    public static bool TryApplyGlassBorder(IntPtr hwnd, bool dark, out int error)
    {
        error = 0;
        if (hwnd == IntPtr.Zero) { error = -1; return false; }
        uint border = DwmColorNone;
        error = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
        return error == 0;
    }

    public static bool TryApply(IntPtr hwnd, bool dark, out int error)
    {
        error = 0;
        if (hwnd == IntPtr.Zero) { error = -1; return false; }

        uint caption = dark ? ColorRef(12, 16, 53) : ColorRef(247, 248, 252);
        uint text = dark ? ColorRef(238, 240, 246) : ColorRef(30, 33, 43);
        uint border = caption;

        int captionResult = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(uint));
        int textResult = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(uint));
        int borderResult = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
        error = captionResult != 0 ? captionResult : textResult != 0 ? textResult : borderResult;
        return error == 0;
    }

    public static bool TryVerify(IntPtr hwnd, bool dark, out string detail)
    {
        uint expected = dark ? ColorRef(12, 16, 53) : ColorRef(247, 248, 252);
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            detail = "GetWindowRect failed.";
            return false;
        }

        int width = rect.Right - rect.Left;
        if (width < 240)
        {
            detail = $"Window width invalid: {width}.";
            return false;
        }

        int flushResult = DwmFlush();
        if (flushResult != 0)
        {
            detail = $"DwmFlush failed: {flushResult}.";
            return false;
        }

        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            detail = "GetDC failed.";
            return false;
        }

        try
        {
            uint p1 = GetPixel(dc, rect.Left + width / 3, rect.Top + 10) & 0x00FFFFFF;
            uint p2 = GetPixel(dc, rect.Left + width / 2, rect.Top + 10) & 0x00FFFFFF;
            uint p3 = GetPixel(dc, rect.Right - 200, rect.Top + 10) & 0x00FFFFFF;
            bool ok = p1 == expected && p2 == expected && p3 == expected;
            detail = $"expected=0x{expected:X6} captionPixels=0x{p1:X6}/0x{p2:X6}/0x{p3:X6}";
            return ok;
        }
        finally { _ = ReleaseDC(IntPtr.Zero, dc); }
    }

    private static uint ColorRef(byte r, byte g, byte b) =>
        (uint)(r | (g << 8) | (b << 16));
}
