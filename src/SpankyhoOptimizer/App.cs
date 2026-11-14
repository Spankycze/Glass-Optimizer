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
    private const int SwShowNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int SmokeWindowX = -32000;
    private const int SmokeWindowY = -32000;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    internal static bool IsSmokeUi { get; private set; }

    private static void PrepareSmokeWindow(Window window)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetWindowPos(hwnd, IntPtr.Zero, SmokeWindowX, SmokeWindowY, 1100, 760, SwpNoActivate | SwpNoZOrder);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = false;
        }
        catch { }
    }

    private static void ShowSmokeWindowWithoutActivation(Window window)
    {
        PrepareSmokeWindow(window);
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.Show(false);
        }
        catch
        {
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                ShowWindow(hwnd, SwShowNoActivate);
            }
            catch { }
        }
    }

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

        if (HasArg(cli, "--automatic-update-popup"))
        {
            string? manifestArgument = Array.Find(cli, x => x.StartsWith("--automatic-update-popup-manifest=", StringComparison.Ordinal));
            AppUpdateManifest? manifest;
            if (manifestArgument != null)
                manifest = AppUpdateService.DeserializePopupManifest(manifestArgument["--automatic-update-popup-manifest=".Length..]);
            else
            {
                AppUpdateCheck check = await AppUpdateService.CheckOfficialGithubAsync(_lifetimeCts.Token);
                manifest = check.Available ? check.Manifest : null;
            }
            if (manifest == null)
            {
                Environment.Exit(2);
                return;
            }
            bool completed = await MainWindow.RunAutomaticAppUpdatePopupAsync(manifest);
            Environment.Exit(completed ? 0 : 1);
            return;
        }

        if (HasArg(cli, "--fan-agent"))
        {
            await FanProfileService.RunAgentAsync();
            Environment.Exit(0);
            return;
        }

        if (HasArg(cli, "--ai-agent"))
        {
            await FanProfileService.RunAiAgentAsync();
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
        bool nonStartupSmokeLaunch = cli.Any(x => x.Contains("smoke", StringComparison.OrdinalIgnoreCase)) &&
                                     !startupHotPathSmoke &&
                                     !startupIsolatedBundle;
        if (safeModeStartup)
            await WaitForSafeModeHandoffAsync(cli);
        bool normalGuiLaunch = cli.Length <= 1 || safeModeStartup || (openSectionLaunch && !nonStartupSmokeLaunch);

        if (normalGuiLaunch && !await AcquireSingleInstanceWithHandoffAsync())
        {
            LogService.Info("startup", "SECOND_INSTANCE_HANDOFF_FAILED");
            Environment.Exit(0);
            return;
        }

        if (cli.Length <= 1 || startupHotPathSmoke || startupIsolatedBundle || runtimeReadyProbe || safeModeStartup ||
            (openSectionLaunch && !nonStartupSmokeLaunch))
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

        bool runtimeReadyProbe = cli.Any(x => x.StartsWith("--runtime-ready-file=", StringComparison.OrdinalIgnoreCase));
        // Readiness-only CLI must not touch persistent crash-loop history.
        Task<bool>? crashLoopStartupTask = !smokeMode && !runtimeReadyProbe
            ? Task.Run(() => CrashLoopGuardService.BeginStartup(AppVersion))
            : null;
        if (!smokeMode && !runtimeReadyProbe && !_ownsSingleInstanceMutex && !await AcquireSingleInstanceWithHandoffAsync())
        {
            LogService.Info("startup", "SECOND_INSTANCE_HANDOFF_FAILED_BEFORE_WINDOW");
            Environment.Exit(0);
            return;
        }

        StartupDiagnostics.Mark("MainWindow.Create");
        _window = new MainWindow();
        var mainWindow = _window as MainWindow;
        _window.Closed += (_, _) =>
        {
            try { mainWindow?.ShutdownActiveOperations(); } catch { }
            try { _lifetimeCts.Cancel(); } catch { }
            HardwareChangeMonitorService.Stop();
            ReleaseSingleInstance();
        };
        StartupDiagnostics.Mark("MainWindow.Created");

        IsSmokeUi = smokeMode || runtimeReadyProbe;
        if (IsSmokeUi) ShowSmokeWindowWithoutActivation(_window);
        else _window.Activate();
        StartupDiagnostics.Mark("Window.Activated");
        long windowActivatedMs = StartupDiagnostics.ElapsedMilliseconds;

        // Crash-loop persistence starts in parallel before MainWindow creation, but disk/registry
        // latency must never block the first visible frame. Await it only after activation so
        // CompleteStartup still observes a finished BeginStartup transaction.
        if (crashLoopStartupTask != null)
            await crashLoopStartupTask;

        if (!smokeMode && !runtimeReadyProbe)
        {
            SafeTaskService.Forget(
                WatchSingleInstanceHandoffAsync(_window, _lifetimeCts.Token),
                "startup",
                "Single-instance handoff watcher");
            // Migration is intentionally after first paint; only already-enabled
            // custom Explorer verbs are upgraded and smoke runs never touch HKCU.
            SafeTaskService.Forget(
                MigrateContextCommandsDeferredAsync(_lifetimeCts.Token),
                "shell", "Native context menu upgrade");
            SafeTaskService.Forget(
                CheckIntegrationUpdatesAfterStartupAsync(_lifetimeCts.Token),
                "integrations", "Deferred Chris Titus update discovery");
            // Runs after first paint. Never writes Windows without a user's
            // explicit per-group approval in Settings.
            SafeTaskService.Forget(
                WindowsDefaultGuardService.RunAsync(_lifetimeCts.Token),
                "settings", "Approved Windows default registry guard");
        }

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
        if (!runtimeReadyProbe)
        {
            CrashLoopGuardService.CompleteStartup(AppVersion);

            // Readiness probes must not queue maintenance or touch the
            // installed fan agent, registry, service or user feature state.
            SafeTaskService.ForgetDeferred(
                RunPostReadyStartupMaintenanceAsync(_lifetimeCts.Token),
                "startup",
                "Post-ready startup maintenance");
            LogService.Info("startup", "Startup background prefetch disabled; heavy modules load on demand.");
        }

        string? explicitReadyArg = cli
            .FirstOrDefault(x => x.StartsWith("--runtime-ready-file=", StringComparison.OrdinalIgnoreCase));
        bool explicitReadiness = explicitReadyArg is not null;
        string? runtimeReadyFile = explicitReadyArg?
            .Substring("--runtime-ready-file=".Length)
            .Trim('"');
        if (!explicitReadiness && string.IsNullOrWhiteSpace(runtimeReadyFile))
            runtimeReadyFile = Environment.GetEnvironmentVariable("SPANKY_RUNTIME_READY_FILE");
        string isolatedMarker = string.Empty;
        if (explicitReadiness &&
            !TryValidateIsolatedReadinessMarkerPath(runtimeReadyFile, out isolatedMarker))
        {
            LogService.Error("startup", "RUNTIME_READY_INVALID_ISOLATED_MARKER_PATH");
            Environment.Exit(74);
            return;
        }
        if (!string.IsNullOrWhiteSpace(runtimeReadyFile))
        {
            try
            {
                string markerText =
                    $"version={AppVersion}{Environment.NewLine}activatedMs={windowActivatedMs}{Environment.NewLine}pid={Environment.ProcessId}{Environment.NewLine}";
                if (explicitReadiness)
                {
                    // FileMode.CreateNew rejects an existing marker even if
                    // another process creates it after path validation.
                    await using var marker = new FileStream(isolatedMarker, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await marker.WriteAsync(System.Text.Encoding.UTF8.GetBytes(markerText));
                    await marker.FlushAsync();
                }
                else
                {
                    string? readyDir = Path.GetDirectoryName(runtimeReadyFile);
                    if (!string.IsNullOrWhiteSpace(readyDir)) Directory.CreateDirectory(readyDir);
                    await File.WriteAllTextAsync(runtimeReadyFile, markerText);
                }
                LogService.Info("startup", $"RUNTIME_READY_SIGNAL_OK activatedMs={windowActivatedMs}");
                if (explicitReadiness)
                {
                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogService.Error("startup", "Runtime readiness signal failed", ex);
                if (explicitReadiness)
                {
                    Environment.Exit(74);
                    return;
                }
            }
        }
    }

    private async Task RunPostReadyStartupMaintenanceAsync(System.Threading.CancellationToken token)
    {
        // Give the first interactive second to the shell/Overview before background cleanup,
        // revalidation and hardware monitors begin competing for CPU, disk and thread-pool time.
        await Task.Delay(900, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        int removedDataRoots = DataRootCleanupService.CleanupUserDataRoots();
        if (removedDataRoots > 0)
            LogService.Info("startup", $"DATA_ROOT_CLEANUP_OK removed={removedDataRoots}");

        // Task Scheduler, agent-version files and heartbeat/process checks may
        // block on disk or COM. Run them independently of the post-ready
        // continuation so they cannot delay other UI-facing background work.
        // Keep the order within this task (registration -> update -> startup).
        SafeTaskService.Forget(
            Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                BackgroundUpdateService.EnsureStartupRegistration();
                token.ThrowIfCancellationRequested();
                if (LianLiSlInfinityService.Detect().Present)
                {
                    FanProfileService.EnsureStartupAgentRegistered();
                    token.ThrowIfCancellationRequested();
                    FanProfileService.EnsureAgentUpdated();
                    token.ThrowIfCancellationRequested();
                    FanProfileService.StartAgentIfNeeded();
                }
                else
                {
                    FanProfileService.EnsureAiStartupAgentRegistered();
                    FanProfileService.StartAiAgentIfNeeded();
                }
            }, token),
            "startup", "Fan agent post-ready maintenance");

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

            await WriteSingleInstanceHandoffAsync(
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

        // Isolated regression: pure registry decisions plus a disposable temp journal.
        // No live Windows registry writes, approval changes or NIC operations.
        if (await DispatchDiagnosticsAsync(cli)) return;

        string? cursorApplySmokeArg = cli.FirstOrDefault(x => x.Equals("--community-cursor-apply-smoke", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("--community-cursor-apply-smoke=", StringComparison.OrdinalIgnoreCase));
        if (cursorApplySmokeArg != null)
        {
            try
            {
                string? requestedId = cursorApplySmokeArg.Contains('=') ? cursorApplySmokeArg.Split('=', 2)[1] : null;
                string? activeId = requestedId ?? CursorCommunityPackService.CurrentCommunityPackId;
                CommunityCursorPack? pack = CursorCommunityPackService.LoadBundledPacks()
                    .FirstOrDefault(p => string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase));
                if (pack == null) throw new InvalidOperationException("Active cursor pack is not available.");
                CursorCommunityPackService.Apply(pack);
                LogService.Info("cursor", "CURSOR_APPLY_SMOKE_OK id=" + pack.Id);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("cursor", "CURSOR_APPLY_SMOKE_FAIL", ex);
                Environment.Exit(114);
            }
            return;
        }

        if (await DispatchTestsAsync(cli)) return;

        if (await DispatchRepairAsync(cli)) return;

        if (await DispatchAiAsync(cli)) return;

        if (await DispatchRegistryAsync(cli)) return;

        if (await DispatchTweaksAsync(cli)) return;

        if (await DispatchPowerNetworkAsync(cli)) return;
        if (await DispatchStorageAsync(cli)) return;

        if (await DispatchWorkersAsync(cli)) return;
        if (await DispatchIntegrationsAsync(cli)) return;
        if (await DispatchPipelineAsync(cli)) return;
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
        IsSmokeUi = smokeMode;
        if (smokeMode) ShowSmokeWindowWithoutActivation(_window);
        else _window.Activate();
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

        if (await DispatchUiAsync(cli, windowActivatedMs)) return;

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

        if (await DispatchPagesAsync(cli)) return;

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
                string realStateBefore = ShortcutAppearanceService.ReadRealOverlayFingerprintForSmoke();
                int[] before = System.Diagnostics.Process.GetProcessesByName("explorer").Select(p => { try { return p.Id; } finally { p.Dispose(); } }).OrderBy(x => x).ToArray();

                // Release smoke must never run the production icon write, legacy HKLM/HKCU
                // migration or shell/icon-cache refresh against the live user profile.
                string isolated = await ShortcutAppearanceService.RunIsolatedPersistenceSmokeAsync();
                if (!isolated.Contains("actualUserSettingsTouched=false", StringComparison.Ordinal) ||
                    !isolated.Contains("shellRefreshSkipped=true", StringComparison.Ordinal))
                    throw new InvalidOperationException("Isolated shortcut overlay smoke did not confirm isolation.");

                await Task.Delay(350, cts.Token);
                int[] after = System.Diagnostics.Process.GetProcessesByName("explorer").Select(p => { try { return p.Id; } finally { p.Dispose(); } }).OrderBy(x => x).ToArray();
                if (managed && !before.SequenceEqual(after))
                    throw new InvalidOperationException($"Startup shortcut migration restarted Explorer. before={string.Join(',', before)} after={string.Join(',', after)}");

                string realStateAfter = ShortcutAppearanceService.ReadRealOverlayFingerprintForSmoke();
                if (!string.Equals(realStateBefore, realStateAfter, StringComparison.Ordinal))
                    throw new InvalidOperationException("Shortcut overlay smoke changed real shell state.");

                string iconProbe = Path.Combine(Path.GetTempPath(), "SpankyShortcutIconSmoke-" + Guid.NewGuid().ToString("N") + ".ico");
                try
                {
                    await File.WriteAllBytesAsync(iconProbe, ShortcutAppearanceService.BuildTransparentIconForSmoke(), cts.Token);
                    using var transparentIcon = new System.Drawing.Icon(iconProbe);
                    using var iconBitmap = transparentIcon.ToBitmap();
                    int nonTransparentPixels = 0;
                    int maximumAlpha = 0;
                    for (int y = 0; y < iconBitmap.Height; y++)
                        for (int x = 0; x < iconBitmap.Width; x++)
                        {
                            int alpha = iconBitmap.GetPixel(x, y).A;
                            if (alpha != 0) nonTransparentPixels++;
                            maximumAlpha = Math.Max(maximumAlpha, alpha);
                        }
                    if (iconBitmap.Width != 32 || iconBitmap.Height != 32 ||
                        nonTransparentPixels > 1 || maximumAlpha > 1)
                        throw new InvalidOperationException("Transparent shortcut ICO Windows decode failed.");
                }
                finally
                {
                    try { if (File.Exists(iconProbe)) File.Delete(iconProbe); } catch { }
                }

                LogService.Info("explorer", $"SHORTCUT_OVERLAY_SMOKE_OK managed={managed} explorerStable={!managed || before.SequenceEqual(after)} realStateUnchanged=true realIconWrites=0 temporaryIconDecode=true {isolated}");
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

        if (HasArg(cli, "--overview-gauge-hotpath-smoke") && _window is MainWindow gaugeMw)
        {
            await Task.Delay(350);
            try { await gaugeMw.RunOverviewGaugeHotpathSmokeAsync(includeNativeBenchmark: true); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("OVERVIEW_GAUGE_HOTPATH_SMOKE_FAIL", ex); Environment.Exit(86); }
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

    private static async Task CheckIntegrationUpdatesAfterStartupAsync(System.Threading.CancellationToken token)
    {
        try
        {
            // Never impact first paint. Discovery is read-only; actual automatic
            // installation requires explicit per-integration opt-in in the UI.
            await Task.Delay(15000, token).ConfigureAwait(false);
            var updates = await ChrisTitusIntegrationService.CheckForUpdatesAsync(token).ConfigureAwait(false);
            if (updates.ProfileUpdate)
                LogService.Info("integrations", "TITUS_POWERSHELL_UPDATE_AVAILABLE profile=true");
            await ChrisTitusIntegrationService.RunOptInAutoUpdatesAsync(updates, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Info("integrations", "Deferred GitHub update discovery unavailable: " + ex.Message);
        }
    }

    private static async Task MigrateContextCommandsDeferredAsync(System.Threading.CancellationToken token)
    {
        try
        {
            await Task.Delay(5000, token).ConfigureAwait(false);
            await Task.Run(ContextMenuService.MigrateExistingNativeCommands, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Error("shell", "Native context command migration failed", ex); }
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

        cts.Token.ThrowIfCancellationRequested();
        PowerShellSyntaxService.ValidateFile(runtime);

        LogService.Info("ai", $"AI_RUNTIME_HARDENING_SMOKE_OK sourceBytes={sourceText.Length} runtimeBytes={runtimeText.Length} parse=ok");
    }

    private static async Task RunDebloatCacheSmokeCoreAsync()
    {
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
        InstalledAppService.InvalidateCache();
        InstalledAppService.ClearPersistentLogoCacheForSmoke();
        InstalledAppService.RunLogoRebaseSmoke();
        MainWindow.RunDebloatSummaryEqualitySmoke();
        SpankyhoOptimizer.ViewModels.DebloatRowViewModel.RunIconUpdatePolicySmoke();
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
        var hydratedMemory = InstalledAppService.TryGetMemorySnapshot(includeLogos: false);
        if (!ReferenceEquals(staleDisplay, hydratedMemory))
            throw new InvalidOperationException("Persisted Debloat display snapshot was not published to first-paint memory cache.");
        bool staleRefreshPending = InstalledAppService.SummaryNeedsBackgroundRefresh;
        LogService.Info("debloat", "DEBLOAT_DISPLAY_HYDRATION_SMOKE_OK firstPaintMemory=true staleRefreshPending=" + staleRefreshPending);
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

            await RunAsync("SERVICE_RECOMMENDED_FAST_READ_SMOKE",
                ServiceCatalogService.RunRecommendedStartModesSmokeAsync);

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

            await RunAsync("WORKER_IPC_OUTPUT_PATH_SMOKE", () =>
            {
                LogService.Info("regression", WorkerIpcSecurity.RunOutputPathSmoke());
                return Task.CompletedTask;
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

            await RunAsync("AI_CLOUD_UPLOAD_DEDUP_SMOKE", () =>
            {
                DateTime nowUtc = DateTime.UtcNow;
                if (!AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authA",
                        nowUtc.AddMinutes(-1), nowUtc))
                    throw new InvalidOperationException("Recent matching cloud upload should be reusable.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authB",
                        nowUtc.AddMinutes(-1), nowUtc))
                    throw new InvalidOperationException("Cloud upload cache reused a different OAuth session.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "changed", "authA", "authA",
                        nowUtc.AddMinutes(-1), nowUtc))
                    throw new InvalidOperationException("Cloud upload cache reused changed content.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authA",
                        nowUtc.AddMinutes(-4), nowUtc))
                    throw new InvalidOperationException("Cloud upload cache should expire.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authA",
                        nowUtc.AddMinutes(1), nowUtc))
                    throw new InvalidOperationException("Future-dated cloud upload cache should not be reusable.");
                return Task.CompletedTask;
            });
            await RunAsync("USER_SCOPED_CACHE_CLOCK_SKEW_SMOKE", () =>
            {
                string key = "release-cache-clock-skew-" + Guid.NewGuid().ToString("N");
                try
                {
                    UserScopedCacheService.Write(key, "healthy");
                    if (!UserScopedCacheService.TryRead<string>(key, TimeSpan.FromMinutes(10), out var initial) ||
                        initial != "healthy")
                        throw new InvalidOperationException("User-scoped cache initial write/read mismatch.");

                    UserScopedCacheService.BackdateForSmoke(key, TimeSpan.FromMinutes(-5));
                    if (UserScopedCacheService.TryRead<string>(key, TimeSpan.FromMinutes(10), out _))
                        throw new InvalidOperationException("Future-dated user cache should not be accepted.");

                    UserScopedCacheService.BackdateForSmoke(key, TimeSpan.FromMinutes(11));
                    if (UserScopedCacheService.TryRead<string>(key, TimeSpan.FromMinutes(10), out _))
                        throw new InvalidOperationException("Expired user cache should not be accepted.");
                }
                finally
                {
                    UserScopedCacheService.Remove(key);
                }
                return Task.CompletedTask;
            });
            await RunAsync("MEMORY_COMPRESSION_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WindowsActions.RunMemoryCompressionCacheSmokeAsync(cts.Token);
            });

            await RunAsync("MEMORY_NATIVE_STATE_SMOKE", async () =>
            {
                LogService.Info("performance", MemoryStateService.RunSnapshotReadBenchmark());
                await MemoryStateService.RunWarmFirstPaintSmokeAsync();
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
                // Release smoke must never write a live power plan, even with
                // an unchanged value. Confirm repeated native read-back only.
                bool? after = await WindowsActions.GetUsbSelectiveSuspendDisabledAsync(cts.Token);
                if (after != before)
                    throw new InvalidOperationException($"USB Selective Suspend read-only readback mismatch: before={before} after={after}.");
                LogService.Info("release-smoke", $"USB_SELECTIVE_SUSPEND_STATE disabled={after.Value} readOnly=true");
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
                {
                    // The test machine may explicitly deny direct port-53
                    // sockets while ordinary OS name resolution still works.
                    // Never change its DNS/firewall just to pass a release.
                    // Only a NATIVE AccessDenied verdict, not timeout or a
                    // resolver regression, permits an honest Unsupported.
                    bool blockedByHostPolicy = false;
                    try
                    {
                        using var check = new System.Net.Sockets.TcpClient();
                        using var deadline = new System.Threading.CancellationTokenSource(
                            TimeSpan.FromSeconds(2));
                        await check.ConnectAsync(System.Net.IPAddress.Parse("1.1.1.1"),
                            53, deadline.Token);
                    }
                    catch (System.Net.Sockets.SocketException ex)
                        when (ex.SocketErrorCode ==
                            System.Net.Sockets.SocketError.AccessDenied)
                    {
                        blockedByHostPolicy = true;
                    }
                    catch (OperationCanceledException) { /* not an access-denied verdict */ }
                    catch (System.Net.Sockets.SocketException) { /* not access denied */ }
                    if (!blockedByHostPolicy)
                        throw new InvalidOperationException(
                            "DNS benchmark received no resolver responses without confirmed host access denial.");
                    LogService.Info("release-smoke",
                        $"DNS_BENCHMARK_SMOKE_UNSUPPORTED reason=host-denies-public-tcp53 " +
                        $"catalog={results.Count} alive=0 resolver-performance-unverified=true");
                }
                else
                {
                    if (alive.Any(x => x.AverageMs <= 0 || x.Successful <= 0))
                        throw new InvalidOperationException("DNS benchmark returned invalid latency data.");
                    LogService.Info("release-smoke",
                        $"DNS_BENCHMARK_SMOKE_OK total={results.Count} alive={alive.Length} best={alive[0].Name} avgMs={alive[0].AverageMs:0.0}");
                }
            });

            await RunAsync("DATA_ROOT_RECOGNITION_SMOKE", () =>
            {
                LogService.Info("release-smoke", DataRootCleanupService.RunRecognitionSmoke());
                // Pure synthetic denial/cancellation checks; no actual MMDevices
                // keys or saved audio ownership journal are touched by smoke.
                LogService.Info("release-smoke",
                    AudioEnhancementsTweakService.RunPermissionFlowSmoke());
                return Task.CompletedTask;
            });

            await RunAsync("FAN_AGENT_JSON_CONTEXT_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    FanAgentJsonBenchmark.RunCompatibilityAndPerformanceSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_TELEMETRY_READ_CACHE_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetrySnapshotReadCache.RunAtomicFreshnessSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_BRIDGE_MUTEX_THREAD_AFFINITY_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetryBridgeService.RunBridgeMutexThreadAffinitySmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_BRIDGE_MUTEX_CANCEL_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetryBridgeService.RunBridgeMutexCancellationSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_BRIDGE_DIRECTORY_REUSE_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetryBridgeService.RunBridgeDirectoryHotpathSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("FAN_TICK_PHASE_METRICS_SMOKE", () =>
            {
                LogService.Info("release-smoke", FanAgentStageMetrics.RunPolicySmoke());
                return Task.CompletedTask;
            });

            await RunAsync("FAN_TRAY_DEDUP_SMOKE", () =>
            {
                LogService.Info("release-smoke", FanTrayIconService.RunNotificationDedupSmoke());
                LogService.Info("release-smoke", FanTrayIconService.RunHoverGdiResourceSmoke());
                LogService.Info("release-smoke", FanTrayIconService.RunHoverGlassOpacitySmoke());
                // Assert the native GDI handle count stabilizes after lazy
                // initialization, rather than merely allowing small growth.
                LogService.Info("release-smoke", FanTrayIconService.RunHoverGdiResourceSmoke(
                    steadyState: true));
                LogService.Info("release-smoke", FanProfileService.RunTrayCompositionBenchmark());
                return Task.CompletedTask;
            });



            // Passive RuntimeHost resource sampler was removed. This bundle
            // still verifies the functional services and their safe readbacks.
            LogService.Info("release-smoke", "RELEASE_SERVICE_SMOKE_BUNDLE_OK count=29");
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
