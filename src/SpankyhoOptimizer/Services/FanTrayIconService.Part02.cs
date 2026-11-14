using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class FanTrayIconService : IDisposable
{
    internal static string RunNotificationDedupSmoke()
    {
        if (SelectTrayIconSize(96) != 16 || SelectTrayIconSize(144) != 24 ||
            SelectTrayIconSize(192) != 32 || SelectTrayIconSize(288) != 48)
            throw new InvalidOperationException("Tray DPI scaling regression.");
        if (ShellRecoveryDue(TimeSpan.Zero) ||
            ShellRecoveryDue(TimeSpan.FromSeconds(1)) ||
            ShellRecoveryDue(TimeSpan.FromSeconds(14)) ||
            !ShellRecoveryDue(TimeSpan.FromSeconds(15)) ||
            !ShellRecoveryDue(TimeSpan.FromSeconds(60)) ||
            !ShellRecoveryDue(TimeSpan.FromSeconds(-1)))
            throw new InvalidOperationException(
                "Fan tray Shell_NotifyIcon recovery retry interval regressed.");
        if (FormatCpuTrayValue("CPU 51°C") != "51°C" ||
            FormatCpuTrayValue(" CPU —°C ") != "—" ||
            FormatCpuTrayValue(null) != "—")
            throw new InvalidOperationException("Tray CPU caption duplicated or missing-value fallback regressed.");
        string[]? fourFanSpeeds = ParseFanRpmValues("Fan 1200/1300/1400/1500 RPM");
        if (fourFanSpeeds == null || fourFanSpeeds.Length != 4 ||
            fourFanSpeeds[0] != "1200" || fourFanSpeeds[3] != "1500" ||
            ParseFanRpmValues("RPM —") != null ||
            ParseFanRpmValues("Fan 1200/no/1400 RPM") != null ||
            ParseFanRpmValues("Fan 1200/1300/1400/1500/1600 RPM") != null)
            throw new InvalidOperationException("Fan tray four-channel rendering parser regressed.");
        string longTrayText = "CPU 45°C • Fan 1000/1100/1200/1300 RPM • AUTO • " + new string('A', 128);
        (string clippedShell, string intactPopup) = PrepareTrayDisplayText(longTrayText);
        if (ClipShellTooltip(longTrayText).Length != 127 ||
            ClipShellTooltip("Short tooltip") != "Short tooltip")
            throw new InvalidOperationException("Explorer recovery shell tooltip clipping regressed.");
        string boundaryEmoji = new string('A', 126) + "🔧" + "B";
        (string shellEmoji, string popupEmoji) = PrepareTrayDisplayText(boundaryEmoji);
        if (shellEmoji.Length != 126 || shellEmoji != new string('A', 126) ||
            popupEmoji != boundaryEmoji ||
            ClipShellTooltip(new string('A', 125) + "🔧" + "B").Length != 127)
            throw new InvalidOperationException("Shell tooltip split UTF-16 surrogate pair.");
        if (clippedShell.Length != 127 || intactPopup != longTrayText ||
            PrepareTrayDisplayText("  CPU 42°C  ").popupText != "CPU 42°C" ||
            PrepareTrayDisplayText(" ").popupText != "Spankyho Fan Agent")
            throw new InvalidOperationException("Fan tray shell text truncated the full hover card telemetry.");
        if (HoverRetryDue(TimeSpan.Zero) ||
            HoverRetryDue(TimeSpan.FromSeconds(59)) ||
            !HoverRetryDue(TimeSpan.FromSeconds(60)) ||
            !HoverRetryDue(TimeSpan.FromSeconds(-1)))
            throw new InvalidOperationException("Fan tray hover failure recovery backoff regressed.");
        _ = RunHoverPositionSmoke();
        if (!NeedsHoverReposition(WmDisplayChange) ||
            !NeedsHoverReposition(WmSettingChange) ||
            !NeedsHoverReposition(WmDpiChanged) ||
            NeedsHoverReposition(WmMouseMove))
            throw new InvalidOperationException("Tray popup display-change positioning regression.");
        if (!IsHoverOpenMessage((int)NinPopupOpen) ||
            !IsHoverOpenMessage((int)WmMouseMove) ||
            IsHoverOpenMessage((int)NinPopupClose) ||
            !IsHoverCloseMessage((int)NinPopupClose) ||
            IsHoverCloseMessage((int)NinPopupOpen))
            throw new InvalidOperationException("Fan tray hover card event routing regressed.");
        if (!ShouldNotify("1620 RPM", FanTrayState.Ok, null, FanTrayState.Ok, TimeSpan.Zero) ||
            ShouldNotify("1620 RPM", FanTrayState.Ok, "1620 RPM", FanTrayState.Ok, TimeSpan.FromSeconds(1)) ||
            ShouldNotify("1700 RPM", FanTrayState.Ok, "1620 RPM", FanTrayState.Ok, TimeSpan.FromSeconds(1)) ||
            !ShouldNotify("1700 RPM", FanTrayState.Ok, "1620 RPM", FanTrayState.Ok, TimeSpan.FromSeconds(5)) ||
            !ShouldNotify("1620 RPM", FanTrayState.Error, "1620 RPM", FanTrayState.Ok, TimeSpan.FromSeconds(1)) ||
            !ShouldNotify("1620 RPM", FanTrayState.Warning, "1620 RPM", FanTrayState.Ok, TimeSpan.FromSeconds(1)) ||
            !ShouldNotify("1620 RPM", FanTrayState.Ok, "1620 RPM", FanTrayState.Ok, TimeSpan.FromSeconds(46)) ||
            !ShouldNotify("1700 RPM", FanTrayState.Ok, "1620 RPM", FanTrayState.Ok, TimeSpan.FromSeconds(-1)))
            throw new InvalidOperationException("Tray dedup policy regression.");
        // A real changing tooltip every second previously caused one Explorer
        // modification per tick. Count pure policy decisions, never send IPC.
        const int seconds = 120;
        int oldSends = seconds;
        int newSends = 0;
        string? last = null;
        DateTime lastUtc = DateTime.MinValue;
        DateTime start = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < seconds; i++)
        {
            string current = $"{1600 + i} RPM";
            DateTime time = start.AddSeconds(i);
            if (!ShouldNotify(current, FanTrayState.Ok, last, FanTrayState.Ok, time - lastUtc))
                continue;
            last = current;
            lastUtc = time;
            newSends++;
        }
        if (newSends != 24 ||
            !NeedsTooltipCompose(false, FanTrayState.Ok, FanTrayState.Ok, TimeSpan.Zero) ||
            NeedsTooltipCompose(true, FanTrayState.Ok, FanTrayState.Ok, TimeSpan.FromSeconds(1)) ||
            !NeedsTooltipCompose(true, FanTrayState.Ok, FanTrayState.Ok, TimeSpan.FromSeconds(5)) ||
            !NeedsTooltipCompose(true, FanTrayState.Warning, FanTrayState.Ok, TimeSpan.FromMilliseconds(10)) ||
            !NeedsTooltipCompose(true, FanTrayState.Error, FanTrayState.Warning, TimeSpan.FromMilliseconds(10)) ||
            !NeedsTooltipCompose(true, FanTrayState.Ok, FanTrayState.Error, TimeSpan.FromMilliseconds(10)) ||
            !NeedsTooltipCompose(true, FanTrayState.Ok, FanTrayState.Ok, TimeSpan.FromSeconds(-1)))
            throw new InvalidOperationException($"Tray tooltip IPC/composition budget regressed: {newSends}.");
        int composed = 0;
        lastUtc = DateTime.MinValue;
        bool hasLast = false;
        for (int i = 0; i < seconds; i++)
        {
            DateTime t = start.AddSeconds(i);
            if (!NeedsTooltipCompose(hasLast, FanTrayState.Ok, FanTrayState.Ok, t - lastUtc))
                continue;
            lastUtc = t;
            hasLast = true;
            composed++;
        }
        if (composed != 24)
            throw new InvalidOperationException($"Tray string composition budget regressed: {composed}.");
        int pendingRefresh = 0, scheduled = 0;
        for (int i = 0; i < 10000; i++)
            if (TryAcquireHoverRefreshSlot(ref pendingRefresh, 9))
                scheduled++;
        if (scheduled != 1)
            throw new InvalidOperationException("Tray redraw message coalescing regressed.");
        // Hidden/reopened popup: stale generation 9 MUST NOT acknowledge the
        // replacement frame in generation 10.
        if (!TryAcknowledgeHoverRefreshSlot(ref pendingRefresh, 9) ||
            !TryAcquireHoverRefreshSlot(ref pendingRefresh, 10) ||
            TryAcknowledgeHoverRefreshSlot(ref pendingRefresh, 9) ||
            pendingRefresh != 10 ||
            !TryAcknowledgeHoverRefreshSlot(ref pendingRefresh, 10) ||
            !TryAcquireHoverRefreshSlot(ref pendingRefresh, 10))
            throw new InvalidOperationException("Tray redraw epoch guard regressed.");
        return $"FAN_TRAY_DEDUP_SMOKE_OK same=skip changed=5s warning=immediate error=immediate refresh=46s " +
            $"simulatedSeconds={seconds} baselineShellCalls={oldSends} throttledShellCalls={newSends} " +
            $"baselineFormats={seconds} throttledFormats={composed}";
    }

    public void PumpMessages()
    {
        if (_ownerMessageThread != null &&
            Environment.CurrentManagedThreadId != _ownerManagedThreadId) return;
        while (PeekMessageW(out Msg msg, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    // Never let a managed error escape the delegate invoked by user32.dll.
    // Apart from context-menu/profile failures, hover-close, DPI updates,
    // Explorer registration and left-click callbacks can all fault too.
    internal static IntPtr InvokeTrayWindowProcedureSafely(
        Func<IntPtr> callback, Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(reportError);
        try { return callback(); }
        catch (Exception ex)
        {
            try { reportError(ex); }
            catch { /* Never throw a secondary diagnostics error into user32. */ }
            return IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam) =>
        InvokeTrayWindowProcedureSafely(
            () => WndProcCore(hwnd, message, wParam, lParam),
            ex => LogService.Error(
                "lianli", $"Fan tray HWND callback failed message=0x{message:X}", ex));

    private IntPtr WndProcCore(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WmTrayShutdown)
        {
            // Even if the hover GDI/HWND teardown fails, the owner message
            // thread MUST receive WM_QUIT. Do not strand Dispose's join or
            // unwind through an unmanaged window-procedure callback.
            RunTrayShutdownSafely(
                HideHoverPopup,
                () => PostQuitMessage(0),
                ex => LogService.Error("lianli", "Fan tray hover shutdown failed", ex));
            return IntPtr.Zero;
        }
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            ReRegisterAfterExplorerRestart();
            return IntPtr.Zero;
        }
        if (NeedsHoverReposition(message))
        {
            // The popup is re-anchored with the new monitor work area and DPI
            // on the next mouse event. Do not touch the Explorer registration.
            HideHoverPopup();
        }
        if (message == WmAppTray)
        {
            int raw = unchecked((int)lParam.ToInt64());
            int code = raw & 0xFFFF;

            // TrackPopupMenuEx pumps a nested message loop. Hover-open or
            // left-click shell callbacks delivered while our context menu
            // is active must not put a glass hover card over that menu or
            // enqueue an unsolicited detached OpenFans launch. Keep close,
            // display/DPI and taskbar recovery notifications intact.
            if (SuppressTrayInputWhileMenuActive(
                    code, Volatile.Read(ref _contextMenuActive)))
                return IntPtr.Zero;

            if (code == WmContextMenu || code == WmRButtonUp)
            {
                // The menu loads a persisted fan profile and invokes Win32
                // UI APIs. A corrupt profile/native failure must never
                // unwind across our unmanaged WndProc callback and bring
                // down the safety-critical FanAgent. Nested clicks remain
                // suppressed, and diagnostics are strictly best-effort.
                _ = TryHandleContextMenuSafely(
                    ref _contextMenuActive,
                    HideHoverPopup,
                    ShowContextMenu,
                    ex => LogService.Error(
                        "lianli", "Fan tray context menu unavailable", ex));
                return IntPtr.Zero;
            }

            if (code == WmLButtonUp || code == WmLButtonDblClk)
            {
                HideHoverPopup();
                long nowTick = Environment.TickCount64;
                uint doubleClickMs = GetDoubleClickTime();
                if (ShouldQueueFanOpenClick(
                        code, nowTick, _lastFanOpenClickTick, doubleClickMs))
                {
                    _lastFanOpenClickTick = nowTick;
                    _actions.Enqueue(FanTrayAction.OpenFans);
                }
                return IntPtr.Zero;
            }
            if (code == NinPopupOpen || code == WmMouseMove)
            {
                TryShowHoverPopup();
                return IntPtr.Zero;
            }
            if (code == NinPopupClose)
            {
                HideHoverPopup();
                return IntPtr.Zero;
            }
        }

        return CallWindowProcW(_originalWndProc, hwnd, message, wParam, lParam);
    }

    // Suppress only input that competes with an already-open native menu.
    // Shell popup-close notifications must still hide a visible hover card,
    // and explicit context-menu notifications remain governed by the
    // reentrancy gate. This is pure so no real tray click is needed in tests.
    internal static bool SuppressTrayInputWhileMenuActive(int code, int menuActive) =>
        menuActive != 0 &&
        (code is WmLButtonUp or WmLButtonDblClk ||
         IsHoverOpenMessage(code));

    // Windows often reports LEFT UP, LEFT DOUBLECLICK, LEFT UP for one
    // double-click. The fan action consumer launches the main app for EACH
    // enqueued OpenFans action, so collapse the entire OS-defined gesture.
    // Environment.TickCount64 is monotonic across local clock adjustments.
    // A clock reset/long overflow fails open rather than locking out clicks.
    internal static bool ShouldQueueFanOpenClick(
        int code, long nowTick, long lastAcceptedTick, uint doubleClickMs)
    {
        if (code is not (WmLButtonUp or WmLButtonDblClk)) return false;
        long interval = doubleClickMs is >= 1 and <= 5000
            ? doubleClickMs : 500;
        return lastAcceptedTick == long.MinValue ||
            nowTick < lastAcceptedTick ||
            nowTick - lastAcceptedTick >= interval;
    }

    // A callback from an unmanaged Win32 window procedure must contain
    // exceptions from fan-profile loading, menu construction, tracking and
    // diagnostics, without changing the fan control/safety loop. The guard
    // is held through error reporting, and ALWAYS released even if reporting
    // itself fails. False means a nested native menu was already active.
    internal static bool TryHandleContextMenuSafely(
        ref int active, Action hideHover, Action showMenu,
        Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(hideHover);
        ArgumentNullException.ThrowIfNull(showMenu);
        ArgumentNullException.ThrowIfNull(reportError);
        if (!TryAcquireContextMenu(ref active))
            return false;
        try
        {
            try
            {
                hideHover();
                showMenu();
            }
            catch (Exception ex)
            {
                try { reportError(ex); }
                catch { /* Diagnostics must not escape the native WndProc. */ }
            }
            return true;
        }
        finally { ReleaseContextMenu(ref active); }
    }

    // The native context menu pumps its own message loop and can synchronously
    // re-enter WndProc through additional shell click notifications. Acquire
    // before popup teardown/menu creation; release in the caller's finally
    // even when the anchor cannot be obtained or TrackPopupMenuEx throws.
    internal static bool TryAcquireContextMenu(ref int active) =>
        Interlocked.CompareExchange(ref active, 1, 0) == 0;

    internal static void ReleaseContextMenu(ref int active) =>
        Interlocked.Exchange(ref active, 0);

    // Shell cursor positioning occasionally fails during Explorer recovery.
    // Prefer the actual click position; only fall back to a verified icon
    // rectangle, never the uninitialized Win32 POINT (0,0).
    internal static (int X, int Y)? ResolveContextMenuAnchor(
        bool cursorSucceeded, int cursorX, int cursorY,
        bool shellSucceeded, int left, int top, int right, int bottom)
    {
        if (cursorSucceeded) return (cursorX, cursorY);
        if (!HasUsableHoverAnchor(
                shellSucceeded, left, top, right, bottom)) return null;
        return HoverAnchorCenter(left, top, right, bottom);
    }

    // Shell notification-icon menus require a benign WM_NULL posted AFTER
    // TrackPopupMenuEx returns. Without it, subsequent right-click menus can
    // stay stuck or dismiss incorrectly when Explorer changes foreground
    // ownership. The finally also covers cancellation (command 0) and any
    // native interop exception; no wake message is sent for a menu that was
    // never tracked.
    internal static uint TrackContextMenuWithWake(
        Action activateOwner, Func<uint> track, Action<uint> postMessage)
    {
        ArgumentNullException.ThrowIfNull(activateOwner);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(postMessage);
        activateOwner();
        try { return track(); }
        finally { postMessage(WmNull); }
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            FanProfile profile = FanProfileService.Load();
            bool fanHardwareAvailable = false;
            try { fanHardwareAvailable = LianLiSlInfinityService.Detect().Present; }
            catch (Exception ex) { LogService.Error("lianli", "Tray hardware detection failed", ex); }

            if (fanHardwareAvailable)
            {
                uint autoFlags = MfString | (profile.CpuTempTargetEnabled ? MfChecked : 0);
                AppendMenuW(menu, autoFlags, CmdAuto65, "AUTO 65 °C (min 1620 RPM)");
                AppendMenuW(menu, MfSeparator, 0, string.Empty);
                AppendMenuW(menu, MfString, Cmd700, "700 RPM");
                AppendMenuW(menu, MfString, Cmd1100, "1100 RPM");
                AppendMenuW(menu, MfString, Cmd1620, "1620 RPM");
                AppendMenuW(menu, MfString, Cmd2100, "2100 RPM");
                AppendMenuW(menu, MfSeparator, 0, string.Empty);
                AppendMenuW(menu, MfString, CmdOpen, "Otevřít Ventilátory");
            }
            else
            {
                AppendMenuW(menu, MfString | MfDisabled, 0,
                    "Ventilátory: Lian Li SL-Infinity nenalezeny");
            }
            AppendMenuW(menu, MfString, CmdRestart, "Restart Fan Agentu");

            bool cursorSucceeded = GetCursorPos(out Point point);
            TrayRect iconRect = default;
            bool shellSucceeded = false;
            if (!cursorSucceeded)
            {
                var iconId = new TrayNotifyIconIdentifier
                {
                    cbSize = (uint)Marshal.SizeOf<TrayNotifyIconIdentifier>(),
                    hWnd = _window,
                    uID = _id
                };
                shellSucceeded = Shell_NotifyIconGetRect(ref iconId, out iconRect) == 0;
            }
            var anchor = ResolveContextMenuAnchor(
                cursorSucceeded, point.X, point.Y, shellSucceeded,
                iconRect.left, iconRect.top, iconRect.right, iconRect.bottom);
            if (anchor is null) return;
            uint selected = TrackContextMenuWithWake(
                () => { _ = SetForegroundWindow(_window); },
                () => TrackPopupMenuEx(
                    menu,
                    TpmRightButton | TpmReturnCmd,
                    anchor.Value.X,
                    anchor.Value.Y,
                    _window,
                    IntPtr.Zero),
                message => { _ = PostMessageW(
                    _window, message, UIntPtr.Zero, IntPtr.Zero); });

            FanTrayAction action = selected switch
            {
                CmdAuto65 => FanTrayAction.ToggleAuto65,
                Cmd700 => FanTrayAction.Set700,
                Cmd1100 => FanTrayAction.Set1100,
                Cmd1620 => FanTrayAction.Set1620,
                Cmd2100 => FanTrayAction.Set2100,
                CmdOpen => FanTrayAction.OpenFans,
                CmdRestart => FanTrayAction.RestartAgent,
                _ => FanTrayAction.None
            };

            if (action != FanTrayAction.None)
                _actions.Enqueue(action);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
