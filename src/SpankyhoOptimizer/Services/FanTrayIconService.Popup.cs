using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

// A lightweight native popup: no WinUI initialization or additional process
// in the long-running fan safety RuntimeHost. It shares the tray's HWND thread.
public sealed partial class FanTrayIconService
{
    private const uint WmTrayShutdown = 0x8000 + 79;
    private const uint WmPopupRefresh = 0x8000 + 80;
    private const uint WmMouseMove = 0x0200;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDpiChanged = 0x02E0;
    private const uint NinPopupOpen = 0x0406;
    private const uint NinPopupClose = 0x0407;
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmNcHitTest = 0x0084;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    // Translucent dark glass; keep glyphs readable and avoid extra UI threads.
    // 190/255 ≈ 75% opacity: desktop remains visible through dark glass.
    // Keep bright status text in its small badge, never as full-width rails.
    private const byte TrayGlassOpacity = 190;
    internal static byte HoverGlassOpacity => TrayGlassOpacity;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int DtSingleLine = 0x0020;
    private const int DtVCenter = 0x0004;
    private const int DtEndEllipsis = 0x8000;
    private const int DtNoPrefix = 0x0800;
    private const int DtFlags = DtSingleLine | DtVCenter | DtEndEllipsis | DtNoPrefix;
    private const int HtTransparent = -1;
    private const int MonitorDefaultNearest = 2;
    private const uint SrcCopy = 0x00CC0020;

    private string _popupTooltip = "CPU — • Fan — RPM • AUTO • AI —";
    private IntPtr _popupWindow;
    private WindowProc? _popupWndProc;
    private IntPtr _popupOriginalWndProc;
    private IntPtr _popupHeaderFont;
    private IntPtr _popupBodyFont;
    private int _popupFontWidth;
    private bool _popupVisible;
    private bool _popupFailureLogged;
    private DateTime _popupFailureUtc;
    private uint _taskbarCreatedMessage;

    internal static bool HoverRetryDue(TimeSpan elapsed) =>
        elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromSeconds(60);

    private void RecordHoverFailure(string message, Exception ex)
    {
        _popupFailureLogged = true;
        _popupFailureUtc = DateTime.UtcNow;
        // Error handling must itself be safe: a failing ShowWindow/GDI
        // teardown must not escape the catch path of PopupWndProc/hover open.
        // Log the ORIGINAL failure even if the cleanup and its logger fail.
        ReportHoverFailureSafely(
            HideHoverPopup,
            cleanupError => LogService.Error(
                "lianli", "Fan tray hover recovery cleanup failed", cleanupError),
            () => LogService.Error("lianli", message, ex));
    }

    internal static void ReportHoverFailureSafely(
        Action hideHover, Action<Exception> reportCleanupError,
        Action reportOriginalError)
    {
        ArgumentNullException.ThrowIfNull(reportOriginalError);
        _ = TryTrayCleanupSafely(hideHover, reportCleanupError);
        try { reportOriginalError(); }
        catch { /* Failed diagnostics must not escape a native callback. */ }
    }

    private void TryShowHoverPopup()
    {
        if (_popupFailureLogged &&
            !HoverRetryDue(DateTime.UtcNow - _popupFailureUtc)) return;
        // Retry once per minute after transient shell/GDI failures rather than
        // hiding the hover card permanently until the FanAgent is restarted.
        _popupFailureLogged = false;
        try { ShowHoverPopup(); }
        catch (Exception ex) { RecordHoverFailure("Fan tray hover popup unavailable", ex); }
    }

    internal static bool IsHoverOpenMessage(int message) =>
        message is (int)NinPopupOpen or (int)WmMouseMove;

    internal static bool IsHoverCloseMessage(int message) =>
        message == (int)NinPopupClose;

    internal static bool NeedsHoverReposition(uint message) =>
        message is WmDisplayChange or WmSettingChange or WmDpiChanged;

    private void ReRegisterAfterExplorerRestart()
    {
        // Explorer loses every tray registration on restart, even when the
        // original Shell_NotifyIconW call returned success.
        var data = CreateData(Volatile.Read(ref _popupTooltip), _state);
        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (_added)
        {
            data.uTimeoutOrVersion = NotifyIconVersion4;
            _ = Shell_NotifyIconW(NimSetVersion, ref data);
            _shellRecoveryPending = false;
            _lastNotifiedTooltip = null;
            TryPromoteSelfIcon();
        }
        else
        {
            _shellRecoveryPending = true;
            _lastShellFailureUtc = DateTime.UtcNow.AddSeconds(-15);
        }
        HideHoverPopup();
        _popupFailureLogged = false;
        _popupFailureUtc = default;
    }

