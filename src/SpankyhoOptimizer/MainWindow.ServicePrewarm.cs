using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private bool _virtualControlFactoriesPrewarmed;
    private bool _virtualTweakResourcesPrewarmed;
    private bool _powerCardFactoryPrewarmed;
    private bool _actionButtonFactoriesPrewarmed;
    private bool _driverUiFactoryPrewarmed;
    private bool _networkUiFactoriesPrewarmed;
    private Microsoft.UI.Xaml.FrameworkElement? _prewarmedDebloatLoadingHero;
    private AppThemePreference? _prewarmedDebloatThemePreference;

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
        if (!CanCommitOverviewPrewarm(_virtualTweakResourcesPrewarmed,
                _section == "overview"))
        {
            await profileWarmTask.ConfigureAwait(false);
            return;
        }
        await PrewarmActionButtonFactoriesEarlyAsync().ConfigureAwait(false);
        // An aborted/rejected action-button stage may leave Overview active;
        // do not queue more expensive UI factory work unless BOTH prerequisite
        // prewarm sets really finished. Preserve their on-demand fallbacks.
        if (!CanContinueOverviewPrewarm(_virtualTweakResourcesPrewarmed,
                _actionButtonFactoriesPrewarmed, _section == "overview"))
        {
            await profileWarmTask.ConfigureAwait(false);
            return;
        }
        await PrewarmVirtualControlFactoriesAfterFirstRenderAsync().ConfigureAwait(false);
        if (!CanCommitOverviewPrewarm(_virtualControlFactoriesPrewarmed,
                _section == "overview"))
        {
            await profileWarmTask.ConfigureAwait(false);
            return;
        }
        // Driver row/shell factories are optional and unrelated to System
        // navigation. Do not charge their 9+6+4ms UI work to the awaited
        // virtual-tweak pipeline: strict first-System navigation follows it.
        // Let them warm later only if the user remains on Overview.
        SafeTaskService.Forget(
            PrewarmDriverUiFactoryAfterFirstRenderAsync(),
            "prewarm", "Optional driver UI factory warmup");
        await profileWarmTask.ConfigureAwait(false);
    }

    private async Task PrewarmVirtualTweakResourcesEarlyAsync()
    {
        // Keep the prewarm close to first render, but split cold XAML/resource activation across
        // Low-priority dispatcher turns so one 15-25 ms callback cannot cause a visible post-startup hitch.
        await Task.Yield();
        if (_section != "overview") return;

        var total = System.Diagnostics.Stopwatch.StartNew();

        if (!await RunLowPriorityVirtualResourcePrewarmStageAsync("tweak-template", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TweakVirtualRowTemplate")).ConfigureAwait(false)) return;
        if (!await RunLowPriorityVirtualResourcePrewarmStageAsync("task-template", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TaskRowTemplateV136")).ConfigureAwait(false)) return;
        if (!await RunLowPriorityVirtualResourcePrewarmStageAsync("startup-template", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("StartupRowTemplateV209")).ConfigureAwait(false)) return;
        if (!await RunLowPriorityVirtualResourcePrewarmStageAsync("items-panel", () =>
            _ = UiResourceService.Get<Microsoft.UI.Xaml.Controls.ItemsPanelTemplate>("SpankyVirtualizingItemsPanel")).ConfigureAwait(false)) return;
        if (!await RunLowPriorityVirtualResourcePrewarmStageAsync("icons", () =>
        {
            _ = SectionIconSource("system");
            _ = SectionIconSource("privacy");
            _ = SectionIconSource("ui");
            _ = SectionIconSource("update");
            _ = SectionIconSource("gpu");
            _ = SectionIconSource("drivers");
        }).ConfigureAwait(false)) return;

        // Navigation can switch pages between the final await and commit.
        if (!CanCommitOverviewPrewarm(true, _section == "overview")) return;
        _virtualTweakResourcesPrewarmed = true;
        LogService.Info("performance", $"VIRTUAL_RESOURCE_EARLY_PREWARM staged=true totalWallMs={total.ElapsedMilliseconds}");
    }

    // The dispatcher may accept a stage after navigation has left Overview.
    // A skipped/rejected stage is NOT a completed prewarm. Callers must
    // preserve the on-demand System fallback rather than setting ready=true.
    private static bool CanCommitOverviewPrewarm(bool stageRan, bool stillOnOverview) =>
        stageRan && stillOnOverview;

    // Two independent prerequisites must be ready before optional virtual
    // factory construction can consume further UI dispatcher turns.
    private static bool CanContinueOverviewPrewarm(
        bool resourcesReady, bool actionsReady, bool stillOnOverview) =>
        resourcesReady && actionsReady && stillOnOverview;

    private Task<bool> RunLowPriorityVirtualResourcePrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool ran = _section == "overview";
                if (ran) action();
                LogService.Info("performance", $"VIRTUAL_RESOURCE_PREWARM_STAGE stage={stage} ms={sw.ElapsedMilliseconds} applied={ran}");
                tcs.TrySetResult(ran);
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

        if (!await RunNormalPriorityFactoryPrewarmStageAsync("quick-action", () =>
            _ = QuickButton("Prewarm", () => Task.CompletedTask)).ConfigureAwait(false)) return;

        await Task.Yield();

        if (!await RunNormalPriorityFactoryPrewarmStageAsync("primary-action", () =>
            _ = PrimaryQuickButton("Prewarm", () => Task.CompletedTask)).ConfigureAwait(false)) return;

        // Give the dispatcher another scheduling boundary before selector-specific work.
        await Task.Yield();

        if (!await RunNormalPriorityFactoryPrewarmStageAsync("components-selector", () =>
        {
            // Components navigation now uses direct properties instead of a runtime Style/Setter graph.
            foreach (string label in new[] { "Služby", "Ovladače", "Task Scheduler" })
            {
                var button = new Microsoft.UI.Xaml.Controls.Button { Content = label, Tag = label };
                ApplyPillButtonAppearance(button);
            }
        }).ConfigureAwait(false)) return;

        if (!CanCommitOverviewPrewarm(true, _section == "overview")) return;
        _actionButtonFactoriesPrewarmed = true;
    }

    private Task<bool> RunNormalPriorityFactoryPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool ran = _section == "overview" && !_windowLifetimeCts.IsCancellationRequested;
                if (ran) action();
                LogService.Info("performance",
                    $"ACTION_BUTTON_FACTORY_PREWARM stage={stage} ms={sw.ElapsedMilliseconds} priority=Normal applied={ran}");
                tcs.TrySetResult(ran);
            }
            catch (System.Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        return tcs.Task;
    }

    // Smoke entry points are dispatched right after the window is created.
    // RootGrid.Loaded may still have its startup navigation pending; without
    // waiting, the smoke changes _section first and the startup navigation
    // then navigates a second time behind it, cancelling the first deferred
    // build. Wait for the real initial navigation so the measurement below
    // covers exactly one cold System navigation.
    private async Task WaitForInitialNavigationSmokeAsync()
    {
        for (int i = 0; i < 300 && !_initialSectionNavigationCompleted; i++)
            await Task.Delay(10);
        if (!_initialSectionNavigationCompleted)
            throw new System.InvalidOperationException(
                "Initial section navigation did not complete before the smoke started.");
    }

    internal async Task RunSystemColdRenderSmokeAsync()
    {
        await WaitForInitialNavigationSmokeAsync();
        if (_section != "overview")
            throw new System.InvalidOperationException("System cold render smoke expected Overview before navigation.");

        bool resourcesWereCold = !_virtualTweakResourcesPrewarmed;
        InvalidateSystemPageCache();
        // A fresh process starts with no warmed virtual resources. Track that
        // so the regression proves the intentional two-stage cold path:
        // one deferred turn for templates, one for catalog + virtual cards.
        long navigationStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
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
        int deferredTurns = _systemDeferredDispatchTurns;
        int expectedTurns = resourcesWereCold ? 2 : 1;
        if (deferredTurns != expectedTurns)
            throw new System.InvalidOperationException(
                $"System cold deferred materialization used {deferredTurns} dispatcher turns; expected {expectedTurns} (resourcesWereCold={resourcesWereCold}).");
        long deferredReadyMs = _systemDeferredReadyTimestamp == 0
            ? -1
            : (long)System.Diagnostics.Stopwatch.GetElapsedTime(navigationStartTs, _systemDeferredReadyTimestamp).TotalMilliseconds;
        int deferredAtRoot = _systemDeferredAtRootBuild;
        if (deferredAtRoot < 20)
            throw new System.InvalidOperationException($"System cold render deferred too little work: {deferredAtRoot} groups.");

        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await Task.Delay(25);
        int remaining = GetDeferredVirtualGroupCount("system");
        if (remaining != 0)
            throw new System.InvalidOperationException($"System cold deferred groups incomplete: {remaining} remaining.");
        long groupsReadyMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(navigationStartTs).TotalMilliseconds;

        LogService.Info("performance",
            $"SYSTEM_COLD_RENDER_SMOKE_OK renderMs={renderMs} deferredAtRoot={deferredAtRoot} deferredRemaining={remaining} deferredReadyMs={deferredReadyMs} groupsReadyMs={groupsReadyMs} deferredTurns={deferredTurns}");
    }

    internal async Task RunVirtualResourcePrewarmSmokeAsync()
    {
        await WaitForInitialNavigationSmokeAsync();
        // Regression: a dispatcher callback may be skipped when navigation
        // leaves Overview between scheduling and running a prewarm stage.
        // Neither skipped stage nor departed page may assert cache readiness.
        if (CanCommitOverviewPrewarm(false, true) ||
            CanCommitOverviewPrewarm(true, false) ||
            CanCommitOverviewPrewarm(false, false) ||
            !CanCommitOverviewPrewarm(true, true))
            throw new System.InvalidOperationException(
                "Overview resource prewarm incorrectly commits a skipped stage.");
        if (!CanContinueOverviewPrewarm(true, true, true) ||
            CanContinueOverviewPrewarm(false, true, true) ||
            CanContinueOverviewPrewarm(true, false, true) ||
            CanContinueOverviewPrewarm(true, true, false) ||
            CanContinueOverviewPrewarm(false, false, true))
            throw new System.InvalidOperationException(
                "Overview prewarm continued without both prerequisite stages.");
        if (_section != "overview")
            throw new System.InvalidOperationException("Virtual resource smoke expected Overview before navigation.");
        // Synthetic stage actions exercise all three production gates without
        // creating controls or touching persisted/user system state.
        bool actionStageRan = false;
        bool driverStageRan = false;
        bool networkStageRan = false;
        if (!await RunNormalPriorityFactoryPrewarmStageAsync("smoke-action",
                () => actionStageRan = true) || !actionStageRan ||
            !await RunLowPriorityCommonControlPrewarmStageAsync("smoke-driver",
                () => driverStageRan = true) || !driverStageRan ||
            !await RunLowPriorityNetworkPrewarmStageAsync("smoke-network",
                () => networkStageRan = true) || !networkStageRan)
            throw new System.InvalidOperationException(
                "Overview prewarm stage did not confirm actual UI execution.");
        if (_virtualTweakResourcesPrewarmed || _virtualControlFactoriesPrewarmed)
            throw new System.InvalidOperationException("Virtual resources/factories were eagerly prewarmed during startup.");

        var onDemandWarm = System.Diagnostics.Stopwatch.StartNew();
        await PrewarmVirtualTweakPipelineAsync();
        long prewarmReadyMs = onDemandWarm.ElapsedMilliseconds;
        if (!_virtualTweakResourcesPrewarmed)
            throw new System.InvalidOperationException("On-demand virtual resource warmup did not complete.");
        if (!_virtualControlFactoriesPrewarmed)
            throw new System.InvalidOperationException("On-demand virtual control factory warmup did not complete.");
        if (!_actionButtonFactoriesPrewarmed)
            throw new System.InvalidOperationException("On-demand action button factory warmup did not complete.");
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
        if (_systemDeferredDispatchTurns != 1)
            throw new System.InvalidOperationException(
                $"Prewarmed System deferred materialization used {_systemDeferredDispatchTurns} dispatcher turns; expected one.");
        if (_systemDeferredAtRootBuild < 20)
            throw new System.InvalidOperationException($"Prewarmed System deferred too little work: {_systemDeferredAtRootBuild} groups.");

        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await Task.Delay(25);
        int remaining = GetDeferredVirtualGroupCount("system");
        if (remaining != 0)
            throw new System.InvalidOperationException($"Prewarmed System deferred groups incomplete: {remaining} remaining.");

        LogService.Info("performance",
            $"VIRTUAL_RESOURCE_PREWARM_SMOKE_OK prewarmReadyMs={prewarmReadyMs} systemRenderMs={renderMs} deferredAtRoot={_systemDeferredAtRootBuild} deferredRemaining={remaining} deferredTurns={_systemDeferredDispatchTurns}");
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

            if (!await RunVirtualControlPrewarmStageAsync("header", () =>
                header = CreateVirtualGroupHeader("PREWARM   •   0 TWEAKŮ")).ConfigureAwait(false)) return;

            if (!await RunVirtualControlPrewarmStageAsync("template", () =>
                rowTemplate = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TweakVirtualRowTemplate")).ConfigureAwait(false)) return;

            if (!await RunVirtualControlPrewarmStageAsync("repeater-ctor", () =>
                repeater = new Microsoft.UI.Xaml.Controls.ItemsRepeater()).ConfigureAwait(false)) return;

            // Match the production ItemsRepeater initialization order:
            // layout -> template -> source. The previous factory prewarm used
            // template -> source -> layout, exercising a different cold WinUI
            // dependency-property path than the first real System groups.
            if (!await RunVirtualControlPrewarmStageAsync("repeater-layout", () =>
            {
                repeater!.Layout = new Microsoft.UI.Xaml.Controls.StackLayout { Spacing = 6 };
                repeater.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            }).ConfigureAwait(false)) return;

            if (!await RunVirtualControlPrewarmStageAsync("repeater-template", () =>
                repeater!.ItemTemplate = rowTemplate).ConfigureAwait(false)) return;

            if (!await RunLowPriorityVirtualResourcePrewarmStageAsync("repeater-source", () =>
                repeater!.ItemsSource = new System.Collections.Generic.List<TweakVirtualRowViewModel>()).ConfigureAwait(false)) return;

            if (!await RunVirtualControlPrewarmStageAsync("expander", () =>
            {
                var expander = CreateVirtualGroupExpander(header!, repeater!);
                _ = expander is Microsoft.UI.Xaml.Controls.Expander control ? control.Content : (expander as Microsoft.UI.Xaml.Controls.Border)?.Child;
            }).ConfigureAwait(false)) return;

            if (!CanCommitOverviewPrewarm(true, _section == "overview")) return;
            _virtualControlFactoriesPrewarmed = true;
            LogService.Info("performance",
                $"VIRTUAL_CONTROL_PREWARM_SPLIT totalWallMs={total.ElapsedMilliseconds} priority=Normal stages=7");
        }
        catch (System.Exception ex)
        {
            LogService.Error("performance", "Virtual control factory prewarm failed", ex);
        }
    }

    private Task<bool> RunVirtualControlPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool ran = _section == "overview";
                if (ran) action();
                LogService.Info("performance",
                    $"VIRTUAL_CONTROL_PREWARM_STAGE stage={stage} ms={sw.ElapsedMilliseconds} priority=Normal applied={ran}");
                tcs.TrySetResult(ran);
            }
            catch (System.Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);

        return tcs.Task;
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

        // Give navigation its first second without unrelated WinUI factory
        // work. If the user already moved off Overview, do not warm drivers
        // behind the active page or block its first render.
        try { await Task.Delay(1000, _windowLifetimeCts.Token).ConfigureAwait(false); }
        catch (System.OperationCanceledException) { return; }
        if (_driverUiFactoryPrewarmed || _section != "overview") return;

        if (!await RunLowPriorityCommonControlPrewarmStageAsync("drivers-rows", () =>
        {
            PrewarmDriverRowsCache();
        }).ConfigureAwait(false)) return;
        if (_section != "overview" || _windowLifetimeCts.IsCancellationRequested) return;

        await Task.Yield();

        if (!await RunLowPriorityCommonControlPrewarmStageAsync("drivers-shell", () =>
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
        }).ConfigureAwait(false)) return;
        if (_section != "overview" || _windowLifetimeCts.IsCancellationRequested) return;

        await Task.Yield();

        if (!await RunLowPriorityCommonControlPrewarmStageAsync("drivers-list", () =>
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
        }).ConfigureAwait(false)) return;
        if (!CanCommitOverviewPrewarm(true,
                _section == "overview" && !_windowLifetimeCts.IsCancellationRequested)) return;

        _driverUiFactoryPrewarmed = true;
        LogService.Info("performance", "DRIVER_UI_FACTORY_PREWARM staged=true");
    }

    private Task<bool> RunLowPriorityCommonControlPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_section != "overview" || _windowLifetimeCts.IsCancellationRequested)
                {
                    tcs.TrySetResult(false);
                    return;
                }
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

    private async Task PrewarmNetworkUiStagedAsync()
    {
        if (_networkUiFactoriesPrewarmed) return;
        if (!await RunLowPriorityNetworkPrewarmStageAsync("hero", () =>
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
        }).ConfigureAwait(false)) return;

        if (!await RunLowPriorityNetworkPrewarmStageAsync("surface", () =>
            _ = BuildSectionSurface("network", "Network", "Prewarm", new Microsoft.UI.Xaml.Controls.StackPanel())).ConfigureAwait(false)) return;

        if (!await RunLowPriorityNetworkPrewarmStageAsync("summary", () =>
            _ = BuildPageSummaryStrip(("Network", "Ready", "SpankySuccessBrush"))).ConfigureAwait(false)) return;

        if (!await RunLowPriorityNetworkPrewarmStageAsync("action", () =>
            _ = ActionCard("Network", "Prewarm", "OK", () => Task.CompletedTask)).ConfigureAwait(false)) return;

        if (!CanCommitOverviewPrewarm(true, _section == "overview")) return;
        _networkUiFactoriesPrewarmed = true;
    }

    private Task<bool> RunLowPriorityNetworkPrewarmStageAsync(string stage, System.Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool ran = _section == "overview" && !_windowLifetimeCts.IsCancellationRequested;
                if (ran) RunWithNeutralPrewarmPalette(action);
                LogService.Info("performance", $"NETWORK_UI_PREWARM_STAGE stage={stage} ms={sw.ElapsedMilliseconds} applied={ran}");
                tcs.TrySetResult(ran);
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
