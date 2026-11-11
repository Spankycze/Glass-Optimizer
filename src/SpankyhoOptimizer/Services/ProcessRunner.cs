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

        // Trusted tool resolution never consults PATH and children always get a
        // hardened PATH. Verify both properties without mutating the
        // process-wide environment, which other release gates run concurrently.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var probe = new ProcessStartInfo { FileName = cmd, Arguments = "/D /C echo probe" };
        HardenEnvironment(probe);
        string expectedPath = string.Join(Path.PathSeparator, new[]
        {
            system32,
            windows,
            Path.Combine(system32, "Wbem"),
            expectedPowerShellDir
        });
        if (!probe.Environment.TryGetValue("PATH", out string? hardenedPath) ||
            !string.Equals(hardenedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Child process PATH was not hardened to trusted Windows directories.");

        var result = await RunAsync("cmd.exe", "/C echo SPANKY_PROCESS_HARDENING_OK", TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (!result.Success || !result.StdOut.Contains("SPANKY_PROCESS_HARDENING_OK", StringComparison.Ordinal))
            throw new InvalidOperationException("Trusted cmd.exe execution failed.");
    }

    internal static async Task<string> RunProcessQueueDeadlineSmokeAsync()
    {
        // Reserve every process slot without launching any child process.
        // A queued 120ms request must expire while waiting, not after a slot
        // eventually becomes available. Test also verifies caller cancellation.
        int acquired = 0;
        try
        {
            for (int i = 0; i < ProcessConcurrency; i++)
            {
                if (!await ProcessGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    throw new InvalidOperationException("Could not reserve all synthetic process slots.");
                acquired++;
            }

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Task<ProcessResult> pending = RunAsync(
                "__spanky_process_queue_never_launch__.exe", "",
                TimeSpan.FromMilliseconds(120), watchdog.Token);

            bool deadlineEnforced = false;
            try
            {
                await pending.WaitAsync(TimeSpan.FromMilliseconds(550)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // WaitAsync can time out before the underlying call, so check
                // that the call itself is finished rather than accepting it.
                if (pending.IsCompleted)
                {
                    try { await pending.ConfigureAwait(false); }
                    catch (TimeoutException) { deadlineEnforced = true; }
                }
            }
            if (!deadlineEnforced)
            {
                watchdog.Cancel();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                throw new InvalidOperationException("Process queue ignored the caller's 120ms timeout.");
            }

            using var cancellation = new CancellationTokenSource();
            Task<ProcessResult> cancelled = RunAsync(
                "__spanky_process_queue_never_launch__.exe", "",
                TimeSpan.FromSeconds(5), cancellation.Token);
            cancellation.Cancel();
            bool cancelledObserved = false;
            try { await cancelled.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelledObserved = true; }
            if (!cancelledObserved)
                throw new InvalidOperationException("Process queue failed to respect caller cancellation.");
        }
        finally
        {
            if (acquired > 0) ProcessGate.Release(acquired);
        }
        if (ProcessGate.CurrentCount != ProcessConcurrency)
            throw new InvalidOperationException("Timed-out process waiter corrupted the concurrency gate.");
        return $"PROCESS_QUEUE_DEADLINE_SMOKE_OK permits={ProcessConcurrency} timedOut=true externalCancel=true childProcesses=0";
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

            using Process? started = Process.Start(startInfo);
            return started != null;
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
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(effectiveTimeout);
        var startInfo = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(file) ?? AppContext.BaseDirectory
        };
        if (!string.IsNullOrWhiteSpace(verb))
            startInfo.Verb = verb;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Proces {file} se nepodařilo spustit.");
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            try
            {
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            }
            catch { }
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"{file} překročil timeout {effectiveTimeout.TotalSeconds:0}s.");
            throw;
        }
        return new ProcessResult(process.ExitCode, string.Empty, string.Empty);
    }

    // Keep the pre-environmentVariables signature for callers compiled against earlier releases.
    public static Task<ProcessResult> RunAsync(string file, string arguments, TimeSpan? timeout,
        CancellationToken cancellationToken, bool logNonZeroExitAsError, string caller) =>
        RunAsync(file, arguments, timeout, cancellationToken, logNonZeroExitAsError, caller, null);

    public static async Task<ProcessResult> RunAsync(string file, string arguments, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, bool logNonZeroExitAsError = true,
        [CallerMemberName] string caller = "", IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        return await RunStreamingAsync(file, arguments, timeout, cancellationToken,
            line => AppendBounded(stdout, line, MaxStdOutChars),
            line => AppendBounded(stderr, line, MaxStdErrChars), stdout, stderr, logNonZeroExitAsError, caller, environmentVariables).ConfigureAwait(false);
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
        StringBuilder stdout, StringBuilder stderr, bool logNonZeroExitAsError, string caller,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        // The timeout budget starts before queue admission, not after it.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue) linked.CancelAfter(timeout.Value);
        try
        {
            await FaultInjectionService.InjectAsync("process.run", linked.Token).ConfigureAwait(false);
            await ProcessGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                 timeout.HasValue && linked.IsCancellationRequested)
        {
            throw new TimeoutException($"{file} překročil timeout {timeout.Value.TotalSeconds:0}s (včetně fronty).");
        }

        try
        {
            string resolvedFile = ResolveExecutable(file);
            // Uses the same timeout token that admitted this process slot.

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
            if (environmentVariables != null)
                foreach (var pair in environmentVariables) startInfo.Environment[pair.Key] = pair.Value;
            using var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            var processExited = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => processExited.TrySetResult(null);
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

            try
            {
                // WaitForExitAsync also awaits pipe EOF, which descendants can keep open.
                await processExited.Task.WaitAsync(linked.Token).ConfigureAwait(false);
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
