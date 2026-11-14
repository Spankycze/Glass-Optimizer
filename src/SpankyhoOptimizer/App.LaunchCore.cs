using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
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
        await PowerShellSyntaxService.ValidateFileAsync(runtime);

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
}
