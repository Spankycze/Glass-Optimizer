using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class UnelevatedProcessRunner
{
    private const uint TOKEN_QUERY = 0x0008, TOKEN_DUPLICATE = 0x0002, TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint MAXIMUM_ALLOWED = 0x02000000, CREATE_NO_WINDOW = 0x08000000, LOGON_WITH_PROFILE = 0x00000001;
    private const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;

    public static async Task<ProcessResult> RunAsync(string file, string arguments, TimeSpan timeout,
        CancellationToken token, Action<string>? onOutput = null)
    {
        string log = Path.Combine(Path.GetTempPath(), $"spanky-user-{Guid.NewGuid():N}.log");
        try
        {
            string cmd = $"\"{file}\" {arguments} > \"{log}\" 2>&1";
            return await RunCommandAsync(cmd, log, timeout, token, onOutput).ConfigureAwait(false);
        }
        finally { try { File.Delete(log); } catch { } }
    }
    private static async Task<ProcessResult> RunCommandAsync(string command, string log, TimeSpan timeout,
        CancellationToken token, Action<string>? onOutput)
    {
        using Process explorer = Process.GetProcessesByName("explorer")
            .FirstOrDefault(p => p.SessionId == Process.GetCurrentProcess().SessionId)
            ?? throw new InvalidOperationException("Explorer.exe běžného uživatele nebyl nalezen.");

        if (!OpenProcessToken(explorer.Handle, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY, out IntPtr sourceToken))
            throw new InvalidOperationException($"OpenProcessToken selhal: {Marshal.GetLastWin32Error()}.");
        try
        {
            if (!DuplicateTokenEx(sourceToken, MAXIMUM_ALLOWED, IntPtr.Zero, 2, 1, out IntPtr userToken))
                throw new InvalidOperationException($"DuplicateTokenEx selhal: {Marshal.GetLastWin32Error()}.");
            try
            {
                string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                string commandLine = $"\"{cmdExe}\" /d /s /c \"{command}\"";
                if (!CreateProcessWithTokenW(userToken, LOGON_WITH_PROFILE, cmdExe, commandLine,
                    CREATE_NO_WINDOW, IntPtr.Zero, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref si, out PROCESS_INFORMATION pi))
                    throw new InvalidOperationException($"CreateProcessWithTokenW selhal: {Marshal.GetLastWin32Error()}.");
                return await WaitForProcessAsync(pi, log, timeout, token, onOutput).ConfigureAwait(false);
            }
            finally { CloseHandle(userToken); }
        }
        finally { CloseHandle(sourceToken); }
    }
    private static async Task<ProcessResult> WaitForProcessAsync(PROCESS_INFORMATION pi, string log, TimeSpan timeout,
        CancellationToken token, Action<string>? onOutput)
    {
        var output = new StringBuilder();
        var seenLines = 0;
        var started = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (started.Elapsed > timeout)
                {
                    TerminateProcess(pi.hProcess, 1460);
                    throw new TimeoutException($"Proces běžného uživatele překročil timeout {timeout.TotalSeconds:0}s.");
                }

                ReadNewLines(log, ref seenLines, output, onOutput);
                uint wait = WaitForSingleObject(pi.hProcess, 180);
                if (wait == WAIT_OBJECT_0) break;
                if (wait != WAIT_TIMEOUT) throw new InvalidOperationException($"WaitForSingleObject selhal: {Marshal.GetLastWin32Error()}.");
                await Task.Delay(80, token).ConfigureAwait(false);
            }
            ReadNewLines(log, ref seenLines, output, onOutput);
            if (!GetExitCodeProcess(pi.hProcess, out uint exit)) exit = 1;
            return new ProcessResult(unchecked((int)exit), output.ToString(), string.Empty);
        }
        catch (OperationCanceledException)
        {
            try { TerminateProcess(pi.hProcess, 1223); } catch { }
            throw;
        }
        finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
    }

    private static void ReadNewLines(string log, ref int seenLines, StringBuilder output, Action<string>? onOutput)
    {
        try
        {
            if (!File.Exists(log)) return;
            string[] lines = File.ReadAllLines(log);
            for (int i = seenLines; i < lines.Length; i++)
            {
                string line = lines[i]; output.AppendLine(line);
                try { onOutput?.Invoke(line); } catch { }
            }
            seenLines = lines.Length;
        }
        catch (IOException) { }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName,
        string commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