    // Pure geometry: position next to vertical taskbars, above/below horizontal
    // taskbars, and clamp entirely to the active monitor's work area.
    internal static (int X, int Y) CalculateHoverPosition(
        int anchorLeft, int anchorTop, int anchorRight, int anchorBottom,
        int workLeft, int workTop, int workRight, int workBottom,
        int width, int height)
    {
        const int gap = 10, margin = 5;
        // The native virtual desktop may include monitors at negative
        // coordinates. Subtracting two signed Win32 coordinates and then
        // adding popup dimensions as int can wrap BEFORE the work-area clamp.
        // Use the same overflow-safe icon centre as MonitorFromPoint.
        var centre = HoverAnchorCenter(
            anchorLeft, anchorTop, anchorRight, anchorBottom);
        long x = (long)centre.X - width / 2;
        long y = (long)anchorTop - height - gap;
        if (anchorRight <= workLeft) // Left taskbar
        {
            x = (long)anchorRight + gap;
            y = (long)centre.Y - height / 2;
        }
        else if (anchorLeft >= workRight) // Right taskbar
        {
            x = (long)anchorLeft - width - gap;
            y = (long)centre.Y - height / 2;
        }
        else if (anchorBottom <= workTop) // Top taskbar
            y = (long)anchorBottom + gap;
        else if (y < (long)workTop + margin) // Overflow flyout near the top
            y = (long)anchorBottom + gap;

        // Do the clamp in widened arithmetic too. The final HWND position
        // remains representable even with invalid/extreme monitor metadata.
        long minX = Math.Clamp((long)workLeft + margin, int.MinValue, int.MaxValue);
        long minY = Math.Clamp((long)workTop + margin, int.MinValue, int.MaxValue);
        long maxX = Math.Clamp(
            Math.Max(minX, (long)workRight - width - margin), int.MinValue, int.MaxValue);
        long maxY = Math.Clamp(
            Math.Max(minY, (long)workBottom - height - margin), int.MinValue, int.MaxValue);
        return ((int)Math.Clamp(x, minX, maxX),
                (int)Math.Clamp(y, minY, maxY));
    }

    // Fit the existing proportional GDI card inside the actual monitor work
    // area. On a compact screen with 200–300% DPI, simply clamping x/y can
    // leave the right/bottom half clipped. The card renderer already scales
    // its fonts and cells from physical width; never introduce scrollbars.
    internal static (int Width, int Height) CalculateHoverSize(
        int dpi, int workWidth, int workHeight)
    {
        const int margin = 5;
        double dpiScale = Math.Clamp(dpi, 96, 288) / 96d;
        double availableWidth = Math.Max(1, workWidth - 2 * margin);
        double availableHeight = Math.Max(1, workHeight - 2 * margin);
        double scale = Math.Min(dpiScale,
            Math.Min(availableWidth / 480d, availableHeight / 300d));
        return (Math.Max(1, (int)Math.Floor(480d * scale)),
                Math.Max(1, (int)Math.Floor(300d * scale)));
    }

