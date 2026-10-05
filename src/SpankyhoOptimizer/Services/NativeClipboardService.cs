using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

public static class NativeClipboardService
{
    private const uint CfUnicodeText = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    public static bool TryGetText(out string text)
    {
        text = string.Empty;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!IsClipboardFormatAvailable(CfUnicodeText))
                        return false;

                    IntPtr handle = GetClipboardData(CfUnicodeText);
                    if (handle == IntPtr.Zero)
                        return false;

                    IntPtr pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero)
                        return false;

                    try
                    {
                        text = Marshal.PtrToStringUni(pointer)?.Trim() ?? string.Empty;
                        return text.Length > 0;
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(25);
        }

        return false;
    }
}
