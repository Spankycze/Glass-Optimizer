using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetPopupStyle735(IntPtr hwnd, int index);
    [StructLayout(LayoutKind.Sequential)]
    private struct PopupPoint735 { public int X, Y; }
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    private static extern bool ClientToScreen735(IntPtr hwnd, ref PopupPoint735 point);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendPopupMessage735(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);

    private static async Task VerifyPopupNativeFrame735Async(bool resize)
    {
        var popup = MakeContinuationPopup((_, _) => Task.FromResult(new DesktopUpdateCompletion("Ready", "Close")));
        try
        {
            popup.ShowWithoutActivation(); await Task.Delay(100);
            var hwnd = WindowNative.GetWindowHandle(popup);
            if (!resize)
            {
                long style = GetPopupStyle735(hwnd, -16).ToInt64();
                if ((style & 0x00C00000L) != 0)
                    throw new InvalidOperationException($"Borderless popup retains native caption/frame bits: 0x{style:X}.");
                long exStyle = GetPopupStyle735(hwnd, -20).ToInt64();
                if ((exStyle & 0x08000080L) != 0x08000080L)
                    throw new InvalidOperationException("No-activate/tool-window flags were lost.");
            }
            else
            {
                var point = new PopupPoint735 { X = 200, Y = 3 };
                if (!ClientToScreen735(hwnd, ref point)) throw new InvalidOperationException("Client point unavailable.");
                var packed = new IntPtr(unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16))));
                long hit = SendPopupMessage735(hwnd, 0x84, IntPtr.Zero, packed).ToInt64();
                if (hit >= 10 && hit <= 17)
                    throw new InvalidOperationException("Fixed-size popup exposes native resize hit target: " + hit);
            }
        }
        finally { popup.Close(); }
    }
}
