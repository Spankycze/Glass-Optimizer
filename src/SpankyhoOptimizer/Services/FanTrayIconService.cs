using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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

public sealed class FanTrayIconService : IDisposable
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
    private readonly IntPtr _appIcon;
    private readonly IntPtr _warningIcon;
    private readonly IntPtr _errorIcon;
    private readonly uint _id = 1;
    private readonly ConcurrentQueue<FanTrayAction> _actions = new();
    private readonly WindowProc _windowProc;
    private readonly IntPtr _originalWndProc;
    private bool _added;
    private bool _firstUpdateLogged;
    private FanTrayState _state;

    public FanTrayIconService()
    {
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

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(AppContext.BaseDirectory, "App.ico");

        _appIcon = File.Exists(iconPath)
            ? LoadImageW(IntPtr.Zero, iconPath, ImageIcon, 16, 16, LrLoadFromFile)
            : IntPtr.Zero;
        if (_appIcon == IntPtr.Zero)
            _appIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512);

        _warningIcon = LoadIconW(IntPtr.Zero, (IntPtr)IdiWarning);
        _errorIcon = LoadIconW(IntPtr.Zero, (IntPtr)IdiError);

        _windowProc = WndProc;
        IntPtr callback = Marshal.GetFunctionPointerForDelegate(_windowProc);
        _originalWndProc = SetWindowLongPtrW(_window, GwlpWndProc, callback);

        var data = CreateData("Spankyho Fan Agent • startuje…", FanTrayState.Ok);
        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (_added)
        {
            data.uTimeoutOrVersion = NotifyIconVersion4;
            Shell_NotifyIconW(NimSetVersion, ref data);
            TryPromoteSelfIcon();
        }
    }

    public bool TryDequeueAction(out FanTrayAction action) =>
        _actions.TryDequeue(out action);

    public void Update(string tooltip, FanTrayState state = FanTrayState.Ok)
    {
        if (!_added) return;

        string text = string.IsNullOrWhiteSpace(tooltip)
            ? "Spankyho Fan Agent"
            : tooltip.Trim();

        if (text.Length > 127)
            text = text[..127];

        _state = state;
        var data = CreateData(text, state);
        bool ok = Shell_NotifyIconW(NimModify, ref data);
        if (!ok)
        {
            LogService.Error("lianli", "Fan tray tooltip update failed.");
        }
        else if (!_firstUpdateLogged)
        {
            _firstUpdateLogged = true;
            LogService.Info("lianli", $"FAN_TRAY_READY tooltip={text}");
        }

        PumpMessages();
    }

    public void PumpMessages()
    {
        while (PeekMessageW(out Msg msg, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WmAppTray)
        {
            int raw = unchecked((int)lParam.ToInt64());
            int code = raw & 0xFFFF;

            if (code == WmContextMenu || code == WmRButtonUp)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }

            if (code == WmLButtonUp || code == WmLButtonDblClk)
            {
                _actions.Enqueue(FanTrayAction.OpenFans);
                return IntPtr.Zero;
            }
        }

        return CallWindowProcW(_originalWndProc, hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            FanProfile profile = FanProfileService.Load();
            string voiceState = SpankyVoiceListenerService.IsListening ? "Listening" : SpankyVoiceListenerService.LastStatus;
            AppendMenuW(menu, MfString | MfDisabled, 0, $"Spanky Voice AI: {voiceState}");
            if (!string.IsNullOrWhiteSpace(SpankyVoiceListenerService.LastCommand))
                AppendMenuW(menu, MfString | MfDisabled, 0, $"Last: {SpankyVoiceListenerService.LastCommand}");
            AppendMenuW(menu, MfSeparator, 0, string.Empty);
            uint autoFlags = MfString | (profile.CpuTempTargetEnabled ? MfChecked : 0);
            AppendMenuW(menu, autoFlags, CmdAuto65, "AUTO 65 °C (min 1620 RPM)");
            AppendMenuW(menu, MfSeparator, 0, string.Empty);
            AppendMenuW(menu, MfString, Cmd700, "700 RPM");
            AppendMenuW(menu, MfString, Cmd1100, "1100 RPM");
            AppendMenuW(menu, MfString, Cmd1620, "1620 RPM");
            AppendMenuW(menu, MfString, Cmd2100, "2100 RPM");
            AppendMenuW(menu, MfSeparator, 0, string.Empty);
            AppendMenuW(menu, MfString, CmdOpen, "Otevřít Ventilátory");
            AppendMenuW(menu, MfString, CmdRestart, "Restart Fan Agentu");

            GetCursorPos(out Point point);
            SetForegroundWindow(_window);
            uint selected = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                _window,
                IntPtr.Zero);

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
            uFlags = NifMessage | NifTip | NifShowTip | (icon != IntPtr.Zero ? NifIcon : 0),
            uCallbackMessage = WmAppTray,
            hIcon = icon,
            szTip = tip,
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

        try
        {
            if (_appIcon != IntPtr.Zero)
                DestroyIcon(_appIcon);
        }
        catch { }

        try
        {
            if (_window != IntPtr.Zero)
                DestroyWindow(_window);
        }
        catch { }
    }

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

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

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(
        IntPtr lpPrevWndFunc,
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(
        out Msg lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr hmenu,
        uint fuFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
