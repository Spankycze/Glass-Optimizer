using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

public sealed partial class FanTrayIconService
{
    private void ShowHoverPopup()
    {
        if (!_added) return;
        if (_popupWindow == IntPtr.Zero)
            CreateHoverPopup();
        if (_popupWindow == IntPtr.Zero)
            throw new InvalidOperationException("Unable to create native tray hover window.");
        if (_popupVisible) return;

        TrayRect anchor;
        var id = new TrayNotifyIconIdentifier
        {
            cbSize = (uint)Marshal.SizeOf<TrayNotifyIconIdentifier>(),
            hWnd = _window,
            uID = _id
        };
        bool shellSucceeded = Shell_NotifyIconGetRect(ref id, out anchor) == 0;
        if (!HasUsableHoverAnchor(shellSucceeded,
                anchor.left, anchor.top, anchor.right, anchor.bottom))
        {
            // Explorer sometimes has no rect while the icon is in overflow
            // or its taskbar is being recreated. If the cursor ALSO cannot
            // be located, keep the popup hidden rather than showing it at
            // phantom virtual-desktop coordinates (0,0).
            if (!GetCursorPos(out Point mouse) ||
                !CanMakeHoverCursorAnchor(mouse.X, mouse.Y)) return;
            anchor = new TrayRect { left = mouse.X, right = mouse.X + 16,
                top = mouse.Y, bottom = mouse.Y + 16 };
        }
        var center = HoverAnchorCenter(
            anchor.left, anchor.top, anchor.right, anchor.bottom);
        Point point = new() { X = center.X, Y = center.Y };
        IntPtr monitor = MonitorFromPoint(point, MonitorDefaultNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new TrayMonitorInfo
        {
            cbSize = (uint)Marshal.SizeOf<TrayMonitorInfo>()
        };
        if (!GetMonitorInfoW(monitor, ref info) ||
            !HasUsableHoverWorkArea(
                info.rcWork.left, info.rcWork.top,
                info.rcWork.right, info.rcWork.bottom)) return;
        TrayRect work = info.rcWork;

        int dpi = 96;
        try { dpi = (int)Math.Clamp(GetDpiForWindow(_window), 96u, 288u); }
        catch (EntryPointNotFoundException) { }
        try
        {
            if (GetDpiForMonitor(monitor, 0, out uint monitorDpi, out _) == 0)
                dpi = (int)Math.Clamp(monitorDpi, 96u, 288u);
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        (int width, int height) = CalculateHoverSize(
            dpi,
            (int)Math.Clamp((long)work.right - work.left, 1, int.MaxValue),
            (int)Math.Clamp((long)work.bottom - work.top, 1, int.MaxValue));

        (int x, int y) = CalculateHoverPosition(
            anchor.left, anchor.top, anchor.right, anchor.bottom,
            work.left, work.top, work.right, work.bottom, width, height);
        // Apply clipping BEFORE making the popup visible so there is never
        // an initial square white or transparent frame on the desktop.
        IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1,
            Math.Max(4, (int)Math.Round(18d * width / 480)),
            Math.Max(4, (int)Math.Round(18d * width / 480)));
        if (region == IntPtr.Zero)
            throw new InvalidOperationException("Unable to create rounded tray popup clipping region.");
        if (SetWindowRgn(_popupWindow, region, false) == 0)
        {
            _ = DeleteObject(region);
            throw new InvalidOperationException("Unable to apply rounded tray popup clipping region.");
        }
        // SetWindowRgn takes ownership of the region on success.
        _popupVisible = SetWindowPos(_popupWindow, new IntPtr(-1), x, y,
            width, height, SwpNoActivate | SwpShowWindow);
        if (!_popupVisible)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to position native tray popup.");
        _ = InvalidateRect(_popupWindow, IntPtr.Zero, false);
    }

    private void HideHoverPopup()
    {
        if (_popupWindow != IntPtr.Zero && _popupVisible)
            _ = ShowWindow(_popupWindow, SwHide);
        _popupVisible = false;
        // A queued refresh from the previous hover must not suppress the
        // next card opening or leave a sticky pending bit.
        // Advance epoch so stale messages from the previous hover cannot
        // consume the refresh slot owned by a newly opened hover card.
        int generation = Interlocked.Increment(ref _popupRefreshGeneration);
        if (generation == 0) _ = Interlocked.Increment(ref _popupRefreshGeneration);
        Interlocked.Exchange(ref _popupRefreshQueued, 0);
    }

    private void CreateHoverPopup()
    {
        _popupWindow = CreateWindowExW(
            WsExToolWindow | WsExNoActivate | WsExTopmost | WsExLayered,
            "STATIC", "Glass Optimizer — Tray", WsPopup,
            0, 0, 480, 300, _window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_popupWindow == IntPtr.Zero) return;
        _popupWndProc = PopupWndProc;
        _popupOriginalWndProc = SetWindowLongPtrW(_popupWindow, GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_popupWndProc));
        if (_popupOriginalWndProc == IntPtr.Zero)
        {
            _ = DestroyWindow(_popupWindow);
            _popupWindow = IntPtr.Zero;
            throw new InvalidOperationException("Unable to subclass native tray hover card.");
        }
        // Never silently show the old opaque look if the compositor refuses
        // the layered alpha. Destroy the disposable hover window, report a
        // bounded warning, and let the existing one-minute retry rebuild it.
        // Fan safety/control is entirely unaffected by a decorative failure.
        if (!ApplyVerifiedHoverAlpha(_popupWindow, TrayGlassOpacity))
        {
            int win32Error = Marshal.GetLastWin32Error();
            DisposeHoverPopup();
            throw new System.ComponentModel.Win32Exception(win32Error,
                "Native tray glass opacity could not be applied and verified.");
        }
    }

