using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class ProcessRunner
{
    private const int MaxStdOutChars = 1_048_576;
    private const int MaxStdErrChars = 262_144;
    private static readonly int ProcessConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 4, 6);
    private static readonly SemaphoreSlim ProcessGate = new(ProcessConcurrency, ProcessConcurrency);
    private static readonly SemaphoreSlim TelemetryWriteGate = new(1, 1);
    private static readonly string TelemetrySentinelPath =
        Path.Combine(Path.GetTempPath(), "SpankyOptimizer.PerfTelemetry.enabled");
    private static readonly bool TelemetrySentinelEnabled =
        File.Exists(TelemetrySentinelPath) &&
        DateTime.UtcNow - File.GetLastWriteTimeUtc(TelemetrySentinelPath) <= TimeSpan.FromMinutes(15);
    private static bool TelemetryEnabled() =>
        TelemetrySentinelEnabled ||
        string.Equals(Environment.GetEnvironmentVariable("SPANKY_PROCESS_TELEMETRY"), "1", StringComparison.Ordinal);
    private static readonly HashSet<string> TrustedWindowsTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "powercfg.exe", "netsh.exe", "pnputil.exe", "dism.exe", "sfc.exe", "schtasks.exe",
        "sc.exe", "reg.exe", "wevtutil.exe", "ipconfig.exe", "whoami.exe", "taskkill.exe",
        "cmd.exe", "powershell.exe", "bcdedit.exe", "fsutil.exe", "cleanmgr.exe", "defrag.exe"
    };

    private static string ResolveExecutable(string file)
    {
        if (Path.IsPathRooted(file)) return file;
        if (!TrustedWindowsTools.Contains(file)) return file;

        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string candidate = string.Equals(file, "powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe")
            : Path.Combine(system32, file);
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Důvěryhodný Windows nástroj nebyl nalezen v systémové cestě: {file}", candidate);
        return candidate;
    }

    private static void HardenEnvironment(ProcessStartInfo startInfo)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0");

        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, new[]
        {
            system32,
            windows,
            Path.Combine(system32, "Wbem"),
            powershell
        });
        startInfo.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        startInfo.Environment["SystemRoot"] = windows;
        startInfo.Environment["WINDIR"] = windows;

        if (string.Equals(Path.GetFileName(startInfo.FileName), "powershell.exe", StringComparison.OrdinalIgnoreCase))
            startInfo.Environment["PSModulePath"] = Path.Combine(powershell, "Modules");
    }

    private static string HardenArguments(string resolvedFile, string arguments)
    {
        if (string.Equals(Path.GetFileName(resolvedFile), "cmd.exe", StringComparison.OrdinalIgnoreCase) &&
            !arguments.TrimStart().StartsWith("/D", StringComparison.OrdinalIgnoreCase))
            return "/D " + arguments;
        return arguments;
    }

    internal static async Task RunHardeningSmokeAsync(CancellationToken token = default)
    {
        string cmd = ResolveExecutable("cmd.exe");
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Path.IsPathRooted(cmd) || !string.Equals(Path.GetDirectoryName(cmd), system32, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Trusted cmd.exe did not resolve to System32.");

        if (!HardenArguments(cmd, "/C echo test").StartsWith("/D ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("cmd.exe /D hardening was not applied.");
        if (!string.Equals(HardenArguments(cmd, "/d /c echo test"), "/d /c echo test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("cmd.exe /D hardening was duplicated.");

        string powershell = ResolveExecutable("powershell.exe");
        string expectedPowerShellDir = Path.Combine(system32, "WindowsPowerShell", "v1.0");
        if (!Path.IsPathRooted(powershell) ||
            !string.Equals(Path.GetDirectoryName(powershell), expectedPowerShellDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Trusted powershell.exe did not resolve to WindowsPowerShell\\v1.0.");

        string poison = Path.Combine(Path.GetTempPath(), "SpankyProcessRunnerPoison");
        Directory.CreateDirectory(poison);
        string fake = Path.Combine(poison, "cmd.exe");
        await File.WriteAllTextAsync(fake, "not a Windows executable", token).ConfigureAwait(false);
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", poison);
            var result = await RunAsync("cmd.exe", "/C echo SPANKY_PROCESS_HARDENING_OK", TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            if (!result.Success || !result.StdOut.Contains("SPANKY_PROCESS_HARDENING_OK", StringComparison.Ordinal))
                throw new InvalidOperationException("Poisoned PATH replaced or broke trusted cmd.exe resolution.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { File.Delete(fake); Directory.Delete(poison, false); } catch { }
        }
    }

    private static void AppendBounded(StringBuilder buffer, string line, int maxChars)
    {
        lock (buffer)
        {
            if (buffer.Length >= maxChars) return;
            int remaining = maxChars - buffer.Length;
            if (line.Length + Environment.NewLine.Length <= remaining) buffer.AppendLine(line);
            else if (remaining > 0) buffer.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
        }
    }

    public static Process StartBackground(
        string file,
        string arguments,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? (Path.IsPathRooted(file) ? Path.GetDirectoryName(file) ?? AppContext.BaseDirectory : AppContext.BaseDirectory)
                : workingDirectory
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Proces {file} se nepodařilo spustit.");
        }

        return process;
    }

    public static bool StartDetached(
        string file,
        string arguments = "",
        bool useShellExecute = true,
        string? verb = null,
        string? workingDirectory = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = useShellExecute,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? (Path.IsPathRooted(file) ? Path.GetDirectoryName(file) ?? AppContext.BaseDirectory : AppContext.BaseDirectory)
                    : workingDirectory
            };

            if (!string.IsNullOrWhiteSpace(verb))
                startInfo.Verb = verb;

            return Process.Start(startInfo) != null;
        }
        catch (Exception ex)
        {
            LogService.Error("process", $"DETACHED START FAIL file={Path.GetFileName(file)}", ex);
            return false;
        }
    }

    public static Process StartManaged(
        string file,
        IEnumerable<string> arguments,
        string? workingDirectory = null)
    {
        string resolvedFile = ResolveExecutable(file);
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedFile,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? (Path.GetDirectoryName(resolvedFile) ?? AppContext.BaseDirectory)
                : workingDirectory
        };

        if (TrustedWindowsTools.Contains(Path.GetFileName(file)))
            HardenEnvironment(startInfo);

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Proces {file} se nepodařilo spustit.");
        }

        return process;
    }

    public static async Task<ProcessResult> StartShellAndWaitAsync(
        string file,
        string arguments,
        string? verb = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(file) ?? AppContext.BaseDirectory
        };
        if (!string.IsNullOrWhiteSpace(verb))
            startInfo.Verb = verb;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Proces {file} se nepodařilo spustit.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, string.Empty, string.Empty);
    }

    public static async Task<ProcessResult> RunAsync(string file, string arguments, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, bool logNonZeroExitAsError = true,
        [CallerMemberName] string caller = "")
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        return await RunStreamingAsync(file, arguments, timeout, cancellationToken,
            line => AppendBounded(stdout, line, MaxStdOutChars),
            line => AppendBounded(stderr, line, MaxStdErrChars), stdout, stderr, logNonZeroExitAsError, caller).ConfigureAwait(false);
    }

    public static async Task<ProcessResult> RunStreamingAsync(string file, string arguments, TimeSpan? timeout,
        CancellationToken cancellationToken, Action<string>? onStdOut, Action<string>? onStdErr,
        [CallerMemberName] string caller = "")
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        return await RunStreamingAsync(file, arguments, timeout, cancellationToken,
            line => { AppendBounded(stdout, line, MaxStdOutChars); SafeInvokeCallback(onStdOut, line, "stdout"); },
            line => { AppendBounded(stderr, line, MaxStdErrChars); SafeInvokeCallback(onStdErr, line, "stderr"); }, stdout, stderr, logNonZeroExitAsError: true, caller).ConfigureAwait(false);
    }

    private static void SafeInvokeCallback(Action<string>? callback, string line, string stream)
    {
        if (callback == null) return;
        try { callback(line); }
        catch (Exception ex) { LogService.Error("process", $"Streaming {stream} callback failed", ex); }
    }

    private static async Task<ProcessResult> RunStreamingAsync(string file, string arguments, TimeSpan? timeout,
        CancellationToken cancellationToken, Action<string> onStdOut, Action<string> onStdErr,
        StringBuilder stdout, StringBuilder stderr, bool logNonZeroExitAsError, string caller)
    {
        await FaultInjectionService.InjectAsync("process.run", cancellationToken).ConfigureAwait(false);
        // Bound concurrent child processes to avoid launch storms and redirected-pipe pressure.
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string resolvedFile = ResolveExecutable(file);
            string hardenedArguments = HardenArguments(resolvedFile, arguments);

            var startInfo = new ProcessStartInfo
            {
                    FileName = resolvedFile,
                    Arguments = hardenedArguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.IsPathRooted(resolvedFile) ? (Path.GetDirectoryName(resolvedFile) ?? AppContext.BaseDirectory) : AppContext.BaseDirectory
            };
            if (TrustedWindowsTools.Contains(Path.GetFileName(file)))
                HardenEnvironment(startInfo);
            using var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            var stdoutClosed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrClosed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) onStdOut(e.Data);
                else stdoutClosed.TrySetResult(null);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) onStdErr(e.Data);
                else stderrClosed.TrySetResult(null);
            };

            using var activity = DiagnosticsTelemetry.Start("process.run",
                ("file", Path.GetFileName(resolvedFile)), ("caller", caller));
            var started = Stopwatch.StartNew();
            try
            {
                if (!process.Start()) throw new InvalidOperationException($"Proces {file} se nepodařilo spustit.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                LogService.Error("process", $"START FAIL file={Path.GetFileName(file)}", ex);
                throw;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout.HasValue) linked.CancelAfter(timeout.Value);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                await DrainRedirectedStreamsAsync(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillTree(process);
                try
                {
                    using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
                    await DrainRedirectedStreamsAsync(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);
                }
                catch { }

                if (!cancellationToken.IsCancellationRequested && timeout.HasValue)
                    throw new TimeoutException($"{file} překročil timeout {timeout.Value.TotalSeconds:0}s.");
                throw;
            }

            started.Stop();
            int exitCode = process.ExitCode;
            activity?.SetTag("exit.code", exitCode);
            DiagnosticsTelemetry.RecordDuration("process.run", started.Elapsed.TotalMilliseconds,
                ("file", Path.GetFileName(resolvedFile)), ("caller", caller), ("exit", exitCode));
            if (exitCode != 0 && logNonZeroExitAsError)
            {
                string detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                if (detail.Length > 1200) detail = detail[..1200];
                LogService.Error("process", $"EXIT file={Path.GetFileName(file)} code={exitCode} ms={started.ElapsedMilliseconds} detail={detail.Trim()}");
            }
            else if (started.Elapsed > TimeSpan.FromSeconds(2))
            {
                LogService.Info("process", $"SLOW file={Path.GetFileName(file)} ms={started.ElapsedMilliseconds}");
            }

            string stdoutText = stdout.ToString();
            string stderrText = stderr.ToString();
            if (TelemetryEnabled())
            {
                int stdoutBytes = Encoding.UTF8.GetByteCount(stdoutText);
                int stderrBytes = Encoding.UTF8.GetByteCount(stderrText);
                string telemetryLine =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} PROCESS_TELEMETRY file={Path.GetFileName(resolvedFile)} caller={caller} ms={started.ElapsedMilliseconds} exit={exitCode} stdoutBytes={stdoutBytes} stderrBytes={stderrBytes}{Environment.NewLine}";
                try
                {
                    Directory.CreateDirectory(LogService.Root);
                    await TelemetryWriteGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await File.AppendAllTextAsync(
                            Path.Combine(LogService.Root, "process-telemetry.log"),
                            telemetryLine,
                            Encoding.UTF8).ConfigureAwait(false);
                    }
                    finally
                    {
                        TelemetryWriteGate.Release();
                    }
                }
                catch { }
            }

            return new ProcessResult(exitCode, stdoutText, stderrText);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    private static async Task DrainRedirectedStreamsAsync(Task stdoutClosed, Task stderrClosed)
    {
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await Task.WhenAll(stdoutClosed, stderrClosed).WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogService.Info("process", "Redirected stream drain timed out after process exit.");
        }
    }

    private static void TryKillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    public static void Open(string target)
    {
        using var started = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        if (started == null) throw new InvalidOperationException($"Nelze otevřít: {target}");
    }
}
