using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public enum FanTrayAction
{
    None,
    ToggleAuto65,
    Set700,
    Set1100,
    Set1620,
    Set2100,
    OpenFans,
    RestartAgent
}

public enum FanTrayState
{
    Ok,
    Warning,
    Error
}

public sealed partial class FanTrayIconService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint PmRemove = 0x0001;
    private const uint WmNull = 0x0000;
    private const uint WmAppTray = 0x8000 + 77;
    private const int WmContextMenu = 0x007B;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int GwlpWndProc = -4;
    private const uint MfString = 0x00000000;
    private const uint MfDisabled = 0x00000002;
    private const uint MfSeparator = 0x00000800;
    private const uint MfChecked = 0x00000008;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int IdiError = 32513;
    private const int IdiWarning = 32515;

    private const uint CmdAuto65 = 1001;
    private const uint Cmd700 = 1002;
    private const uint Cmd1100 = 1003;
    private const uint Cmd1620 = 1004;
    private const uint Cmd2100 = 1005;
    private const uint CmdOpen = 1006;
    private const uint CmdRestart = 1007;

    private readonly IntPtr _window;
    private readonly int _trayIconSize;
    private readonly IntPtr _appIcon;
    private readonly bool _appIconOwned;
    private readonly IntPtr _warningIcon;
    private readonly IntPtr _errorIcon;
    private readonly bool _warningIconOwned;
    private readonly bool _errorIconOwned;
    private readonly uint _id = 1;
    private readonly ConcurrentQueue<FanTrayAction> _actions = new();
    // TrackPopupMenuEx pumps a nested Win32 message loop. Another
    // WM_CONTEXTMENU / WM_RBUTTONUP may re-enter WndProc before it returns.
    private int _contextMenuActive;
    // Both the owner message-loop finally and callers of Dispose() can
    // reach native cleanup. DestroyWindow/DestroyIcon must run at most once.
    private int _disposeCoreStarted;
    // A native double-click delivers first UP, DBLCLK, then another UP.
    // Do not queue three detached OpenFans launches for one user gesture.
    private long _lastFanOpenClickTick = long.MinValue;
    private readonly WindowProc _windowProc;
    private readonly IntPtr _originalWndProc;
    private bool _added;
    private bool _firstUpdateLogged;
    private FanTrayState _state;
    private string? _lastNotifiedTooltip;
    private FanTrayState _lastNotifiedState;
    private DateTime _lastNotifyUtc;
    // Explorer may discard a tray entry while the fan agent continues to run.
    // A failed modify must not spam Shell_NotifyIconW and disk logs every 1.5s.
    private bool _shellRecoveryPending;
    private DateTime _lastShellFailureUtc;
    private DateTime _lastShellFailureLoggedUtc;
    private Thread? _ownerMessageThread;
    private readonly int _ownerManagedThreadId;
    private static long _startupGeneration;
    private static long _activeStartup;

    // FanAgent resumes after await on worker threads, but tray mouse/click
    // messages must be pumped on the HWND-creating thread. GetMessage sleeps
    // while idle, so this dedicated thread is not a polling CPU workload.
    public static FanTrayIconService StartOnMessageThread()
    {
        long startupId = Interlocked.Increment(ref _startupGeneration);
        if (Interlocked.CompareExchange(ref _activeStartup, startupId, 0) != 0)
            throw new InvalidOperationException("Fan tray UI startup is already in progress.");

        var ready = new TaskCompletionSource<(FanTrayIconService? Tray, Exception? Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handshake = new object();
        bool abandoned = false;
        var worker = new Thread(() =>
        {
            FanTrayIconService? tray;
            try
            {
                tray = new FanTrayIconService();
                tray._ownerMessageThread = Thread.CurrentThread;
            }
            catch (Exception ex)
            {
                ready.TrySetResult((null, ex));
                Interlocked.CompareExchange(ref _activeStartup, 0, startupId);
                return;
            }

            lock (handshake)
            {
                if (abandoned)
                {
                    tray.DisposeCore();
                    Interlocked.CompareExchange(ref _activeStartup, 0, startupId);
                    return;
                }
                ready.TrySetResult((tray, null));
            }
            Interlocked.CompareExchange(ref _activeStartup, 0, startupId);
            tray.RunTrayMessageLoop();
        })
        {
            IsBackground = true,
            Name = "Spankyho Fan Tray UI"
        };
        try { worker.Start(); }
        catch
        {
            Interlocked.CompareExchange(ref _activeStartup, 0, startupId);
            throw;
        }
        if (!ready.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            lock (handshake)
            {
                if (!ready.Task.IsCompleted)
                {
                    abandoned = true;
                    throw new InvalidOperationException("Fan tray UI did not initialize.");
                }
            }
        }

        (FanTrayIconService? Tray, Exception? Error) outcome = ready.Task.GetAwaiter().GetResult();
        if (outcome.Error != null)
            throw new InvalidOperationException("Fan tray UI initialization failed.", outcome.Error);
        return outcome.Tray!;
    }

    // Win32 GetMessageW: positive = dispatch, zero = WM_QUIT,
    // negative = native failure. Capture the native error immediately after
    // the P/Invoke and never process an undefined MSG after failure.
    internal static bool ShouldDispatchTrayMessage(int result, int nativeError)
    {
        if (result > 0) return true;
        if (result == 0) return false;
        throw new System.ComponentModel.Win32Exception(
            nativeError, "Native fan tray GetMessageW failed.");
    }

    private void RunTrayMessageLoop()
    {
        try
        {
            while (true)
            {
                int result = GetMessageW(out Msg msg, IntPtr.Zero, 0, 0);
                // GetMessageW returns -1 for a Win32 failure and 0 only for
                // WM_QUIT. Treating both as a clean stop silently loses tray
                // diagnostics and hides broken message-pump failures.
                if (!ShouldDispatchTrayMessage(
                        result, result < 0 ? Marshal.GetLastWin32Error() : 0))
                    break;
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            // A broken UI callback must never take down fan safety control,
            // even when the diagnostics sink itself is unavailable.
            try { LogService.Error("lianli", "Fan tray UI message loop failed", ex); }
            catch { /* Always enter DisposeCore instead of crashing a worker thread. */ }
        }
        finally { DisposeCore(); }
    }

    public FanTrayIconService()
    {
        _ownerManagedThreadId = Environment.CurrentManagedThreadId;
        _window = CreateWindowExW(
            0,
            "STATIC",
            "Spankyho Fan Agent",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
            throw new InvalidOperationException("Nelze vytvořit skryté okno pro tray ikonu.");

        // Respect taskbar DPI: loading a fixed 16px raster and letting Explorer
        // upscale it makes the high-contrast mark blurry on 125–200% displays.
        _trayIconSize = GetTrayIconSize();
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(AppContext.BaseDirectory, "App.ico");

        IntPtr appBrand = File.Exists(iconPath)
            ? LoadImageW(IntPtr.Zero, iconPath, ImageIcon, _trayIconSize, _trayIconSize, LrLoadFromFile)
            : IntPtr.Zero;
        _appIconOwned = appBrand != IntPtr.Zero;
        _appIcon = _appIconOwned ? appBrand : LoadIconW(IntPtr.Zero, (IntPtr)32512);

        // Branded high-contrast tray glyphs keep warning/error visually related to
        // the application. Fall back to Windows stock icons only if assets are absent.
        IntPtr warningBrand = LoadBrandedStatusIcon("TrayWarning.ico");
        IntPtr errorBrand = LoadBrandedStatusIcon("TrayError.ico");
        _warningIconOwned = warningBrand != IntPtr.Zero;
        _errorIconOwned = errorBrand != IntPtr.Zero;
        _warningIcon = _warningIconOwned ? warningBrand : LoadIconW(IntPtr.Zero, (IntPtr)IdiWarning);
        _errorIcon = _errorIconOwned ? errorBrand : LoadIconW(IntPtr.Zero, (IntPtr)IdiError);

        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _windowProc = WndProc;
        IntPtr callback = Marshal.GetFunctionPointerForDelegate(_windowProc);
        _originalWndProc = SetWindowLongPtrW(_window, GwlpWndProc, callback);

        var data = CreateData("Spankyho Fan Agent • startuje…", FanTrayState.Ok);
        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (_added)
        {
            data.uTimeoutOrVersion = NotifyIconVersion4;
            _ = Shell_NotifyIconW(NimSetVersion, ref data);
            TryPromoteSelfIcon();
        }
        else
        {
            // Explorer can be starting when the scheduled Fan Agent launches.
            // An initial failure must not permanently disable all tray retries.
            _shellRecoveryPending = true;
            _lastShellFailureUtc = DateTime.UtcNow;
        }
    }

    internal static int SelectTrayIconSize(uint systemDpi)
    {
        // Select native ICO frames at the taskbar's system DPI. Windows can
        // interpolate between frames for uncommon scaling percentages.
        if (systemDpi < 96) systemDpi = 96;
        return Math.Clamp((int)Math.Round(systemDpi * 16d / 96d), 16, 48);
    }

    private static int GetTrayIconSize()
    {
        try { return SelectTrayIconSize(GetDpiForSystem()); }
        catch (EntryPointNotFoundException) { return 16; }
    }

    private IntPtr LoadBrandedStatusIcon(string fileName)
    {
        string asset = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        return File.Exists(asset)
            ? LoadImageW(IntPtr.Zero, asset, ImageIcon, _trayIconSize, _trayIconSize, LrLoadFromFile)
            : IntPtr.Zero;
    }

    public bool TryDequeueAction(out FanTrayAction action) =>
        _actions.TryDequeue(out action);

    // FanAgent can skip constructing multiple live CPU/RPM/mode strings
    // altogether on cycles where Explorer isn't due for another tooltip.
    // A state change (including warning/error/recovery) remains immediate.
    internal bool ShouldComposeTooltip(FanTrayState state)
    {
        DateTime now = DateTime.UtcNow;
        if (!_added)
            return _shellRecoveryPending &&
                ShellRecoveryDue(now - _lastShellFailureUtc);
        if (_shellRecoveryPending)
            return ShellRecoveryDue(now - _lastShellFailureUtc);
        return NeedsTooltipCompose(_lastNotifiedTooltip != null, state,
            _lastNotifiedState, now - _lastNotifyUtc);
    }

    internal static bool ShellRecoveryDue(TimeSpan sinceFailure) =>
        sinceFailure < TimeSpan.Zero || sinceFailure >= TimeSpan.FromSeconds(15);

    internal static bool NeedsTooltipCompose(bool hasNotified, FanTrayState nextState,
        FanTrayState previousState, TimeSpan sinceLast) =>
        !hasNotified || nextState != previousState ||
        sinceLast < TimeSpan.Zero ||
        sinceLast >= TimeSpan.FromSeconds(5);

    internal static string ClipShellTooltip(string text)
    {
        // NOTIFYICONDATA.szTip has 128 UTF-16 code units including NUL.
        // Avoid cutting a high/low surrogate pair at the 127-unit boundary:
        // that would send a broken glyph to Explorer. The full card text is
        // kept separately and is never subject to this shell-only limit.
        const int maxUnits = 127;
        if (text.Length <= maxUnits)
            return text;
        int length = maxUnits;
        if (char.IsHighSurrogate(text[length - 1]) &&
            char.IsLowSurrogate(text[length]))
            length--;
        return text[..length];
    }

    internal static (string shellText, string popupText) PrepareTrayDisplayText(string? tooltip)
    {
        string full = string.IsNullOrWhiteSpace(tooltip)
            ? "Spankyho Fan Agent"
            : tooltip.Trim();
        // NOTIFYICONDATA shell text is limited to 127 characters, but custom
        // card text must retain the full CPU, fan, mode and AI information.
        return (ClipShellTooltip(full), full);
    }

    public void Update(string tooltip, FanTrayState state = FanTrayState.Ok)
    {
        if (!_added && !_shellRecoveryPending) return;

        (string text, string popupText) = PrepareTrayDisplayText(tooltip);
        string previousPopupText = Volatile.Read(ref _popupTooltip);
        FanTrayState previousState = _state;
        _state = state;
        Volatile.Write(ref _popupTooltip, popupText);
        // Redraw only a visible hover card whose content actually changed.
        // Hidden cards repaint when shown; posting into their HWND on every
        // telemetry tick otherwise wakes the UI thread for no visible work.
        if (Volatile.Read(ref _popupVisible) && _popupWindow != IntPtr.Zero &&
            (previousState != state ||
             !string.Equals(previousPopupText, popupText, StringComparison.Ordinal)))
            QueueHoverRefresh();
        DateTime now = DateTime.UtcNow;
        if (_shellRecoveryPending &&
            !ShellRecoveryDue(now - _lastShellFailureUtc))
        {
            // The fan profile/RPM worker must continue at the normal cadence;
            // only failing Explorer tray IPC has a 15-second retry budget.
            PumpMessages();
            return;
        }
        if (!_shellRecoveryPending &&
            !ShouldNotify(text, state, _lastNotifiedTooltip,
                _lastNotifiedState, now - _lastNotifyUtc))
        {
            PumpMessages();
            return;
        }

        var data = CreateData(text, state);
        bool readded = false;
        bool ok;
        if (_shellRecoveryPending)
        {
            // Explorer can restart and forget our existing tray registration.
            // Re-register first, but permit MODIFY if it already exists.
            readded = Shell_NotifyIconW(NimAdd, ref data);
            ok = readded || (_added && Shell_NotifyIconW(NimModify, ref data));
        }
        else
        {
            ok = Shell_NotifyIconW(NimModify, ref data);
            if (!ok)
            {
                // One immediate self-healing attempt, then back off. Never
                // hammer Explorer and log on every 1.5-second fan RPM tick.
                readded = Shell_NotifyIconW(NimAdd, ref data);
                ok = readded;
            }
        }

        if (ok)
        {
            if (readded)
            {
                data.uTimeoutOrVersion = NotifyIconVersion4;
                _ = Shell_NotifyIconW(NimSetVersion, ref data);
            }
            _added = true;
            _shellRecoveryPending = false;
            _lastNotifiedTooltip = text;
            _lastNotifiedState = state;
            _lastNotifyUtc = now;
            if (!_firstUpdateLogged)
            {
                // The registry entry often appears after initial NIM_ADD.
                // Promote once when the first real live RPM/temperature is sent.
                TryPromoteSelfIcon();
                _firstUpdateLogged = true;
                LogService.Info("lianli", $"FAN_TRAY_READY tooltip={text}");
            }
        }
        else
        {
            // Windows may temporarily reject notifications or our hidden
            // tray HWND may be unavailable; keep fan control independent.
            _shellRecoveryPending = true;
            _lastShellFailureUtc = now;
            if (_lastShellFailureLoggedUtc == default ||
                now < _lastShellFailureLoggedUtc ||
                now - _lastShellFailureLoggedUtc >= TimeSpan.FromMinutes(1))
            {
                _lastShellFailureLoggedUtc = now;
                LogService.Error("lianli",
                    "Fan tray icon update failed; retrying with 15s backoff.");
            }
        }

        PumpMessages();
    }

    internal static bool ShouldNotify(
        string text, FanTrayState state, string? lastText, FanTrayState lastState, TimeSpan sinceLast)
    {
        if (lastText == null || state != lastState || sinceLast < TimeSpan.Zero)
            return true;
        // Temperature and RPM fluctuate on nearly every 1–2s agent tick.
        // Do not send a Shell_NotifyIconW IPC round-trip for each tooltip
        // fluctuation. Critical Warning/Error state transitions still publish
        // immediately; ordinary readouts update no more than every 5 seconds.
        if (!string.Equals(text, lastText, StringComparison.Ordinal))
            return sinceLast >= TimeSpan.FromSeconds(5);
        // Periodic refresh keeps the icon recoverable after Explorer disruptions.
        return sinceLast >= TimeSpan.FromSeconds(45);
    }

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

    private NotifyIconData CreateData(string tip, FanTrayState state)
    {
        IntPtr icon = state switch
        {
            FanTrayState.Warning when _warningIcon != IntPtr.Zero => _warningIcon,
            FanTrayState.Error when _errorIcon != IntPtr.Zero => _errorIcon,
            _ => _appIcon
        };

        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = _id,
            // NOTIFYICON_VERSION_4 without NIF_SHOWTIP asks Explorer to send
            // NIN_POPUPOPEN/CLOSE so our own styled hover card replaces the
            // default single-line shell tooltip.
            uFlags = NifMessage | NifTip | (icon != IntPtr.Zero ? NifIcon : 0),
            uCallbackMessage = WmAppTray,
            hIcon = icon,
            // Defensive 127-character guard also covers TaskbarCreated recovery,
            // not only the ordinary Update() publication path.
            szTip = ClipShellTooltip(tip),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private void TryPromoteSelfIcon()
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings", writable: true);
            if (root == null) return;

            string exe = Environment.ProcessPath ?? string.Empty;
            var stale = new List<string>();

            foreach (string subName in root.GetSubKeyNames())
            {
                using RegistryKey? key = root.OpenSubKey(subName, writable: true);
                if (key == null) continue;

                string path = key.GetValue("ExecutablePath")?.ToString() ?? string.Empty;
                string tooltip = key.GetValue("InitialTooltip")?.ToString() ?? string.Empty;
                bool ours = path.Contains("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase) ||
                            tooltip.Contains("Spankyho Fan", StringComparison.OrdinalIgnoreCase);

                if (!ours) continue;

                if (!string.IsNullOrWhiteSpace(exe) &&
                    path.Equals(exe, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                }
                else if (!string.IsNullOrWhiteSpace(path) &&
                         path.Contains("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase))
                {
                    stale.Add(subName);
                }
            }

            foreach (string subName in stale)
            {
                try { root.DeleteSubKeyTree(subName, throwOnMissingSubKey: false); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Tray icon promotion failed", ex);
        }
    }

    public void Dispose()
    {
        // The HWND/HICON set has already been released (or its owner has
        // started releasing it). Never post shutdown to a stale HWND.
        if (Volatile.Read(ref _disposeCoreStarted) != 0) return;
        if (_ownerMessageThread is { } thread &&
            Environment.CurrentManagedThreadId != _ownerManagedThreadId)
        {
            if (_window != IntPtr.Zero)
                _ = PostMessageW(_window, WmTrayShutdown, UIntPtr.Zero, IntPtr.Zero);
            _ = thread.Join(TimeSpan.FromSeconds(3));
            return;
        }
        DisposeCore();
    }

    // Faulty GDI/Win32 hover teardown or even a broken diagnostic sink must
    // never skip release of the actual tray icon, HWND and owned HICONs.
    internal static bool TryTrayCleanupSafely(
        Action cleanup, Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(reportError);
        try
        {
            cleanup();
            return true;
        }
        catch (Exception ex)
        {
            try { reportError(ex); }
            catch { /* A failed diagnostics sink cannot abort cleanup. */ }
            return false;
        }
    }

    // Invoke the same fault-contained hover cleanup used by DisposeCore,
    // then unconditionally post quit after it finishes. Never allow hover
    // failures to suppress the tray message-loop shutdown signal.
    internal static void RunTrayShutdownSafely(
        Action hideHover, Action requestQuit, Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(requestQuit);
        try { _ = TryTrayCleanupSafely(hideHover, reportError); }
        finally { requestQuit(); }
    }

    // Be resilient to independent native HICON destruction failures.
    // This routine is pure apart from its injected release/log callbacks:
    // tests never touch a real window or an OS-owned icon.
    internal static int ReleaseOwnedTrayIconsSafely(
        (IntPtr Handle, bool Owned) appIcon,
        (IntPtr Handle, bool Owned) warningIcon,
        (IntPtr Handle, bool Owned) errorIcon,
        Func<IntPtr, bool> destroyIcon,
        Action<IntPtr, Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(destroyIcon);
        ArgumentNullException.ThrowIfNull(reportError);
        int released = 0;
        foreach (var icon in new[] { appIcon, warningIcon, errorIcon })
        {
            if (!icon.Owned || icon.Handle == IntPtr.Zero)
                continue;
            try
            {
                if (!destroyIcon(icon.Handle))
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Native fan tray DestroyIcon returned FALSE.");
                released++;
            }
            catch (Exception ex)
            {
                try { reportError(icon.Handle, ex); }
                catch { /* Failed diagnostics must never prevent remaining HICON cleanup. */ }
            }
        }
        return released;
    }

    // One winner per native HWND/HICON resource set. The message-loop
    // finally, a direct owner-thread Dispose, or a reentrant callback may
    // all attempt shutdown. Never free the same Windows handle twice.
    internal static bool TryBeginTrayCleanup(ref int state) =>
        Interlocked.CompareExchange(ref state, 1, 0) == 0;

    private void DisposeCore()
    {
        if (!TryBeginTrayCleanup(ref _disposeCoreStarted)) return;
        _ = TryTrayCleanupSafely(
            DisposeHoverPopup,
            ex => LogService.Error("lianli", "Fan tray hover cleanup failed", ex));
        try
        {
            if (_added)
            {
                var data = CreateData("Spankyho Fan Agent", _state);
                Shell_NotifyIconW(NimDelete, ref data);
                _added = false;
            }
        }
        catch { }

        try
        {
            if (_originalWndProc != IntPtr.Zero)
                SetWindowLongPtrW(_window, GwlpWndProc, _originalWndProc);
        }
        catch { }

        // Only destroy icons allocated by LoadImageW; stock LoadIconW
        // handles belong to Windows. Every owned handle is released
        // independently: a failing DestroyIcon must not skip later HICONs.
        _ = ReleaseOwnedTrayIconsSafely(
            (_appIcon, _appIconOwned),
            (_warningIcon, _warningIconOwned),
            (_errorIcon, _errorIconOwned),
            h => DestroyIcon(h),
            (h, ex) => LogService.Error(
                "lianli", $"Fan tray icon cleanup failed hIcon=0x{h.ToInt64():X}", ex));

        try
        {
            if (_window != IntPtr.Zero)
                DestroyWindow(_window);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessageW(out Msg msg, IntPtr hWnd,
        uint min, uint max);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint message,
        UIntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hWnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(
        IntPtr lpPrevWndFunc,
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        IntPtr lParam);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(
        out Msg lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr hmenu,
        uint fuFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
