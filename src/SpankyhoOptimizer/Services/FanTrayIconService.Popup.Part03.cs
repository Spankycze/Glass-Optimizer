using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

public sealed partial class FanTrayIconService
{
    // Invoked only from the isolated release-service smoke; never from the
    // long-running FanAgent. Exercises the same native GDI allocators, rounded
    // painter and buffer transfer without creating a tray icon or touching
    // the user's desktop pixels.
    internal static string RunHoverGdiResourceSmoke(string? captureDir = null,
        bool steadyState = false)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 0);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("Unable to obtain read-only screen DC for tray GDI smoke.");

        const int rounds = 96;
        // Test-only diagnostic: explicit CLI opt-in, never from the FanAgent.
        if (!string.IsNullOrWhiteSpace(captureDir))
            Directory.CreateDirectory(captureDir);
        IntPtr[] headers = new IntPtr[3], bodies = new IntPtr[3];
        try
        {
            for (int dpiIndex = 0; dpiIndex < 3; dpiIndex++)
            {
                // 100%, 200% and 300% physical popup sizes.
                int scale = dpiIndex + 1;
                headers[dpiIndex] = CreateFontW(-17 * scale, 0, 0, 0, 700, 0, 0, 0,
                    1, 0, 0, 5, 0, "Segoe UI");
                bodies[dpiIndex] = CreateFontW(-13 * scale, 0, 0, 0, 400, 0, 0, 0,
                    1, 0, 0, 5, 0, "Segoe UI");
                if (headers[dpiIndex] == IntPtr.Zero || bodies[dpiIndex] == IntPtr.Zero)
                    throw new InvalidOperationException("Tray test DPI font initialization failed.");
            }
            for (int i = 0; i < rounds; i++)
            {
                int dpiIndex = i % 3;
                int statusIndex = (i / 3) % 3;
                int scale = dpiIndex + 1;
                int width = 480 * scale, height = 220 * scale;
                IntPtr sourceDc = IntPtr.Zero, destinationDc = IntPtr.Zero;
                IntPtr sourceBitmap = IntPtr.Zero, destinationBitmap = IntPtr.Zero;
                IntPtr sourceOriginal = IntPtr.Zero, destinationOriginal = IntPtr.Zero;
                try
                {
                    sourceDc = CreateCompatibleDC(screenDc);
                    destinationDc = CreateCompatibleDC(screenDc);
                    sourceBitmap = CreateCompatibleBitmap(screenDc, width, height);
                    destinationBitmap = CreateCompatibleBitmap(screenDc, width, height);
                    if (sourceDc == IntPtr.Zero || destinationDc == IntPtr.Zero ||
                        sourceBitmap == IntPtr.Zero || destinationBitmap == IntPtr.Zero)
                        throw new InvalidOperationException("Tray offscreen GDI allocation failed.");
                    sourceOriginal = SelectObject(sourceDc, sourceBitmap);
                    destinationOriginal = SelectObject(destinationDc, destinationBitmap);
                    if (sourceOriginal == IntPtr.Zero || destinationOriginal == IntPtr.Zero ||
                        sourceOriginal == new IntPtr(-1) || destinationOriginal == new IntPtr(-1))
                        throw new InvalidOperationException("Tray offscreen GDI bitmap selection failed.");

                    // This is the actual production renderer, not a simplified
                    // duplicate of its rounded panels or RPM cells.
                    string tooltip = statusIndex switch
                    {
                        0 => "CPU 51°C • Fan 1200/1300/1400/1500 RPM • AUTO65 • AI AUTO",
                        1 => "CPU 91°C • Fan 1620/1740/1860/1980 RPM • AUTO65 • DIABLO PERF • AI 77",
                        _ => "CPU —°C • RPM — • Balanced"
                    };
                    PaintHoverCard(sourceDc, width, height, tooltip,
                        (FanTrayState)statusIndex, headers[dpiIndex], bodies[dpiIndex]);
                      uint secondaryPanel = Rgb(32, 32, 32);
                      uint outerCard = Rgb(32, 32, 32);
                     if (!BitBlt(destinationDc, 0, 0, width, height, sourceDc, 0, 0, SrcCopy))
                         throw new InvalidOperationException("Tray rendered card GDI bitmap copy failed.");
                     uint pBackgroundTop = GetPixel(destinationDc, 5 * scale, 5 * scale);
                     uint pBackgroundLeft = GetPixel(destinationDc, 2 * scale, 140 * scale);
                      uint pFan = GetPixel(destinationDc, 440 * scale, 180 * scale);
                     uint pOuter = GetPixel(destinationDc, 290 * scale, 180 * scale);
                     uint pBackgroundBottom = GetPixel(destinationDc, 400 * scale, height - 10 * scale);
                     uint pCpu = GetPixel(destinationDc, 32 * scale, 58 * scale);
                     uint pBackgroundBottomLeft = GetPixel(destinationDc, 32 * scale, height - 10 * scale);
                      if (pBackgroundTop != Rgb(24, 24, 24) ||
                         pBackgroundLeft != Rgb(24, 24, 24) ||
                          pFan != secondaryPanel || pOuter != outerCard ||
                          pBackgroundBottom != Rgb(24, 24, 24) ||
                          pCpu != Rgb(32, 32, 32) ||
                           pBackgroundBottomLeft != Rgb(24, 24, 24))
                          throw new InvalidOperationException($"Tray rendered card GDI pixel smoke failed at frame {i}.");
                    if (!string.IsNullOrWhiteSpace(captureDir) && i < 9)
                    {
                        // Deselect before GetDIBits (Win32 requirement).
                        _ = SelectObject(destinationDc, destinationOriginal);
                        destinationOriginal = IntPtr.Zero;
                        string bmp = Path.Combine(captureDir,
                            $"tray-status-{statusIndex}-dpi-{scale}x.bmp");
                        SaveTrayDiagnosticBmp(screenDc, destinationBitmap,
                            width, height, bmp);
                    }
                }
                finally
                {
                    if (sourceDc != IntPtr.Zero &&
                        sourceOriginal != IntPtr.Zero && sourceOriginal != new IntPtr(-1))
                        _ = SelectObject(sourceDc, sourceOriginal);
                    if (destinationDc != IntPtr.Zero &&
                        destinationOriginal != IntPtr.Zero && destinationOriginal != new IntPtr(-1))
                        _ = SelectObject(destinationDc, destinationOriginal);
                    if (sourceBitmap != IntPtr.Zero) _ = DeleteObject(sourceBitmap);
                    if (destinationBitmap != IntPtr.Zero) _ = DeleteObject(destinationBitmap);
                    if (sourceDc != IntPtr.Zero) _ = DeleteDC(sourceDc);
                    if (destinationDc != IntPtr.Zero) _ = DeleteDC(destinationDc);
                }
            }
        }
        finally
        {
            for (int i = 0; i < 3; i++)
            {
                if (headers[i] != IntPtr.Zero) _ = DeleteObject(headers[i]);
                if (bodies[i] != IntPtr.Zero) _ = DeleteObject(bodies[i]);
            }
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
        uint after = GetGuiResources(process.Handle, 0);
        uint growthBudget = steadyState ? 1u : 8u;
        if (after > before + growthBudget)
            throw new InvalidOperationException(
                $"Tray GDI resources grew after {rounds} draws: {before} -> {after}; " +
                $"steadyState={steadyState} budget={growthBudget}.");
        return $"FAN_TRAY_GDI_SMOKE_OK draws={rounds} dpiScales=1x,2x,3x " +
            $"gdiBefore={before} gdiAfter={after} steadyState={steadyState}";
    }

    private void EnsureHoverFonts(int width)
    {
        if (_popupFontWidth == width &&
            _popupHeaderFont != IntPtr.Zero && _popupBodyFont != IntPtr.Zero) return;
        // A tray popup can move to a differently scaled monitor during one session.
        // Recreate cached GDI fonts when the physical popup width changes.
        if (_popupHeaderFont != IntPtr.Zero) _ = DeleteObject(_popupHeaderFont);
        if (_popupBodyFont != IntPtr.Zero) _ = DeleteObject(_popupBodyFont);
        double scale = width / 480d;
        _popupHeaderFont = CreateFontW(-(int)Math.Round(19 * scale), 0, 0, 0, 700,
            0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _popupBodyFont = CreateFontW(-(int)Math.Round(15 * scale), 0, 0, 0, 400,
            0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _popupFontWidth = width;
    }

    internal static uint HoverStatusAccent(FanTrayState state) => state switch
    {
        FanTrayState.Error => Rgb(251, 130, 133),
        FanTrayState.Warning => Rgb(248, 192, 96),
        _ => Rgb(94, 181, 255)
    };

    private static void PaintHoverCard(IntPtr dc, int width, int height,
        string tooltip, FanTrayState state, IntPtr headerFont, IntPtr bodyFont)
    {
            double s = width / 480d;
            int Px(double v) => (int)Math.Round(v * s);
            uint accent = HoverStatusAccent(state);
            Fill(dc, 0, 0, width, height, Rgb(24, 24, 24));
            FillRounded(dc, Px(16), Px(18), width - Px(16), height - Px(18), Px(8), Rgb(64, 64, 64));
            FillRounded(dc, Px(17), Px(19), width - Px(17), height - Px(19), Px(8), Rgb(32, 32, 32));
            _ = SetBkMode(dc, 1);

            void Text(string value, int x, int top, int right, int size, uint color,
                bool bold = false, bool centered = false)
            {
                IntPtr font = bold ? headerFont : bodyFont;
                IntPtr old = font != IntPtr.Zero ? SelectObject(dc, font) : IntPtr.Zero;
                try
                {
                    _ = SetTextColor(dc, color);
                    var rect = new TrayRect { left = Px(x), top = Px(top),
                        right = Px(right), bottom = Px(top + size) };
                    _ = DrawTextW(dc, value, -1, ref rect, DtFlags | (centered ? 1 : 0));
                }
                finally
                {
                    if (old != IntPtr.Zero) _ = SelectObject(dc, old);
                }
            }
            string[] parts = tooltip
                .Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Text("Teplota CPU", 46, 42, 150, 24, Rgb(206, 206, 206));
            Text(FormatCpuTrayValue(parts.Length > 0 ? parts[0] : null),
                160, 42, 300, 24, Rgb(255, 255, 255), true);

            string fanText = parts.Length > 1 ? parts[1] : "RPM —";
            string[]? fanSpeeds = ParseFanRpmValues(fanText);
            Text("FAN · RPM", 318, 42, 438, 24, Rgb(206, 206, 206));
            if (fanSpeeds is { Length: >= 1 })
            {
                for (int i = 0; i < fanSpeeds.Length; i++)
                    Text(fanSpeeds[i], 318, 70 + i * 22, 438, 22,
                        Rgb(255, 255, 255), true);
            }
            else
            {
                Text("Nedostupné", 318, 70, 438, 26, Rgb(255, 255, 255));
                Text("Bez odečtu", 318, 100, 438, 22, Rgb(206, 206, 206));
            }

            string mode = parts.Length > 2 ? parts[2] : "AUTO";
            string ai = "—";
            string activity = string.Empty;
            for (int i = 3; i < parts.Length; i++)
            {
                if (ai == "—" && parts[i].StartsWith("AI ", StringComparison.OrdinalIgnoreCase))
                    ai = parts[i][3..];
                else
                    activity += (activity.Length == 0 ? string.Empty : " · ") + parts[i];
            }
            Text("Režim", 46, 78, 150, 24, Rgb(206, 206, 206));
            Text(mode, 160, 78, 300, 24, Rgb(255, 255, 255), true);
            Text("AI", 46, 114, 150, 24, Rgb(206, 206, 206));
            Text(ai, 160, 114, 300, 24, Rgb(255, 255, 255), true);
            string status = state switch
            {
                FanTrayState.Error => "Chyba",
                FanTrayState.Warning => "Varování",
                _ => "V pořádku"
            };
            if (activity.Length > 0) status += " · " + activity;
            Fill(dc, Px(44), Px(164), Px(436), Px(164) + Math.Max(1, Px(1)), Rgb(56, 56, 56));
            FillRounded(dc, Px(44), Px(180), Px(52), Px(188), Px(4), accent);
            Text(status, 64, 172, 438, 24, Rgb(238, 238, 238));
    }

    private void DrawHoverPopup(IntPtr hwnd)
    {
        IntPtr paintDc = BeginPaint(hwnd, out TrayPaintStruct paint);
        if (paintDc == IntPtr.Zero) return;
        IntPtr bufferDc = IntPtr.Zero;
        IntPtr bufferBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        IntPtr dc = paintDc;
        try
        {
            _ = GetClientRect(hwnd, out TrayRect bounds);
            int width = Math.Max(1, bounds.right - bounds.left);
            int height = Math.Max(1, bounds.bottom - bounds.top);
            // Paint complete card offscreen, then transfer once. This avoids
            // flashing between separate GDI background/text/rounded-cell draws.
            bufferDc = CreateCompatibleDC(paintDc);
            if (bufferDc != IntPtr.Zero)
            {
                bufferBitmap = CreateCompatibleBitmap(paintDc, width, height);
                if (bufferBitmap != IntPtr.Zero)
                {
                    oldBitmap = SelectObject(bufferDc, bufferBitmap);
                    if (oldBitmap != IntPtr.Zero && oldBitmap != new IntPtr(-1))
                        dc = bufferDc;
                }
            }
            EnsureHoverFonts(width);
            PaintHoverCard(dc, width, height, Volatile.Read(ref _popupTooltip),
                _state, _popupHeaderFont, _popupBodyFont);
            if (dc == bufferDc)
                _ = BitBlt(paintDc, 0, 0, width, height, bufferDc, 0, 0, SrcCopy);
        }
        finally
        {
            if (dc == bufferDc && oldBitmap != IntPtr.Zero)
                _ = SelectObject(bufferDc, oldBitmap);
            if (bufferBitmap != IntPtr.Zero) _ = DeleteObject(bufferBitmap);
            if (bufferDc != IntPtr.Zero) _ = DeleteDC(bufferDc);
            _ = EndPaint(hwnd, ref paint);
        }
    }

    private void DisposeHoverPopup()
    {
        HideHoverPopup();
        if (_popupWindow != IntPtr.Zero)
        {
            if (_popupOriginalWndProc != IntPtr.Zero)
                _ = SetWindowLongPtrW(_popupWindow, GwlpWndProc, _popupOriginalWndProc);
            _ = DestroyWindow(_popupWindow);
            _popupWindow = IntPtr.Zero;
            _popupOriginalWndProc = IntPtr.Zero;
        }
        if (_popupHeaderFont != IntPtr.Zero)
            _ = DeleteObject(_popupHeaderFont);
        if (_popupBodyFont != IntPtr.Zero)
            _ = DeleteObject(_popupBodyFont);
        _popupHeaderFont = _popupBodyFont = IntPtr.Zero;
        _popupFontWidth = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayRect { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayNotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayMonitorInfo
    {
        public uint cbSize;
        public TrayRect rcMonitor, rcWork;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayPaintStruct
    {
        public IntPtr hdc;
        public int fErase;
        public TrayRect rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(
        ref TrayNotifyIconIdentifier identifier, out TrayRect iconLocation);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType,
        out uint dpiX, out uint dpiY);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref TrayMonitorInfo info);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd,
        uint colorKey, byte alpha, uint flags);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd,
        out uint colorKey, out byte alpha, out uint flags);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top,
        int right, int bottom, int ellipseWidth, int ellipseHeight);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out TrayPaintStruct paint);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref TrayPaintStruct paint);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out TrayRect rect);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr dc, string text, int chars,
        ref TrayRect rect, int format);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref TrayRect rect, IntPtr brush);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern int FillRgn(IntPtr dc, IntPtr region, IntPtr brush);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rasterOp);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start,
        uint lines, [Out] byte[] bits, ref TrayBitmapInfoHeader info, uint usage);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width,
        int escapement, int orientation, int weight, uint italic,
        uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily,
        string faceName);
}