    internal static string RunHoverPositionSmoke()
    {
        // Side-taskbars should place the card next to the icon, not hundreds
        // of pixels above it as the original bottom-only logic did.
        var left = CalculateHoverPosition(8, 530, 32, 554,
            48, 0, 1920, 1080, 480, 300);
        var right = CalculateHoverPosition(1880, 530, 1904, 554,
            0, 0, 1872, 1080, 480, 300);
        var top = CalculateHoverPosition(1500, 8, 1524, 36,
            0, 48, 1920, 1080, 480, 300);
        var bottom = CalculateHoverPosition(1820, 1040, 1844, 1064,
            0, 0, 1920, 1040, 480, 300);
        var secondary = CalculateHoverPosition(-60, 1360, -36, 1390,
            -2560, 0, 0, 1350, 684, 468);
        var overflow = CalculateHoverPosition(400, 60, 416, 80,
            0, 0, 1920, 1040, 480, 300);
        if (left != (53, 392) || right != (1387, 392) ||
            top != (1272, 53) || bottom != (1435, 730) ||
            secondary != (-689, 877) || overflow != (168, 90))
            throw new InvalidOperationException(
                $"Tray placement: left={left} right={right} top={top} " +
                $"bottom={bottom} secondary={secondary} overflow={overflow}.");
        if (!HasUsableHoverAnchor(true, 100, 200, 116, 216) ||
            HasUsableHoverAnchor(false, 100, 200, 116, 216) ||
            HasUsableHoverAnchor(true, 100, 200, 100, 216) ||
            HasUsableHoverAnchor(true, 100, 200, 116, 200) ||
            HoverAnchorCenter(1900, 100, 1950, 150) != (1925, 125) ||
            HoverAnchorCenter(-80, 1080, -40, 1120) != (-60, 1100) ||
            HoverAnchorCenter(int.MinValue, -5, int.MaxValue, 7) != (-1, 1))
            throw new InvalidOperationException(
                "Tray anchor validity/monitor centre regression.");
        if (CalculateHoverPosition(
                int.MinValue, 500, int.MaxValue, 516,
                  -1000, 0, 1000, 900, 480, 300) != (-241, 190) ||
            CalculateHoverPosition(
                100, int.MinValue, 116, int.MaxValue,
                 0, 0, 1920, 1080, 480, 300) != (5, 775) ||
            !CanMakeHoverCursorAnchor(int.MinValue, int.MinValue) ||
            !CanMakeHoverCursorAnchor(int.MaxValue - 16, 0) ||
            CanMakeHoverCursorAnchor(int.MaxValue - 15, 0) ||
            CanMakeHoverCursorAnchor(0, int.MaxValue) ||
            !HasUsableHoverWorkArea(-2560, 0, 0, 1440) ||
            !HasUsableHoverWorkArea(0, 0, 1920, 1040) ||
            HasUsableHoverWorkArea(0, 0, 11, 100) ||
            HasUsableHoverWorkArea(500, 0, 400, 300))
            throw new InvalidOperationException(
                "Tray virtual-desktop signed coordinate/work-area regression.");
        if (ResolveContextMenuAnchor(
                true, -2400, 850, false, 0, 0, 0, 0) != (-2400, 850) ||
            ResolveContextMenuAnchor(
                true, 500, 600, true, 10, 20, 30, 40) != (500, 600) ||
            ResolveContextMenuAnchor(
                false, 0, 0, true, -80, 1080, -40, 1120) != (-60, 1100) ||
            ResolveContextMenuAnchor(
                false, 0, 0, true,
                int.MinValue, -5, int.MaxValue, 7) != (-1, 1) ||
            ResolveContextMenuAnchor(
                false, 0, 0, true, 0, 0, 0, 0) is not null ||
            ResolveContextMenuAnchor(
                false, 0, 0, false, 100, 200, 116, 216) is not null)
            throw new InvalidOperationException(
                "Tray context menu cursor/shell anchor fallback regression.");
        foreach ((int workWidth, int workHeight, int dpi) in new[]
        {
            (1920, 1080, 96), (1920, 1080, 288),
            (800, 600, 288), (640, 480, 192), (320, 200, 144)
        })
        {
            var size = CalculateHoverSize(dpi, workWidth, workHeight);
            if (size.Width <= 0 || size.Height <= 0 ||
                size.Width > workWidth - 10 || size.Height > workHeight - 10)
                throw new InvalidOperationException(
                    $"Tray viewport clipped at {workWidth}x{workHeight}, " +
                    $"dpi={dpi}, size={size}.");
            var placement = CalculateHoverPosition(workWidth - 32,
                workHeight - 34, workWidth - 8, workHeight - 10,
                0, 0, workWidth, workHeight, size.Width, size.Height);
            if (placement.X < 5 || placement.Y < 5 ||
                placement.X + size.Width > workWidth - 5 ||
                placement.Y + size.Height > workHeight - 5)
                throw new InvalidOperationException(
                    $"Tray popup outside monitor work area at dpi={dpi}: " +
                    $"size={size} position={placement}.");
        }
        // The compiled --tray-glass-smoke also exercises the context-menu
        // post-track wake sequence without opening any native menu.
        string menuOrder = "";
        uint dismissed = TrackContextMenuWithWake(
            () => menuOrder += "F",
            () => { menuOrder += "T"; return 0u; },
            message =>
            {
                if (message != WmNull)
                    throw new InvalidOperationException("Tray menu wake must be WM_NULL.");
                menuOrder += "N";
            });
        if (dismissed != 0 || menuOrder != "FTN")
            throw new InvalidOperationException("Tray context-menu dismissal wake regression.");
        int menuActive = 0;
        if (!TryAcquireContextMenu(ref menuActive) ||
            TryAcquireContextMenu(ref menuActive) ||
            menuActive != 1)
            throw new InvalidOperationException("Nested tray context menu guard failed.");
        ReleaseContextMenu(ref menuActive);
        if (menuActive != 0 || !TryAcquireContextMenu(ref menuActive))
            throw new InvalidOperationException("Tray context menu guard remained held.");
        ReleaseContextMenu(ref menuActive);
        if (!SuppressTrayInputWhileMenuActive(0x0202, 1) ||
            !SuppressTrayInputWhileMenuActive(0x0203, 1) ||
            !SuppressTrayInputWhileMenuActive((int)WmMouseMove, 1) ||
            !SuppressTrayInputWhileMenuActive((int)NinPopupOpen, 1) ||
            SuppressTrayInputWhileMenuActive((int)NinPopupClose, 1) ||
            SuppressTrayInputWhileMenuActive(0x007B, 1) ||
            SuppressTrayInputWhileMenuActive((int)WmMouseMove, 0))
            throw new InvalidOperationException(
                "Tray native modal menu must suppress left-click/hover input only.");
        int safeMenuActive = 0, safeMenuErrors = 0;
        if (!TryHandleContextMenuSafely(ref safeMenuActive,
                () => { },
                () => throw new InvalidOperationException("synthetic menu failure"),
                _ => { safeMenuErrors++; throw new IOException("synthetic log failure"); }) ||
            safeMenuActive != 0 || safeMenuErrors != 1 ||
            !TryAcquireContextMenu(ref safeMenuActive))
            throw new InvalidOperationException(
                "Tray unmanaged context menu error escaped or leaked guard.");
        ReleaseContextMenu(ref safeMenuActive);
        // Compiled EXE also checks fault-contained tray shutdown and cleanup;
        // these are fake delegates, never real native HWND destruction.
        int simulatedQuit = 0, simulatedCleanupReports = 0;
        RunTrayShutdownSafely(
            () => throw new InvalidOperationException("synthetic hide failure"),
            () => simulatedQuit++,
            _ => { simulatedCleanupReports++; throw new IOException("synthetic logging failure"); });
        if (simulatedQuit != 1 || simulatedCleanupReports != 1 ||
            TryTrayCleanupSafely(
                () => throw new InvalidOperationException("synthetic GDI teardown"),
                _ => { throw new IOException("synthetic cleanup log failure"); }))
            throw new InvalidOperationException(
                "Fan tray shutdown did not contain failure and request exit.");
        if (!ShouldDispatchTrayMessage(1, 0) ||
            ShouldDispatchTrayMessage(0, 5))
            throw new InvalidOperationException(
                "Tray GetMessageW orderly termination classification regressed.");
        bool nativeMessageFailureObserved = false;
        try { _ = ShouldDispatchTrayMessage(-1, 1400); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1400)
        {
            nativeMessageFailureObserved = true;
        }
        if (!nativeMessageFailureObserved)
            throw new InvalidOperationException(
                "Tray GetMessageW error was silently accepted as WM_QUIT.");
        int callbackReports = 0, recoveryReports = 0, originalReports = 0;
        IntPtr callbackResult = InvokeTrayWindowProcedureSafely(
            () => throw new InvalidOperationException("synthetic native callback"),
            _ => ReportHoverFailureSafely(
                () => throw new IOException("synthetic popup hide failure"),
                __ => recoveryReports++,
                () => originalReports++));
        if (callbackResult != IntPtr.Zero || recoveryReports != 1 ||
            originalReports != 1 ||
            InvokeTrayWindowProcedureSafely(
                () => new IntPtr(-1), _ => callbackReports++) != new IntPtr(-1) ||
            callbackReports != 0)
            throw new InvalidOperationException(
                "Fan tray unmanaged callback/hover error recovery regression.");
        // Pure HICON cleanup test: the first owned release fails, an OS
        // stock handle is skipped, but a later owned HICON still releases.
        // Do not call DestroyIcon or any actual Windows release API here.
        int iconReleaseCalls = 0, iconReleaseReports = 0;
        int iconsFreed = ReleaseOwnedTrayIconsSafely(
            (new IntPtr(101), true),
            (new IntPtr(202), false),
            (new IntPtr(303), true),
            h =>
            {
                iconReleaseCalls++;
                return h != new IntPtr(101);
            },
            (_, _) =>
            {
                iconReleaseReports++;
                throw new IOException("synthetic HICON cleanup log failure");
            });
        if (iconsFreed != 1 || iconReleaseCalls != 2 || iconReleaseReports != 1)
            throw new InvalidOperationException(
                "Fan tray independently owned HICON cleanup regression.");
        // A real fan tray may reach DisposeCore from both the owner
        // message-loop finally and a direct Dispose call. Model both here:
        // exactly one may release the owned native HWND/HICON resources.
        int cleanupState = 0, nativeCleanupCount = 0;
        if (!TryBeginTrayCleanup(ref cleanupState))
            throw new InvalidOperationException("Tray dispose-once initial gate failed.");
        nativeCleanupCount++;
        if (TryBeginTrayCleanup(ref cleanupState) ||
            TryBeginTrayCleanup(ref cleanupState) ||
            nativeCleanupCount != 1 || cleanupState != 1)
            throw new InvalidOperationException(
                "Tray native DisposeCore cleanup can execute twice.");
        if (!ShouldQueueFanOpenClick(0x0202, 1000, long.MinValue, 500) ||
            ShouldQueueFanOpenClick(0x0203, 1100, 1000, 500) ||
            ShouldQueueFanOpenClick(0x0202, 1150, 1000, 500) ||
            !ShouldQueueFanOpenClick(0x0202, 1500, 1000, 500) ||
            ShouldQueueFanOpenClick(0x0204, 1500, long.MinValue, 500))
            throw new InvalidOperationException(
                "Tray single/double click OpenFans action deduplication regressed.");
        return "FAN_TRAY_POSITION_SMOKE_OK sides=4 secondary=1 overflow=1 compactDpi=5";
    }

