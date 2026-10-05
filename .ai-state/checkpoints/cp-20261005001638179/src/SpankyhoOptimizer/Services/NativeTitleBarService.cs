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

    private const uint GlassResizeSubclassId = 0x53474C41;
    private delegate IntPtr WindowSubclassProc(IntPtr hwnd,uint message,UIntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data);
    private static readonly WindowSubclassProc GlassResizeCallback = GlassResizeWindowProc;
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd,WindowSubclassProc callback,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd,WindowSubclassProc callback,UIntPtr id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd,uint message,UIntPtr wParam,IntPtr lParam);
    private static IntPtr GlassResizeWindowProc(IntPtr hwnd,uint message,UIntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        if(message == 0x82) RemoveWindowSubclass(hwnd,GlassResizeCallback,id); // WM_NCDESTROY
        IntPtr original=DefSubclassProc(hwnd,message,wParam,lParam);
        if(message != 0x84 || (original.ToInt64() != 1 && original.ToInt64() != 2)) return original;
        try
        {
            if(!GetClientRect(hwnd,out var client)) return original;
            var origin=new NativePoint();
            if(!ClientToScreen(hwnd,ref origin)) return original;
            long packed=lParam.ToInt64();
            int x=(short)(packed & 0xffff)-origin.X,y=(short)((packed >> 16) & 0xffff)-origin.Y;
            if(x < 0 || y < 0 || x >= client.Right || y >= client.Bottom) return original;
            int grip=(int)data.ToUInt32();
            bool left=x<grip,right=x>=client.Right-grip,top=y<grip,bottom=y>=client.Bottom-grip;
            int hit=top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
            return hit == 0 ? original : new IntPtr(hit);
        }
        catch { return original; }
    }
    public static bool TrySetRoundedGlassRegion(IntPtr hwnd, bool rounded, double radius, out int error)
    {
        error = 0;
        if (hwnd == IntPtr.Zero) { error = -1; return false; }
        if (!rounded)
        {
            RemoveWindowSubclass(hwnd,GlassResizeCallback,new UIntPtr(GlassResizeSubclassId));
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
        if (SetWindowRgn(hwnd, region, true) != 0)
        {
            // Windows owns the HRGN. The client edge must remain a native resize target.
            int grip = Math.Max(1,(int)Math.Round(radius*8/25));
            if (SetWindowSubclass(hwnd,GlassResizeCallback,new UIntPtr(GlassResizeSubclassId),new UIntPtr((uint)grip))) return true;
            error = -3; return false;
        }
        error = Marshal.GetLastWin32Error();
        DeleteObject(region);
        return false;
    }

    public static bool VerifyRoundedGlassRegion(IntPtr hwnd, out string detail)
    {
        detail = "geometry unavailable";
        if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var client)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) return false;
        IntPtr region = CreateRoundRectRgn(0,0,1,1,1,1);
        if (region == IntPtr.Zero) return false;
        try
        {
            int left = origin.X-window.Left, top = origin.Y-window.Top;
            int kind = GetWindowRgn(hwnd,region);
            bool corner = PtInRegion(region,left,top), center = PtInRegion(region,left+client.Right/2,top+client.Bottom/2);
            detail = $"kind={kind} origin={left},{top} client={client.Right}x{client.Bottom} window={window.Right-window.Left}x{window.Bottom-window.Top} corner={corner} center={center}";
            return kind > 0 && !corner && center;
        }
        finally { DeleteObject(region); }
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public static bool HasCustomWindowRegion(IntPtr hwnd)
    {
        IntPtr region=CreateRoundRectRgn(0,0,1,1,1,1);
        if(region == IntPtr.Zero) throw new InvalidOperationException("Region query allocation failed");
        try { return GetWindowRgn(hwnd,region) > 0; }
        finally { DeleteObject(region); }
    }
    public static void VerifyGlassResizeEdges(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var client)) throw new InvalidOperationException("Resize client rectangle unavailable");
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd,ref origin)) throw new InvalidOperationException("Resize origin unavailable");
        var probes = new[] { (3, client.Bottom/2, 10), (client.Right-3, client.Bottom/2,11),
            (client.Right/2,3,12), (client.Right/2,client.Bottom-3,15) };
        foreach (var (x,y,expected) in probes)
        {
            int sx=origin.X+x,sy=origin.Y+y;
            var packed = new IntPtr(unchecked((int)((uint)(ushort)sx | ((uint)(ushort)sy << 16))));
            int actual=SendMessageW(hwnd,0x84,IntPtr.Zero,packed).ToInt32(); // WM_NCHITTEST: query only, no input injection.
            if(actual != expected) throw new InvalidOperationException($"Glass resize hit-test failed x={x} y={y} actual={actual} expected={expected}");
        }
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
