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
    // Opaque Windows 11 dark surface keeps desktop text out of the readings.
    // Keep bright status text in its small badge, never as full-width rails.
    private const byte TrayGlassOpacity = 255;
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
            Math.Min(availableWidth / 480d, availableHeight / 220d));
        return (Math.Max(1, (int)Math.Floor(480d * scale)),
                 Math.Max(1, (int)Math.Floor(220d * scale)));
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
}
