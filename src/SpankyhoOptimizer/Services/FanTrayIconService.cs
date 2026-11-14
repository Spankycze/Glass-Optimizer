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

        var ready = new ManualResetEventSlim();
        (FanTrayIconService? Tray, Exception? Error) outcome = (null, null);
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
                outcome = (null, ex);
                ready.Set();
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
                outcome = (tray, null);
                ready.Set();
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
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            lock (handshake)
            {
                if (!ready.IsSet)
                {
                    abandoned = true;
                    throw new InvalidOperationException("Fan tray UI did not initialize.");
                }
            }
        }

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
}
