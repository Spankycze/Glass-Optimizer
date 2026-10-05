using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private Window? _window;
    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private readonly System.Threading.CancellationTokenSource _lifetimeCts = new();
    private const string SingleInstanceMutexName = @"Local\SpankyhoOptimizer.MainGui.SingleInstance";
    private static readonly string SingleInstanceHandoffPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "instance-handoff.request");
    internal static readonly string AppVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    public App()
    {
        OpenTelemetryBootstrap.Initialize();
        StartupDiagnostics.Mark("App.Constructor");
        InitializeComponent();
        StartupDiagnostics.Mark("App.InitializeComponent");
        UnhandledException += (_, e) =>
        {
            LogService.Error("startup", $"Unhandled WinUI stage={StartupDiagnostics.LastStage}", e.Exception);
            CrashFingerprintService.Record(e.Exception, "WinUI", StartupDiagnostics.LastStage);
            CrashDumpService.TryWrite("WinUI-" + StartupDiagnostics.LastStage);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogService.Error("startup", $"Fatal AppDomain stage={StartupDiagnostics.LastStage}", ex);
            CrashFingerprintService.Record(ex, "AppDomain", StartupDiagnostics.LastStage);
            CrashDumpService.TryWrite("AppDomain-" + StartupDiagnostics.LastStage);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogService.Error("startup", "Unobserved task exception", e.Exception);
            CrashFingerprintService.Record(e.Exception, "TaskScheduler", StartupDiagnostics.LastStage);
            e.SetObserved();
        };
        StartupDiagnostics.Mark("App.CrashHooksReady");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Mark("OnLaunched.Enter");
        var launchTask = LaunchGuardedAsync();
        StartupDiagnostics.Mark("OnLaunched.TaskCreated");
        SafeTaskService.ForgetDeferred(launchTask, "startup", "Application launch");
        StartupDiagnostics.Mark("OnLaunched.ForgetQueued");
    }

    private async Task LaunchGuardedAsync()
    {
        StartupDiagnostics.Mark("LaunchGuarded.Enter");
        string id = ErrorService.NewCorrelationId();
        StartupDiagnostics.Mark("LaunchGuarded.CorrelationReady");
        StartupDiagnostics.Mark("LaunchGuarded.ScopeDeferred");
        try { await LaunchCoreAsync(id); }
        catch (Exception ex)
        {
            LogService.Error("startup", $"Launch failed stage={StartupDiagnostics.LastStage} id={id}", ex);
            CrashDumpService.TryWrite("Launch-" + id);
            Environment.Exit(70);
        }
    }

    private async Task LaunchCoreAsync(string startupCorrelationId)
    {
        StartupDiagnostics.Mark("LaunchCore.Start");
        StartupDiagnostics.LogEnvironment(AppVersion);
        StartupDiagnostics.Mark("CommandLine");
        string[] cli = Environment.GetCommandLineArgs();

        if (HasArg(cli, "--fan-agent"))
        {
            await FanProfileService.RunAgentAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--fan-watchdog"))
        {
            await FanProfileService.RunWatchdogAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--request-exit"))
        {
            await RequestPrimaryInstanceExitAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--provision-hardware-bridge"))
        {
            string protectedRuntimeHost = Path.Combine(
                AppContext.BaseDirectory,
                "SpankyhoOptimizer.RuntimeHost.exe");
            HardwareTelemetryBridgeService.EnsureRegisteredBestEffort(protectedRuntimeHost);
            bool bridgeReady = false;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (HardwareTelemetryBridgeService.TryReadFreshCurrentVersion(TimeSpan.FromSeconds(8)) != null)
                {
                    bridgeReady = true;
                    break;
                }
                await Task.Delay(200);
            }
            if (bridgeReady)
                HardwareTelemetryBridgeService.CleanupLegacyStagedCopiesBestEffort();

            Environment.Exit(bridgeReady ? 0 : 42);
            return;
        }

        bool startupHotPathSmoke = HasArg(cli, "--startup-hotpath-smoke");
        bool startupIsolatedBundle = HasArg(cli, "--startup-isolated-smoke-bundle");
        bool runtimeReadyProbe = cli.Any(x => x.StartsWith("--runtime-ready-file=", StringComparison.OrdinalIgnoreCase));
        bool safeModeStartup = HasArg(cli, "--safe-mode");
        bool openSectionLaunch = cli.Any(x => x.StartsWith("--open-section=", StringComparison.OrdinalIgnoreCase));
        if (safeModeStartup)
            await WaitForSafeModeHandoffAsync(cli);
        bool normalGuiLaunch = cli.Length <= 1 || safeModeStartup || openSectionLaunch;
        if (normalGuiLaunch)
        {
            FanProfileService.EnsureStartupAgentRegistered();
            FanProfileService.EnsureAgentUpdated();
            FanProfileService.StartAgentIfNeeded();
        }

        if (normalGuiLaunch && !await AcquireSingleInstanceWithHandoffAsync())
        {
            LogService.Info("startup", "SECOND_INSTANCE_HANDOFF_FAILED");
            Environment.Exit(0);
            return;
        }

        if (cli.Length <= 1 || startupHotPathSmoke || startupIsolatedBundle || runtimeReadyProbe || safeModeStartup || openSectionLaunch)
        {
            await LaunchNormalStartupAsync(cli, startupHotPathSmoke || startupIsolatedBundle, startupCorrelationId);
            return;
        }

        await LaunchCoreLegacyAsync();
    }

    private async Task LaunchNormalStartupAsync(string[] cli, bool smokeMode, string startupCorrelationId)
    {
        if (!smokeMode && HasArg(cli, "--safe-mode"))
            CrashLoopGuardService.ForceSafeMode();

        Task<bool>? crashLoopStartupTask = !smokeMode
            ? Task.Run(() => CrashLoopGuardService.BeginStartup(AppVersion))
            : null;

        bool runtimeReadyProbe = cli.Any(x => x.StartsWith("--runtime-ready-file=", StringComparison.OrdinalIgnoreCase));
        if (!smokeMode && !runtimeReadyProbe && !_ownsSingleInstanceMutex && !await AcquireSingleInstanceWithHandoffAsync())
        {
            LogService.Info("startup", "SECOND_INSTANCE_HANDOFF_FAILED_BEFORE_WINDOW");
            Environment.Exit(0);
            return;
        }

        StartupDiagnostics.Mark("MainWindow.Create");
        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            try { (_window as MainWindow)?.ShutdownActiveOperations(); } catch { }
            try { _lifetimeCts.Cancel(); } catch { }
            HardwareChangeMonitorService.Stop();
            ReleaseSingleInstance();
        };
        StartupDiagnostics.Mark("MainWindow.Created");

        if (crashLoopStartupTask != null)
            await crashLoopStartupTask;

        _window.Activate();
        if (!smokeMode && !runtimeReadyProbe)
        {
            SafeTaskService.Forget(
                WatchSingleInstanceHandoffAsync(_window, _lifetimeCts.Token),
                "startup",
                "Single-instance handoff watcher");
        }
        StartupDiagnostics.Mark("Window.Activated");
        long windowActivatedMs = StartupDiagnostics.ElapsedMilliseconds;

        if (smokeMode)
        {
            using var startupSmokeScope = LogService.BeginScope("startup", startupCorrelationId);
            if (windowActivatedMs > 750)
                throw new InvalidOperationException($"Startup hot-path regression: Window.Activated at {windowActivatedMs} ms.");
            LogService.Info($"STARTUP_HOTPATH_SMOKE_OK activatedMs={windowActivatedMs}");
            StartupDiagnostics.Complete(synchronousFlush: true);

            if (HasArg(cli, "--startup-isolated-smoke-bundle"))
            {
                if (_window is not MainWindow startupBundleMw)
                    throw new InvalidOperationException("Startup isolated smoke bundle has no MainWindow.");

                var bundleSw = System.Diagnostics.Stopwatch.StartNew();
                await startupBundleMw.RunStartupOverviewVisualSmokeAsync();
                await startupBundleMw.RunDeferredTitlebarSmokeAsync();
                await startupBundleMw.RunLazyActionBarSmokeAsync();
                LogService.Info("startup", $"ACTIONBAR_LAZY_SMOKE_OK bundleMs={bundleSw.ElapsedMilliseconds}");
                LogService.Info("startup", "STARTUP_ISOLATED_SMOKE_BUNDLE_OK count=4");
            }

            Environment.Exit(0);
            return;
        }

        StartupDiagnostics.Complete();
        using var startupScope = LogService.BeginScope("startup", startupCorrelationId);
        CrashLoopGuardService.CompleteStartup(AppVersion);

        // Maintenance/prefetch work is not required for the first usable window.
        // Start it shortly after Ready so it cannot compete with activation/first paint.
        SafeTaskService.ForgetDeferred(
            RunPostReadyStartupMaintenanceAsync(_lifetimeCts.Token),
            "startup",
            "Post-ready startup maintenance");
        LogService.Info("startup", "Startup background prefetch disabled; heavy modules load on demand.");

        string? runtimeReadyFile = cli
            .FirstOrDefault(x => x.StartsWith("--runtime-ready-file=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--runtime-ready-file=".Length)
            .Trim('"');
        if (string.IsNullOrWhiteSpace(runtimeReadyFile))
            runtimeReadyFile = Environment.GetEnvironmentVariable("SPANKY_RUNTIME_READY_FILE");
        if (!string.IsNullOrWhiteSpace(runtimeReadyFile))
        {
            try
            {
                string? readyDir = Path.GetDirectoryName(runtimeReadyFile);
                if (!string.IsNullOrWhiteSpace(readyDir)) Directory.CreateDirectory(readyDir);
                await File.WriteAllTextAsync(runtimeReadyFile,
                    $"version={AppVersion}{Environment.NewLine}activatedMs={windowActivatedMs}{Environment.NewLine}pid={Environment.ProcessId}{Environment.NewLine}");
                LogService.Info("startup", $"RUNTIME_READY_SIGNAL_OK activatedMs={windowActivatedMs}");
                if (cli.Any(x => x.StartsWith("--runtime-ready-file=", StringComparison.OrdinalIgnoreCase)))
                {
                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogService.Error("startup", "Runtime readiness signal failed", ex);
            }
        }
    }

    private async Task RunPostReadyStartupMaintenanceAsync(System.Threading.CancellationToken token)
    {
        await Task.Delay(200, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        int removedDataRoots = DataRootCleanupService.CleanupUserDataRoots();
        if (removedDataRoots > 0)
            LogService.Info("startup", $"DATA_ROOT_CLEANUP_OK removed={removedDataRoots}");

        SafeTaskService.Forget(RunLegacyShortcutOverlayMigrationAsync(token), "startup", "Legacy shortcut overlay migration");
        SafeTaskService.Forget(RunWindowsBuildRevalidationAsync(token), "startup", "Windows build revalidation");
        SafeTaskService.Forget(RunHardwareChangeMonitorStartAsync(token), "startup", "Hardware change monitor start");
        SafeTaskService.Forget(RunPendingTransactionRecoveryAsync(token), "startup", "Pending transaction recovery");
        SafeTaskService.Forget(IdlePrewarmService.RunAsync(token), "startup", "Idle prewarm");
    }

    private async Task<bool> AcquireSingleInstanceWithHandoffAsync()
    {
        if (TryAcquireSingleInstance())
        {
            TryDeleteSingleInstanceHandoffRequest();
            return true;
        }

        ReleaseSingleInstance();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SingleInstanceHandoffPath)!);
            string? openSection = Array.Find(
                Environment.GetCommandLineArgs(),
                x => x.StartsWith("--open-section=", StringComparison.OrdinalIgnoreCase));
            string sectionLine = openSection == null
                ? string.Empty
                : $"section={openSection.Substring("--open-section=".Length).Trim()}{Environment.NewLine}";

            await File.WriteAllTextAsync(
                SingleInstanceHandoffPath,
                $"action=activate{Environment.NewLine}{sectionLine}pid={Environment.ProcessId}{Environment.NewLine}version={AppVersion}{Environment.NewLine}utc={DateTime.UtcNow:O}{Environment.NewLine}");
            LogService.Info("startup", "SECOND_INSTANCE_ACTIVATE_REQUESTED" + (openSection == null ? string.Empty : " section"));
        }
        catch (Exception ex)
        {
            LogService.Error("startup", "Single-instance activation request write failed", ex);
        }

        // The first instance always stays alive. A secondary launch only signals it
        // to come to the foreground and then exits; it never waits to steal the mutex.
        return false;
    }

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
            await File.WriteAllTextAsync(
                SingleInstanceHandoffPath,
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
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
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
        int recovered = await WriteAheadTransactionService.RecoverPendingAsync(token).ConfigureAwait(false);
        if (recovered > 0)
            LogService.Info("transaction", $"WRITE_AHEAD_STARTUP_RECOVERY_OK recovered={recovered}");
    }

    private async Task LaunchCoreLegacyAsync()
    {
        // LaunchCoreAsync already captured environment + command line once. Keep the legacy
        // command dispatcher, but do not repeat startup diagnostics or allocate the args again.
        string[] cli = Environment.GetCommandLineArgs();

        if (await WorkerHostService.TryHandleAsync(cli))
        {
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--release-service-smoke"))
        {
            int code = await RunReleaseServiceSmokeBundleAsync();
            Environment.Exit(code);
            return;
        }

        if (HasArg(cli, "--crashloop-guard-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _ = CrashLoopGuardService.BeginStartup(AppVersion);
                long firstBeginMs = sw.ElapsedMilliseconds;
                sw.Restart();
                CrashLoopGuardService.CompleteStartup(AppVersion);
                long firstCompleteMs = sw.ElapsedMilliseconds;

                sw.Restart();
                _ = CrashLoopGuardService.BeginStartup(AppVersion);
                long hotBeginMs = sw.ElapsedMilliseconds;
                sw.Restart();
                CrashLoopGuardService.CompleteStartup(AppVersion);
                long hotCompleteMs = sw.ElapsedMilliseconds;

                LogService.Info(
                    $"CRASHLOOP_GUARD_SMOKE_OK firstBeginMs={firstBeginMs} firstCompleteMs={firstCompleteMs} hotBeginMs={hotBeginMs} hotCompleteMs={hotCompleteMs}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("CRASHLOOP_GUARD_SMOKE_FAIL", ex);
                Environment.Exit(67);
            }
            return;
        }

        if (HasArg(cli, "--crash-dump-smoke"))
        {
            string? dump = CrashDumpService.TryWrite("smoke");
            if (string.IsNullOrWhiteSpace(dump) || !File.Exists(dump) || new FileInfo(dump).Length < 4096)
            {
                LogService.Error("CRASH_DUMP_SMOKE_FAIL");
                Environment.Exit(53);
                return;
            }
            long bytes = new FileInfo(dump).Length;
            try { File.Delete(dump); } catch { }
            LogService.Info($"CRASH_DUMP_SMOKE_OK bytes={bytes}");
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--hardware-identity-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fresh = await SystemInfoService.GetSnapshotAsync(forceRefresh: true);
                long freshMs = sw.ElapsedMilliseconds;
                sw.Restart();
                var cached = await SystemInfoService.GetSnapshotAsync(forceRefresh: false);
                long cachedMs = sw.ElapsedMilliseconds;

                if (string.IsNullOrWhiteSpace(fresh.Cpu) || fresh.Cpu.Contains("neuvedeno", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CPU identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.Motherboard) || fresh.Motherboard.Contains("neuvedena", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Motherboard identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.RamHardware) || fresh.RamHardware.Contains("neuveden", StringComparison.OrdinalIgnoreCase) ||
                    !fresh.RamHardware.Contains("GB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RAM hardware identity is missing.");
                if (!string.Equals(fresh.Cpu, cached.Cpu, StringComparison.Ordinal) ||
                    !string.Equals(fresh.Motherboard, cached.Motherboard, StringComparison.Ordinal) ||
                    !string.Equals(fresh.RamHardware, cached.RamHardware, StringComparison.Ordinal))
                    throw new InvalidOperationException("Hardware identity cache mismatch.");
                if (cachedMs > 20)
                    throw new InvalidOperationException($"Hardware identity cached lookup is too slow: {cachedMs} ms.");

                LogService.Info($"HARDWARE_IDENTITY_SMOKE_OK freshMs={freshMs} cachedMs={cachedMs} cpu={fresh.Cpu} board={fresh.Motherboard} ram={fresh.RamHardware}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("HARDWARE_IDENTITY_SMOKE_FAIL", ex); Environment.Exit(65); }
            return;
        }

        if (HasArg(cli, "--provider-degradation-smoke"))
        {
            var mem = CatalogService.GetTweaks("memory").First(x => x.Id.Equals("mem.compression", StringComparison.OrdinalIgnoreCase));
            var caps = new WindowsCapabilitySnapshot(Environment.OSVersion.Version.Build, "smoke", true, false, true, true, true, new[] { "PS_MMAgent" });
            var check = TweakCompatibilityService.CheckStatic(mem, caps);
            if (check.Supported)
            {
                LogService.Error("PROVIDER_DEGRADATION_SMOKE_FAIL");
                Environment.Exit(54);
                return;
            }
            LogService.Info("PROVIDER_DEGRADATION_SMOKE_OK reason=" + check.Reason);
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--gpu-detection-smoke"))
        {
            try
            {
                var coldSw = System.Diagnostics.Stopwatch.StartNew();
                var cold = await GpuDriverService.DetectPrimaryGpuAsync(forceRefresh: true);
                if (cold == null) throw new InvalidOperationException("GPU not detected on cold refresh.");
                long coldMs = coldSw.ElapsedMilliseconds;

                var warmSw = System.Diagnostics.Stopwatch.StartNew();
                var warm = await GpuDriverService.DetectPrimaryGpuAsync(forceRefresh: true);
                if (warm == null) throw new InvalidOperationException("GPU not detected on warm refresh.");
                long warmMs = warmSw.ElapsedMilliseconds;

                LogService.Info($"GPU_DETECTION_SMOKE_OK coldMs={coldMs} warmMs={warmMs} gpu={warm.Name} version={warm.DisplayVersion}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("GPU_DETECTION_SMOKE_FAIL", ex);
                Environment.Exit(67);
            }
            return;
        }

        if (HasArg(cli, "--gpu-driver-smoke"))
        {
            try
            {
                var gpu = await GpuDriverService.DetectPrimaryGpuAsync();
                if (gpu == null) throw new InvalidOperationException("GPU not detected");
                var official = await GpuDriverService.CheckOfficialAsync(gpu);
                var ex = await GpuDriverService.CheckExostenzaAsync();
                if (ex != null) ex = await GpuDriverService.ResolveExostenzaDownloadAsync(ex);
                LogService.Info("GPU_DRIVER_SMOKE_OK | " + gpu.Name + " | installed=" + gpu.DisplayVersion +
                    " | official=" + (official?.Version ?? "n/a") + " | exostenza=" + (ex?.Version ?? "n/a") +
                    " | direct=" + (string.IsNullOrWhiteSpace(ex?.DownloadUrl) ? "no" : "yes"));
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GPU_DRIVER_SMOKE_FAIL", ex); Environment.Exit(41); }
            return;
        }

        if (HasArg(cli, "--single-instance-smoke"))
        {
            try
            {
                LogService.Info(RunSingleInstanceMutexSmoke());
                LogService.Info(await RunSingleInstanceHandoffWatcherSmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SINGLE_INSTANCE_SMOKE_FAIL", ex); Environment.Exit(41); }
            return;
        }

        if (HasArg(cli, "--exostenza-cache-reuse-smoke"))
        {
            try
            {
                LogService.Info(GpuDriverService.RunExostenzaCacheReuseSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("EXOSTENZA_CACHE_REUSE_SMOKE_FAIL", ex); Environment.Exit(41); }
            return;
        }

        if (HasArg(cli, "--shell-setup-idempotency-smoke"))
        {
            try
            {
                LogService.Info(PowerShellCenterService.RunProfileIdempotencySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SHELL_SETUP_IDEMPOTENCY_SMOKE_FAIL", ex); Environment.Exit(41); }
            return;
        }

        if (HasArg(cli, "--startup-delta-smoke"))
        {
            try
            {
                LogService.Info(StartupManagerService.RunDeltaSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STARTUP_DELTA_SMOKE_FAIL", ex); Environment.Exit(41); }
            return;
        }

        if (HasArg(cli, "--dns-readback-guard-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string result = DnsProfileService.RunReadbackGuardSmoke();
                sw.Stop();
                LogService.Info(result + $" elapsedMs={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DNS_READBACK_GUARD_SMOKE_FAIL", ex); Environment.Exit(41); }
            return;
        }

        if (HasArg(cli, "--dns-benchmark-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = await DnsBenchmarkService.RunSmokeAsync(cts.Token);
                var alive = results.Where(x => x.IsAlive).ToArray();
                if (results.Count < 8) throw new InvalidOperationException($"DNS benchmark returned too few smoke resolvers: {results.Count}.");
                if (alive.Length == 0) throw new InvalidOperationException("DNS benchmark received no resolver responses.");
                if (alive.Any(x => x.AverageMs <= 0 || x.Successful <= 0)) throw new InvalidOperationException("DNS benchmark returned invalid latency data.");
                LogService.Info($"DNS_BENCHMARK_SMOKE_OK total={results.Count} alive={alive.Length} best={alive[0].Name} avgMs={alive[0].AverageMs:0.0} elapsedMs={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DNS_BENCHMARK_SMOKE_FAIL", ex); Environment.Exit(74); }
            return;
        }

        if (HasArg(cli, "--dns-catalog-update-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await DnsResolverCatalogService.UpdateAsync(cts.Token);
                if (result.Count < 300 || result.UdpTcp < 100 || result.Doh < 50 || result.Dot < 30)
                    throw new InvalidOperationException($"DNS catalog update returned suspicious counts: {result}.");
                LogService.Info($"DNS_CATALOG_UPDATE_SMOKE_OK total={result.Count} udp={result.UdpTcp} doh={result.Doh} dot={result.Dot} doq={result.Doq} elapsedMs={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DNS_CATALOG_UPDATE_SMOKE_FAIL", ex); Environment.Exit(75); }
            return;
        }

        if (HasArg(cli, "--process-runner-bench"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                var samples = new System.Collections.Generic.List<long>(20);
                for (int i = 0; i < 20; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await ProcessRunner.RunAsync(
                        "cmd.exe",
                        "/C echo SPANKY_PROCESS_BENCH",
                        TimeSpan.FromSeconds(5),
                        cts.Token);
                    sw.Stop();
                    if (!result.Success || !result.StdOut.Contains("SPANKY_PROCESS_BENCH", StringComparison.Ordinal))
                        throw new InvalidOperationException("ProcessRunner bench child failed.");
                    samples.Add(sw.ElapsedMilliseconds);
                }

                samples.Sort();
                double median = samples.Count % 2 == 0
                    ? (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2d
                    : samples[samples.Count / 2];
                long p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * 0.95) - 1)];
                LogService.Info("process",
                    $"PROCESS_RUNNER_BENCH_OK launches={samples.Count} medianMs={median:0.0} p95Ms={p95} maxMs={samples[^1]}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("process", "PROCESS_RUNNER_BENCH_FAIL", ex);
                Environment.Exit(41);
            }
            return;
        }

        if (HasArg(cli, "--process-hardening-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await ProcessRunner.RunHardeningSmokeAsync(cts.Token);
                LogService.Info("PROCESS_HARDENING_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_HARDENING_SMOKE_FAIL", ex); Environment.Exit(68); }
            return;
        }

        if (HasArg(cli, "--write-ahead-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WriteAheadTransactionService.RunPersistenceSmokeAsync(cts.Token);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WRITE_AHEAD_TRANSACTION_SMOKE_FAIL", ex); Environment.Exit(69); }
            return;
        }

        if (HasArg(cli, "--worker-ipc-cleanup-smoke"))
        {
            try
            {
                WorkerClientService.RunIpcCleanupSmoke();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WORKER_IPC_CLEANUP_SMOKE_FAIL", ex); Environment.Exit(70); }
            return;
        }

        if (HasArg(cli, "--bad-tweak-repair-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
                await SystemRepairDiagnosticsService.RepairBadTweaksAsync(cts.Token);
                var report = await SystemRepairDiagnosticsService.ScanAsync(cts.Token);
                var bad = report.FirstOrDefault(x => x.Area == "Škodlivé / legacy tweaky");
                if (bad == null || !bad.Healthy)
                    throw new InvalidOperationException("Bad tweak repair read-back failed: " + bad?.Detail);
                LogService.Info("BAD_TWEAK_REPAIR_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("BAD_TWEAK_REPAIR_SMOKE_FAIL", ex); Environment.Exit(72); }
            return;
        }

        if (HasArg(cli, "--update-retention-smoke"))
        {
            try
            {
                AppUpdateService.RunRetentionSmoke();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("UPDATE_RETENTION_SMOKE_FAIL", ex); Environment.Exit(71); }
            return;
        }

        if (HasArg(cli, "--startup-inventory-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                StartupManagerService.InvalidateCache();
                int loadsBefore = StartupManagerService.LoadCountForDiagnostics;
                var first = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var second = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var pair = await Task.WhenAll(first, second).WaitAsync(cts.Token);
                var items = pair[0];
                int loads = StartupManagerService.LoadCountForDiagnostics - loadsBefore;
                if (loads != 1)
                    throw new InvalidOperationException($"Startup inventory single-flight failed: loads={loads}.");
                if (!ReferenceEquals(pair[0], pair[1]) && !pair[0].SequenceEqual(pair[1]))
                    throw new InvalidOperationException("Startup inventory concurrent results differ.");
                if (items.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                    throw new InvalidOperationException("Startup inventory contains duplicate IDs.");
                if (items.Any(x => x.SourceKind is not ("Registry" or "StartupFolder" or "ScheduledTask")))
                    throw new InvalidOperationException("Startup inventory contains unknown source type.");

                StartupManagerService.ClearMemoryCacheForSmoke();
                var persistentSw = System.Diagnostics.Stopwatch.StartNew();
                var persisted = StartupManagerService.TryGetFreshSnapshot();
                persistentSw.Stop();
                if (persisted == null || persisted.Count != items.Count)
                    throw new InvalidOperationException("Startup persistent snapshot did not hydrate the inventory.");
                if (!StartupManagerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Startup persistent snapshot did not request a background refresh.");
                if (persistentSw.ElapsedMilliseconds > 100)
                    throw new InvalidOperationException($"Startup persistent snapshot too slow: {persistentSw.ElapsedMilliseconds} ms.");

                int taskScansBeforeBackground = TaskSchedulerService.ScanCountForDiagnostics;
                var backgroundSw = System.Diagnostics.Stopwatch.StartNew();
                var refreshed = await StartupManagerService.GetAsync(
                    forceRefresh: true,
                    forceTaskSchedulerRefresh: false).WaitAsync(cts.Token);
                backgroundSw.Stop();
                int taskScansAfterBackground = TaskSchedulerService.ScanCountForDiagnostics;
                if (taskScansAfterBackground != taskScansBeforeBackground)
                    throw new InvalidOperationException(
                        $"Startup background refresh unexpectedly rescanned Task Scheduler: before={taskScansBeforeBackground} after={taskScansAfterBackground}.");
                if (refreshed.Count != items.Count)
                    throw new InvalidOperationException(
                        $"Startup background refresh count mismatch: fresh={items.Count} refreshed={refreshed.Count}.");
                if (StartupManagerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Startup background refresh did not clear refresh-pending state.");

                LogService.Info($"STARTUP_INVENTORY_SMOKE_OK total={items.Count} registry={items.Count(x => x.SourceKind == "Registry")} folders={items.Count(x => x.SourceKind == "StartupFolder")} tasks={items.Count(x => x.SourceKind == "ScheduledTask")} loads={loads} persistentMs={persistentSw.ElapsedMilliseconds} backgroundMs={backgroundSw.ElapsedMilliseconds} taskRescans={taskScansAfterBackground - taskScansBeforeBackground}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STARTUP_INVENTORY_SMOKE_FAIL", ex); Environment.Exit(70); }
            return;
        }

        if (HasArg(cli, "--drift-watchdog-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
                var report = await TweakDriftWatchdogService.CheckAppliedTweaksAsync(cts.Token);
                LogService.Info($"TWEAK_DRIFT_WATCHDOG_SMOKE_OK checked={report.Checked} drifted={report.Drifted.Length}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TWEAK_DRIFT_WATCHDOG_SMOKE_FAIL", ex); Environment.Exit(71); }
            return;
        }

        if (HasArg(cli, "--power-profile-smoke"))
        {
            await RunBackendSmokeAsync("POWER_PROFILE", 31, PerformanceProfilesService.ApplyPerformancePowerProfileAsync);
            return;
        }

        if (HasArg(cli, "--power-registry-prewarm-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                PowerPlanStateService.InvalidateCache();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool warmed = await PowerPlanStateService.TryPrewarmRegistrySnapshotAsync(cts.Token);
                long warmMs = sw.ElapsedMilliseconds;
                if (!warmed || PowerPlanStateService.TryGetFreshSnapshot() == null)
                    throw new InvalidOperationException("Power registry prewarm did not publish a snapshot.");
                sw.Restart();
                var cached = await PowerPlanStateService.ReadAsync(cts.Token);
                long readMs = sw.ElapsedMilliseconds;
                if (cached.Settings.Count == 0)
                    throw new InvalidOperationException("Power registry prewarm returned an empty settings snapshot.");
                LogService.Info($"POWER_REGISTRY_PREWARM_SMOKE_OK warmMs={warmMs} cachedReadMs={readMs} settings={cached.Settings.Count}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("POWER_REGISTRY_PREWARM_SMOKE_FAIL", ex); Environment.Exit(56); }
            return;
        }

        if (HasArg(cli, "--power-plan-guard-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var snapshot = await PowerPlanStateService.ReadAsync(cts.Token, forceRefresh: true);
                if (string.IsNullOrWhiteSpace(snapshot.ActiveGuid) ||
                    !snapshot.ActivePlan.Contains("Spankyho Performance", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Power Plan guard smoke vyžaduje aktivní Spankyho Performance profil.");
                await PowerPlanGuardService.EnsureAsync(snapshot.ActiveGuid, cts.Token);
                string? guarded = await PowerPlanGuardService.TryGetConfiguredGuidAsync(cts.Token);
                if (!string.Equals(snapshot.ActiveGuid, guarded, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Power Plan guard GUID mismatch active={snapshot.ActiveGuid} guard={guarded ?? "none"}");
                var guardStatus = await PowerPlanGuardService.ReadStatusAsync(snapshot.ActiveGuid, cts.Token);
                if (!guardStatus.LogonConfigured || !guardStatus.BootConfigured)
                    throw new InvalidOperationException($"Power Plan Boot/Logon guard mismatch boot={guardStatus.BootConfigured} logon={guardStatus.LogonConfigured}");

                await PowerPlanGuardService.DisableAsync(cts.Token);
                if (await PowerPlanGuardService.TryGetConfiguredGuidAsync(cts.Token) != null)
                    throw new InvalidOperationException("Power Plan guard delete smoke failed.");

                await PowerPlanGuardService.EnsureAsync(snapshot.ActiveGuid, cts.Token);
                guarded = await PowerPlanGuardService.TryGetConfiguredGuidAsync(cts.Token);
                if (!string.Equals(snapshot.ActiveGuid, guarded, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Power Plan guard recreate smoke failed.");
                if (!File.Exists(PowerPlanGuardService.GuardScriptPath))
                    throw new InvalidOperationException("Power Plan guard script po recreate chybí.");

                await PowerPlanGuardService.RunNowAsync(cts.Token);
                await Task.Delay(1200, cts.Token);
                var afterRun = await PowerPlanStateService.ReadAsync(cts.Token, forceRefresh: true);
                if (!string.Equals(afterRun.ActiveGuid, snapshot.ActiveGuid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Power Plan guard run změnil/neudržel aktivní GUID. expected={snapshot.ActiveGuid} actual={afterRun.ActiveGuid ?? "none"}");
                foreach (string id in new[] { "disk", "display", "sleep", "hibernate" })
                {
                    var setting = afterRun.Settings.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (setting?.IsApplied != true)
                        throw new InvalidOperationException($"Power Plan guard critical read-back selhal: {id}={setting?.CurrentText ?? "missing"}");
                }

                guardStatus = await PowerPlanGuardService.ReadStatusAsync(snapshot.ActiveGuid, cts.Token);
                if (!guardStatus.LogonConfigured || !guardStatus.BootConfigured || !guardStatus.LastRunOk)
                    throw new InvalidOperationException($"Power Plan guard final status selhal boot={guardStatus.BootConfigured} logon={guardStatus.LogonConfigured} last={guardStatus.LastResult}");

                LogService.Info($"POWER_PLAN_GUARD_SMOKE_OK guid={guarded} boot=ok logon=ok deleteRecreate=ok runNow=ok activeReadback=ok criticalNever=4");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("POWER_PLAN_GUARD_SMOKE_FAIL", ex); Environment.Exit(56); }
            return;
        }

        if (HasArg(cli, "--realtek-summary-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string summary = await WindowsActions.GetRealtekSummaryAsync(cts.Token);
                sw.Stop();
                if (string.IsNullOrWhiteSpace(summary))
                    throw new InvalidOperationException("Realtek summary je prázdné.");
                LogService.Info("network", $"REALTEK_SUMMARY_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} chars={summary.Length}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("network", "REALTEK_SUMMARY_SMOKE_FAIL", ex);
                Environment.Exit(88);
            }
            return;
        }

        if (HasArg(cli, "--nic-capability-smoke"))
        {
            try
            {
                PerformanceProfilesService.InvalidateNicCache();
                NativeNicTuningService.PrewarmReadScope();
                NativeNicTuningService.ValidateProviderReadStateClassificationForSmoke();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var caps = await PerformanceProfilesService.GetNicCapabilitiesAsync(cts.Token);
                sw.Stop();
                string adapter = caps.TryGetValue("Adapter", out var adapterName) ? adapterName : string.Empty;
                if (string.IsNullOrWhiteSpace(adapter))
                    throw new InvalidOperationException("NIC capability smoke nenašel aktivní adaptér.");
                foreach (string key in new[] { "Interrupt Moderation", "Flow Control", "Packet Coalescing", "RSS", "RSC", "Selective Suspend" })
                    if (!caps.ContainsKey(key))
                        throw new InvalidOperationException("NIC capability smoke chybí položka: " + key);

                LogService.Info("nic", $"NIC_CAPABILITY_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} adapter={adapter} interrupt={caps["Interrupt Moderation"]} flow={caps["Flow Control"]} packet={caps["Packet Coalescing"]} rss={caps["RSS"]} rsc={caps["RSC"]} selective={caps["Selective Suspend"]}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("nic", "NIC_CAPABILITY_SMOKE_FAIL", ex);
                Environment.Exit(63);
            }
            return;
        }

        if (HasArg(cli, "--nic-smoke"))
        {
            await RunBackendSmokeAsync("NIC", 42, PerformanceProfilesService.ApplyNicPerformanceProfileAsync);
            return;
        }
        if (HasArg(cli, "--storage-probe-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var disks = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (disks.Count == 0)
                    throw new InvalidOperationException("Storage probe returned no disks.");
                if (disks.Any(x => string.IsNullOrWhiteSpace(x.Name)))
                    throw new InvalidOperationException("Storage probe returned a disk without a name.");
                if (disks.All(x =>
                        string.IsNullOrWhiteSpace(x.DriverProvider) ||
                        string.IsNullOrWhiteSpace(x.DriverVersion)))
                    throw new InvalidOperationException("Storage probe returned no verified disk driver metadata.");
                long coldMs = sw.ElapsedMilliseconds;
                NativeSystemProbeService.InvalidateStorageCache(preserveStableDriverMetadata: true);
                var refreshSw = System.Diagnostics.Stopwatch.StartNew();
                var refreshed = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (refreshed.Count != disks.Count)
                    throw new InvalidOperationException("Storage dynamic refresh changed disk count unexpectedly.");
                LogService.Info(
                    $"STORAGE_PROBE_SMOKE_OK count={disks.Count} coldMs={coldMs} refreshMs={refreshSw.ElapsedMilliseconds} " +
                    $"drivers={string.Join(";", disks.Select(x => x.DriverProvider + " " + x.DriverVersion).Distinct())}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("STORAGE_PROBE_SMOKE_FAIL", ex);
                Environment.Exit(66);
            }
            return;
        }
        if (HasArg(cli, "--debloat-cache-smoke"))
        {
            try
            {
                await RunDebloatCacheSmokeCoreAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DEBLOAT_CACHE_SMOKE_FAIL", ex); Environment.Exit(57); }
            return;
        }

        if (HasArg(cli, "--task-scheduler-basic-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tasks = await Task.Run(
                    () => TaskSchedulerComService.GetAll(cts.Token, includeStartupTrigger: false),
                    cts.Token).ConfigureAwait(false);
                sw.Stop();
                if (tasks.Count == 0)
                    throw new InvalidOperationException("Task Scheduler basic inventář je prázdný.");
                if (tasks.Any(x => x.StartupTrigger))
                    throw new InvalidOperationException("Basic Task Scheduler scan neočekávaně materializoval startup trigger.");
                LogService.Info($"TASK_SCHEDULER_BASIC_SMOKE_OK tasks={tasks.Count} ms={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TASK_SCHEDULER_BASIC_SMOKE_FAIL", ex); Environment.Exit(47); }
            return;
        }

        if (HasArg(cli, "--task-scheduler-backend-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                var scheduler = new TaskSchedulerService();
                var freshSw = System.Diagnostics.Stopwatch.StartNew();
                var tasks = await scheduler.GetAsync(true, cts.Token);
                freshSw.Stop();
                if (tasks.Count == 0) throw new InvalidOperationException("Task Scheduler inventář je prázdný.");

                TaskSchedulerService.ClearMemoryCacheForSmoke();
                var cacheSw = System.Diagnostics.Stopwatch.StartNew();
                var cached = await scheduler.GetAsync(false, cts.Token);
                cacheSw.Stop();

                if (cached.Count != tasks.Count)
                    throw new InvalidOperationException($"Task Scheduler persistent cache count mismatch fresh={tasks.Count} cached={cached.Count}.");
                if (cacheSw.ElapsedMilliseconds > 150)
                    throw new InvalidOperationException($"Task Scheduler persistent cache is too slow: {cacheSw.ElapsedMilliseconds} ms.");
                if (!TaskSchedulerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Task Scheduler persistent snapshot did not request a background refresh.");

                int scansBeforeRefresh = TaskSchedulerService.ScanCountForDiagnostics;
                var refreshA = scheduler.GetAsync(true, cts.Token);
                var refreshB = scheduler.GetAsync(true, cts.Token);
                var refreshedPair = await Task.WhenAll(refreshA, refreshB);
                int refreshScans = TaskSchedulerService.ScanCountForDiagnostics - scansBeforeRefresh;
                if (refreshScans != 1)
                    throw new InvalidOperationException($"Task Scheduler force-refresh single-flight failed: scans={refreshScans}.");
                if (refreshedPair[0].Count != tasks.Count || refreshedPair[1].Count != tasks.Count)
                    throw new InvalidOperationException("Task Scheduler background refresh count mismatch.");
                if (TaskSchedulerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Task Scheduler background refresh left refreshPending set.");

                LogService.Info($"TASK_SCHEDULER_BACKEND_SMOKE_OK tasks={tasks.Count} freshMs={freshSw.ElapsedMilliseconds} persistentMs={cacheSw.ElapsedMilliseconds} refreshScans={refreshScans}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TASK_SCHEDULER_BACKEND_SMOKE_FAIL", ex); Environment.Exit(46); }
            return;
        }
        if (HasArg(cli, "--worker-proxy-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
                var drivers = await DriverInventoryService.GetAsync(true, cts.Token);
                var apps = await InstalledAppService.GetInstalledAsync(cts.Token, true, false);
                var services = await new ServiceCatalogService().GetAsync(true, cts.Token);
                if (drivers.Count == 0 || apps.Count == 0 || services.Count == 0) throw new InvalidOperationException("Worker proxy returned an empty inventory.");
                LogService.Info($"WORKER_PROXY_SMOKE_OK drivers={drivers.Count} apps={apps.Count} services={services.Count}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WORKER_PROXY_SMOKE_FAIL", ex); Environment.Exit(47); }
            return;
        }
        if (HasArg(cli, "--worker-stream-smoke"))
        {
            try
            {
                var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long firstMs = -1;
                var ack = await WorkerClientService.RunStreamingAsync<WorkerAck>("stream-smoke", null, TimeSpan.FromSeconds(15), System.Threading.CancellationToken.None,
                    line => { System.Threading.Interlocked.CompareExchange(ref firstMs, sw.ElapsedMilliseconds, -1); lines.Enqueue("O:" + line); },
                    line => { System.Threading.Interlocked.CompareExchange(ref firstMs, sw.ElapsedMilliseconds, -1); lines.Enqueue("E:" + line); });
                sw.Stop();
                string joined = string.Join("|", lines);
                if (!ack.Ok || firstMs < 0 || firstMs >= sw.ElapsedMilliseconds || !joined.Contains("O:STREAM_OUT_1") || !joined.Contains("E:STREAM_ERR_1") || !joined.Contains("O:STREAM_OUT_2"))
                    throw new InvalidOperationException($"stream invalid first={firstMs} total={sw.ElapsedMilliseconds} lines={joined}");
                LogService.Info($"WORKER_STREAM_SMOKE_OK firstMs={firstMs} totalMs={sw.ElapsedMilliseconds} lines={joined}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WORKER_STREAM_SMOKE_FAIL", ex); Environment.Exit(55); }
            return;
        }
        if (HasArg(cli, "--authenticode-smoke"))
        {
            try
            {
                LogService.Info("security", AuthenticodeVerifier.RunSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("security", "AUTHENTICODE_NATIVE_SMOKE_FAIL", ex);
                Environment.Exit(65);
            }
            return;
        }

        if (HasArg(cli, "--spicetify-status-smoke"))
        {
            try
            {
                await RunSpicetifyStatusSmokeCoreAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("spicetify", "SPICETIFY_STATUS_SMOKE_FAIL", ex);
                Environment.Exit(64);
            }
            return;
        }

        if (HasArg(cli, "--spicetify-marketplace-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                var status = await SpicetifyService.GetStatusAsync(cts.Token);
                if (!status.SpicetifyInstalled) throw new InvalidOperationException("Spicetify resolver did not find the installed CLI.");
                var result = await SpicetifyService.InstallMarketplaceAsync(cts.Token);
                if (!result.Success) throw new InvalidOperationException($"Marketplace install failed: {result.StdOut} {result.StdErr}");
                var after = await SpicetifyService.GetStatusAsync(cts.Token);
                if (!after.MarketplaceInstalled) throw new InvalidOperationException("Marketplace folder was not detected after install.");
                LogService.Info($"SPICETIFY_MARKETPLACE_SMOKE_OK version={after.Version}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SPICETIFY_MARKETPLACE_SMOKE_FAIL", ex); Environment.Exit(59); }
            return;
        }
        if (HasArg(cli, "--process-callback-smoke"))
        {
            try
            {
                int callbacks = 0;
                var ack = await WorkerClientService.RunStreamingAsync<WorkerAck>("stream-smoke", null, TimeSpan.FromSeconds(15), System.Threading.CancellationToken.None,
                    line => { callbacks++; if (callbacks == 1) throw new InvalidOperationException("Expected callback failure"); },
                    _ => { });
                if (!ack.Ok || callbacks == 0) throw new InvalidOperationException("Callback guard smoke did not receive output.");
                LogService.Info($"PROCESS_CALLBACK_SMOKE_OK callbacks={callbacks}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_CALLBACK_SMOKE_FAIL", ex); Environment.Exit(56); }
            return;
        }
        if (HasArg(cli, "--process-timeout-smoke"))
        {
            try
            {
                int baselineProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool timedOut = false;
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>("delay-smoke", null, TimeSpan.FromMilliseconds(450), System.Threading.CancellationToken.None);
                }
                catch (TimeoutException) { timedOut = true; }
                sw.Stop();
                if (!timedOut) throw new InvalidOperationException("Timeout smoke did not time out.");
                if (sw.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException($"Timeout cleanup was too slow: {sw.ElapsedMilliseconds}ms.");
                await Task.Delay(250);
                int afterProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                if (afterProcesses > baselineProcesses) throw new InvalidOperationException($"Timeout smoke leaked process(es): before={baselineProcesses}, after={afterProcesses}.");
                LogService.Info($"PROCESS_TIMEOUT_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} before={baselineProcesses} after={afterProcesses}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_TIMEOUT_SMOKE_FAIL", ex); Environment.Exit(57); }
            return;
        }
        if (HasArg(cli, "--process-cancel-smoke"))
        {
            try
            {
                int baselineProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool cancelled = false;
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>("delay-smoke", null, TimeSpan.FromSeconds(15), cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
                sw.Stop();
                if (!cancelled) throw new InvalidOperationException("Cancel smoke did not cancel.");
                if (sw.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException($"Cancel cleanup was too slow: {sw.ElapsedMilliseconds}ms.");
                await Task.Delay(250);
                int afterProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                if (afterProcesses > baselineProcesses) throw new InvalidOperationException($"Cancel smoke leaked process(es): before={baselineProcesses}, after={afterProcesses}.");
                LogService.Info($"PROCESS_CANCEL_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} before={baselineProcesses} after={afterProcesses}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_CANCEL_SMOKE_FAIL", ex); Environment.Exit(58); }
            return;
        }
        if (HasArg(cli, "--cache-cleanup-smoke"))
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "SpankyhoCacheSmoke_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                await File.WriteAllBytesAsync(Path.Combine(root, "a.bin"), new byte[32768]);
                await File.WriteAllBytesAsync(Path.Combine(root, "nested", "b.bin"), new byte[16384]);
                var result = await LauncherCacheService.ClearVerifiedAsync(new[] { root });
                if (result.BeforeBytes < 49152 || result.AfterBytes != 0 || result.FailedFiles != 0)
                    throw new InvalidOperationException($"before={result.BeforeBytes} after={result.AfterBytes} failed={result.FailedFiles}");
                try { Directory.Delete(root, true); } catch { }
                LogService.Info($"CACHE_CLEANUP_SMOKE_OK before={result.BeforeBytes} freed={result.FreedBytes} after={result.AfterBytes}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CACHE_CLEANUP_SMOKE_FAIL", ex); Environment.Exit(50); }
            return;
        }

        if (HasArg(cli, "--apply-profile-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await ApplyProfileSmokeService.RunAsync(cts.Token);
                LogService.Info("APPLY_PROFILE_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("APPLY_PROFILE_SMOKE_FAIL", ex); Environment.Exit(48); }
            return;
        }

        if (HasArg(cli, "--preflight-warm-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                string result = await ApplyProfileSmokeService.RunPreflightWarmAsync(cts.Token);
                LogService.Info("PREFLIGHT_WARM_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PREFLIGHT_WARM_SMOKE_FAIL", ex); Environment.Exit(48); }
            return;
        }

        if (HasArg(cli, "--startup-warm-apply-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await ApplyProfileSmokeService.RunStartupWarmApplyAsync(cts.Token);
                LogService.Info("STARTUP_WARM_APPLY_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STARTUP_WARM_APPLY_SMOKE_FAIL", ex); Environment.Exit(48); }
            return;
        }

        if (HasArg(cli, "--bcd-cache-smoke"))
        {
            try
            {
                BcdEditStateService.Invalidate();
                int before = BcdEditStateService.LoadCountForDiagnostics;
                var pair = await Task.WhenAll(BcdEditStateService.GetCurrentAsync(), BcdEditStateService.GetCurrentAsync());
                int loads = BcdEditStateService.LoadCountForDiagnostics - before;
                if (loads != 1 || !string.Equals(pair[0], pair[1], StringComparison.Ordinal))
                    throw new InvalidOperationException($"BCD cache single-flight failed: loads={loads}");

                BcdEditStateService.ClearMemoryCacheForSmoke();
                int beforePersistent = BcdEditStateService.LoadCountForDiagnostics;
                var persistentSw = System.Diagnostics.Stopwatch.StartNew();
                string persisted = await BcdEditStateService.GetCurrentAsync();
                persistentSw.Stop();
                if (!string.Equals(pair[0], persisted, StringComparison.Ordinal))
                    throw new InvalidOperationException("BCD persistent cache returned a different snapshot.");
                if (BcdEditStateService.LoadCountForDiagnostics != beforePersistent)
                    throw new InvalidOperationException("BCD persistent cache unexpectedly launched bcdedit.exe.");

                LogService.Info($"BCD_CACHE_SMOKE_OK loads={loads} persistentMs={persistentSw.ElapsedMilliseconds} chars={pair[0].Length}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("BCD_CACHE_SMOKE_FAIL", ex); Environment.Exit(48); }
            return;
        }

        if (HasArg(cli, "--bcd-persistent-read-smoke"))
        {
            try
            {
                int before = BcdEditStateService.LoadCountForDiagnostics;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string snapshot = await BcdEditStateService.GetCurrentAsync();
                sw.Stop();
                if (string.IsNullOrWhiteSpace(snapshot))
                    throw new InvalidOperationException("BCD persistent read returned an empty snapshot.");
                if (BcdEditStateService.LoadCountForDiagnostics != before)
                    throw new InvalidOperationException("BCD cross-process persistent cache missed and launched bcdedit.exe.");
                LogService.Info($"BCD_PERSISTENT_PROCESS_SMOKE_OK ms={sw.ElapsedMilliseconds} chars={snapshot.Length}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("BCD_PERSISTENT_PROCESS_SMOKE_FAIL", ex); Environment.Exit(48); }
            return;
        }

        if (HasArg(cli, "--atomic-apply-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await AtomicApplySmokeService.RunAsync(cts.Token);
                LogService.Info("ATOMIC_APPLY_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("ATOMIC_APPLY_SMOKE_FAIL", ex); Environment.Exit(48); }
            return;
        }
        if (HasArg(cli, "--native-perf-smoke"))
        {
            try
            {
                var r = await NativePerformanceSmokeService.RunAsync();
                LogService.Info($"NATIVE_PERF_SMOKE_OK rust={r.RustAvailable} managedPowerUs={r.ManagedPowerUs:0.000} rustPowerUs={r.RustPowerUs:0.000} managedDevicesMs={r.ManagedDevicesMs:0.00} rustDevicesMs={r.RustDevicesMs:0.00} managedCount={r.ManagedDeviceCount} rustCount={r.RustDeviceCount} managedServiceUs={r.ManagedServiceUs:0.000} rustServiceUs={r.RustServiceUs:0.000} preferPower={r.PreferRustPower} preferDevices={r.PreferRustDevices} preferService={r.PreferRustService}");
                Environment.Exit(r.RustAvailable && r.ManagedDeviceCount == r.RustDeviceCount ? 0 : 49);
            }
            catch (Exception ex) { LogService.Error("NATIVE_PERF_SMOKE_FAIL", ex); Environment.Exit(49); }
            return;
        }
        if (HasArg(cli, "--update-smoke"))
        {
            try
            {
                var check = await AppUpdateService.CheckAsync();
                if (!check.Configured || !check.Available || check.Manifest == null) throw new InvalidOperationException(check.Message);
                string path = await AppUpdateService.DownloadAsync(check.Manifest);
                if (!File.Exists(path)) throw new FileNotFoundException("Stažený installer nebyl nalezen.", path);
                LogService.Info($"UPDATE_SMOKE_OK | version={check.Manifest.Version} | file={Path.GetFileName(path)}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("UPDATE_SMOKE_FAIL", ex); Environment.Exit(44); }
            return;
        }

        if (HasArg(cli, "--recommended-detect-smoke"))
        {
            try
            {
                string[] sections = { "system", "gpu", "memory", "storage", "network", "ui", "update", "privacy", "ai", "defender", "services", "drivers", "tasks" };
                string[] browsers = { "Firefox", "Chrome", "Edge", "Brave", "Opera", "Opera GX", "Vivaldi" };
                var recommended = sections.SelectMany(x => CatalogService.GetTweaks(x))
                    .Concat(browsers.SelectMany(x => CatalogService.GetTweaks("browsers", x)))
                    .Where(x => x.Recommended)
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToArray();

                var failures = new System.Collections.Generic.List<string>();
                var inactiveIds = new System.Collections.Generic.List<string>();
                int active = 0;
                foreach (var tweak in recommended)
                {
                    using var detectCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        bool current = await tweak.DetectAsync(detectCts.Token);
                        if (current) active++;
                        else inactiveIds.Add(tweak.Id);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(tweak.Id + "=" + ex.GetType().Name + ":" + ex.Message);
                    }
                }

                if (failures.Count > 0)
                    throw new InvalidOperationException("Recommended detect failures: " + string.Join(" | ", failures.Take(20)));
                LogService.Info($"RECOMMENDED_DETECT_SMOKE_OK count={recommended.Length} active={active} inactive={recommended.Length - active}");
                if (inactiveIds.Count > 0)
                    LogService.Info("RECOMMENDED_DETECT_INACTIVE " + string.Join(",", inactiveIds));
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("RECOMMENDED_DETECT_SMOKE_FAIL", ex); Environment.Exit(73); }
            return;
        }

        if (HasArg(cli, "--audit-smoke") || HasArg(cli, "--catalog-audit-smoke"))
        {
            var originalUi = UiStateService.Load();
            var originalTheme = AppThemeService.LoadPreference();
            try
            {
                if (CatalogService.Sections.Any(x => x.Key.Equals("vibrance", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Vibrance section still exists.");
                var system = CatalogService.GetTweaks("system");
                if (system.Any(x => x.Id.Equals("sys.startupdelay", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Startup Delay is still in System tweaks.");
                var privacy = CatalogService.GetTweaks("privacy");
                if (privacy.Any(x => x.Id.StartsWith("sec.", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Privacy still exposes sec.* hardening.");
                string[] sections = { "system", "gpu", "memory", "storage", "network", "ui", "update", "privacy", "ai", "defender", "services", "drivers", "tasks" };
                string[] browsers = { "Firefox", "Chrome", "Edge", "Brave", "Opera", "Opera GX", "Vivaldi" };
                var all = sections.SelectMany(x => CatalogService.GetTweaks(x))
                    .Concat(browsers.SelectMany(x => CatalogService.GetTweaks("browsers", x)))
                    .ToArray();
                var recommended = all.Where(x => x.Recommended).ToArray();

                if (CatalogService.EnumerateTweakSources().Count < 20)
                    throw new InvalidOperationException("Data-driven tweak registry is unexpectedly incomplete.");

                var registryTweaks = all.Where(x =>
                        x.RegistryHive.HasValue &&
                        !string.IsNullOrWhiteSpace(x.RegistryPath) &&
                        !string.IsNullOrWhiteSpace(x.RegistryValueName))
                    .ToArray();
                var registryWithoutOriginalRevert = registryTweaks
                    .Where(x => !x.CanRestoreOriginal ||
                                !x.RevertMode.Equals("OriginalRegistryValue", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Id)
                    .ToArray();
                if (registryWithoutOriginalRevert.Length > 0)
                    throw new InvalidOperationException("Registry tweaks without original-value revert: " +
                        string.Join(",", registryWithoutOriginalRevert));

                var nvidiaTelemetry = all.FirstOrDefault(x => x.Id.Equals("gt.privacy.nvtelemetry", StringComparison.OrdinalIgnoreCase));
                var intelTelemetry = all.FirstOrDefault(x => x.Id.Equals("gt.privacy.inteltelemetry", StringComparison.OrdinalIgnoreCase));
                if (nvidiaTelemetry == null || !nvidiaTelemetry.HardwareScope.Equals("nvidia", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("NVIDIA tweak compatibility scope missing.");
                if (intelTelemetry == null || !intelTelemetry.HardwareScope.Equals("intel", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Intel tweak compatibility scope missing.");

                var cleanerScan = await StorageMaintenanceService.ScanSafeCleanupAsync();
                string[] requiredCleanerCategories = { "temp", "d3d", "nvidia", "amd" };
                var cleanerIds = cleanerScan.Categories.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (requiredCleanerCategories.Any(x => !cleanerIds.Contains(x)))
                    throw new InvalidOperationException("Cleaner scan categories are incomplete.");
                if (cleanerScan.TotalBytes != cleanerScan.Categories.Sum(x => x.Bytes))
                    throw new InvalidOperationException("Cleaner scan total does not match category sum.");

                var duplicateIds = all.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                if (duplicateIds.Length > 0)
                    throw new InvalidOperationException("Duplicate tweak IDs: " + string.Join(",", duplicateIds));

                var unsafeRecommended = recommended.Where(x => x.Risk == Models.TweakRisk.Advanced).Select(x => x.Id).ToArray();
                if (unsafeRecommended.Length > 0)
                    throw new InvalidOperationException("Advanced Recommended: " + string.Join(",", unsafeRecommended));

                var registryConflicts = recommended
                    .Where(x => x.RegistryHive.HasValue && !string.IsNullOrWhiteSpace(x.RegistryPath) &&
                                !string.IsNullOrWhiteSpace(x.RegistryValueName) && x.RegistryEnabledValue.HasValue)
                    .GroupBy(x => $"{x.RegistryHive}|{x.RegistryPath}|{x.RegistryValueName}", StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Select(x => x.RegistryEnabledValue!.Value).Distinct().Count() > 1)
                    .Select(g => g.Key + " => " + string.Join("/", g.Select(x => x.Id + ":" + x.RegistryEnabledValue)))
                    .ToArray();
                if (registryConflicts.Length > 0)
                    throw new InvalidOperationException("Recommended registry conflicts: " + string.Join(" | ", registryConflicts));

                var recommendedIds = recommended.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var declaredConflicts = recommended
                    .Where(x => x.ConflictsWithTweaks.Any(recommendedIds.Contains))
                    .Select(x => x.Id + "<->" + string.Join(",", x.ConflictsWithTweaks.Where(recommendedIds.Contains)))
                    .ToArray();
                if (declaredConflicts.Length > 0)
                    throw new InvalidOperationException("Recommended declared conflicts: " + string.Join(" | ", declaredConflicts));

                var missingRequirements = recommended
                    .Where(x => x.RequiresTweaks.Any(req => !recommendedIds.Contains(req)))
                    .Select(x => x.Id + " requires " + string.Join(",", x.RequiresTweaks.Where(req => !recommendedIds.Contains(req))))
                    .ToArray();
                if (missingRequirements.Length > 0)
                    throw new InvalidOperationException("Recommended missing requirements: " + string.Join(" | ", missingRequirements));
                UiStateService.Save(new UiState("privacy", null, "Recommended", 123.0));
                var roundtrip = UiStateService.Load();
                if (roundtrip.Section != "privacy" || roundtrip.ViewMode != "Recommended" || Math.Abs(roundtrip.ScrollOffset - 123.0) > .01) throw new InvalidOperationException("UI state round-trip failed.");
                AppThemeService.SavePreference(AppThemePreference.Light);
                if (AppThemeService.LoadPreference() != AppThemePreference.Light || AppThemeService.ResolveDark(AppThemePreference.Light)) throw new InvalidOperationException("Light theme round-trip failed.");
                AppThemeService.SavePreference(AppThemePreference.Dark);
                if (AppThemeService.LoadPreference() != AppThemePreference.Dark || !AppThemeService.ResolveDark(AppThemePreference.Dark)) throw new InvalidOperationException("Dark theme round-trip failed.");
                LogService.Info($"AUDIT_SMOKE_OK sections={CatalogService.Sections.Count} auditedTweaks={all.Length} privacy={privacy.Count} defaultTheme={originalTheme}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AUDIT_SMOKE_FAIL", ex); Environment.Exit(52); }
            finally { UiStateService.Save(originalUi); AppThemeService.SavePreference(originalTheme); }
            return;
        }
        bool smokeMode = Array.Exists(cli, x => x.Contains("smoke", StringComparison.OrdinalIgnoreCase));
        if (!smokeMode && HasArg(cli, "--safe-mode")) CrashLoopGuardService.ForceSafeMode();
        Task<bool>? crashLoopStartupTask = !smokeMode
            ? Task.Run(() => CrashLoopGuardService.BeginStartup(AppVersion))
            : null;
        if (!smokeMode && !_ownsSingleInstanceMutex && !TryAcquireSingleInstance())
        {
            LogService.Info("startup", "SECOND_INSTANCE_BLOCKED_BEFORE_WINDOW");
            Environment.Exit(0);
            return;
        }

        StartupDiagnostics.Mark("MainWindow.Create");
        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            try { _lifetimeCts.Cancel(); } catch { }
            HardwareChangeMonitorService.Stop();
            ReleaseSingleInstance();
        };
        StartupDiagnostics.Mark("MainWindow.Created");
        if (crashLoopStartupTask != null)
            await crashLoopStartupTask;
        _window.Activate();
        StartupDiagnostics.Mark("Window.Activated");
        long windowActivatedMs = StartupDiagnostics.ElapsedMilliseconds;

        if (!smokeMode)
            SafeTaskService.Forget(RunLegacyShortcutOverlayMigrationAsync(_lifetimeCts.Token), "startup", "Legacy shortcut overlay migration");

        // Všechny nenutné disk/WMI operace až po prvním renderu.
        // Critical leak measures navigation retention in an otherwise quiescent process;
        // the delayed +6s build revalidation is covered by dedicated drift/revalidation gates.
        if (!HasArg(cli, "--critical-leak-smoke"))
            SafeTaskService.Forget(RunWindowsBuildRevalidationAsync(_lifetimeCts.Token), "startup", "Windows build revalidation");
        if (!smokeMode)
        {
            SafeTaskService.Forget(RunHardwareChangeMonitorStartAsync(_lifetimeCts.Token), "startup", "Hardware change monitor start");
            SafeTaskService.Forget(RunPendingTransactionRecoveryAsync(_lifetimeCts.Token), "startup", "Pending transaction recovery");
            SafeTaskService.Forget(IdlePrewarmService.RunAsync(_lifetimeCts.Token), "startup", "Idle prewarm");
        }

        if (HasArg(cli, "--powershell-prewarm-smoke") && _window is MainWindow powerShellPrewarmMw)
        {
            try
            {
                await powerShellPrewarmMw.RunPowerShellPrewarmSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWERSHELL_PREWARM_SMOKE_FAIL", ex);
                Environment.Exit(74);
            }
            return;
        }

        if (HasArg(cli, "--powershell-center-smoke") && _window is MainWindow powerShellCenterMw)
        {
            try
            {
                await powerShellCenterMw.RunPowerShellCenterSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWERSHELL_CENTER_SMOKE_FAIL", ex);
                Environment.Exit(74);
            }
            return;
        }

        if (HasArg(cli, "--live-metrics-lifecycle-smoke") && _window is MainWindow liveMetricsMw)
        {
            try { await liveMetricsMw.RunLiveMetricsLifecycleSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("LIVE_METRICS_LIFECYCLE_SMOKE_FAIL", ex); Environment.Exit(73); }
            return;
        }
        if (HasArg(cli, "--navigation-cache-smoke") && _window is MainWindow navigationMw)
        {
            try
            {
                await Task.Delay(500);
                for (int i = 0; i < 12; i++) await navigationMw.RunOverviewCacheSmokeAsync();
                LogService.Info("NAVIGATION_CACHE_REPEAT_SMOKE_OK samples=12");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("NAVIGATION_CACHE_REPEAT_SMOKE_FAIL", ex); Environment.Exit(73); }
            return;
        }
        if (HasArg(cli, "--release-ui-smoke") && _window is MainWindow releaseUiMw)
        {
            try
            {
                static async Task RunGateAsync(string gate, Func<Task> action)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await action();
                    LogService.Info("release-ui-smoke", $"{gate}_OK ms={sw.ElapsedMilliseconds}");
                }

                // Match the standalone UI smoke startup settle once for the whole bundle.
                await Task.Delay(500);
                await RunGateAsync("DRIVERS_UI_SMOKE", releaseUiMw.RunDriversUiSmokeAsync);
                await RunGateAsync("TASK_SCHEDULER_UI_SMOKE", releaseUiMw.RunTaskSchedulerUiSmokeAsync);
                await RunGateAsync("STARTUP_RENDER_SMOKE", releaseUiMw.RunStartupRenderSmokeAsync);
                await RunGateAsync("POWER_RENDER_SMOKE", releaseUiMw.RunPowerRenderSmokeAsync);
                await RunGateAsync("NAV_TAIL_SMOKE", releaseUiMw.RunNavigationTailLatencySmokeAsync);
                await RunGateAsync("AI_PROGRESS_UI_SMOKE", () => releaseUiMw.RunAiProgressUiSmokeAsync());
                await RunGateAsync("AI_RUNTIME_PAGE_CACHE_SMOKE", releaseUiMw.RunAiRuntimePageCacheSmokeAsync);
                await RunGateAsync("NIC_UI_SMOKE", releaseUiMw.RunNicUiSmokeAsync);
                await RunGateAsync("GPU_DEFERRED_RENDER_SMOKE", releaseUiMw.RunGpuDeferredRenderSmokeAsync);
                await RunGateAsync("USB_DEFERRED_RENDER_SMOKE", releaseUiMw.RunUsbDeferredRenderSmokeAsync);
                await RunGateAsync("DEFENDER_DEFERRED_ACTIONS_SMOKE", releaseUiMw.RunDefenderDeferredActionsSmokeAsync);
                await RunGateAsync("APPS_DEFERRED_BODY_SMOKE", releaseUiMw.RunAppsDeferredBodySmokeAsync);
                await RunGateAsync("COMMUNITY_CURSOR_POPUP_SMOKE", () =>
                {
                    LogService.Info("release-ui-smoke", releaseUiMw.RunCommunityCursorPopupLifecycleSmoke());
                    return Task.CompletedTask;
                });
                await RunGateAsync("CURSOR_WATCHDOG_SMOKE", () =>
                {
                    LogService.Info("release-ui-smoke", CursorThemeService.RunWatchdogNonMutatingSmoke());
                    return Task.CompletedTask;
                });
                await RunGateAsync("STORAGE_TOOLS_CACHE_SMOKE", releaseUiMw.RunStorageToolsCacheSmokeAsync);
                await RunGateAsync("OVERVIEW_CACHE_SMOKE", releaseUiMw.RunOverviewCacheSmokeAsync);
                await RunGateAsync("LIVE_METRICS_LIFECYCLE_SMOKE", releaseUiMw.RunLiveMetricsLifecycleSmokeAsync);
                await RunGateAsync("OVERVIEW_ACTIVE_TWEAK_MERGE_SMOKE", () =>
                {
                    LogService.Info("release-ui-smoke", MainWindow.RunOverviewActiveTweakMergeSmoke());
                    return Task.CompletedTask;
                });
                await RunGateAsync("GPU_DEFERRED_TWEAK_SMOKE", releaseUiMw.RunGpuDeferredTweakSmokeAsync);
                await RunGateAsync("PROGRESS_PREDICATE_SMOKE", releaseUiMw.RunProgressPredicateSmokeAsync);
                await RunGateAsync("POWERSHELL_CENTER_SMOKE", releaseUiMw.RunPowerShellCenterSmokeAsync);
                // Theme render runs last because it intentionally rebuilds shell/page caches.
                await RunGateAsync("THEME_RENDER_SMOKE", releaseUiMw.RunThemeRenderSmokeAsync);
                LogService.Info("release-ui-smoke", "RELEASE_UI_SMOKE_BUNDLE_OK count=21");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("release-ui-smoke", "RELEASE_UI_SMOKE_BUNDLE_FAIL", ex);
                Environment.Exit(73);
            }
            return;
        }

        if (HasArg(cli, "--progress-lifecycle-smoke") && _window is MainWindow progressLifecycleMw)
        {
            try
            {
                await progressLifecycleMw.RunProgressPredicateSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "PROGRESS_LIFECYCLE_SMOKE_FAIL", ex);
                Environment.Exit(74);
            }
            return;
        }

        if (HasArg(cli, "--system-cold-render-smoke") && _window is MainWindow systemColdMw)
        {
            try { await systemColdMw.RunSystemColdRenderSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("SYSTEM_COLD_RENDER_SMOKE_FAIL", ex); Environment.Exit(57); }
            return;
        }

        if (HasArg(cli, "--virtual-resource-prewarm-smoke") && _window is MainWindow virtualPrewarmMw)
        {
            try { await virtualPrewarmMw.RunVirtualResourcePrewarmSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VIRTUAL_RESOURCE_PREWARM_SMOKE_FAIL", ex); Environment.Exit(55); }
            return;
        }

        if (HasArg(cli, "--resource-cache-smoke-bundle") && _window is MainWindow resourceCacheMw)
        {
            try
            {
                var bundleSw = System.Diagnostics.Stopwatch.StartNew();
                await resourceCacheMw.RunVirtualResourcePrewarmSmokeAsync();
                LogService.Info("resource-cache-smoke", $"VIRTUAL_RESOURCE_PREWARM_SMOKE_OK ms={bundleSw.ElapsedMilliseconds}");
                bundleSw.Restart();
                await RunDebloatCacheSmokeCoreAsync();
                LogService.Info("resource-cache-smoke", $"DEBLOAT_CACHE_SMOKE_OK ms={bundleSw.ElapsedMilliseconds}");
                LogService.Info("resource-cache-smoke", "RESOURCE_CACHE_SMOKE_BUNDLE_OK count=2");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("resource-cache-smoke", "RESOURCE_CACHE_SMOKE_BUNDLE_FAIL", ex);
                Environment.Exit(76);
            }
            return;
        }

        if (HasArg(cli, "--theme-render-smoke") && _window is MainWindow themeMw)
        {
            try { await themeMw.RunThemeRenderSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("THEME_RENDER_SMOKE_FAIL", ex); Environment.Exit(54); }
            return;
        }

        if (HasArg(cli, "--visual-layout-smoke") && _window is MainWindow visualMw)
        {
            try { await visualMw.RunVisualLayoutSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VISUAL_LAYOUT_SMOKE_FAIL", ex); Environment.Exit(77); }
            return;
        }

        if (HasArg(cli, "--visual-capture-smoke") && _window is MainWindow captureMw)
        {
            try { await captureMw.RunVisualCaptureSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VISUAL_CAPTURE_SMOKE_FAIL", ex); Environment.Exit(78); }
            return;
        }

        if (HasArg(cli, "--visual-size-smoke") && _window is MainWindow sizeMw)
        {
            try { await sizeMw.RunVisualSizeMatrixSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VISUAL_SIZE_SMOKE_FAIL", ex); Environment.Exit(79); }
            return;
        }

        if (HasArg(cli, "--page-scroll-input-smoke") && _window is MainWindow pageScrollMw)
        {
            try { await pageScrollMw.RunPageScrollInputSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("PAGE_SCROLL_INPUT_SMOKE_FAIL", ex); Environment.Exit(84); }
            return;
        }

        if (HasArg(cli, "--keyboard-accessibility-smoke") && _window is MainWindow keyboardMw)
        {
            try { await keyboardMw.RunKeyboardAccessibilitySmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("KEYBOARD_ACCESSIBILITY_SMOKE_FAIL", ex); Environment.Exit(80); }
            return;
        }

        if (HasArg(cli, "--keyboard-activation-smoke") && _window is MainWindow activationMw)
        {
            try { await activationMw.RunKeyboardActivationSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("KEYBOARD_ACTIVATION_SMOKE_FAIL", ex); Environment.Exit(81); }
            return;
        }

        if (HasArg(cli, "--section-tab-smoke") && _window is MainWindow sectionTabMw)
        {
            try { await sectionTabMw.RunSectionTabTraversalSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("SECTION_TAB_SMOKE_FAIL", ex); Environment.Exit(82); }
            return;
        }

        if (HasArg(cli, "--navigation-race-smoke") && _window is MainWindow navigationRaceMw)
        {
            try { await navigationRaceMw.RunNavigationRaceSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("NAVIGATION_RACE_SMOKE_FAIL", ex); Environment.Exit(83); }
            return;
        }

        if (HasArg(cli, "--gpu-deferred-render-smoke") && _window is MainWindow gpuDeferredMw)
        {
            try
            {
                await Task.Delay(250);
                await gpuDeferredMw.RunGpuDeferredRenderSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GPU_DEFERRED_RENDER_SMOKE_FAIL", ex); Environment.Exit(56); }
            return;
        }

        if (HasArg(cli, "--process-hardening-smoke"))
        {
            try
            {
                await ProcessRunner.RunHardeningSmokeAsync();
                LogService.Info("PROCESS_HARDENING_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("PROCESS_HARDENING_SMOKE_FAIL", ex);
                Environment.Exit(68);
            }
            return;
        }

        if (HasArg(cli, "--full-smoke") && _window is MainWindow fullMw)
        {
            try
            {
                int navigationFailuresAtStart = fullMw.NavigationFailureCount;
                if (navigationFailuresAtStart != 0)
                    throw new InvalidOperationException($"Full smoke started with {navigationFailuresAtStart} navigation failure(s).");
                if (windowActivatedMs > 1500) throw new InvalidOperationException($"Startup audit failed: Window.Activated at {windowActivatedMs} ms.");
                LogService.Info($"STARTUP_AUDIT_OK activatedMs={windowActivatedMs}");
                await Task.Delay(350);
                LogService.Info("FULL_SMOKE_STARTUP_OK");
                await WatchdogService.RunAsync("watchdog-complete-smoke", async ct => await Task.Delay(5, ct), TimeSpan.FromSeconds(1));
                bool watchdogTimedOut = false;
                try
                {
                    await WatchdogService.RunAsync("watchdog-timeout-smoke", async ct => await Task.Delay(200, ct), TimeSpan.FromMilliseconds(20), logTimeoutAsError: false);
                }
                catch (TimeoutException) { watchdogTimedOut = true; }
                if (!watchdogTimedOut) throw new InvalidOperationException("Watchdog timeout smoke did not time out.");
                using (var watchdogCancel = new System.Threading.CancellationTokenSource())
                {
                    watchdogCancel.CancelAfter(20);
                    bool externalCancelled = false;
                    try
                    {
                        await WatchdogService.RunAsync("watchdog-cancel-smoke", async ct => await Task.Delay(200, ct), TimeSpan.FromSeconds(1), watchdogCancel.Token);
                    }
                    catch (OperationCanceledException) { externalCancelled = true; }
                    if (!externalCancelled) throw new InvalidOperationException("Watchdog external cancellation smoke did not cancel.");
                }
                LogService.Info("WATCHDOG_SMOKE_OK");
                var fullPhase = System.Diagnostics.Stopwatch.StartNew();
                long fullPhaseLast = 0;
                void FullPhase(string name)
                {
                    long now = fullPhase.ElapsedMilliseconds;
                    LogService.Info("full-smoke", $"FULL_SMOKE_PHASE {name}={now - fullPhaseLast}ms total={now}ms");
                    fullPhaseLast = now;
                }
                await ProcessRunner.RunHardeningSmokeAsync();
                LogService.Info("PROCESS_HARDENING_SMOKE_OK");
                FullPhase("process-hardening");
                await fullMw.RunDirtyRefreshCoalescingSmokeAsync();
                await fullMw.RunSectionSmokeTestAsync();
                await fullMw.RunDeferredWindowSetupSmokeAsync();
                await fullMw.RunDeferredTitlebarSmokeAsync();
                await fullMw.RunSidebarDeferredSmokeAsync();
                await fullMw.RunHeroActionDeferredSmokeAsync();
                await fullMw.RunUserNavigationNoOpSmokeAsync();
                await fullMw.RunSystemDeferredRenderSmokeAsync();
                await fullMw.RunPrivacyDeferredRenderSmokeAsync();
                await fullMw.RunPrivacyRevisionRefreshSmokeAsync();
                await fullMw.RunUiDeferredRenderSmokeAsync();
                await fullMw.RunToggleStateConsistencySmokeAsync();
                await fullMw.RunImmediateToggleBackendSmokeAsync();
                await fullMw.RunContextMenuUiSmokeAsync();
                await fullMw.RunUpdateVirtualCacheSmokeAsync();
                await fullMw.RunNetworkDeferredRenderSmokeAsync();
                LogService.Info(MainWindow.RunDnsApplyPlanSmoke());
                LogService.Info(DnsProfileService.RunReadbackGuardSmoke());
                LogService.Info(StartupManagerService.RunDeltaSmoke());
                await fullMw.RunMtuReadbackSmokeAsync();
                await fullMw.RunMtuProbeSmokeAsync();
                await fullMw.RunBrowserCacheSmokeAsync();
                await fullMw.RunSpicetifyDeferredRenderSmokeAsync();
                await fullMw.RunRestoreCacheSmokeAsync();
                await fullMw.RunPageCacheLifetimeSmokeAsync();
                await fullMw.RunOptimizationPlanSmokeAsync();
                FullPhase("navigation-render-cache-smokes");
                // Section smoke immediately above already completed one full catalog pass.
                // Keep standalone regression at 3 passes; Full Smoke adds two more for 3 total passes.
                await fullMw.RunRegressionSmokeTestAsync(repeatedNavigationPasses: 2);
                await fullMw.RunPerformanceSmokeAsync();
                FullPhase("regression-performance");
                fullMw.RunBackingCacheTrimSmoke();
                fullMw.RunIdleCacheTrimSmoke();
                FullPhase("cache-trim");
                await fullMw.RunLeakAndCancellationAuditAsync();
                FullPhase("leak-cancellation");
                await fullMw.RunSoakAuditAsync();
                FullPhase("soak-audit");
                var batchErrors = await WorkerClientService.RunAsync<string[]>("service-set-batch",
                    WorkerPayloads.Encode(new ServiceBatchRequest(Array.Empty<ServiceChangeRequest>())), TimeSpan.FromSeconds(20));
                if (batchErrors.Length != 0) throw new InvalidOperationException("Empty service batch returned errors.");
                LogService.Info("SERVICE_BATCH_WORKER_SMOKE_OK");
                var taskBatchErrors = await new TaskSchedulerService().SetEnabledBatchAsync(Array.Empty<(string Path, bool Enabled)>());
                if (taskBatchErrors.Length != 0) throw new InvalidOperationException("Empty task batch returned errors.");
                LogService.Info("TASK_BATCH_COM_SMOKE_OK");
                TaskSchedulerService.RunCacheMergeSmoke();
                await TaskSchedulerService.RunBasicCachePreservationSmokeAsync();
                PowerPlanGuardService.RunArgumentSmoke();
                var mutationAudit = await NonTweakMutationAuditService.RunAsync();
                LogService.Info($"NON_TWEAK_MUTATION_AUDIT audited={mutationAudit.Audited} failed={mutationAudit.Failed} {string.Join("; ", mutationAudit.Details)}");
                if (mutationAudit.Failed > 0) throw new InvalidOperationException("Non-tweak mutation audit failed: " + string.Join(" | ", mutationAudit.Details.Where(x => x.Contains("=FAIL", StringComparison.OrdinalIgnoreCase))));
                var stateAudit = await StateAccuracyAuditService.RunAsync();
                LogService.Info($"STATE_ACCURACY_AUDIT audited={stateAudit.Audited} unsupported={stateAudit.Unsupported} failed={stateAudit.Failed}");
                if (stateAudit.Failed > 0) throw new InvalidOperationException("State accuracy audit failed: " + string.Join(" | ", stateAudit.Issues.Take(8)));
                var compat = PlatformCompatibilityAuditService.Run();
                LogService.Info("COMPAT_AUDIT " + compat.Summary);
                if (!compat.FutureBuildSafe) throw new InvalidOperationException("Platform compatibility audit failed: " + compat.Summary);
                if (!compat.StandardCimv2 || !compat.MmAgent) LogService.Info("COMPAT_DEGRADED providers missing; affected features stay Unsupported instead of failing navigation.");
                FullPhase("worker-state-compat-audits");
                int navigationFailuresAtEnd = fullMw.NavigationFailureCount;
                if (navigationFailuresAtEnd != navigationFailuresAtStart)
                    throw new InvalidOperationException(
                        $"Full smoke observed hidden navigation failures: start={navigationFailuresAtStart} end={navigationFailuresAtEnd}.");
                LogService.Info("FULL_SMOKE_NAVIGATION_FAILURE_GUARD_OK count=0");
                LogService.Info("FULL_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("FULL_SMOKE_FAIL", ex); Environment.Exit(51); }
            return;
        }
        if (HasArg(cli, "--startup-smoke"))
        {
            await Task.Delay(2500);
            LogService.Info($"STARTUP_SMOKE_OK activatedMs={windowActivatedMs}");
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--power-preview") && _window is MainWindow powerPreviewMw)
        {
            await Task.Delay(350);
            await powerPreviewMw.ShowPowerPreviewAsync();
            return;
        }

        if (HasArg(cli, "--power-ui-smoke") && _window is MainWindow powerMw)
        {
            await Task.Delay(250);
            await powerMw.RunPowerUiSmokeAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--nic-ui-smoke") && _window is MainWindow nicMw)
        {
            await Task.Delay(500);
            await nicMw.RunNicUiSmokeAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--mtu-smoke") && _window is MainWindow mtuMw)
        {
            await Task.Delay(250);
            await mtuMw.RunMtuReadbackSmokeAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--mtu-probe-smoke") && _window is MainWindow mtuProbeMw)
        {
            await Task.Delay(250);
            await mtuProbeMw.RunMtuProbeSmokeAsync();
            Environment.Exit(0);
            return;
        }

        string? mtuApplyArg = cli.FirstOrDefault(x => x.StartsWith("--mtu-apply-verified=", StringComparison.OrdinalIgnoreCase));
        if (mtuApplyArg != null)
        {
            try
            {
                if (!int.TryParse(mtuApplyArg["--mtu-apply-verified=".Length..], out int verifiedMtu))
                    throw new InvalidOperationException("Neplatná MTU hodnota.");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var applied = await MtuOptimizerService.ApplyAsync(verifiedMtu, applyIpv6: false, cts.Token);
                LogService.Info($"MTU_APPLY_VERIFIED_SMOKE_OK adapter={applied.Name} ipv4={applied.Ipv4Mtu} ipv6={applied.Ipv6Mtu}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("MTU_APPLY_VERIFIED_SMOKE_FAIL", ex); Environment.Exit(44); }
            return;
        }

        if (HasArg(cli, "--exostenza-ui-smoke") && _window is MainWindow exMw)
        {
            await Task.Delay(500);
            await exMw.RunExostenzaUiSmokeAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--dialog-lifecycle-smoke") && _window is MainWindow dialogMw)
        {
            await Task.Delay(250);
            LogService.Info(dialogMw.RunDialogLifecycleSmoke());
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--firefox-update-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var first = await FirefoxUpdateService.GetStatusAsync(forceRefresh: true, cts.Token);
                if (!first.Installed || string.IsNullOrWhiteSpace(first.InstalledVersion) ||
                    string.IsNullOrWhiteSpace(first.LatestVersion) || string.IsNullOrWhiteSpace(first.ExePath))
                    throw new InvalidOperationException("Firefox updater status je neúplný.");
                if (!System.IO.File.Exists(first.ExePath))
                    throw new InvalidOperationException("Firefox updater vrátil neexistující firefox.exe.");
                if (FirefoxUpdateService.IsNewer(first.InstalledVersion, first.LatestVersion))
                    throw new InvalidOperationException("Firefox version comparison je obráceně.");
                var cached = await FirefoxUpdateService.GetStatusAsync(token: cts.Token);
                if (!string.Equals(first.InstalledVersion, cached.InstalledVersion, StringComparison.Ordinal) ||
                    !string.Equals(first.LatestVersion, cached.LatestVersion, StringComparison.Ordinal))
                    throw new InvalidOperationException("Firefox updater cache vrátila jiný stav.");
                LogService.Info("firefox",
                    $"FIREFOX_UPDATE_SMOKE_OK installed={first.InstalledVersion} latest={first.LatestVersion} update={first.UpdateAvailable}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("firefox", "FIREFOX_UPDATE_SMOKE_FAIL", ex);
                Environment.Exit(87);
            }
            return;
        }

        if (HasArg(cli, "--software-updater-smoke") && _window is MainWindow updaterMw)
        {
            try
            {
                await Task.Delay(350);
                await updaterMw.RunSoftwareUpdaterSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "SOFTWARE_UPDATER_SMOKE_FAIL", ex);
                Environment.Exit(53);
            }
            return;
        }
        if (HasArg(cli, "--software-updater-release-smoke") && _window is MainWindow updaterReleaseMw)
        {
            try
            {
                await Task.Delay(350);
                var before = WingetUpdateService.GetUpgradeCacheDiagnostics();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await updaterReleaseMw.RunSoftwareUpdaterInlineSmokeAsync();
                long updaterMs = sw.ElapsedMilliseconds;
                var afterUpdater = WingetUpdateService.GetUpgradeCacheDiagnostics();
                int updaterScans = afterUpdater.ScanCount - before.ScanCount;
                if (updaterScans < 0 || updaterScans > 1)
                    throw new InvalidOperationException($"Software Updater expected persistent-cache reuse or one fresh Winget scan, scans={updaterScans}.");

                WingetUpdateService.InvalidateStatusMetadataOnly();
                var status = await WingetUpdateService.GetStatusAsync();
                long totalMs = sw.ElapsedMilliseconds;
                var afterStatus = WingetUpdateService.GetUpgradeCacheDiagnostics();
                int totalScans = afterStatus.ScanCount - before.ScanCount;
                if (totalScans < 0 || totalScans > 1)
                    throw new InvalidOperationException($"Winget status/updater path repeated the upgrade scan, scans={totalScans}.");
                if (!status.Available || string.IsNullOrWhiteSpace(status.Version) ||
                    status.Version.Equals("neznámá", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bundled Winget status did not return a valid fresh version.");

                LogService.Info("winget", $"SOFTWARE_UPDATER_RELEASE_SMOKE_OK version={status.Version} upgrades={status.UpgradeCount} updaterMs={updaterMs} totalMs={totalMs} scans={afterStatus.ScanCount - before.ScanCount}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "SOFTWARE_UPDATER_RELEASE_SMOKE_FAIL", ex);
                Environment.Exit(74);
            }
            return;
        }

        if (HasArg(cli, "--software-updater-inline-smoke") && _window is MainWindow updaterInlineMw)
        {
            try
            {
                await Task.Delay(350);
                await updaterInlineMw.RunSoftwareUpdaterInlineSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "SOFTWARE_UPDATER_INLINE_SMOKE_FAIL", ex);
                Environment.Exit(54);
            }
            return;
        }

        if (HasArg(cli, "--winget-status-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                WingetUpdateService.InvalidateStatus();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var status = await WingetUpdateService.GetStatusAsync(cts.Token);
                sw.Stop();
                long freshMs = sw.ElapsedMilliseconds;
                if (!status.Available)
                    throw new InvalidOperationException("Fresh Winget status reported unavailable: " + status.Message);
                if (string.IsNullOrWhiteSpace(status.Version) ||
                    status.Version.Equals("neznámá", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fresh Winget version probe did not return a valid version.");

                int scansAfterFresh = WingetUpdateService.GetUpgradeCacheDiagnostics().ScanCount;
                WingetUpdateService.ClearMemoryStatusCacheForSmoke();
                sw.Restart();
                var persistedStatus = await WingetUpdateService.GetStatusAsync(cts.Token);
                long persistentMs = sw.ElapsedMilliseconds;
                int scansAfterPersistent = WingetUpdateService.GetUpgradeCacheDiagnostics().ScanCount;
                if (scansAfterPersistent != scansAfterFresh)
                    throw new InvalidOperationException("Persistent Winget status unexpectedly started an upgrade scan.");
                if (persistedStatus.Version != status.Version || persistedStatus.UpgradeCount != status.UpgradeCount || persistedStatus.Available != status.Available)
                    throw new InvalidOperationException("Persistent Winget status mismatch.");
                if (persistentMs > 100)
                    throw new InvalidOperationException($"Persistent Winget status too slow: {persistentMs} ms.");

                LogService.Info("winget", $"WINGET_STATUS_SMOKE_OK version={status.Version} upgrades={status.UpgradeCount} freshMs={freshMs} persistentMs={persistentMs}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "WINGET_STATUS_SMOKE_FAIL", ex);
                Environment.Exit(59);
            }
            return;
        }

        if (HasArg(cli, "--winget-cache-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                WingetUpdateService.InvalidateStatus();
                var before = WingetUpdateService.GetUpgradeCacheDiagnostics();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var a = WingetUpdateService.GetUpgradesAsync(cts.Token);
                var b = WingetUpdateService.GetUpgradesAsync(cts.Token);
                var c = WingetUpdateService.GetUpgradesAsync(cts.Token);
                var coldResults = await Task.WhenAll(a, b, c);
                long coldMs = sw.ElapsedMilliseconds;
                var afterCold = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (afterCold.ScanCount - before.ScanCount != 1)
                    throw new InvalidOperationException($"Winget in-flight dedupe failed scans={afterCold.ScanCount - before.ScanCount}.");
                if (coldResults[0].Count != coldResults[1].Count || coldResults[0].Count != coldResults[2].Count)
                    throw new InvalidOperationException("Winget in-flight callers returned inconsistent counts.");

                sw.Restart();
                var warm = await WingetUpdateService.GetUpgradesAsync(cts.Token);
                long warmMs = sw.ElapsedMilliseconds;
                var afterWarm = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (afterWarm.ScanCount != afterCold.ScanCount)
                    throw new InvalidOperationException("Winget warm cache unexpectedly started a new scan.");
                if (warm.Count != coldResults[0].Count)
                    throw new InvalidOperationException("Winget warm cache count mismatch.");

                UserScopedCacheService.BackdateForSmoke("winget-upgrades-v1", TimeSpan.FromMinutes(5));
                WingetUpdateService.ClearMemoryUpgradesCacheForSmoke();
                var beforeStaleDisplay = WingetUpdateService.GetUpgradeCacheDiagnostics();
                sw.Restart();
                var staleDisplay = WingetUpdateService.TryGetDisplayUpgrades()
                    ?? throw new InvalidOperationException("Winget stale display snapshot was not available.");
                long staleDisplayMs = sw.ElapsedMilliseconds;
                var afterStaleDisplay = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (!staleDisplay.NeedsRefresh)
                    throw new InvalidOperationException("Winget stale display snapshot did not request refresh.");
                if (afterStaleDisplay.ScanCount != beforeStaleDisplay.ScanCount)
                    throw new InvalidOperationException("Winget stale display snapshot unexpectedly started a scan.");
                if (staleDisplay.Items.Count != warm.Count)
                    throw new InvalidOperationException("Winget stale display count mismatch.");
                if (staleDisplayMs > 100)
                    throw new InvalidOperationException($"Winget stale display too slow: {staleDisplayMs} ms.");

                WingetUpdateService.InvalidateStatus();
                var beforeForce = WingetUpdateService.GetUpgradeCacheDiagnostics();
                sw.Restart();
                var forceA = WingetUpdateService.GetUpgradesAsync(cts.Token, forceRefresh: true);
                var forceB = WingetUpdateService.GetUpgradesAsync(cts.Token, forceRefresh: true);
                var forceC = WingetUpdateService.GetUpgradesAsync(cts.Token, forceRefresh: true);
                var forceResults = await Task.WhenAll(forceA, forceB, forceC);
                long forceMs = sw.ElapsedMilliseconds;
                var afterForce = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (afterForce.ScanCount - beforeForce.ScanCount != 1)
                    throw new InvalidOperationException($"Winget force-refresh in-flight dedupe failed scans={afterForce.ScanCount - beforeForce.ScanCount}.");
                if (forceResults[0].Count != forceResults[1].Count || forceResults[0].Count != forceResults[2].Count)
                    throw new InvalidOperationException("Winget force-refresh callers returned inconsistent counts.");

                LogService.Info("winget", $"WINGET_CACHE_SMOKE_OK count={warm.Count} coldMs={coldMs} warmMs={warmMs} staleDisplayMs={staleDisplayMs} forceMs={forceMs} scans={afterForce.ScanCount - before.ScanCount} forceScans={afterForce.ScanCount - beforeForce.ScanCount}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "WINGET_CACHE_SMOKE_FAIL", ex);
                Environment.Exit(58);
            }
            return;
        }

        if (HasArg(cli, "--winget-user-context-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var groups = await UnelevatedProcessRunner.RunAsync("whoami.exe", "/groups", TimeSpan.FromSeconds(10), cts.Token);
                if (!groups.Success || !groups.StdOut.Contains("S-1-16-8192", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unelevated runner nepotvrdil Medium Integrity user token.");

                var version = await UnelevatedProcessRunner.RunAsync("winget.exe", "--version", TimeSpan.FromSeconds(15), cts.Token);
                if (!version.Success || string.IsNullOrWhiteSpace(version.StdOut))
                    throw new InvalidOperationException("Winget se v unelevated user contextu nepodařilo spustit.");

                LogService.Info("winget", $"WINGET_USER_CONTEXT_SMOKE_OK version={version.StdOut.Trim()} integrity=medium");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "WINGET_USER_CONTEXT_SMOKE_FAIL", ex);
                Environment.Exit(59);
            }
            return;
        }

        if (HasArg(cli, "--section-smoke") && _window is MainWindow sectionMw)
        {
            await Task.Delay(500);
            await sectionMw.RunSectionSmokeTestAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--lazy-actionbar-smoke") && _window is MainWindow lazyActionBarMw)
        {
            try
            {
                await lazyActionBarMw.RunLazyActionBarSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("ACTIONBAR_LAZY_SMOKE_FAIL", ex);
                Environment.Exit(67);
            }
            return;
        }

        if (HasArg(cli, "--hero-action-deferred-smoke") && _window is MainWindow heroActionMw)
        {
            try
            {
                await heroActionMw.RunHeroActionDeferredSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("HERO_ACTION_DEFERRED_SMOKE_FAIL", ex);
                Environment.Exit(68);
            }
            return;
        }

        if (HasArg(cli, "--deferred-titlebar-smoke") && _window is MainWindow deferredTitlebarMw)
        {
            try
            {
                await deferredTitlebarMw.RunDeferredTitlebarSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("DEFERRED_TITLEBAR_SMOKE_FAIL", ex);
                Environment.Exit(76);
            }
            return;
        }

        if (HasArg(cli, "--startup-render-smoke") && _window is MainWindow startupRenderMw)
        {
            try
            {
                await Task.Delay(500);
                await startupRenderMw.RunStartupRenderSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("STARTUP_RENDER_SMOKE_FAIL", ex);
                Environment.Exit(59);
            }
            return;
        }

        if (HasArg(cli, "--power-render-smoke") && _window is MainWindow powerRenderMw)
        {
            try
            {
                await Task.Delay(500);
                await powerRenderMw.RunPowerRenderSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWER_RENDER_SMOKE_FAIL", ex);
                Environment.Exit(58);
            }
            return;
        }

        if (HasArg(cli, "--power-ui-smoke") && _window is MainWindow powerUiMw)
        {
            try
            {
                await Task.Delay(500);
                await powerUiMw.RunPowerUiSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWER_UI_SMOKE_FAIL", ex);
                Environment.Exit(55);
            }
            return;
        }

        if (HasArg(cli, "--power-window-backend-smoke"))
        {
            try
            {
                await Task.Delay(500);
                LogService.Info("POWER_WINDOW_BACKEND_SMOKE_START");
                string result = await PerformanceProfilesService.ApplyPerformancePowerProfileAsync();
                LogService.Info("POWER_WINDOW_BACKEND_SMOKE_BACKEND_OK | " + result.Replace(Environment.NewLine, " | "));
                await Task.Delay(1500);
                LogService.Info("POWER_WINDOW_BACKEND_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWER_WINDOW_BACKEND_SMOKE_FAIL", ex);
                Environment.Exit(56);
            }
            return;
        }

        if (HasArg(cli, "--drivers-ui-smoke") && _window is MainWindow driversMw)
        {
            await Task.Delay(500);
            await driversMw.RunDriversUiSmokeAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--task-scheduler-ui-smoke") && _window is MainWindow taskMw)
        {
            await Task.Delay(500);
            await taskMw.RunTaskSchedulerUiSmokeAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--regression-smoke") && _window is MainWindow regressionMw)
        {
            await Task.Delay(500);
            await regressionMw.RunRegressionSmokeTestAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--soak-smoke") && _window is MainWindow soakMw)
        {
            await Task.Delay(500);
            try { await soakMw.RunSoakAuditAsync(settleStartup: true); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("SOAK_SMOKE_FAIL", ex); Environment.Exit(51); }
            return;
        }

        if (HasArg(cli, "--critical-leak-smoke") && _window is MainWindow criticalLeakMw)
        {
            await Task.Delay(500);
            try { await criticalLeakMw.RunCriticalSectionLeakAuditAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("CRITICAL_LEAK_SMOKE_FAIL", ex); Environment.Exit(52); }
            return;
        }

        if (HasArg(cli, "--ui-cache-residency-smoke") && _window is MainWindow cacheResidencyMw)
        {
            await Task.Delay(500);
            try { await cacheResidencyMw.RunUiCacheResidencyAuditAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("UI_CACHE_RESIDENCY_SMOKE_FAIL", ex); Environment.Exit(55); }
            return;
        }

        if (HasArg(cli, "--nav500-smoke") && _window is MainWindow nav500Mw)
        {
            await Task.Delay(500);
            try { await nav500Mw.RunNavigation500SoakAuditAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("NAV500_SMOKE_FAIL", ex); Environment.Exit(54); }
            return;
        }

        if (HasArg(cli, "--navigation-tail-smoke") && _window is MainWindow navTailMw)
        {
            await Task.Delay(500);
            try { await navTailMw.RunNavigationTailLatencySmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("NAV_TAIL_SMOKE_FAIL", ex); Environment.Exit(52); }
            return;
        }

        string? sectionSoak = Array.Find(cli, x => x.StartsWith("--section-soak=", StringComparison.OrdinalIgnoreCase));
        if (sectionSoak != null && _window is MainWindow sectionSoakMw)
        {
            string section = sectionSoak.Substring("--section-soak=".Length);
            await Task.Delay(500);
            try { await sectionSoakMw.RunSingleSectionSoakAuditAsync(section); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("SECTION_SOAK_FAIL " + section, ex); Environment.Exit(53); }
            return;
        }

        if (HasArg(cli, "--shortcut-overlay-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                bool managed = ShortcutAppearanceService.ManagedOverlayConfiguredForSmoke();
                int[] before = System.Diagnostics.Process.GetProcessesByName("explorer").Select(p => { try { return p.Id; } finally { p.Dispose(); } }).OrderBy(x => x).ToArray();

                bool iconChanged = ShortcutAppearanceService.EnsureTransparentIconForSmoke();
                bool restartGuardReady = !managed || ShortcutAppearanceService.PrepareManagedOverlayForExplorerRestart();
                if (!restartGuardReady)
                    throw new InvalidOperationException("Managed shortcut overlay restart guard is not ready.");

                bool migrated = false;
                if (managed)
                    migrated = await ShortcutAppearanceService.MigrateLegacyShortcutOverlayAsync(cts.Token);

                await Task.Delay(350, cts.Token);
                int[] after = System.Diagnostics.Process.GetProcessesByName("explorer").Select(p => { try { return p.Id; } finally { p.Dispose(); } }).OrderBy(x => x).ToArray();
                if (managed && !before.SequenceEqual(after))
                    throw new InvalidOperationException($"Startup shortcut migration restarted Explorer. before={string.Join(',', before)} after={string.Join(',', after)}");

                string iconPath = ShortcutAppearanceService.TransparentIconPathForSmoke;
                string escaped = iconPath.Replace("'", "''");
                string ps = "Add-Type -AssemblyName System.Drawing;$i=[System.Drawing.Icon]::new('" + escaped + "');$b=$i.ToBitmap();$n=0;$maxA=0;for($y=0;$y -lt $b.Height;$y++){for($x=0;$x -lt $b.Width;$x++){$a=$b.GetPixel($x,$y).A;if($a -ne 0){$n++};if($a -gt $maxA){$maxA=$a}}};$w=$b.Width;$h=$b.Height;$b.Dispose();$i.Dispose();if($w -ne 32 -or $h -ne 32 -or $n -gt 1 -or $maxA -gt 1){exit 2}else{'ICON_OK'}";
                var decode = await ProcessRunner.RunAsync("powershell.exe",
                    "-NoLogo -NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"",
                    TimeSpan.FromSeconds(15), cts.Token);
                if (!decode.Success || !decode.StdOut.Contains("ICON_OK", StringComparison.Ordinal))
                    throw new InvalidOperationException("Transparent shortcut ICO Windows decode failed: " + (decode.StdErr + decode.StdOut).Trim());

                LogService.Info("explorer", $"SHORTCUT_OVERLAY_SMOKE_OK managed={managed} iconChanged={iconChanged} restartGuard={restartGuardReady} migrated={migrated} explorerStable={!managed || before.SequenceEqual(after)} bytes={new FileInfo(iconPath).Length}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("explorer", "SHORTCUT_OVERLAY_SMOKE_FAIL", ex);
                Environment.Exit(62);
            }
            return;
        }

        string? perfTraceArg = cli.FirstOrDefault(x =>
            x.StartsWith("--elevated-perf-trace-smoke-b64=", StringComparison.OrdinalIgnoreCase));
        if ((HasArg(cli, "--elevated-perf-trace-smoke") || perfTraceArg != null) && _window is MainWindow traceMw)
        {
            try
            {
                string outputDir = Path.Combine(Path.GetTempPath(), "SpankyOptimizerPerfTrace");
                if (perfTraceArg != null)
                {
                    string encoded = perfTraceArg["--elevated-perf-trace-smoke-b64=".Length..];
                    outputDir = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                }
                else
                {
                    outputDir = Environment.GetEnvironmentVariable("SPANKY_PERF_TRACE_DIR") ?? outputDir;
                }
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(40));
                var session = await PerformanceTraceService.StartAsync(outputDir, cts.Token);
                await Task.Delay(500, cts.Token);
                await traceMw.RunTraceWorkloadAsync();
                string result = await PerformanceTraceService.StopAsync(session, cts.Token);
                await File.WriteAllTextAsync(Path.Combine(outputDir, "trace.complete"), result, cts.Token);
                LogService.Info("performance", result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PERF_TRACE_SMOKE_FAIL", ex); Environment.Exit(50); }
            return;
        }

        if (HasArg(cli, "--performance-smoke") && _window is MainWindow perfMw)
        {
            await Task.Delay(350);
            try { await perfMw.RunPerformanceSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("PERFORMANCE_SMOKE_FAIL", ex); Environment.Exit(50); }
            return;
        }

        if (HasArg(cli, "--idle-cpu-smoke") && _window is MainWindow idleCpuMw)
        {
            await Task.Delay(350);
            try { await idleCpuMw.RunIdleCpuAuditAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("IDLE_CPU_SMOKE_FAIL", ex); Environment.Exit(50); }
            return;
        }

        if (HasArg(cli, "--ai-progress-ui-smoke") && _window is MainWindow aiProgressMw)
        {
            try
            {
                await Task.Delay(350);
                await aiProgressMw.RunAiProgressUiSmokeAsync();
                LogService.Info("ai", "AI_PROGRESS_UI_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("ai", "AI_PROGRESS_UI_SMOKE_FAIL", ex);
                Environment.Exit(61);
            }
            return;
        }

        if (HasArg(cli, "--ai-runtime-hardening-smoke"))
        {
            try
            {
                await RunAiRuntimeHardeningSmokeCoreAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("ai", "AI_RUNTIME_HARDENING_SMOKE_FAIL", ex);
                Environment.Exit(60);
            }
            return;
        }

        if (HasArg(cli, "--ai-cache-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string p1 = await ZoicwareToolsService.RefreshRemoveWindowsAiAsync();
                long firstMs = sw.ElapsedMilliseconds;
                string h1 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(p1)));
                sw.Restart();
                string p2 = await ZoicwareToolsService.RefreshRemoveWindowsAiAsync();
                long secondMs = sw.ElapsedMilliseconds;
                string h2 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(p2)));
                LogService.Info($"AI_CACHE_SMOKE_OK firstMs={firstMs} secondMs={secondMs} samePath={p1 == p2} sameHash={h1 == h2} bytes={new FileInfo(p2).Length}");
                Environment.Exit(p1 == p2 && h1 == h2 ? 0 : 54);
            }
            catch (Exception ex) { LogService.Error("AI_CACHE_SMOKE_FAIL", ex); Environment.Exit(54); }
            return;
        }

        string[] unhandledSwitches = cli.Skip(1)
            .Where(x => x.StartsWith("--", StringComparison.Ordinal) &&
                        !x.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (unhandledSwitches.Length > 0)
        {
            LogService.Error("startup", "UNKNOWN_CLI_SWITCH rejected=" + string.Join(",", unhandledSwitches));
            Environment.Exit(64);
            return;
        }

        StartupDiagnostics.Complete(synchronousFlush: smokeMode);
        if (!smokeMode)
        {
            CrashLoopGuardService.CompleteStartup(AppVersion);
            LogService.Info("startup", "Startup background prefetch disabled; heavy modules load on demand.");
        }
    }

    private static async Task RunSpicetifyStatusSmokeCoreAsync()
    {
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        SpicetifyService.InvalidateStatusCache();
        var status = await SpicetifyService.GetStatusAsync(cts.Token);

        if (!status.SpicetifyInstalled)
        {
            if (status.MarketplaceInstalled)
                throw new InvalidOperationException("Marketplace nesmí být Installed, když Spicetify CLI chybí.");
            LogService.Info("spicetify", "SPICETIFY_STATUS_SMOKE_OK cli=missing marketplace=false");
            return;
        }

        var config = await UnelevatedProcessRunner.RunAsync(status.ExecutablePath, "config custom_apps",
            TimeSpan.FromSeconds(15), cts.Token);
        if (!config.Success)
            throw new InvalidOperationException("Spicetify config custom_apps read-back selhal: " + (config.StdErr + " " + config.StdOut).Trim());

        bool cliHasMarketplace = config.StdOut.Contains("marketplace", StringComparison.OrdinalIgnoreCase);
        if (status.MarketplaceInstalled != cliHasMarketplace)
            throw new InvalidOperationException($"Marketplace status nesouhlasí s CLI configem. status={status.MarketplaceInstalled} cli={cliHasMarketplace}");

        LogService.Info("spicetify", $"SPICETIFY_STATUS_SMOKE_OK version={status.Version} marketplace={status.MarketplaceInstalled} cliPath={status.ExecutablePath}");
    }

    private static async Task RunAiRuntimeHardeningSmokeCoreAsync()
    {
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        string source = await ZoicwareToolsService.RefreshRemoveWindowsAiAsync(cts.Token);
        string runtime = await ZoicwareToolsService.PrepareHardenedRuntimeScriptAsync(source, cts.Token);
        string sourceText = await File.ReadAllTextAsync(source, cts.Token);
        string runtimeText = await File.ReadAllTextAsync(runtime, cts.Token);
        bool legacySource = sourceText.Contains("$R.WaitForExit()", StringComparison.Ordinal);
        bool nativeSource = ZoicwareToolsService.HasNativeTrustedInstallerLifecycle(sourceText);
        if (!legacySource && !nativeSource)
            throw new InvalidOperationException("Upstream TrustedInstaller guard nemá podporovaný tvar.");
        if (!ZoicwareToolsService.HasHardenedTrustedInstallerRuntime(runtimeText))
            throw new InvalidOperationException("Runtime TrustedInstaller hardening nebyl aplikován.");

        string escaped = runtime.Replace("'", "''");
        string ps = "$e=$null;$t=$null;[System.Management.Automation.Language.Parser]::ParseFile('" + escaped + "',[ref]$t,[ref]$e)|Out-Null;if($e.Count){$e|ForEach-Object{$_.Message};exit 2}else{'PARSE_OK'}";
        var parse = await ProcessRunner.RunAsync("powershell.exe",
            "-NoLogo -NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"",
            TimeSpan.FromSeconds(15), cts.Token);
        if (!parse.Success || !parse.StdOut.Contains("PARSE_OK", StringComparison.Ordinal))
            throw new InvalidOperationException("Hardened RemoveWindowsAI runtime PowerShell parse failed: " + (parse.StdErr + parse.StdOut).Trim());

        LogService.Info("ai", $"AI_RUNTIME_HARDENING_SMOKE_OK sourceBytes={sourceText.Length} runtimeBytes={runtimeText.Length} parse=ok");
    }

    private static async Task RunDebloatCacheSmokeCoreAsync()
    {
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
        InstalledAppService.InvalidateCache();
        InstalledAppService.ClearPersistentLogoCacheForSmoke();
        InstalledAppService.RunLogoRebaseSmoke();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = await InstalledAppService.GetInstalledAsync(cts.Token, forceRefresh: true, includeLogos: false);
        long freshSummaryMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var detail = await InstalledAppService.GetInstalledAsync(cts.Token, forceRefresh: false, includeLogos: true);
        long detailMs = sw.ElapsedMilliseconds;
        int logoCount = detail.Count(x => !string.IsNullOrWhiteSpace(x.LogoPath));

        InstalledAppService.ClearMemoryCacheForSmoke();
        sw.Restart();
        var persisted = await InstalledAppService.GetInstalledAsync(cts.Token, forceRefresh: false, includeLogos: false);
        long persistentMs = sw.ElapsedMilliseconds;
        bool refreshPending = InstalledAppService.SummaryNeedsBackgroundRefresh;

        sw.Restart();
        var instantDetail = InstalledAppService.TryGetFreshSnapshot(includeLogos: true)
            ?? throw new InvalidOperationException("Debloater persistent detail snapshot was not hydrated.");
        long instantDetailMs = sw.ElapsedMilliseconds;
        int instantLogoCount = instantDetail.Count(x => !string.IsNullOrWhiteSpace(x.LogoPath));
        bool refreshStillPendingAfterInstantDetail = InstalledAppService.SummaryNeedsBackgroundRefresh;

        sw.Restart();
        var warmDetail = await InstalledAppService.GetInstalledAsync(cts.Token, forceRefresh: true, includeLogos: true);
        long warmDetailMs = sw.ElapsedMilliseconds;
        int warmLogoCount = warmDetail.Count(x => !string.IsNullOrWhiteSpace(x.LogoPath));
        bool refreshCleared = !InstalledAppService.SummaryNeedsBackgroundRefresh;

        int joinsBefore = InstalledAppService.SummaryJoinDetailCountForSmoke;
        InstalledAppService.InvalidateCache();
        var concurrentDetailTask = InstalledAppService.GetInstalledAsync(cts.Token, forceRefresh: false, includeLogos: true);
        var concurrentSummaryTask = InstalledAppService.GetInstalledAsync(cts.Token, forceRefresh: false, includeLogos: false);
        await Task.WhenAll(concurrentDetailTask, concurrentSummaryTask);
        var concurrentDetail = await concurrentDetailTask;
        var concurrentSummary = await concurrentSummaryTask;
        int joinedDetailFlights = InstalledAppService.SummaryJoinDetailCountForSmoke - joinsBefore;
        if (joinedDetailFlights < 1)
            throw new InvalidOperationException("Debloater summary did not join the active detail inventory flight.");
        if (Math.Abs(concurrentDetail.Count - concurrentSummary.Count) > 2)
            throw new InvalidOperationException($"Debloater joined inventory count mismatch detail={concurrentDetail.Count} summary={concurrentSummary.Count}.");

        UserScopedCacheService.BackdateForSmoke("installed-apps-summary-v2", TimeSpan.FromMinutes(20));
        InstalledAppService.ClearMemoryCacheForSmoke();
        var staleDisplay = InstalledAppService.TryGetDisplaySnapshot()
            ?? throw new InvalidOperationException("Debloater stale display snapshot was not available.");
        bool staleRefreshPending = InstalledAppService.SummaryNeedsBackgroundRefresh;
        sw.Restart();
        var staleFreshSummary = await InstalledAppService.GetInstalledAsync(
            cts.Token, forceRefresh: true, includeLogos: false);
        long staleFreshSummaryMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var staleDetail = InstalledAppService.TryGetFreshSnapshot(includeLogos: true)
            ?? throw new InvalidOperationException("Debloater stale summary refresh did not preserve persistent detail hydration.");
        long staleDetailHydrateMs = sw.ElapsedMilliseconds;
        bool staleRefreshCleared = !InstalledAppService.SummaryNeedsBackgroundRefresh;

        var logoSample = warmDetail.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.LogoPath))
            ?? throw new InvalidOperationException("Debloater persistent-logo smoke found no sample icon.");
        InstalledAppService.ClearMemoryCacheForSmoke();
        sw.Restart();
        string? lazyLogo = await InstalledAppService.GetLogoPathAsync(logoSample.PackageFullName, cts.Token);
        long lazyLogoMs = sw.ElapsedMilliseconds;

        if (summary.Count == 0 || detail.Count == 0 || persisted.Count == 0 || instantDetail.Count == 0 || warmDetail.Count == 0 || staleDisplay.Count == 0 || staleFreshSummary.Count == 0 || staleDetail.Count == 0)
            throw new InvalidOperationException("Debloater cache smoke returned an empty inventory.");
        if (logoCount == 0 || instantLogoCount == 0 || warmLogoCount == 0)
            throw new InvalidOperationException("Debloater logo resolver returned zero icons.");
        if (refreshPending)
            throw new InvalidOperationException("Fresh persistent Debloater snapshot incorrectly requested a background refresh.");
        if (refreshStillPendingAfterInstantDetail)
            throw new InvalidOperationException("Instant Debloater detail hydration incorrectly requested a background refresh.");
        if (!refreshCleared)
            throw new InvalidOperationException("Fresh Debloater detail refresh left refreshPending set.");
        if (!staleRefreshPending)
            throw new InvalidOperationException("Stale Debloater display snapshot did not request a background refresh.");
        if (!staleRefreshCleared)
            throw new InvalidOperationException("Fresh Debloater summary refresh did not clear refreshPending.");
        if (Math.Abs(summary.Count - persisted.Count) > 2 ||
            Math.Abs(instantDetail.Count - persisted.Count) > 2 ||
            Math.Abs(warmDetail.Count - persisted.Count) > 2 ||
            Math.Abs(staleFreshSummary.Count - persisted.Count) > 2 ||
            Math.Abs(staleDetail.Count - persisted.Count) > 2)
            throw new InvalidOperationException($"Debloater cache count mismatch fresh={summary.Count} persisted={persisted.Count} instant={instantDetail.Count} warmDetail={warmDetail.Count} staleSummary={staleFreshSummary.Count} staleDetail={staleDetail.Count}.");
        if (Math.Abs(logoCount - instantLogoCount) > 2 || Math.Abs(logoCount - warmLogoCount) > 2)
            throw new InvalidOperationException($"Debloater persistent logo cache mismatch cold={logoCount} instant={instantLogoCount} warm={warmLogoCount}.");
        if (string.IsNullOrWhiteSpace(lazyLogo) || !File.Exists(lazyLogo))
            throw new InvalidOperationException("Debloater lazy persistent-logo lookup did not return a valid file.");

        LogService.Info($"DEBLOAT_CACHE_SMOKE_OK summary={summary.Count} detail={detail.Count} logos={logoCount} instantDetail={instantDetail.Count} instantLogos={instantLogoCount} warmLogos={warmLogoCount} staleSummary={staleFreshSummary.Count} staleDetail={staleDetail.Count} freshSummaryMs={freshSummaryMs} detailMs={detailMs} persistentMs={persistentMs} instantDetailMs={instantDetailMs} warmDetailMs={warmDetailMs} staleFreshSummaryMs={staleFreshSummaryMs} staleDetailHydrateMs={staleDetailHydrateMs} lazyLogoMs={lazyLogoMs}");
    }

    private static async Task<int> RunReleaseServiceSmokeBundleAsync()
    {
        static async Task RunAsync(string gate, Func<Task> action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await action();
            sw.Stop();
            LogService.Info("release-smoke", $"{gate}_OK ms={sw.ElapsedMilliseconds}");
        }

        try
        {
            await RunAsync("MACHINE_STABLE_MEMORY_CACHE_SMOKE", () =>
            {
                PersistentHardwareCacheService.RunMachineStableMemoryCacheSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("HARDWARE_IDENTITY_SMOKE", async () =>
            {
                var fresh = await SystemInfoService.GetSnapshotAsync(forceRefresh: true);
                var cached = await SystemInfoService.GetSnapshotAsync(forceRefresh: false);
                if (string.IsNullOrWhiteSpace(fresh.Cpu) || fresh.Cpu.Contains("neuvedeno", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CPU identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.Motherboard) || fresh.Motherboard.Contains("neuvedena", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Motherboard identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.RamHardware) || !fresh.RamHardware.Contains("GB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RAM hardware identity is missing.");
                if (!string.Equals(fresh.Cpu, cached.Cpu, StringComparison.Ordinal) ||
                    !string.Equals(fresh.Motherboard, cached.Motherboard, StringComparison.Ordinal) ||
                    !string.Equals(fresh.RamHardware, cached.RamHardware, StringComparison.Ordinal))
                    throw new InvalidOperationException("Hardware identity cache mismatch.");
            });

            await RunAsync("STORAGE_PROBE_SMOKE", async () =>
            {
                var disks = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (disks.Count == 0 || disks.Any(x => string.IsNullOrWhiteSpace(x.Name)))
                    throw new InvalidOperationException("Storage probe returned invalid disk inventory.");
                if (disks.All(x => string.IsNullOrWhiteSpace(x.DriverProvider) || string.IsNullOrWhiteSpace(x.DriverVersion)))
                    throw new InvalidOperationException("Storage probe returned no verified disk driver metadata.");
                NativeSystemProbeService.InvalidateStorageCache(preserveStableDriverMetadata: true);
                var refreshed = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (refreshed.Count != disks.Count)
                    throw new InvalidOperationException("Storage refresh changed disk count unexpectedly.");
            });

            await RunAsync("STORAGE_TEMPERATURE_STALE_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                await NativeSystemProbeService.RunStorageTemperatureStaleCacheSmokeAsync(cts.Token);
            });

            await RunAsync("WRITE_AHEAD_TRANSACTION_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WriteAheadTransactionService.RunPersistenceSmokeAsync(cts.Token);
            });

            await RunAsync("WORKER_IPC_CLEANUP_SMOKE", () =>
            {
                WorkerClientService.RunIpcCleanupSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("UPDATE_RETENTION_SMOKE", () =>
            {
                AppUpdateService.RunRetentionSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("STARTUP_INVENTORY_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                StartupManagerService.InvalidateCache();
                int before = StartupManagerService.LoadCountForDiagnostics;
                var first = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var second = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var pair = await Task.WhenAll(first, second).WaitAsync(cts.Token);
                int loads = StartupManagerService.LoadCountForDiagnostics - before;
                if (loads != 1)
                    throw new InvalidOperationException($"Startup inventory single-flight failed: loads={loads}.");
                var items = pair[0];
                if (items.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                    throw new InvalidOperationException("Startup inventory contains duplicate IDs.");
            });

            await RunAsync("TWEAK_DRIFT_WATCHDOG_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
                _ = await TweakDriftWatchdogService.CheckAppliedTweaksAsync(cts.Token);
            });

            await RunAsync("WINDOWS_BUILD_REVALIDATION_SMOKE", () =>
            {
                var state = WindowsBuildRevalidationService.CheckAndUpdate();
                int current = Environment.OSVersion.Version.Build;
                if (state.CurrentBuild != current)
                    throw new InvalidOperationException($"Windows build revalidation mismatch: state={state.CurrentBuild} current={current}.");
                return Task.CompletedTask;
            });

            await RunAsync("NIC_CAPABILITY_SMOKE", async () =>
            {
                NativeNicTuningService.ValidateProviderReadStateClassificationForSmoke();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                var caps = await PerformanceProfilesService.GetNicCapabilitiesAsync(cts.Token);
                string adapter = caps.TryGetValue("Adapter", out var adapterName) ? adapterName : string.Empty;
                if (string.IsNullOrWhiteSpace(adapter))
                    throw new InvalidOperationException("NIC capability smoke did not find an active adapter.");
                foreach (string key in new[] { "Interrupt Moderation", "Flow Control", "Packet Coalescing", "RSS", "RSC", "Selective Suspend" })
                    if (!caps.ContainsKey(key))
                        throw new InvalidOperationException("NIC capability smoke missing: " + key);
            });

            await RunAsync("NETWORK_TRANSITION_SINGLEFLIGHT_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await NetworkTransitionStateService.RunSingleFlightSmokeAsync(cts.Token);
            });

            await RunAsync("MEMORY_COMPRESSION_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WindowsActions.RunMemoryCompressionCacheSmokeAsync(cts.Token);
            });

            await RunAsync("HARDWARE_MEMORY_TRIM_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                await HardwareSnapshotService.RunMemoryTrimPersistentCacheSmokeAsync(cts.Token);
            });

            await RunAsync("GHOST_USB_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await GhostUsbService.RunPersistentCacheSmokeAsync(cts.Token);
            });

            await RunAsync("USB_SELECTIVE_SUSPEND_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                bool? before = await WindowsActions.GetUsbSelectiveSuspendDisabledAsync(cts.Token);
                if (!before.HasValue)
                    throw new InvalidOperationException("USB Selective Suspend AC state could not be read.");
                await WindowsActions.SetUsbSelectiveSuspendAsync(before.Value, cts.Token);
                bool? after = await WindowsActions.GetUsbSelectiveSuspendDisabledAsync(cts.Token);
                if (after != before)
                    throw new InvalidOperationException($"USB Selective Suspend round-trip mismatch: before={before} after={after}.");
                LogService.Info("release-smoke", $"USB_SELECTIVE_SUSPEND_STATE disabled={after.Value}");
            });

            await RunAsync("FIREWALL_STATE_CACHE_SMOKE", () =>
            {
                FirewallStateService.RunPersistentCacheSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("AUTHENTICODE_NATIVE_SMOKE", () =>
            {
                _ = AuthenticodeVerifier.RunSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("AI_RUNTIME_HARDENING_SMOKE", RunAiRuntimeHardeningSmokeCoreAsync);

            await RunAsync("STARTUP_WARM_APPLY_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await ApplyProfileSmokeService.RunStartupWarmApplyAsync(cts.Token);
                LogService.Info("release-smoke", "STARTUP_WARM_APPLY_SMOKE_OK | " + result);
            });

            await RunAsync("SPICETIFY_STATUS_SMOKE", RunSpicetifyStatusSmokeCoreAsync);

            await RunAsync("PROVIDER_DEGRADATION_SMOKE", () =>
            {
                var mem = CatalogService.GetTweaks("memory").First(x => x.Id.Equals("mem.compression", StringComparison.OrdinalIgnoreCase));
                var caps = new WindowsCapabilitySnapshot(Environment.OSVersion.Version.Build, "smoke", true, false, true, true, true, new[] { "PS_MMAgent" });
                var check = TweakCompatibilityService.CheckStatic(mem, caps);
                if (check.Supported)
                    throw new InvalidOperationException("Provider degradation smoke expected Unsupported.");
                return Task.CompletedTask;
            });

            await RunAsync("OPTIMIZATION_ADVISOR_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(35));
                await OptimizationAdvisorSmokeService.RunAsync(cts.Token);
            });

            await RunAsync("DNS_BENCHMARK_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                var results = await DnsBenchmarkService.RunSmokeAsync(cts.Token);
                var alive = results.Where(x => x.IsAlive).ToArray();
                if (results.Count < 8)
                    throw new InvalidOperationException($"DNS benchmark returned too few smoke resolvers: {results.Count}.");
                if (alive.Length == 0)
                    throw new InvalidOperationException("DNS benchmark received no resolver responses.");
                if (alive.Any(x => x.AverageMs <= 0 || x.Successful <= 0))
                    throw new InvalidOperationException("DNS benchmark returned invalid latency data.");
                LogService.Info("release-smoke",
                    $"DNS_BENCHMARK_SMOKE_OK total={results.Count} alive={alive.Length} best={alive[0].Name} avgMs={alive[0].AverageMs:0.0}");
            });

            await RunAsync("DATA_ROOT_RECOGNITION_SMOKE", () =>
            {
                LogService.Info("release-smoke", DataRootCleanupService.RunRecognitionSmoke());
                return Task.CompletedTask;
            });

            LogService.Info("release-smoke", "RELEASE_SERVICE_SMOKE_BUNDLE_OK count=23");
            return 0;
        }
        catch (Exception ex)
        {
            LogService.Error("release-smoke", "RELEASE_SERVICE_SMOKE_BUNDLE_FAIL", ex);
            return 72;
        }
    }

    private static bool HasArg(string[] args, string value) =>
        Array.Exists(args, x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

    private static async Task RunBackendSmokeAsync(string name, int failureCode,
        Func<System.Threading.CancellationToken, Task<string>> action)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
            string result = await action(cts.Token);
            LogService.Info(name + "_SMOKE_OK | " + result.Replace(Environment.NewLine, " | "));
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            LogService.Error(name + "_SMOKE_FAIL", ex);
            Environment.Exit(failureCode);
        }
    }
}
