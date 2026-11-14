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
}
