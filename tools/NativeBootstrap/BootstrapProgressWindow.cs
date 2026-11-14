using System;
using System.Runtime.InteropServices;
using System.Threading;

// Dedicated native Win32 UI thread. Worker/download threads never directly
// call SendMessage or SetWindowText on foreign-window HWNDs.
internal sealed class BootstrapProgressWindow : IDisposable
{
    private const uint Visible = 0x10000000;
    private const uint Child = 0x40000000;
    private const uint Caption = 0x00C00000;
    private const uint SysMenu = 0x00080000;
    private const uint WmClose = 0x0010;
    private const uint WmQuit = 0x0012;
    private const uint WmUpdate = 0x8001;
    private const uint SetRange32 = 0x0400 + 6;
    private const uint SetPosition = 0x0400 + 2;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly CancellationTokenSource cancellation = new();
    private readonly object stateSync = new();

    private nint window;
    private nint label;
    private nint bar;
    private uint threadId;
    private int disposed;
    private int closingDisabled;
    private bool pendingUpdate;
    private string statusText = "Připravuji potřebné součásti…";
    private int progressPermille;
    private int updateBatches;
    internal int UpdateBatches => Volatile.Read(ref updateBatches);
    internal CancellationToken Cancellation => cancellation.Token;
    internal bool IsVisible => window != 0 && IsWindow(window);

    [StructLayout(LayoutKind.Sequential)]
    private struct CommonControls { internal uint Size; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        internal nint Hwnd;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }

    [DllImport("user32.dll", EntryPoint="CreateWindowExW", CharSet=CharSet.Unicode)]
    private static extern nint CreateWindow(uint exStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint="SetWindowTextW", CharSet=CharSet.Unicode)]
    private static extern bool SetText(nint hwnd, string text);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint GetSystemMenu(nint hwnd, bool revert);
    [DllImport("user32.dll")] private static extern uint EnableMenuItem(nint menu, uint item, uint flags);
    [DllImport("user32.dll")] private static extern bool DrawMenuBar(nint hwnd);
    [DllImport("user32.dll", EntryPoint="SendMessageW")]
    private static extern nint Send(nint hwnd, uint msg, nint param, nint value);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, nint hwnd, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, nint wp, nint lp);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("comctl32.dll", EntryPoint="InitCommonControlsEx")]
    private static extern bool InitControls(ref CommonControls controls);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int data, uint size);

    internal BootstrapProgressWindow()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Spanky Bootstrap Status" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void QueueUpdate()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (stateSync)
        {
            if (pendingUpdate) return;
            pendingUpdate = true;
            if (threadId == 0 || !PostThreadMessageW(threadId, WmUpdate, 0, 0))
                pendingUpdate = false;
        }
    }

    private void UpdateOnUiThread()
    {
        string snapshot;
        int percent;
        lock (stateSync)
        {
            snapshot = statusText;
            percent = progressPermille;
            pendingUpdate = false;
        }
        nint hwnd = label;
        if (hwnd != 0 && IsWindow(hwnd)) SetText(hwnd, snapshot);
        hwnd = bar;
        if (hwnd != 0 && IsWindow(hwnd)) Send(hwnd, SetPosition, percent, 0);
        if (Volatile.Read(ref closingDisabled) != 0)
        {
            hwnd = window;
            if (hwnd != 0 && IsWindow(hwnd))
            {
                nint menu = GetSystemMenu(hwnd, false);
                if (menu != 0)
                {
                    EnableMenuItem(menu, 0xF060, 0x00000001);
                    DrawMenuBar(hwnd);
                }
            }
        }
        Interlocked.Increment(ref updateBatches);
    }

    private void Run()
    {
        try
        {
            threadId = GetCurrentThreadId();
            var controls = new CommonControls { Size = 8, Flags = 0x20 };
            InitControls(ref controls);
            int x = Math.Max(0, (GetSystemMetrics(0) - 470) / 2);
            int y = Math.Max(0, (GetSystemMetrics(1) - 150) / 2);
            nint hwnd = CreateWindow(0, "STATIC", "Spankyho Optimizer – příprava instalace",
                Visible | Caption | SysMenu, x, y, 470, 150, 0, 0, 0, 0);
            window = hwnd;
            if (hwnd == 0) return;
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                int type = 3; // Windows 11 transient system backdrop
                DwmSetWindowAttribute(hwnd, 38, ref type, 4);
            }
            label = CreateWindow(0, "STATIC", statusText,
                Visible | Child, 20, 30, 425, 30, hwnd, 0, 0, 0);
            bar = CreateWindow(0, "msctls_progress32", "", Visible | Child,
                20, 75, 425, 20, hwnd, 0, 0, 0);
            if (bar != 0) Send(bar, SetRange32, 0, 1000);
            ready.Set();

            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Message == WmUpdate && msg.Hwnd == 0)
                {
                    UpdateOnUiThread();
                    continue;
                }
                if (msg.Message == WmClose && msg.Hwnd == window)
                {
                    lock (stateSync)
                    {
                        if (closingDisabled != 0) continue;
                        cancellation.Cancel(); // serialize user close with UAC handoff
                    }
                }

                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
                if (window != 0 && !IsWindow(window))
                {
                    cancellation.Cancel();
                    break;
                }
            }
        }
        catch
        {
            // If the UI thread crashes, terminate any pending download rather than
            // leaving an invisible privileged prerequisite operation in progress.
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        finally
        {
            if (window != 0 && IsWindow(window)) DestroyWindow(window);
            window = 0;
            ready.Set();
        }
    }

    internal void Status(string message)
    {
        lock (stateSync) statusText = message;
        QueueUpdate();
    }

    internal void DownloadProgress(long downloaded, long? total)
    {
        lock (stateSync)
        {
            if (total is > 0)
            {
                int percent = (int)Math.Min(100, downloaded * 100L / total.Value);
                statusText = "Stahuji .NET Desktop Runtime: " + percent + " %";
                progressPermille = percent * 10;
            }
            else
                statusText = "Stahuji .NET Desktop Runtime: " + downloaded / 1048576 + " MiB";
        }
        QueueUpdate();
    }

    internal bool TryBeginInstallation()
    {
        lock (stateSync)
        {
            // Atomic with WM_CLOSE handling: never launch UAC after an X click.
            if (cancellation.IsCancellationRequested || !IsVisible) return false;
            closingDisabled = 1;
        }
        QueueUpdate();
        return true;
    }

    // Diagnostic only: uses actual Win32 message handling.
    internal bool SimulateCloseForTest()
    {
        nint hwnd = window;
        return hwnd != 0 && PostMessageW(hwnd, WmClose, 0, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (threadId != 0) PostThreadMessageW(threadId, WmQuit, 0, 0);
        bool stopped = Thread.CurrentThread == thread || thread.Join(1500);
        if (stopped) cancellation.Dispose();
        ready.Dispose();
    }
}