    // Explorer may return an empty/stale NOTIFYICONIDENTIFIER rectangle
    // during taskbar recreation, overflow transitions or DPI changes.
    // Never select a monitor from an invalid rectangle.
    internal static bool HasUsableHoverAnchor(
        bool shellSucceeded, int left, int top, int right, int bottom) =>
        shellSucceeded && right > left && bottom > top;

    // Select the display containing the icon CENTRE. Its upper-left pixel
    // may be on an adjacent monitor when a taskbar spans a display boundary.
    // Widen before subtraction to avoid signed overflow on virtual desktops.
    internal static (int X, int Y) HoverAnchorCenter(
        int left, int top, int right, int bottom) =>
        (left + (int)(((long)right - left) / 2),
         top + (int)(((long)bottom - top) / 2));

    // The cursor fallback synthesizes a 16px anchor; never let Win32's
    // signed POINT overflow when the cursor reaches an extreme desktop edge.
    internal static bool CanMakeHoverCursorAnchor(int x, int y) =>
        x <= int.MaxValue - 16 && y <= int.MaxValue - 16;

    // Never position a secondary-monitor card using the primary monitor's
    // work area when native monitor metadata is temporarily unavailable.
    internal static bool HasUsableHoverWorkArea(
        int left, int top, int right, int bottom) =>
        (long)right - left >= 12 && (long)bottom - top >= 12;

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
                int width = 480 * scale, height = 300 * scale;
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
                        1 => "CPU 91°C • Fan 1620/1740/1860/1980 RPM • AUTO65 • AI 77",
                        _ => "CPU —°C • RPM — • Balanced"
                    };
                    PaintHoverCard(sourceDc, width, height, tooltip,
                        (FanTrayState)statusIndex, headers[dpiIndex], bodies[dpiIndex]);
                      uint secondaryPanel = Rgb(27, 44, 64);
                      uint outerCard = Rgb(18, 28, 43);
                     if (!BitBlt(destinationDc, 0, 0, width, height, sourceDc, 0, 0, SrcCopy))
                         throw new InvalidOperationException("Tray rendered card GDI bitmap copy failed.");
                     uint pBackgroundTop = GetPixel(destinationDc, 5 * scale, 5 * scale);
                     uint pBackgroundLeft = GetPixel(destinationDc, 2 * scale, 140 * scale);
                      uint pFan = GetPixel(destinationDc, 440 * scale, 180 * scale);
                     uint pOuter = GetPixel(destinationDc, 290 * scale, 180 * scale);
                     uint pBackgroundBottom = GetPixel(destinationDc, 400 * scale, 290 * scale);
                     uint pCpu = GetPixel(destinationDc, 32 * scale, 58 * scale);
                     uint pBackgroundBottomLeft = GetPixel(destinationDc, 32 * scale, 290 * scale);
                     if (pBackgroundTop != Rgb(10, 15, 24) ||
                        pBackgroundLeft != Rgb(10, 15, 24) ||
                          pFan != secondaryPanel || pOuter != outerCard ||
                         pBackgroundBottom != Rgb(10, 15, 24) ||
                         pCpu != Rgb(14, 24, 37) ||
                          pBackgroundBottomLeft != Rgb(10, 15, 24))
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
            // Status accent belongs ONLY to the compact badge; no full-width
            // blue, amber or red decorative bands anywhere in the tray card.
             uint accent = HoverStatusAccent(state);
             // CPU Focus keeps the compact concept composition: no product
             // header, dominant CPU value on the left, and fan card on right.
             Fill(dc, 0, 0, width, height, Rgb(10, 15, 24));
              FillRounded(dc, Px(16), Px(24), width - Px(16), Px(284), Px(12), Rgb(18, 28, 43));
              FillRounded(dc, Px(30), Px(45), Px(280), Px(190), Px(8), Rgb(14, 24, 37));
            _ = SetBkMode(dc, 1);

            void Text(string value, int x, int top, int right, int size, uint color,
                bool bold = false, bool centered = false)
            {
                IntPtr font = bold ? headerFont : bodyFont;
                IntPtr old = font != IntPtr.Zero ? SelectObject(dc, font) : IntPtr.Zero;
                _ = SetTextColor(dc, color);
                var rect = new TrayRect { left = Px(x), top = Px(top),
                    right = Px(right), bottom = Px(top + size) };
                _ = DrawTextW(dc, value, -1, ref rect, DtFlags | (centered ? 1 : 0));
                if (old != IntPtr.Zero) _ = SelectObject(dc, old);
            }
             string[] parts = tooltip
                .Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
             // CPU Focus layout: temperature is the dominant value, while
             // fan data stays in a compact secondary card on the right.
              Text("TEPLOTA PROCESORU", 42, 58, 270, 20, Rgb(205, 218, 232), true);
             Text(FormatCpuTrayValue(parts.Length > 0 ? parts[0] : null),
                 42, 86, 270, 64, Rgb(232, 239, 239), true);
             string fanText = parts.Length > 1 ? parts[1] : "RPM —";
             string[]? fanSpeeds = ParseFanRpmValues(fanText);
             if (fanSpeeds is { Length: >= 2 })
             {
                   FillRounded(dc, Px(300), Px(45), Px(450), Px(190), Px(8), Rgb(27, 44, 64));
                    Text("FAN", 318, 58, 438, 17, Rgb(205, 218, 232), true);
                    Text(fanSpeeds[0], 318, 91, 438, 30, Rgb(245, 248, 250), true);
                    Text(fanSpeeds.Length > 1 ? $"{fanSpeeds[1]} RPM" : "— RPM",
                        318, 137, 438, 22, Rgb(224, 236, 247));
              }
              else
              {
                   FillRounded(dc, Px(300), Px(45), Px(450), Px(235), Px(8), Rgb(27, 44, 64));
                    Text("FAN", 318, 58, 438, 17, Rgb(205, 218, 232), true);
                    Text("NEDOSTUPNÉ", 318, 91, 438, 20, Rgb(224, 236, 247), true);
                    Text("HW nepodporován", 318, 137, 438, 16, Rgb(194, 213, 231));
              }
            string mode = parts.Length > 2 ? parts[2] : "AUTO";
            if (parts.Length > 3)
                mode += "  ·  " + string.Join("  ·  ", parts, 3, parts.Length - 3);
              FillRounded(dc, Px(30), Px(250), Px(450), Px(278), Px(8), Rgb(21, 33, 49));
              Text($"• {mode}", 44, 256, 438, 22, Rgb(208, 231, 249), true);
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
