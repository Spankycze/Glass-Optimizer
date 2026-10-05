using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private bool _virtualControlFactoriesPrewarmed;
    private bool _virtualTweakResourcesPrewarmed;
    private bool _powerCardFactoryPrewarmed;
    private bool _actionButtonFactoriesPrewarmed;
    private bool _commonControlFactoriesPrewarmed;
    private bool _driverUiFactoryPrewarmed;
    private bool _networkUiFactoriesPrewarmed;
    private Microsoft.UI.Xaml.FrameworkElement? _prewarmedDebloatLoadingHero;
    private AppThemePreference? _prewarmedDebloatThemePreference;
    private Microsoft.UI.Xaml.FrameworkElement? _prewarmedPowerShellHero;
    private AppThemePreference? _prewarmedPowerShellThemePreference;

    private async Task RunStagedPostRenderPrewarmAsync()
    {
        // Give the freshly activated Overview a quiet window. These factories improve later
        // first-navigation latency, but they must never compete with the first interactive
        // seconds, live metrics, titlebar completion or a user's immediate navigation.
        const int initialQuietMs = 2200;
        const int betweenWaveMs = 120;
        await Task.Delay(initialQuietMs).ConfigureAwait(false);
        if (_section != "overview") return;

        var total = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info(
            "performance",
            $"POST_RENDER_PREWARM_BEGIN quietMs={initialQuietMs} section={_section}");

        await PrewarmStorageActionsEarlyAfterFirstRenderAsync().ConfigureAwait(false);
        LogService.Info("performance", $"POST_RENDER_PREWARM_WAVE stage=storage ms={total.ElapsedMilliseconds}");

        await Task.Delay(betweenWaveMs).ConfigureAwait(false);
        if (_section != "overview") return;
        await PrewarmVirtualTweakPipelineAsync().ConfigureAwait(false);
        LogService.Info("performance", $"POST_RENDER_PREWARM_WAVE stage=virtual ms={total.ElapsedMilliseconds}");

        await Task.Delay(betweenWaveMs).ConfigureAwait(false);
        if (_section != "overview") return;
        await PrewarmCommonCatalogsAfterFirstRenderAsync().ConfigureAwait(false);
        LogService.Info("performance", $"POST_RENDER_PREWARM_WAVE stage=catalogs ms={total.ElapsedMilliseconds}");

        await Task.Delay(betweenWaveMs).ConfigureAwait(false);
        if (_section != "overview") return;
        await PrewarmEarlySectionUiAfterFirstRenderAsync().ConfigureAwait(false);
        LogService.Info("performance", $"POST_RENDER_PREWARM_WAVE stage=early-sections ms={total.ElapsedMilliseconds}");

        await Task.Delay(betweenWaveMs).ConfigureAwait(false);
        if (_section != "overview") return;
        await PrewarmCommonControlFactoriesAfterFirstRenderAsync().ConfigureAwait(false);
        LogService.Info("performance", $"POST_RENDER_PREWARM_WAVE stage=common-controls ms={total.ElapsedMilliseconds}");

        await Task.Delay(betweenWaveMs).ConfigureAwait(false);
        if (_section != "overview") return;
        await PrewarmGpuTweakCardsEarlyAfterFirstRenderAsync().ConfigureAwait(false);
        LogService.Info("performance", $"POST_RENDER_PREWARM_WAVE stage=gpu ms={total.ElapsedMilliseconds}");

        LogService.Info(
            "performance",
            $"POST_RENDER_PREWARM_COMPLETE totalMs={total.ElapsedMilliseconds} serialized=true quietStart=true");
    }

    private async Task PrewarmVirtualTweakPipelineAsync()
    {
        // Start the tiny persisted Components profile hydrate on a worker immediately after
        // first render so first Components navigation only hits the in-memory cache.
        var profileWarmTask = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = UserFeatureProfileService.Load();
            LogService.Info("performance", $"COMPONENT_PROFILE_PREWARM ms={sw.ElapsedMilliseconds}");
        });

        // Keep the latency-critical virtual tweak path together. Power/common controls have
        // their own delayed stages and must not extend this startup scope by ~1 second.
        await PrewarmVirtualTweakResourcesEarlyAsync().ConfigureAwait(false);
        await PrewarmActionButtonFactoriesEarlyAsync().ConfigureAwait(false);
        await PrewarmVirtualControlFactoriesAfterFirstRenderAsync().ConfigureAwait(false);
        await PrewarmDriverUiFactoryAfterFirstRenderAsync().ConfigureAwait(false);
        await profileWarmTask.ConfigureAwait(false);
    }

    private async Task PrewarmVirtualTweakResourcesEarlyAsync()
    {
        // Keep the prewarm close to first render, but split cold XAML/resource activation across
        // Low-priority dispatcher turns so one 15-25 ms callback cannot cause a visible post-startup hitch.
        await Task.Yield();
        if (_section != "overview") return;

        var total = System.Diagnostics.Stopwatch.StartNew();

        await RunLowPriorityVirtualResourcePrewarmStageAsync("tweak-template", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TweakVirtualRowTemplate")).ConfigureAwait(false);
        await RunLowPriorityVirtualResourcePrewarmStageAsync("task-template", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TaskRowTemplateV136")).ConfigureAwait(false);
        await RunLowPriorityVirtualResourcePrewarmStageAsync("startup-template", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("StartupRowTemplateV209")).ConfigureAwait(false);
        await RunLowPriorityVirtualResourcePrewarmStageAsync("items-panel", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.Controls.ItemsPanelTemplate>("SpankyVirtualizingItemsPanel")).ConfigureAwait(false);
        await RunLowPriorityVirtualResourcePrewarmStageAsync("icons", () =>
        {
            _ = SectionIconSource("system");
            _ = SectionIconSource("privacy");
            _ = SectionIconSource("ui");
            _ = SectionIconSource("update");
            _ = SectionIconSource("gpu");
            _ = SectionIconSource("drivers");
        }).ConfigureAwait(false);

        _virtualTweakResourcesPrewarmed = true;
        LogService.Info("performance", $"VIRTUAL_RESOURCE_EARLY_PREWARM staged=true totalWallMs={total.ElapsedMilliseconds}");
    }

    private Task RunLowPriorityVirtualResourcePrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_section == "overview")
                    action();
                LogService.Info("performance", $"VIRTUAL_RESOURCE_PREWARM_STAGE stage={stage} ms={sw.ElapsedMilliseconds}");
                tcs.TrySetResult(true);
            }
            catch (System.Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        return tcs.Task;
    }

    private async Task PrewarmActionButtonFactoriesEarlyAsync()
    {
        if (_actionButtonFactoriesPrewarmed) return;

        await RunNormalPriorityFactoryPrewarmStageAsync("quick-action", () =>
            _ = QuickButton("Prewarm", () => Task.CompletedTask)).ConfigureAwait(false);

        await Task.Yield();

        await RunNormalPriorityFactoryPrewarmStageAsync("primary-action", () =>
            _ = PrimaryQuickButton("Prewarm", () => Task.CompletedTask)).ConfigureAwait(false);

        // Give the dispatcher another scheduling boundary before selector-specific work.
        await Task.Yield();

        await RunNormalPriorityFactoryPrewarmStageAsync("components-selector", () =>
        {
            // Components navigation now uses direct properties instead of a runtime Style/Setter graph.
            foreach (string label in new[] { "Služby", "Ovladače", "Task Scheduler" })
            {
                var button = new Microsoft.UI.Xaml.Controls.Button { Content = label, Tag = label };
                ApplyPillButtonAppearance(button);
            }
        }).ConfigureAwait(false);

        _actionButtonFactoriesPrewarmed = true;
    }

    private Task RunNormalPriorityFactoryPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                action();
                LogService.Info("performance",
                    $"ACTION_BUTTON_FACTORY_PREWARM stage={stage} ms={sw.ElapsedMilliseconds} priority=Normal");
                tcs.TrySetResult(true);
            }
            catch (System.Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        return tcs.Task;
    }

    private async Task PrewarmEarlySectionUiAfterFirstRenderAsync()
    {
        await Task.Delay(160).ConfigureAwait(false);
        if (_section != "overview") return;

        bool tamper = await Task.Run(WindowsActions.IsTamperProtectionOn).ConfigureAwait(false);
        if (_section != "overview") return;

        await RunLowPriorityCommonControlPrewarmStageAsync("defender-shell-early", () =>
            PrewarmDefenderStaticShell(tamper)).ConfigureAwait(false);

        if (_section == "overview")
            await PrewarmNetworkUiStagedAsync().ConfigureAwait(false);
    }

    private async Task PrewarmCommonCatalogsAfterFirstRenderAsync()
    {
        await Task.Delay(240).ConfigureAwait(false);
        if (_section != "overview") return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() =>
        {
            _tweakCache.TryAdd("system|", CatalogService.GetTweaks("system"));
            _tweakCache.TryAdd("network|", CatalogService.GetTweaks("network"));
            _tweakCache.TryAdd("gpu|", CatalogService.GetTweaks("gpu"));
            _tweakCache.TryAdd("defender|", CatalogService.GetTweaks("defender"));
            _tweakCache.TryAdd("components|Služby", CatalogService.GetTweaks("components", "Služby"));
        }).ConfigureAwait(false);
        LogService.Info("performance", $"COMMON_CATALOG_PREWARM ms={sw.ElapsedMilliseconds}");
    }

    private async Task PrewarmServiceUiAfterFirstRenderAsync()
    {
        // Keep first paint untouched, then warm cheap component data in the background.
        // Leave a quiet gap after live metrics/Fixer/GPU cache warmups.
        await Task.Delay(2800);
        if (_section != "overview") return;
        var perf = System.Diagnostics.Stopwatch.StartNew();
        PrewarmServiceUi();
        long serviceUiMs = perf.ElapsedMilliseconds;
        // Network factories were staged earlier, directly after common-control prewarm.
        long networkUiMs = 0;
        // Service inventory remains on-demand; static tweak catalog is already warmed by CommonCatalogs.
        long catalogMs = 0;
        LogService.Info("performance", $"SERVICE_PREWARM_PHASE serviceUiMs={serviceUiMs} networkUiMs={networkUiMs} catalogMs={catalogMs} workMs={perf.ElapsedMilliseconds} stagedNetwork=early serviceInventory=on-demand");

        // Task Scheduler COM inventory is intentionally NOT prewarmed. It is a heavy
        // COM enumeration and should only run when the user opens Task Scheduler or a feature needs it.
    }

    private async Task PrewarmFixerStaticAfterFirstRenderAsync()
    {
        // Live metrics starts at ~1.2 s; avoid competing for the same post-startup UI turn.
        await Task.Delay(1700).ConfigureAwait(false);
        if (_section != "overview") return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_section == "overview") RunWithNeutralPrewarmPalette(PrewarmFixerStaticUi);
            tcs.TrySetResult(true);
        }))
            return;
        await tcs.Task.ConfigureAwait(false);
    }

    private async Task PrewarmBcdStateAfterFirstRenderAsync()
    {
        await Task.Delay(2300).ConfigureAwait(false);
        if (_section != "overview") return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int loadsBefore = BcdEditStateService.LoadCountForDiagnostics;
        try
        {
            _ = await BcdEditStateService.GetCurrentAsync().ConfigureAwait(false);
            int loadsAfter = BcdEditStateService.LoadCountForDiagnostics;
            LogService.Info("performance",
                $"BCD_STATE_PREWARM ms={sw.ElapsedMilliseconds} newLoads={loadsAfter - loadsBefore} totalLoads={loadsAfter}");
        }
        catch (System.OperationCanceledException) { }
        catch (System.Exception ex)
        {
            LogService.Error("performance", "BCD state prewarm failed", ex);
        }
    }

    private async Task PrewarmMemoryCompressionAfterFirstRenderAsync()
    {
        await Task.Delay(2800).ConfigureAwait(false);
        if (_section != "overview") return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _ = await WindowsActions.GetMemoryCompressionForUiAsync().ConfigureAwait(false);
            LogService.Info("performance", $"MEMORY_COMPRESSION_PREWARM_OK ms={sw.ElapsedMilliseconds}");
        }
        catch (System.OperationCanceledException) { }
        catch (System.Exception ex)
        {
            LogService.Info("performance",
                $"MEMORY_COMPRESSION_PREWARM_SKIP ms={sw.ElapsedMilliseconds} type={ex.GetType().Name}");
        }
    }

    private async Task PrewarmWindowsFeaturesAfterFirstRenderAsync()
    {
        // First hydrate only the persisted feature snapshot. This is disk/JSON work on a
        // worker thread and avoids making an immediate System navigation pay the cold WMI probe.
        try
        {
            if (await WindowsFeatureStateService.TryWarmPersistentClientSnapshotAsync().ConfigureAwait(false))
                return;
        }
        catch (System.OperationCanceledException) { return; }
        catch (System.Exception ex)
        {
            LogService.Error("performance", "Windows Features persistent prewarm failed", ex);
        }

        // Persistent miss: keep the comparatively heavy live probe clear of the first seconds
        // after startup and only run it while Overview remains idle.
        await Task.Delay(3800).ConfigureAwait(false);
        if (_section != "overview") return;
        try
        {
            await WindowsFeatureStateService.PrewarmClientSnapshotAsync().ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) { }
        catch (System.Exception ex)
        {
            LogService.Error("performance", "Windows Features prewarm failed", ex);
        }
    }

    private void PrewarmFixerStaticUi()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _ = GetFixerStaticElements();
            LogService.Info("performance", $"FIXER_STATIC_PREWARM ms={sw.ElapsedMilliseconds}");
        }
        catch (System.Exception ex) { LogService.Error("performance", "Fixer static UI prewarm failed", ex); }
    }

    internal async Task RunSystemColdRenderSmokeAsync()
    {
        if (_section != "overview")
            throw new System.InvalidOperationException("System cold render smoke expected Overview before navigation.");

        InvalidateSystemPageCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("system");
        sw.Stop();
        long renderMs = sw.ElapsedMilliseconds;
        if (renderMs > 35)
            throw new System.InvalidOperationException($"System cold render-first foreground too slow: {renderMs} ms.");

        for (int i = 0; i < 80 && _systemPageCache == null; i++)
            await Task.Delay(25);
        if (_systemPageCache == null || _systemVirtualRowsCache == null)
            throw new System.InvalidOperationException("System cold render deferred root did not materialize.");
        int deferredAtRoot = _systemDeferredAtRootBuild;
        if (deferredAtRoot < 20)
            throw new System.InvalidOperationException($"System cold render deferred too little work: {deferredAtRoot} groups.");

        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await Task.Delay(25);
        int remaining = GetDeferredVirtualGroupCount("system");
        if (remaining != 0)
            throw new System.InvalidOperationException($"System cold deferred groups incomplete: {remaining} remaining.");

        LogService.Info("performance",
            $"SYSTEM_COLD_RENDER_SMOKE_OK renderMs={renderMs} deferredAtRoot={deferredAtRoot} deferredRemaining={remaining}");
    }

    internal async Task RunVirtualResourcePrewarmSmokeAsync()
    {
        if (_section != "overview")
            throw new System.InvalidOperationException("Virtual resource smoke expected Overview before navigation.");
        if (_virtualTweakResourcesPrewarmed || _virtualControlFactoriesPrewarmed)
            throw new System.InvalidOperationException("Virtual resources/factories were eagerly prewarmed during startup.");

        var onDemandWarm = System.Diagnostics.Stopwatch.StartNew();
        await PrewarmVirtualTweakPipelineAsync();
        long prewarmReadyMs = onDemandWarm.ElapsedMilliseconds;
        if (!_virtualTweakResourcesPrewarmed)
            throw new System.InvalidOperationException("On-demand virtual resource warmup did not complete.");
        if (!_virtualControlFactoriesPrewarmed)
            throw new System.InvalidOperationException("On-demand virtual control factory warmup did not complete.");
        if (!ReferenceEquals(SectionIconSource("gpu"), SectionIconSource("gpu")) ||
            !ReferenceEquals(SectionIconSource("drivers"), SectionIconSource("drivers")))
            throw new System.InvalidOperationException("Hardware icon source cache did not reuse warmed SVG sources.");

        InvalidateSystemPageCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("system");
        sw.Stop();
        long renderMs = sw.ElapsedMilliseconds;
        if (renderMs > 25)
            throw new System.InvalidOperationException($"Prewarmed System render-first foreground too slow: {renderMs} ms.");

        for (int i = 0; i < 80 && _systemPageCache == null; i++)
            await Task.Delay(25);
        if (_systemPageCache == null || _systemVirtualRowsCache == null)
            throw new System.InvalidOperationException("Prewarmed System deferred root did not materialize.");
        if (_systemDeferredAtRootBuild < 20)
            throw new System.InvalidOperationException($"Prewarmed System deferred too little work: {_systemDeferredAtRootBuild} groups.");

        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await Task.Delay(25);
        int remaining = GetDeferredVirtualGroupCount("system");
        if (remaining != 0)
            throw new System.InvalidOperationException($"Prewarmed System deferred groups incomplete: {remaining} remaining.");

        LogService.Info("performance",
            $"VIRTUAL_RESOURCE_PREWARM_SMOKE_OK prewarmReadyMs={prewarmReadyMs} systemRenderMs={renderMs} deferredAtRoot={_systemDeferredAtRootBuild} deferredRemaining={remaining}");
    }

    private void PrewarmVirtualTweakResources()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TweakVirtualRowTemplate");
            long templateMs = sw.ElapsedMilliseconds;

            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TaskRowTemplateV136");
            long taskTemplateMs = sw.ElapsedMilliseconds - templateMs;

            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("StartupRowTemplateV209");
            long startupTemplateMs = sw.ElapsedMilliseconds - templateMs - taskTemplateMs;

            _ = UiResourceService.Get<Microsoft.UI.Xaml.Controls.ItemsPanelTemplate>("SpankyVirtualizingItemsPanel");
            long panelMs = sw.ElapsedMilliseconds - templateMs - taskTemplateMs - startupTemplateMs;

            _ = SectionIconSource("system");
            _ = SectionIconSource("privacy");
            _ = SectionIconSource("ui");
            _ = SectionIconSource("update");
            _ = SectionIconSource("gpu");
            _ = SectionIconSource("drivers");
            long iconsMs = sw.ElapsedMilliseconds - templateMs - taskTemplateMs - startupTemplateMs - panelMs;

            LogService.Info("performance",
                $"VIRTUAL_RESOURCE_PHASE templateMs={templateMs} taskTemplateMs={taskTemplateMs} startupTemplateMs={startupTemplateMs} panelMs={panelMs} iconsMs={iconsMs} totalMs={sw.ElapsedMilliseconds}");
        }
        catch (System.Exception ex) { LogService.Error("performance", "Virtual tweak resource prewarm failed", ex); }
    }

    private async Task PrewarmVirtualControlFactoriesAfterFirstRenderAsync()
    {
        try
        {
            // Split the WinUI factory warmup across dispatcher turns. The old single Normal-priority
            // callback could occupy the UI thread for 10-20 ms even though each individual step is small.
            await Task.Yield();
            if (_virtualControlFactoriesPrewarmed) return;

            var total = System.Diagnostics.Stopwatch.StartNew();
            Microsoft.UI.Xaml.UIElement? header = null;
            Microsoft.UI.Xaml.DataTemplate? rowTemplate = null;
            Microsoft.UI.Xaml.Controls.ItemsRepeater? repeater = null;

            await RunVirtualControlPrewarmStageAsync("header", () =>
                header = CreateVirtualGroupHeader("PREWARM   •   0 TWEAKŮ")).ConfigureAwait(false);

            await RunVirtualControlPrewarmStageAsync("template", () =>
                rowTemplate = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TweakVirtualRowTemplate")).ConfigureAwait(false);

            await RunVirtualControlPrewarmStageAsync("repeater-ctor", () =>
                repeater = new Microsoft.UI.Xaml.Controls.ItemsRepeater()).ConfigureAwait(false);

            await RunVirtualControlPrewarmStageAsync("repeater-template", () =>
                repeater!.ItemTemplate = rowTemplate).ConfigureAwait(false);

            await RunLowPriorityVirtualResourcePrewarmStageAsync("repeater-source", () =>
                repeater!.ItemsSource = new System.Collections.Generic.List<TweakVirtualRowViewModel>()).ConfigureAwait(false);

            await RunVirtualControlPrewarmStageAsync("repeater-layout", () =>
            {
                repeater!.Layout = new Microsoft.UI.Xaml.Controls.StackLayout { Spacing = 6 };
                repeater.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            }).ConfigureAwait(false);

            await RunVirtualControlPrewarmStageAsync("expander", () =>
            {
                var expander = CreateVirtualGroupExpander(header!, repeater!);
                _ = expander is Microsoft.UI.Xaml.Controls.Expander control ? control.Content : (expander as Microsoft.UI.Xaml.Controls.Border)?.Child;
            }).ConfigureAwait(false);

            _virtualControlFactoriesPrewarmed = true;
            LogService.Info("performance",
                $"VIRTUAL_CONTROL_PREWARM_SPLIT totalWallMs={total.ElapsedMilliseconds} priority=Normal stages=7");
        }
        catch (System.Exception ex)
        {
            LogService.Error("performance", "Virtual control factory prewarm failed", ex);
        }
    }

    private Task RunVirtualControlPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                action();
                LogService.Info("performance",
                    $"VIRTUAL_CONTROL_PREWARM_STAGE stage={stage} ms={sw.ElapsedMilliseconds} priority=Normal");
                tcs.TrySetResult(true);
            }
            catch (System.Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        return tcs.Task;
    }

    private async Task PrewarmPowerSnapshotEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(120).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool warmed = await PowerPlanStateService.TryPrewarmRegistrySnapshotAsync().ConfigureAwait(false);
        LogService.Info("performance",
            $"POWER_SNAPSHOT_EARLY_PREWARM warmed={warmed} ms={sw.ElapsedMilliseconds}");
    }

    private async Task PrewarmPowerCardFactoryAfterFirstRenderAsync()
    {
        if (_powerCardFactoryPrewarmed) return;

        // Power is not on the critical Overview -> tweak navigation path. Keep the
        // first post-render UI turns free for virtual tweak resources/factories.
        await Task.Delay(350).ConfigureAwait(false);
        if (_powerCardFactoryPrewarmed) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (_powerCardFactoryPrewarmed)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var sample = new PowerSettingState(
                    "prewarm",
                    "CPU prewarm",
                    "Factory prewarm only.",
                    "prewarm-group",
                    "prewarm-setting",
                    0,
                    1,
                    "0",
                    "1");
                _ = PowerSettingCard(sample);
                _powerCardFactoryPrewarmed = true;
                LogService.Info("performance", $"POWER_CARD_FACTORY_PREWARM ms={sw.ElapsedMilliseconds} priority=Low delayedMs=350");
                tcs.TrySetResult(true);
            }
            catch (System.Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
            return;

        await tcs.Task.ConfigureAwait(false);
    }

    private async Task PrewarmDriverUiFactoryAfterFirstRenderAsync()
    {
        if (_driverUiFactoryPrewarmed) return;

        await RunLowPriorityCommonControlPrewarmStageAsync("drivers-rows", () =>
        {
            PrewarmDriverRowsCache();
        }).ConfigureAwait(false);

        await Task.Yield();

        await RunLowPriorityCommonControlPrewarmStageAsync("drivers-shell", () =>
        {
            var actions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            actions.Children.Add(PrimaryQuickButton("Prewarm", () => Task.CompletedTask));
            _ = BuildSectionHero(
                "components",
                "DRIVER TWEAKS",
                "Ovladače",
                "Legacy a volitelné systémové ovladače",
                actions);

            var advisorBody = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 7 };
            advisorBody.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "Prewarm Advisor",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = TextPrimaryBrush()
            });
            advisorBody.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "HW-aware driver advisor factory prewarm",
                Foreground = TextSecondaryBrush(),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
            var advisorActions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            advisorActions.Children.Add(PrimaryQuickButton("Prewarm", () => Task.CompletedTask));
            advisorBody.Children.Add(advisorActions);
            _ = BuildSectionSurface(
                "components",
                "HW-aware Advisor",
                "Služby • ovladače • tasky podle toho, co reálně používáš",
                advisorBody);

            _ = BuildPageSummaryStrip(
                ("Recommended", "0", "SpankyInfoBrush"),
                ("Načteno", "0/25", "SpankyInfoBrush"),
                ("Optimalizováno", "0", "SpankySuccessBrush"),
                ("Protected", "0", "SpankyWarningBrush"));

            var legacy = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 7 };
            legacy.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "RECOMMENDED 0 • UNSAFE 0 • ALREADY OPTIMIZED 0 • PROTECTED 0 • STAV 0/25",
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = TextPrimaryBrush(),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
            _ = BuildSectionSurface(
                "components",
                "Volitelné legacy drivery",
                "Všechny možnosti jsou dostupné hned; vizuální řádky se vytváří jen podle potřeby.",
                legacy);
        }).ConfigureAwait(false);

        await Task.Yield();

        await RunLowPriorityCommonControlPrewarmStageAsync("drivers-list", () =>
        {
            var rows = _driverRows ?? System.Array.Empty<TweakVirtualRowViewModel>();
            _ = new Microsoft.UI.Xaml.Controls.ListView
            {
                MaxHeight = 700,
                SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.None,
                ItemsSource = rows,
                ItemTemplate = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TweakVirtualRowTemplate"),
                ItemsPanel = UiResourceService.Get<Microsoft.UI.Xaml.Controls.ItemsPanelTemplate>("SpankyVirtualizingItemsPanel")
            };
        }).ConfigureAwait(false);

        _driverUiFactoryPrewarmed = true;
        LogService.Info("performance", "DRIVER_UI_FACTORY_PREWARM staged=true");
    }

    private async Task PrewarmStartupManagerUiEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(45).ConfigureAwait(false);
        if (_section != "overview") return;

        var deltaBaselinePrewarm = StartupManagerService.TryWarmDeltaBaselineAsync();

        await RunLowPriorityCommonControlPrewarmStageAsync("startup-manager-list", () =>
        {
            var rowTemplate = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("StartupRowTemplateV209");
            var itemsPanel = UiResourceService.Get<Microsoft.UI.Xaml.Controls.ItemsPanelTemplate>("SpankyVirtualizingItemsPanel");
            _ = new Microsoft.UI.Xaml.Controls.ListView
            {
                MaxHeight = 650,
                SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.None,
                ItemTemplate = rowTemplate,
                ItemsPanel = itemsPanel
            };
        }).ConfigureAwait(false);

        await Task.Yield();

        await RunLowPriorityCommonControlPrewarmStageAsync("startup-manager-hero", () =>
        {
            var actions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            actions.Children.Add(QuickButton("Prewarm", () => Task.CompletedTask));
            _ = BuildModernSectionHero("startup", "Prewarm", "Factory prewarm", "STARTUP", "Ready", actions);
        }).ConfigureAwait(false);

        await Task.Yield();

        await RunLowPriorityCommonControlPrewarmStageAsync("startup-manager-metrics", () =>
        {
            _ = BuildDashboardMetricGrid(
                BuildDashboardMetric("A", "1", "Prewarm"),
                BuildDashboardMetric("B", "2", "Prewarm"),
                BuildDashboardMetric("C", "3", "Prewarm"),
                BuildDashboardMetric("D", "4", "Prewarm"));
        }).ConfigureAwait(false);

        bool baselineReady = await deltaBaselinePrewarm.ConfigureAwait(false);
        LogService.Info("performance", $"STARTUP_DELTA_BASELINE_PREWARM hit={baselineReady}");
    }

    private async Task PrewarmPowerShellHeroAfterFirstRenderAsync()
    {
        await Task.Delay(500).ConfigureAwait(false);
        if (_section != "overview" || _prewarmedPowerShellHero != null) return;

        await RunLowPriorityCommonControlPrewarmStageAsync("powershell-hero", () =>
        {
            var actions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            actions.Children.Add(QuickButton("PowerShell 7", OpenPowerShell7Async));

            var hero = BuildModernSectionHero(
                "powershell",
                "PowerShell / CMD",
                "Full-black shell themes + PowerShell 7, CMD a Windows Terminal v jednom.",
                "TERMINAL",
                "Black Red • Black Green • Pure Black",
                actions);
            hero.Measure(new Windows.Foundation.Size(1100, 190));
            _prewarmedPowerShellHero = hero;
            _prewarmedPowerShellThemePreference = _themePreference;
        }).ConfigureAwait(false);
    }

    private async Task PrewarmDebloatHeroEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(55).ConfigureAwait(false);
        if (_section != "overview") return;

        await RunLowPriorityCommonControlPrewarmStageAsync("debloat-hero-early", () =>
        {
            var hero = BuildModernSectionHero(
                "debloat",
                "Debloater",
                "Bezpečný app manager s Recommended výběrem, Keep listem a Protected balíky.",
                "APP CLEANUP",
                "Načítám nainstalované aplikace…");
            hero.Measure(new Windows.Foundation.Size(1100, 190));
            _prewarmedDebloatLoadingHero = hero;
            _prewarmedDebloatThemePreference = _themePreference;
        }).ConfigureAwait(false);
    }

    private async Task PrewarmUpdateShellEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(220).ConfigureAwait(false);
        if (_section != "overview") return;

        await RunLowPriorityCommonControlPrewarmStageAsync("update-shell-early", () =>
        {
            var actions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            var hero = BuildSectionHero(
                "update",
                "Windows servicing",
                "Windows Update Center",
                "Načítám stav Windows Update…",
                actions);
            var surface = BuildSectionSurface(
                "update",
                "Stav Windows Update",
                "Read-back + pending restart + poslední chyby",
                new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 7 });
            hero.Measure(new Windows.Foundation.Size(1100, 190));
            surface.Measure(new Windows.Foundation.Size(1100, 220));
        }).ConfigureAwait(false);
    }

    private async Task PrewarmUiShellEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(180).ConfigureAwait(false);
        if (_section != "overview") return;

        await RunLowPriorityCommonControlPrewarmStageAsync("ui-shell-early", () =>
        {
            _ = new Microsoft.UI.Xaml.Controls.ToggleSwitch
            {
                IsOn = false,
                OnContent = "",
                OffContent = "",
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            };

            var intro = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 5 };
            intro.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "Prewarm UI",
                FontSize = 17,
                Foreground = TextPrimaryBrush()
            });
            var hero = BuildSectionHero("ui", "Explorer control", "Context Menu Tweaker", "Factory prewarm", null);
            var surface = BuildSectionSurface("ui", "Kontextové menu", "Factory prewarm", intro);
            hero.Measure(new Windows.Foundation.Size(1100, 190));
            surface.Measure(new Windows.Foundation.Size(1100, 220));
        }).ConfigureAwait(false);
    }

    private async Task PrewarmContextMenuRowEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(210).ConfigureAwait(false);
        if (_section != "overview") return;

        await RunLowPriorityCommonControlPrewarmStageAsync("context-menu-row-early", () =>
        {
            var toggle = new Microsoft.UI.Xaml.Controls.ToggleSwitch
            {
                IsOn = false,
                OnContent = "",
                OffContent = "",
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            };
            var row = ContextMenuToggleRow("Prewarm", "Context Menu row factory prewarm.", toggle, "ADMIN");
            row.Measure(new Windows.Foundation.Size(1100, 90));
        }).ConfigureAwait(false);
    }

    private async Task PrewarmGenericTweakCardEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(235).ConfigureAwait(false);
        if (_section != "overview") return;

        var defenderTweaks = CatalogService.GetTweaks("defender");
        if (defenderTweaks.Count == 0) return;

        await RunLowPriorityCommonControlPrewarmStageAsync("generic-tweak-card-early", () =>
        {
            var card = CreateTweakCard(defenderTweaks[0]);
            card.Measure(new Windows.Foundation.Size(1100, 140));
        }).ConfigureAwait(false);
    }

    private async Task PrewarmOverviewHudCardEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(10).ConfigureAwait(false);
        if (_section != "overview") return;

        await RunLowPriorityCommonControlPrewarmStageAsync("overview-hud-card-early", () =>
        {
            var card = BuildHudTelemetryCard("CPU", "Prewarm", CpuMetricBrush(), out _);
            card.Measure(new Windows.Foundation.Size(260, 210));
        }).ConfigureAwait(false);
    }

    private async Task PrewarmGpuTweakCardsEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(120).ConfigureAwait(false);
        if (_section != "overview") return;

        var gpuTweaks = CatalogService.GetTweaks("gpu");
        int count = System.Math.Min(gpuTweaks.Count, 4);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            await RunLowPriorityCommonControlPrewarmStageAsync($"gpu-tweak-card-{index}", () =>
            {
                var card = CreateTweakCard(gpuTweaks[index]);
                card.Measure(new Windows.Foundation.Size(1100, 140));
            }).ConfigureAwait(false);

            if (_section != "overview") return;
        }
    }

    private async Task PrewarmStorageSnapshotAfterFirstRenderAsync()
    {
        await Task.Delay(1200).ConfigureAwait(false);
        if (_section != "overview") return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var disks = await Task.Run(NativeSystemProbeService.ReadStorage).ConfigureAwait(false);
            LogService.Info("performance",
                $"STORAGE_SNAPSHOT_PREWARM count={disks.Count} ms={sw.ElapsedMilliseconds}");
        }
        catch (System.Exception ex)
        {
            LogService.Info("performance",
                $"STORAGE_SNAPSHOT_PREWARM_SKIP ms={sw.ElapsedMilliseconds} type={ex.GetType().Name}");
        }
    }

    private async Task PrewarmStorageActionsEarlyAfterFirstRenderAsync()
    {
        await Task.Delay(35).ConfigureAwait(false);
        if (_storageToolsCard != null && _storageStaticHero != null && _storageStaticMetrics != null) return;

        // Keep this inside the first idle window so Storage never pays the one-time
        // WinUI/button factory cost on its first real navigation.
        for (int i = 0; i < 8 && !_actionButtonFactoriesPrewarmed; i++)
            await Task.Delay(15).ConfigureAwait(false);

        if (_storageToolsCard != null && _storageStaticHero != null && _storageStaticMetrics != null) return;
        await RunLowPriorityCommonControlPrewarmStageAsync("storage-shell-actions-early", () =>
        {
            PrewarmStorageStaticShell();
            PrewarmStorageActionsCard();
        }).ConfigureAwait(false);
    }

    private async Task PrewarmCommonControlFactoriesAfterFirstRenderAsync()
    {
        if (_commonControlFactoriesPrewarmed) return;
        await Task.Delay(120).ConfigureAwait(false);
        if (_commonControlFactoriesPrewarmed) return;

        await RunLowPriorityCommonControlPrewarmStageAsync("action", () =>
            _ = ActionCard("Prewarm", "Factory prewarm", "OK", () => Task.CompletedTask)).ConfigureAwait(false);

        await RunLowPriorityCommonControlPrewarmStageAsync("shell-core", () =>
        {
            var heroActions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            heroActions.Children.Add(PrimaryQuickButton("Prewarm", () => Task.CompletedTask));
            heroActions.Children.Add(QuickButton("Prewarm", () => Task.CompletedTask));
            _ = BuildSectionHero("settings", "Prewarm", "Prewarm", "Factory prewarm", heroActions);
            _ = BuildSectionSurface("settings", "Prewarm", "Factory prewarm", new Microsoft.UI.Xaml.Controls.StackPanel());
            _ = BuildPageSummaryStrip(("Prewarm", "Ready", "SpankySuccessBrush"));
        }).ConfigureAwait(false);

        await Task.Yield();

        await RunLowPriorityCommonControlPrewarmStageAsync("shell-gpu", () =>
        {
            // GPU's first detected render uses the modern hero + feature tile factories.
            var gpuActions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            gpuActions.Children.Add(PrimaryQuickButton("Prewarm", () => Task.CompletedTask));
            gpuActions.Children.Add(QuickButton("Prewarm", () => Task.CompletedTask));
            _ = BuildModernSectionHero("gpu", "Prewarm GPU", "Factory prewarm", "GPU", "Detected GPU • driver 000.00", gpuActions);
            _ = BuildFeatureTile("gpu", "GPU", "Prewarm GPU", "NVIDIA");

            var gpuStatusRoot = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8 };
            gpuStatusRoot.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "Detected GPU",
                FontSize = 14,
                Foreground = TextPrimaryBrush()
            });
            gpuStatusRoot.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "Ready",
                FontSize = 12.5,
                Foreground = TextSecondaryBrush(),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
            var gpuStatusSurface = BuildSectionSurface("gpu", "Driver status", "Factory prewarm", gpuStatusRoot);
            gpuStatusSurface.Measure(new Windows.Foundation.Size(1100, 220));
        }).ConfigureAwait(false);

        await RunLowPriorityCommonControlPrewarmStageAsync("storage-shell", () =>
            PrewarmStorageStaticShell()).ConfigureAwait(false);

        if (_defenderStaticHero == null)
        {
            bool defenderTamper = await Task.Run(WindowsActions.IsTamperProtectionOn).ConfigureAwait(false);
            await RunLowPriorityCommonControlPrewarmStageAsync("defender-shell", () =>
                PrewarmDefenderStaticShell(defenderTamper)).ConfigureAwait(false);
        }

        await RunLowPriorityCommonControlPrewarmStageAsync("dashboard-expander", () =>
        {
            var content = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8 };
            content.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Prewarm" });
            var card = BuildDashboardExpander("Prewarm", "Dashboard expander factory", content);
            card.Measure(new Windows.Foundation.Size(900, 220));

            var taskList = new Microsoft.UI.Xaml.Controls.ListView
            {
                MaxHeight = 650,
                SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.None,
                ItemTemplate = TaskRowTemplate(),
                ItemsPanel = UiResourceService.Get<Microsoft.UI.Xaml.Controls.ItemsPanelTemplate>("SpankyVirtualizingItemsPanel")
            };
            var taskShell = BuildDashboardExpander(
                "Naplánované úlohy",
                "Task Scheduler factory prewarm",
                new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Prewarm" },
                        taskList
                    }
                });
            taskShell.Measure(new Windows.Foundation.Size(1100, 700));
        }).ConfigureAwait(false);

        await RunLowPriorityCommonControlPrewarmStageAsync("metrics", () =>
        {
            var grid = BuildDashboardMetricGrid(
                BuildDashboardMetric("Prewarm A", "1", "Dashboard metric"),
                BuildDashboardMetric("Prewarm B", "2", "Dashboard metric", "SpankySuccessBrush"),
                BuildDashboardMetric("Prewarm C", "3", "Dashboard metric", "SpankyWarningBrush"),
                BuildDashboardMetric("Prewarm D", "4", "Dashboard metric"));
            grid.Measure(new Windows.Foundation.Size(1100, 180));
        }).ConfigureAwait(false);

        await RunLowPriorityCommonControlPrewarmStageAsync("input-controls", () =>
        {
            _ = new Microsoft.UI.Xaml.Controls.ToggleSwitch { IsOn = false, OnContent = "On", OffContent = "Off" };
            _ = new Microsoft.UI.Xaml.Controls.Slider
            {
                Header = "Prewarm",
                Minimum = 0,
                Maximum = 100,
                StepFrequency = 1,
                Value = 50,
                Width = 250
            };
            var combo = new Microsoft.UI.Xaml.Controls.ComboBox
            {
                Header = "Prewarm",
                Width = 155,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left
            };
            combo.Items.Add("A");
            combo.Items.Add("B");
            combo.SelectedIndex = 0;

            var head = new Microsoft.UI.Xaml.Controls.Grid();
            head.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
            head.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
            head.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon
            {
                Glyph = "\uE756",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons")
            });
        }).ConfigureAwait(false);

        await Task.Yield();

        await RunLowPriorityCommonControlPrewarmStageAsync("input-tweak-card", () =>
        {
            var defenderTweaks = CatalogService.GetTweaks("defender");
            if (defenderTweaks.Count > 0) _ = CreateTweakCard(defenderTweaks[0]);
        }).ConfigureAwait(false);

        _commonControlFactoriesPrewarmed = true;
        LogService.Info("performance", "COMMON_CONTROL_FACTORY_PREWARM staged=true");

        // Network uses the same base controls but has a few dashboard-specific factories.
        // Warm those immediately after common controls (~600 ms after first render), before
        // a quick user navigation can pay the one-time 20-32 ms cold Network composition cost.
        if (_section == "overview")
            await PrewarmNetworkUiStagedAsync().ConfigureAwait(false);
    }

    private Task RunLowPriorityCommonControlPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                RunWithNeutralPrewarmPalette(action);
                LogService.Info("performance", $"COMMON_CONTROL_FACTORY_PREWARM_STAGE stage={stage} ms={sw.ElapsedMilliseconds}");
                tcs.TrySetResult(true);
            }
            catch (System.Exception ex)
            {
                LogService.Error("performance", $"Common control factory prewarm stage failed: {stage}", ex);
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        return tcs.Task;
    }

    private async Task PrewarmNicProviderAfterFirstRenderAsync()
    {
        await Task.Delay(900).ConfigureAwait(false);
        if (_section != "overview") return;
        try
        {
            bool cacheReady = await Task.Run(PerformanceProfilesService.TryWarmNicCapabilityPersistentSnapshot).ConfigureAwait(false);
            if (cacheReady)
            {
                LogService.Info("performance", "NIC_PROVIDER_PREWARM_SKIP persistent=true");
                return;
            }

            await Task.Run(NativeNicTuningService.PrewarmReadScope).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            LogService.Info("performance", "NIC_PROVIDER_PREWARM_SKIP type=" + ex.GetType().Name);
        }
    }

    private async Task PrewarmTcpGlobalStateAfterFirstRenderAsync()
    {
        await Task.Delay(300).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await TcpGlobalStateService.GetSnapshotAsync().ConfigureAwait(false);
            LogService.Info("performance", $"TCP_GLOBAL_PREWARM_OK ms={sw.ElapsedMilliseconds}");
        }
        catch (System.Exception ex)
        {
            LogService.Info("performance",
                $"TCP_GLOBAL_PREWARM_SKIP ms={sw.ElapsedMilliseconds} type={ex.GetType().Name}");
        }
    }

    private async Task PrewarmNetworkUiStagedAsync()
    {
        if (_networkUiFactoriesPrewarmed) return;
        await RunLowPriorityNetworkPrewarmStageAsync("hero", () =>
        {
            // Match the real Network dashboard hero factories, not the older section hero.
            // A one-time Measure materializes the WinUI templates off the interactive path.
            var root = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 24, MinHeight = 176 };
            root.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition());
            root.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition
            {
                Width = new Microsoft.UI.Xaml.GridLength(320)
            });

            var left = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8 };
            left.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Internet / Síť", FontSize = 22 });
            left.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Prewarm network hero" });

            var badges = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 7
            };
            badges.Children.Add(Badge("LIVE LINK", "SpankySuccessBrush"));
            badges.Children.Add(Badge("DNS", "SpankyInfoBrush"));
            badges.Children.Add(Badge("MTU", "SpankyInfoBrush"));
            badges.Children.Add(Badge("READ-BACK", "SpankySuccessBrush"));
            left.Children.Add(badges);

            var actions = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8
            };
            actions.Children.Add(PrimaryQuickButton("Network", () => Task.CompletedTask));
            actions.Children.Add(QuickButton("DNS", () => Task.CompletedTask));
            actions.Children.Add(QuickButton("MTU", () => Task.CompletedTask));
            left.Children.Add(actions);
            root.Children.Add(left);

            var card = BuildDashboardCard(root, new Microsoft.UI.Xaml.Thickness(20, 17, 20, 17));
            card.Measure(new Windows.Foundation.Size(1100, 220));
        }).ConfigureAwait(false);

        await RunLowPriorityNetworkPrewarmStageAsync("surface", () =>
            _ = BuildSectionSurface("network", "Network", "Prewarm", new Microsoft.UI.Xaml.Controls.StackPanel())).ConfigureAwait(false);

        await RunLowPriorityNetworkPrewarmStageAsync("summary", () =>
            _ = BuildPageSummaryStrip(("Network", "Ready", "SpankySuccessBrush"))).ConfigureAwait(false);

        await RunLowPriorityNetworkPrewarmStageAsync("action", () =>
            _ = ActionCard("Network", "Prewarm", "OK", () => Task.CompletedTask)).ConfigureAwait(false);

        _networkUiFactoriesPrewarmed = true;
    }

    private Task RunLowPriorityNetworkPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_section == "overview") RunWithNeutralPrewarmPalette(action);
                LogService.Info("performance", $"NETWORK_UI_PREWARM_STAGE stage={stage} ms={sw.ElapsedMilliseconds}");
                tcs.TrySetResult(true);
            }
            catch (System.Exception ex)
            {
                LogService.Error("performance", $"Network UI prewarm stage failed: {stage}", ex);
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        return tcs.Task;
    }

}
