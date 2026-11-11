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
    private static IntPtr OpenExplorerSessionToken()
    {
        int sessionId;
        using (Process current = Process.GetCurrentProcess())
            sessionId = current.SessionId;

        return ProcessSnapshotCleanup.Inspect(
            () => Process.GetProcessesByName("explorer"), explorers =>
            {
                foreach (Process explorer in explorers)
                {
                    int explorerSession;
                    try { explorerSession = explorer.SessionId; }
                    catch (InvalidOperationException) { continue; }
                    if (explorerSession != sessionId) continue;
                    if (!OpenProcessToken(explorer.Handle,
                            TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY,
                            out IntPtr sourceToken))
                        throw new InvalidOperationException($"OpenProcessToken selhal: {Marshal.GetLastWin32Error()}.");
                    return sourceToken;
                }
                throw new InvalidOperationException("Explorer.exe běžného uživatele nebyl nalezen.");
            });
    }

    private static async Task<ProcessResult> RunCommandAsync(string command, string log, TimeSpan timeout,
        CancellationToken token, Action<string>? onOutput)
    {
        // Acquire the token while every explorer Process wrapper is still alive;
        // close all wrappers before launching the child process.
        IntPtr sourceToken = OpenExplorerSessionToken();
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
                    StopProcessTree(pi, 1460);
                    throw new TimeoutException($"Proces běžného uživatele překročil timeout {timeout.TotalSeconds:0}s.");
                }

                ReadNewLines(log, ref seenLines, output, onOutput);
                uint wait = WaitForSingleObject(pi.hProcess, 180);
                if (wait == WAIT_OBJECT_0) break;
                if (wait != WAIT_TIMEOUT) throw new InvalidOperationException($"WaitForSingleObject selhal: {Marshal.GetLastWin32Error()}.");
                await Task.Delay(80, token).ConfigureAwait(false);
            }
            ReadNewLines(log, ref seenLines, output, onOutput, includePartial: true);
            if (!GetExitCodeProcess(pi.hProcess, out uint exit)) exit = 1;
            return new ProcessResult(unchecked((int)exit), output.ToString(), string.Empty);
        }
        catch (OperationCanceledException)
        {
            StopProcessTree(pi, 1223);
            throw;
        }
        finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
    }

    private static void StopProcessTree(PROCESS_INFORMATION pi, uint exitCode)
    {
        try
        {
            using var process = Process.GetProcessById(unchecked((int)pi.dwProcessId));
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        // Retain the native handle fallback for access failures or process-exit races.
        try { TerminateProcess(pi.hProcess, exitCode); } catch { }
        uint wait = WaitForSingleObject(pi.hProcess, 2000);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT)
            LogService.Error("process", $"WaitForSingleObject failed after process termination: code=0x{wait:X8}.");
    }

    private static void ReadNewLines(string log, ref int seenLines, StringBuilder output, Action<string>? onOutput, bool includePartial = false)
    {
        try
        {
            if (!File.Exists(log)) return;
            string text = File.ReadAllText(log);
            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int completeLines = lines.Length - 1;
            if (includePartial && lines[^1].Length > 0) completeLines++;
            for (int i = seenLines; i < completeLines; i++)
            {
                string line = lines[i]; output.AppendLine(line);
                try { onOutput?.Invoke(line); } catch { }
            }
            seenLines = completeLines;
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

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName,
        string commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
