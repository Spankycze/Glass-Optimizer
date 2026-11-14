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