    // 0 means idle. A nonzero token identifies the particular hover epoch.
    // Old Windows messages are harmless after a hide/reopen cycle.
    private int _popupRefreshQueued;
    private int _popupRefreshGeneration = 1;

    internal static bool TryAcquireHoverRefreshSlot(ref int pending, int generation) =>
        generation != 0 && Interlocked.CompareExchange(ref pending, generation, 0) == 0;

    internal static bool TryAcknowledgeHoverRefreshSlot(ref int pending, int generation) =>
        generation != 0 && Interlocked.CompareExchange(ref pending, 0, generation) == generation;

    private void QueueHoverRefresh()
    {
        int generation = Volatile.Read(ref _popupRefreshGeneration);
        if (!TryAcquireHoverRefreshSlot(ref _popupRefreshQueued, generation))
            return;
        if (!PostMessageW(_popupWindow, WmPopupRefresh,
                new UIntPtr(unchecked((uint)generation)), IntPtr.Zero))
            _ = TryAcknowledgeHoverRefreshSlot(ref _popupRefreshQueued, generation);
    }

    private static bool ApplyVerifiedHoverAlpha(IntPtr hwnd, byte opacity)
    {
        if (!SetLayeredWindowAttributes(hwnd, 0, opacity, LwaAlpha))
            return false;
        return GetLayeredWindowAttributes(hwnd, out _, out byte actual, out uint flags) &&
            actual == opacity && (flags & LwaAlpha) != 0;
    }

