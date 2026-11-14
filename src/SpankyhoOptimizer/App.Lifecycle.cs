using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task RequestPrimaryInstanceExitAsync()
    {
        if (TryAcquireSingleInstance())
        {
            ReleaseSingleInstance();
            return;
        }

        ReleaseSingleInstance();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SingleInstanceHandoffPath)!);
            await WriteSingleInstanceHandoffAsync(
                $"action=exit{Environment.NewLine}pid={Environment.ProcessId}{Environment.NewLine}version={AppVersion}{Environment.NewLine}utc={DateTime.UtcNow:O}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "Single-instance exit request write failed", ex);
            return;
        }

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (TryAcquireSingleInstance())
            {
                ReleaseSingleInstance();
                return;
            }
            ReleaseSingleInstance();
        }
    }

    private async Task WatchSingleInstanceHandoffAsync(Window window, System.Threading.CancellationToken token)
    {
        string directory = Path.GetDirectoryName(SingleInstanceHandoffPath)!;
        Directory.CreateDirectory(directory);

        try
        {
            using var signal = new System.Threading.SemaphoreSlim(0, 1);
            using var watcher = new FileSystemWatcher(directory, Path.GetFileName(SingleInstanceHandoffPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };

            void Signal()
            {
                try
                {
                    if (signal.CurrentCount == 0)
                        signal.Release();
                }
                catch (System.Threading.SemaphoreFullException) { }
            }

            FileSystemEventHandler onSignal = (_, _) => Signal();
            RenamedEventHandler onRename = (_, _) => Signal();
            watcher.Created += onSignal;
            watcher.Changed += onSignal;
            watcher.Renamed += onRename;
            watcher.EnableRaisingEvents = true;

            while (!token.IsCancellationRequested)
            {
                if (!File.Exists(SingleInstanceHandoffPath))
                {
                    await signal.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                string request;
                try
                {
                    request = await File.ReadAllTextAsync(SingleInstanceHandoffPath, token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(20, token).ConfigureAwait(false);
                    continue;
                }

                TryDeleteSingleInstanceHandoffRequest();
                bool exitRequested = request.Contains("action=exit", StringComparison.OrdinalIgnoreCase);
                LogService.Info("startup", $"SINGLE_INSTANCE_REQUEST_ACCEPTED exit={exitRequested} {request.Replace(Environment.NewLine, " ").Trim()}");

                if (exitRequested)
                {
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        try { window.Close(); } catch { }
                    });
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(750, System.Threading.CancellationToken.None).ConfigureAwait(false);
                        Environment.Exit(0);
                    }, System.Threading.CancellationToken.None);
                    return;
                }

                string? requestedSection = request
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(x => x.StartsWith("section=", StringComparison.OrdinalIgnoreCase))
                    ?["section=".Length..]
                    .Trim();

                window.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        window.Activate();
                        BringPrimaryWindowToFront(window);
                        if (!string.IsNullOrWhiteSpace(requestedSection) && window is MainWindow mainWindow)
                            SafeTaskService.Forget(mainWindow.NavigateExternalAsync(requestedSection), "navigation", "External section handoff");
                    }
                    catch { }
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "Single-instance handoff watcher failed", ex);
        }
    }

    private static void BringPrimaryWindowToFront(Window window)
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SwRestore);
            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out _);
            uint targetThread = GetWindowThreadProcessId(hwnd, out _);
            bool attached = foregroundThread != 0 && targetThread != 0 &&
                foregroundThread != targetThread && AttachThreadInput(foregroundThread, targetThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(foregroundThread, targetThread, false);
            }
            LogService.Info("startup", $"SINGLE_INSTANCE_WINDOW_FOREGROUNDED hwnd=0x{hwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "Single-instance foreground activation failed", ex);
        }
    }

    private static void TryDeleteSingleInstanceHandoffRequest()
    {
        try
        {
            if (File.Exists(SingleInstanceHandoffPath))
                File.Delete(SingleInstanceHandoffPath);
        }
        catch { }
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new System.Threading.Mutex(false, SingleInstanceMutexName);
            try
            {
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0);
            }
            catch (System.Threading.AbandonedMutexException)
            {
                _ownsSingleInstanceMutex = true;
            }
            return _ownsSingleInstanceMutex;
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "Single-instance guard initialization failed", ex);
            return true;
        }
    }

    private static async Task WriteSingleInstanceHandoffAsync(string request)
    {
        string temporaryPath = SingleInstanceHandoffPath + "." + Environment.ProcessId + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, request).ConfigureAwait(false);
            File.Move(temporaryPath, SingleInstanceHandoffPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }
        }
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex == null) return;
        try
        {
            if (_ownsSingleInstanceMutex)
                _singleInstanceMutex.ReleaseMutex();
        }
        catch { }
        finally
        {
            _ownsSingleInstanceMutex = false;
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    private static async Task<string> RunSingleInstanceHandoffWatcherSmokeAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SpankyhoOptimizer-HandoffSmoke-" + Guid.NewGuid().ToString("N"));
        string requestPath = Path.Combine(directory, "instance-handoff.request");
        Directory.CreateDirectory(directory);
        try
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(directory, Path.GetFileName(requestPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler onSignal = (_, _) => signal.TrySetResult(true);
            RenamedEventHandler onRename = (_, _) => signal.TrySetResult(true);
            watcher.Created += onSignal;
            watcher.Changed += onSignal;
            watcher.Renamed += onRename;

            await File.WriteAllTextAsync(requestPath, "handoff-smoke").ConfigureAwait(false);
            Task completed = await Task.WhenAny(signal.Task, Task.Delay(1500)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, signal.Task) || !signal.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("Handoff watcher did not observe the request file.");
            string request = await File.ReadAllTextAsync(requestPath).ConfigureAwait(false);
            if (!string.Equals(request, "handoff-smoke", StringComparison.Ordinal))
                throw new InvalidOperationException("Handoff request payload changed unexpectedly.");
            File.Delete(requestPath);
            if (File.Exists(requestPath))
                throw new InvalidOperationException("Handoff request file was not cleaned up.");
            return "SINGLE_INSTANCE_HANDOFF_WATCHER_SMOKE_OK eventDriven=yes cleanup=yes";
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static string RunSingleInstanceMutexSmoke()
    {
        string name = @"Local\SpankyhoOptimizer.SingleInstanceSmoke." + Environment.ProcessId;
        using var first = new System.Threading.Mutex(false, name);
        using var second = new System.Threading.Mutex(false, name);
        if (!first.WaitOne(0))
            throw new InvalidOperationException("Test mutex se nepodařilo získat první instancí.");

        bool secondAcquired = false;
        try
        {
            secondAcquired = Task.Run(() =>
            {
                try { return second.WaitOne(0); }
                catch (System.Threading.AbandonedMutexException) { return true; }
            }).GetAwaiter().GetResult();

            if (secondAcquired)
                throw new InvalidOperationException("Single-instance mutex dovolil souběžné druhé vlastnictví.");
            return "SINGLE_INSTANCE_MUTEX_SMOKE_OK blockedSecond=yes";
        }
        finally
        {
            if (secondAcquired)
            {
                try { second.ReleaseMutex(); } catch { }
            }
            try { first.ReleaseMutex(); } catch { }
        }
    }

    private static async Task WaitForSafeModeHandoffAsync(string[] cli)
    {
        string? handoffArg = cli.FirstOrDefault(x =>
            x.StartsWith("--handoff-pid=", StringComparison.OrdinalIgnoreCase));
        if (handoffArg == null) return;

        if (!int.TryParse(handoffArg["--handoff-pid=".Length..], out int previousPid) ||
            previousPid <= 0 || previousPid == Environment.ProcessId)
        {
            LogService.Info("startup", "SAFE_MODE_HANDOFF_INVALID");
            return;
        }

        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(previousPid);
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
            await previous.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            LogService.Info("startup", $"SAFE_MODE_HANDOFF_OK previousPid={previousPid}");
        }
        catch (ArgumentException)
        {
            LogService.Info("startup", $"SAFE_MODE_HANDOFF_ALREADY_EXITED previousPid={previousPid}");
        }
        catch (OperationCanceledException)
        {
            LogService.Info("startup", $"SAFE_MODE_HANDOFF_TIMEOUT previousPid={previousPid}");
        }
        catch (Exception ex)
        {
            LogService.Error("startup", $"Safe Mode handoff wait failed previousPid={previousPid}", ex);
        }
    }

    private static async Task RunLegacyShortcutOverlayMigrationAsync(System.Threading.CancellationToken token)
    {
        await Task.Delay(650, token).ConfigureAwait(false);
        await ShortcutAppearanceService.MigrateLegacyShortcutOverlayAsync(token).ConfigureAwait(false);
    }

    private static async Task RunWindowsBuildRevalidationAsync(System.Threading.CancellationToken token)
    {
        await Task.Delay(6000, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var state = WindowsBuildRevalidationService.CheckAndUpdate();
        if (state.Pending)
        {
            try
            {
                await TweakDriftWatchdogService.CheckAppliedTweaksAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) { LogService.Error("drift", "Windows Update drift watchdog failed", ex); }
        }
    }

    private static async Task RunHardwareChangeMonitorStartAsync(System.Threading.CancellationToken token)
    {
        await Task.Delay(5000, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        HardwareChangeMonitorService.Start();
    }

    private static async Task RunPendingTransactionRecoveryAsync(System.Threading.CancellationToken token)
    {
        await Task.Delay(3500, token).ConfigureAwait(false);
        try
        {
            int recovered = await WriteAheadTransactionService.RecoverPendingAsync(token)
                .ConfigureAwait(false);
            if (recovered > 0)
                LogService.Info("transaction",
                    $"WRITE_AHEAD_STARTUP_RECOVERY_OK recovered={recovered}");
        }
        finally
        {
            // RecoveryFailed and incomplete transactions are never silently
            // dismissed at the next startup. Reuse the existing transaction
            // error log; no new UI controls, notifications or Windows writes.
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var attention = WriteAheadTransactionService.ReadRecoveryAttention();
                    if (attention.Total > 0)
                        LogService.Error("transaction",
                            $"WRITE_AHEAD_MANUAL_REVIEW_REQUIRED total={attention.Total} " +
                            $"pending={attention.Pending} recoveryFailed={attention.RecoveryFailed} " +
                            $"rollbackFailed={attention.RollbackFailed} failed={attention.Failed} " +
                            $"pendingRestart={attention.PendingRestart} unreadable={attention.Unreadable}");
                }
                catch (Exception ex)
                {
                    LogService.Error("transaction",
                        "Cannot inspect unresolved write-ahead transactions", ex);
                }
            }
        }
    }
}
