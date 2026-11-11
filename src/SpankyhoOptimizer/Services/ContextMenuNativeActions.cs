using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Lightweight entry points for Explorer context-menu actions. Program.Main calls
/// these before WinUI/COM startup. Explorer passes the target as a single quoted
/// argument; it is never interpolated into a command-shell script.
/// </summary>
internal static class ContextMenuNativeActions
{
    internal static bool TryHandle(string[] args)
    {
        if (args.Length == 0) return false;
        // Recognize only our own helper verbs; unrelated app smoke CLI flags
        // must still reach the normal WinUI application startup.
        string verb = args[0].ToLowerInvariant();
        if (verb is not ("--context-copy-path" or "--context-new-file" or "--context-sha256" or
            "--context-admin-terminal" or "--context-admin-powershell" or
            "--context-advanced-startup" or "--context-action-smoke")) return false;
        try
        {
            switch (verb)
            {
                case "--context-copy-path":
                    RequireTarget(args);
                    CopyUnicode(args[1]);
                    break;
                case "--context-new-file":
                    RequireTarget(args);
                    CreateEmptyFile(args[1]);
                    break;
                case "--context-sha256":
                    RequireTarget(args);
                    string hash = HashFile(args[1]);
                    CopyUnicode(hash);
                    _ = MessageBoxW(IntPtr.Zero, "SHA-256:\n" + hash + "\n\nHash byl zkopírován do schránky.",
                        "Spankyho Optimizer – SHA-256", 0x40);
                    break;
                case "--context-admin-terminal":
                    RequireTarget(args);
                    LaunchElevated("wt.exe", "-d \"" + Path.GetFullPath(args[1]) + "\"", args[1]);
                    break;
                case "--context-admin-powershell":
                    RequireTarget(args);
                    string ps7 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
                    if (!File.Exists(ps7))
                        throw new FileNotFoundException("PowerShell 7 nebyl nalezen.", ps7);
                    LaunchElevated(ps7, "-NoExit -NoProfile", args[1]);
                    break;
                case "--context-advanced-startup":
                    if (MessageBoxW(IntPtr.Zero,
                        "Opravdu chceš nyní restartovat Windows do pokročilého spuštění?",
                        "Spankyho Optimizer – Pokročilé spuštění", 0x24) == 6)
                    {
                        Process.Start(new ProcessStartInfo(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
                            "/r /o /t 0")
                        { UseShellExecute = false, CreateNoWindow = true });
                    }
                    break;
                case "--context-action-smoke":
                    RunSmoke();
                    break;
                default:
                    throw new ArgumentException("Neznámá kontextová akce.");
            }
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            if (args[0].Equals("--context-action-smoke", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(ex);
            else
                _ = MessageBoxW(IntPtr.Zero, ex.Message, "Spankyho Optimizer – chyba", 0x10);
        }
        return true;
    }

    private static void LaunchElevated(string executable, string arguments, string folder)
    {
        string workingDirectory = Path.GetFullPath(folder);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException("Složka nebyla nalezena: " + workingDirectory);
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", Arguments = arguments,
            WorkingDirectory = workingDirectory
        });
    }

    private static void RequireTarget(string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
            throw new ArgumentException("Chybí nebo není jednoznačně zadaná cesta.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CreateEmptyFile(string directory)
    {
        string folder = Path.GetFullPath(directory);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Složka nebyla nalezena: " + folder);
        for (int suffix = 0; suffix < 10000; suffix++)
        {
            string name = suffix == 0 ? "New File.txt" : "New File (" + suffix + ").txt";
            string path = Path.Combine(folder, name);
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("Nelze vytvořit jedinečný název souboru.");
    }

    private static void RunSmoke()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Spanky Context Smoke " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = CreateEmptyFile(directory);
            string second = CreateEmptyFile(directory);
            if (!File.Exists(first) || !File.Exists(second) || first == second ||
                new FileInfo(first).Length != 0 || new FileInfo(second).Length != 0)
                throw new InvalidOperationException("New empty file failed.");
            string content = Path.Combine(directory, "Unicode & spaces žluťoučký.txt");
            File.WriteAllText(content, "native-context-smoke", Encoding.UTF8);
            string actual = HashFile(content);
            string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(content)));
            if (actual != expected)
                throw new InvalidOperationException("Native SHA256 mismatch.");
            Console.WriteLine("NATIVE_CONTEXT_ACTION_SMOKE_OK duplicateSafe=true unicode=true sha256=true clipboardUntouched=true");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void CopyUnicode(string value)
    {
        byte[] data = Encoding.Unicode.GetBytes(value + "\0");
        IntPtr allocation = GlobalAlloc(0x0002, (UIntPtr)data.Length); // GMEM_MOVEABLE
        if (allocation == IntPtr.Zero) throw new IOException("Nelze alokovat paměť schránky.");
        bool owned = true;
        bool opened = false;
        // EmptyClipboard requires a real owner HWND for SetClipboardData to succeed.
        // A message-only STATIC control needs no visible window or WinUI startup.
        IntPtr clipboardOwner = CreateWindowExW(0, "STATIC", "SpankyClipboardOwner", 0,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (clipboardOwner == IntPtr.Zero) throw new IOException("Nelze vytvořit vlastníka schránky.");
            IntPtr pointer = GlobalLock(allocation);
            if (pointer == IntPtr.Zero) throw new IOException("GlobalLock selhal.");
            try { Marshal.Copy(data, 0, pointer, data.Length); }
            finally { GlobalUnlock(allocation); }
            for (int retry = 0; retry < 5 && !(opened = OpenClipboard(clipboardOwner)); retry++)
                Thread.Sleep(25);
            if (!opened) throw new IOException("Schránka je právě obsazená.");
            if (!EmptyClipboard()) throw new IOException("Nelze vyprázdnit schránku.");
            if (SetClipboardData(13, allocation) == IntPtr.Zero) // CF_UNICODETEXT
                throw new IOException("Nelze zapsat text do schránky.");
            owned = false; // system now owns allocation
        }
        finally
        {
            if (opened) _ = CloseClipboard();
            if (owned) _ = GlobalFree(allocation);
            if (clipboardOwner != IntPtr.Zero) _ = DestroyWindow(clipboardOwner);
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hwnd);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr hwnd, string text, string title, uint type);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr handle);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr handle);
}