    // Hidden HWND test: checks the real Windows compositor API rather than
    // just a C# constant. It creates no tray icon and changes no UI settings.
    internal static string RunHoverGlassOpacitySmoke()
    {
        const int cycles = 24;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 1);
        for (int i = 0; i < cycles; i++)
        {
            IntPtr window = CreateWindowExW(
                WsExToolWindow | WsExNoActivate | WsExLayered,
                "STATIC", "Hidden tray glass verification", WsPopup,
                0, 0, 2, 2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "Unable to create hidden layered HWND.");
            try
            {
                if (!ApplyVerifiedHoverAlpha(window, TrayGlassOpacity))
                    throw new InvalidOperationException(
                        "Tray glass alpha is not confirmed by the native compositor.");
                // Check an immediate second update: layered attributes must
                // remain set, with no window recreation or visual flashing.
                if (!ApplyVerifiedHoverAlpha(window, TrayGlassOpacity))
                    throw new InvalidOperationException(
                        "Tray glass alpha was lost on repeated native update.");
            }
            finally { _ = DestroyWindow(window); }
        }
        uint after = GetGuiResources(process.Handle, 1);
        if (after > before + 4u)
            throw new InvalidOperationException(
                $"Hidden tray glass HWND leak: USER handles {before} -> {after}.");
        return $"FAN_TRAY_GLASS_SMOKE_OK cycles={cycles} alpha={TrayGlassOpacity} " +
            $"userHandlesBefore={before} userHandlesAfter={after}";
    }

    private IntPtr PopupWndProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam) =>
        InvokeTrayWindowProcedureSafely(
            () => PopupWndProcCore(hwnd, message, wParam, lParam),
            ex => RecordHoverFailure(
                $"Fan tray hover HWND callback failed message=0x{message:X}", ex));

    private IntPtr PopupWndProcCore(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        // Per-monitor DPI notifications can target the popup itself without
        // reaching the owner tray HWND. Discard its old geometry and epoch;
        // the next shell hover computes a fresh work area and DPI before show.
        // Never use the OS-suggested rect for a hidden, no-activate card.
        if (NeedsHoverReposition(message))
        {
            HideHoverPopup();
            if (message == WmDpiChanged) return IntPtr.Zero;
        }
        if (message == WmPopupRefresh)
        {
            // Only this hover epoch may release its own pending slot. A stale
            // queued message after hide/reopen must never consume a new frame.
            int generation = unchecked((int)(uint)wParam.ToUInt64());
            if (TryAcknowledgeHoverRefreshSlot(ref _popupRefreshQueued, generation) &&
                _popupVisible)
                _ = InvalidateRect(hwnd, IntPtr.Zero, false);
            return IntPtr.Zero;
        }
        if (message == WmNcHitTest)
            return new IntPtr(HtTransparent); // never steal tray mouse input
        if (message == WmEraseBackground)
            return new IntPtr(1);
        if (message == WmPaint)
        {
            try { DrawHoverPopup(hwnd); }
            catch (Exception ex)
            {
                // A failed decoration must never unwind through a native
                // callback and take down the safety-critical FanAgent.
                RecordHoverFailure("Fan tray hover paint failed", ex);
            }
            return IntPtr.Zero;
        }
        return CallWindowProcW(_popupOriginalWndProc, hwnd, message, wParam, lParam);
    }

    private static uint Rgb(byte r, byte g, byte b) =>
        (uint)(r | (g << 8) | (b << 16));

    private static void Fill(IntPtr dc, int left, int top, int right, int bottom,
        uint color)
    {
        IntPtr brush = CreateSolidBrush(color);
        try
        {
            var rect = new TrayRect { left = left, top = top,
                right = right, bottom = bottom };
            _ = FillRect(dc, ref rect, brush);
        }
        finally { _ = DeleteObject(brush); }
    }

    private static void FillRounded(IntPtr dc, int left, int top, int right, int bottom,
        int corner, uint color)
    {
        IntPtr region = CreateRoundRectRgn(left, top, right, bottom, corner, corner);
        if (region == IntPtr.Zero)
        {
            Fill(dc, left, top, right, bottom, color);
            return;
        }
        try
        {
            IntPtr brush = CreateSolidBrush(color);
            if (brush == IntPtr.Zero) return;
            try { _ = FillRgn(dc, region, brush); }
            finally { _ = DeleteObject(brush); }
        }
        finally { _ = DeleteObject(region); }
    }

    internal static string FormatCpuTrayValue(string? raw)
    {
        string value = raw?.Trim() ?? string.Empty;
        if (value.StartsWith("CPU ", StringComparison.OrdinalIgnoreCase))
            value = value[4..].Trim();
        return string.IsNullOrWhiteSpace(value) || value == "—°C" ? "—" : value;
    }

    internal static string[]? ParseFanRpmValues(string fanText)
    {
        if (!fanText.StartsWith("Fan ", StringComparison.OrdinalIgnoreCase) ||
            !fanText.EndsWith(" RPM", StringComparison.OrdinalIgnoreCase))
            return null;
        string[] values = fanText[4..^4].Split('/',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length is < 1 or > 4) return null;
        foreach (string value in values)
        {
            if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int rpm) ||
                rpm <= 100 || rpm > 99999)
                return null;
        }
        return values;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayBitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    private static void SaveTrayDiagnosticBmp(IntPtr dc, IntPtr bitmap,
        int width, int height, string path)
    {
        int byteCount = checked(width * height * 4);
        byte[] pixels = new byte[byteCount];
        var info = new TrayBitmapInfoHeader
        {
            Size = 40, Width = width, Height = -height,
            Planes = 1, BitCount = 32, Compression = 0,
            SizeImage = (uint)byteCount
        };
        if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
            throw new InvalidOperationException("Unable to capture tray GDI diagnostic pixels.");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);
        writer.Write(checked((uint)(54 + byteCount)));
        writer.Write((uint)0);
        writer.Write((uint)54);
        writer.Write((uint)40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)0);
        writer.Write((uint)byteCount);
        writer.Write(0);
        writer.Write(0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write(pixels);
    }
}
