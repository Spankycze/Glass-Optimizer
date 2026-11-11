using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;
using SpankyhoOptimizer.ViewModels;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private IReadOnlyList<TaskInfo>? _taskRowsSnapshot;
    private TaskRowViewModel[]? _taskRowsCache;
    private int _taskPageLoadGeneration;

    private TaskRowViewModel[] GetTaskRows(IReadOnlyList<TaskInfo> tasks)
    {
        if (ReferenceEquals(_taskRowsSnapshot, tasks) && _taskRowsCache != null) return _taskRowsCache;
        var rows = new TaskRowViewModel[tasks.Count];
        for (int i = 0; i < tasks.Count; i++) rows[i] = new TaskRowViewModel(tasks[i], OnTaskRowChanged);
        _taskRowsSnapshot = tasks;
        return _taskRowsCache = rows;
    }

    private static DataTemplate TaskRowTemplate() => UiResourceService.Get<DataTemplate>("TaskRowTemplateV136");

    internal void PrewarmServiceUi()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _ = BuildModernSectionHero(
            "components",
            "Služby",
            "Bezpečné doporučené nastavení služeb s následným ověřením.",
            "SLUŽBY",
            "4 řízené změny • stav se ověřuje proti Windows",
            actions);
        var statusHost = new StackPanel { Spacing = 8 };
        statusHost.Children.Add(new TextBlock { Text = "Prewarm", Foreground = TextSecondaryBrush() });
        _ = BuildSectionSurface(
            "components",
            "Skutečný stav služeb",
            "CscService • DiagTrack • MapsBroker • StorSvc",
            statusHost);
    }

    private Button ComponentRecommendedCtaButton(string text, Func<Task> action)
    {
        var button = QuickButton(text, action);
        button.MinWidth = 220;
        button.MinHeight = 34;
        button.Padding = new Thickness(13, 6, 13, 6);
        button.CornerRadius = new CornerRadius(10);
        // Same primary blue-glass action used across every section.
        ApplyMaterialButtonAppearance(button, primary: true);
        button.BorderThickness = new Thickness(1);
        button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                SidebarIcon("components", 18),
                new TextBlock { Text = text, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = OnAccentTextBrush(), VerticalAlignment = VerticalAlignment.Center }
            }
        };
        return button;
    }

    private Task BuildServiceManagerPageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int generation = _navigationGeneration;
        // The deferred method does no UI work until its one low-priority
        // dispatcher commit. Avoid queueing the delay itself to the UI thread.
        SafeTaskService.Forget(
            CompleteServiceUiDeferredAsync(token, generation),
            "services",
            "Deferred service UI");
        return Task.CompletedTask;
    }

    private async Task CompleteServiceUiDeferredAsync(CancellationToken token, int generation)
    {
        var firstPaint = System.Diagnostics.Stopwatch.StartNew();
        // One short deferred pass instead of two queued compositions
        // separated by 75+35 ms of unavoidable first-paint waiting.
        await Task.Delay(32, token).ConfigureAwait(false);
        if (token.IsCancellationRequested || generation != _navigationGeneration) return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "components" || _sub != "Služby") return;

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            actions.Children.Add(PrimaryQuickButton("Použít doporučené", ApplyWinUtilRecommendedServicesAsync));
            actions.Children.Add(ComponentRecommendedCtaButton("Podle používání", ShowComponentsContextDialogAsync));
            actions.Children.Add(QuickButton("Obnovit stav", async () =>
            {
                // This panel reads four live registry keys directly;
                // no need to invalidate the expensive all-services catalog.
                InvalidateComponentsPageCache("Služby");
                await ShowSectionAsync("components");
            }));

            ContentStack.Children.Add(BuildModernSectionHero(
                "components",
                "Služby",
                "Bezpečné doporučené nastavení služeb s následným ověřením.",
                "SLUŽBY",
                "4 řízené změny • stav se ověřuje proti Windows",
                actions));

            // Compose both surfaces in the same low-priority dispatcher pass.
            // This preserves navigation cancellation and prevents a redundant
            // second layout invalidation 35 ms later.
            var statusHost = new StackPanel { Spacing = 8 };
            statusHost.Children.Add(new TextBlock
            {
                Text = "Načítám skutečný stav služeb…",
                Foreground = TextSecondaryBrush()
            });
            ContentStack.Children.Add(BuildSectionSurface(
                "components",
                "Skutečný stav služeb",
                "CscService • DiagTrack • MapsBroker • StorSvc",
                statusHost));
            SafeTaskService.Forget(
                LoadManagedServiceStatusAsync(statusHost, token, generation),
                "services",
                "Managed service read-back");
            LogService.Info("performance",
                $"SERVICES_FIRST_PAINT heroAndStatusShellMs={firstPaint.Elapsed.TotalMilliseconds:F2} passes=1 delayMs=32");
        });
    }

    private async Task LoadManagedServiceStatusAsync(StackPanel host, CancellationToken token, int generation)
    {
        try
        {
            var services = await Task.Run(ServiceCatalogService.ReadRecommendedStartTypes, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_sub, "Služby", StringComparison.Ordinal) || host.Parent == null)
                return;

            var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["CscService"] = 4,
                ["DiagTrack"] = 4,
                ["MapsBroker"] = 3,
                ["StorSvc"] = 3
            };
            int matched = expected.Count(x => services.TryGetValue(x.Key, out int mode) && mode == x.Value);
            int missing = expected.Count(x => !services.ContainsKey(x.Key));

            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration ||
                    _section != "components" || _sub != "Služby" || host.Parent == null) return;
                host.Children.Clear();
                host.Children.Add(BuildDashboardMetricGrid(
                    BuildDashboardMetric("Shoda", $"{matched}/{expected.Count}", "Doporučený Start", matched == expected.Count ? "SpankySuccessBrush" : "SpankyWarningBrush"),
                    BuildDashboardMetric("Chybí", missing.ToString(), "Nedetekované služby", missing == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush"),
                    BuildDashboardMetric("Read-back", "ON", "Skutečný stav Windows", "SpankySuccessBrush"),
                    BuildDashboardMetric("Režim", "Safe", "Jen 4 řízené služby")));

                foreach (var item in expected)
                {
                    bool found = services.TryGetValue(item.Key, out int mode);
                    string actual = found ? ServiceStartTypeLabel(mode) : "Nenalezeno";
                    string wanted = ServiceStartTypeLabel(item.Value);
                    bool ok = found && mode == item.Value;
                    host.Children.Add(InsightRow(
                        item.Key,
                        $"{actual} • očekáváno {wanted}{(ok ? " • OK" : " • ODCHYLKA")}"));
                }

            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("services", "Managed service read-back failed", ex);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration ||
                    _section != "components" || _sub != "Služby" || host.Parent == null) return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Skutečný stav služeb se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                host.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadManagedServiceStatusAsync(host, token, generation)));
                ShowActionFeedback("Služby", ActionFeedbackKind.Failed, ex.Message);
            });
        }
    }

    private static string ServiceStartTypeLabel(int startType) => startType switch
    {
        2 => "Automaticky",
        3 => "Ručně",
        4 => "Vypnuto",
        _ => $"Start={startType}"
    };

    private async Task ApplyWinUtilRecommendedServicesAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Read the validated WinUtil data adapter, not a disconnected downloaded archive.
        // This retains our supported four-service scope and all native safety/read-back checks.
        var changes = await Task.Run(WinUtilServicePolicy.ReadRecommended);

        var progressResult = await RunCancellableWithProgressAsync(
            "Služby — Recommended",
            "Aplikuji doporučené nastavení služeb a ověřuji výsledek…",
            async token =>
            {
                var localErrors = new List<string>();

                try
                {
                    localErrors.AddRange(await WorkerClientService.RunAsync<string[]>(
                        "service-set-batch",
                        WorkerPayloads.Encode(new ServiceBatchRequest(changes, SkipMissing: true)),
                        TimeSpan.FromMinutes(2),
                        token));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Error("services", "Recommended service batch failed", ex);
                    localErrors.Add("Services batch: " + ex.Message);
                }

                return localErrors.ToArray();
            },
            result => result.Length == 0,
            result => result.Length == 0
                ? string.Empty
                : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("services", "Services Recommended cancelled by user.");
            ServiceCatalogService.InvalidateCache();
            InvalidateComponentsPageCache("Služby");
            if (string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase))
                await ShowSectionAsync("components");
            return;
        }
        string[] errors = progressResult.Result;

        ServiceCatalogService.InvalidateCache();
        InvalidateComponentsPageCache("Služby");
        // The worker has finished applying/verifying changes. Immediately
        // refresh the live read-back panel instead of leaving pre-apply labels.
        if (_section == "components" && _sub == "Služby")
            await ShowSectionAsync("components");
        LogService.Info("performance", $"SERVICES_RECOMMENDED_PHASE changes={changes.Length} errors={errors.Length} totalMs={sw.ElapsedMilliseconds}");

        if (errors.Length == 0)
        {
            ShowActionFeedback("Služby", ActionFeedbackKind.Verified, "Doporučené nastavení bylo aplikováno a ověřeno.");
            await ShowStyledInfoAsync("Služby — Doporučené",
                "Doporučené nastavení dostupných služeb bylo aplikováno a ověřeno. Neexistující služby byly přeskočeny; chráněné služby zůstaly beze změny.");
        }
        else
        {
            ShowActionFeedback("Služby", ActionFeedbackKind.Failed, string.Join(" • ", errors.Take(3)));
            await ShowStyledInfoAsync("Služby — část změn selhala", string.Join(Environment.NewLine, errors.Take(8)));
        }
    }

    private Task BuildTaskManagerPageAsync(CancellationToken token)
    {
        int pageGeneration = unchecked(++_taskPageLoadGeneration);
        var perf = System.Diagnostics.Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        var tasks = TaskSchedulerService.TryGetBasicFreshSnapshot();
        long snapshotMs = perf.ElapsedMilliseconds;

        var source = tasks == null ? Array.Empty<TaskRowViewModel>() : GetTaskRows(tasks);
        var list = new ListView
        {
            MaxHeight = 650,
            SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = TaskRowTemplate(),
            ItemsPanel = UiResourceService.Get<ItemsPanelTemplate>("SpankyVirtualizingItemsPanel")
        };
        if (tasks != null)
            list.ItemsSource = source;
        long listEndMs = perf.ElapsedMilliseconds;
        long listMs = listEndMs - snapshotMs;

        int recommended = CatalogService.GetTweaks("components", "Task Scheduler")
            .Count(x => x.Recommended && x.Risk != TweakRisk.Advanced);
        long recommendedEndMs = perf.ElapsedMilliseconds;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(ComponentRecommendedCtaButton("Doporučené podle toho, co používám", ShowComponentsContextDialogAsync));
        actions.Children.Add(QuickButton("Použít doporučené", ApplyTaskRecommendedPresetAsync));
        actions.Children.Add(QuickButton("Obnovit", async () =>
        {
            TaskSchedulerService.InvalidateCache();
            InvalidateComponentsPageCache("Task Scheduler");
            await ShowSectionAsync("components");
        }));
        long actionsEndMs = perf.ElapsedMilliseconds;

        string summary = tasks == null ? $"Načítám úlohy… • {recommended} doporučených" : $"{tasks.Count} úloh • {recommended} doporučených";
        ContentStack.Children.Add(BuildModernSectionHero(
            "components",
            "Task Scheduler",
            "Doporučené nastavení, ruční stav úloh a ochrana vybraných položek v jednom přehledu.",
            "ÚLOHY",
            summary,
            actions));
        long heroEndMs = perf.ElapsedMilliseconds;
        var metricsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(metricsHost);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                pageGeneration != _taskPageLoadGeneration ||
                !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_sub, "Task Scheduler", StringComparison.Ordinal) ||
                metricsHost.Children.Count > 0)
                return;

            var metricsPerf = System.Diagnostics.Stopwatch.StartNew();
            metricsHost.Children.Add(BuildDashboardMetricGrid(
                BuildDashboardMetric("Celkem", tasks?.Count.ToString() ?? "…", "Naplánované úlohy"),
                BuildDashboardMetric("Aktivní", tasks == null ? "…" : source.Count(x => x.Enabled).ToString(), "Zapnuté", "SpankySuccessBrush"),
                BuildDashboardMetric("Vypnuté", tasks == null ? "…" : source.Count(x => !x.Enabled).ToString(), "Zakázané"),
                BuildDashboardMetric("Doporučené", recommended.ToString(), "Bez pokročilých zásahů")));
            LogService.Info("performance", $"COMPONENT_TASK_METRICS_DEFERRED ms={metricsPerf.ElapsedMilliseconds}");
        });
        long metricsEndMs = perf.ElapsedMilliseconds;
        var info = new TextBlock
        {
            Text = tasks == null ? "Načítám naplánované úlohy…" : TaskSummary(source),
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        };
        ContentStack.Children.Add(BuildDashboardExpander(
            "Naplánované úlohy",
            "Ruční přepínače a detailní inventář",
            new StackPanel { Spacing = 8, Children = { info, list } }));
        long expanderEndMs = perf.ElapsedMilliseconds;
        bool refreshCachedSnapshot = tasks != null && TaskSchedulerService.NeedsBasicBackgroundRefresh;
        if (tasks == null || refreshCachedSnapshot)
            SafeTaskService.Forget(
                PopulateTaskRowsDeferredAsync(list, info, token, pageGeneration, forceRefresh: refreshCachedSnapshot),
                "tasks",
                refreshCachedSnapshot ? "Refresh cached task inventory" : "Deferred task inventory");
        LogService.Info("performance",
            $"COMPONENT_TASK_UI_PHASE snapshotMs={snapshotMs} listMs={listMs} recommendedMs={recommendedEndMs - listEndMs} actionsMs={actionsEndMs - recommendedEndMs} heroMs={heroEndMs - actionsEndMs} metricsMs={metricsEndMs - heroEndMs} expanderMs={expanderEndMs - metricsEndMs} totalMs={perf.ElapsedMilliseconds} cached={(tasks != null)} refreshQueued={refreshCachedSnapshot} rows={source.Length}");
        return Task.CompletedTask;
    }

    private static string TaskSummary(IReadOnlyCollection<TaskRowViewModel> source)
    {
        int enabled = 0;
        int protectedCount = 0;
        foreach (var row in source)
        {
            if (row.Enabled) enabled++;
            if (!row.CanEdit) protectedCount++;
        }
        return $"Aktivní {enabled} • vypnuté {source.Count - enabled} • chráněné {protectedCount}";
    }

    private void QueueTaskRowsApply(ListView list, TextBlock info, IReadOnlyList<TaskInfo> tasks, CancellationToken token, int pageGeneration)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested ||
                pageGeneration != _taskPageLoadGeneration ||
                !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_sub, "Task Scheduler", StringComparison.Ordinal))
                return;

            var perf = System.Diagnostics.Stopwatch.StartNew();
            var source = GetTaskRows(tasks);
            list.ItemsSource = source;
            info.Text = TaskSummary(source);
            LogService.Info("performance", $"TASK_DEFERRED_POPULATE rows={source.Length} uiMs={perf.ElapsedMilliseconds}");
        });
    }

    private async Task PopulateTaskRowsDeferredAsync(ListView list, TextBlock info, CancellationToken token, int pageGeneration, bool forceRefresh = false)
    {
        try
        {
            var service = new TaskSchedulerService();
            IReadOnlyList<TaskInfo>? tasks = null;
            IReadOnlyList<TaskInfo>? queuedWarmSnapshot = null;

            if (!forceRefresh)
            {
                bool hydrated = await TaskSchedulerService.TryWarmBasicDisplaySnapshotAsync(token).ConfigureAwait(false);
                tasks = TaskSchedulerService.TryGetBasicFreshSnapshot();
                if (hydrated && tasks != null)
                {
                    queuedWarmSnapshot = tasks;
                    QueueTaskRowsApply(list, info, tasks, token, pageGeneration);
                }

                if (TaskSchedulerService.NeedsBasicBackgroundRefresh)
                    tasks = await service.GetBasicAsync(forceRefresh: true, token).ConfigureAwait(false);
            }
            else
            {
                tasks = await service.GetBasicAsync(forceRefresh: true, token).ConfigureAwait(false);
            }

            // If warm hydration produced no snapshot, let the service decide whether its
            // in-memory cache is still usable before forcing another COM enumeration.
            tasks ??= await service.GetBasicAsync(forceRefresh: false, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Warm snapshot has already been queued: avoid a second identical UI
            // ItemsSource rebind and summary traversal unless refresh changed it.
            if (!ReferenceEquals(tasks, queuedWarmSnapshot))
                QueueTaskRowsApply(list, info, tasks, token, pageGeneration);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("tasks", "Deferred task inventory failed", ex);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    pageGeneration != _taskPageLoadGeneration ||
                    !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_sub, "Task Scheduler", StringComparison.Ordinal))
                    return;
                list.ItemsSource = Array.Empty<TaskRowViewModel>();
                info.Text = "Task Scheduler se nepodařilo načíst. Použij Obnovit pro nový pokus.";
                ShowActionFeedback("Task Scheduler", ActionFeedbackKind.Failed, ex.Message);
            });
        }
    }

    private async Task ApplyTaskRecommendedPresetAsync()
    {
        var tweaks = CatalogService.GetTweaks("components", "Task Scheduler")
            .Where(x => x.Recommended && x.Risk != TweakRisk.Advanced).ToArray();
        int appliedCount = 0;
        int preservedPendingCount = 0;
        var progressResult = await RunCancellableWithProgressAsync(
            "Task Scheduler — Recommended",
            "Zjišťuji stav a aplikuji pouze dosud nezměněné doporučené úlohy…",
            async token =>
            {
                await _engine.DetectAsync(tweaks, token);
                // Native readback preserves user-staged Pending edits.
                // Never override those choices with Recommended, and never
                // re-apply an already-active or unreadable task.
                preservedPendingCount = tweaks.Count(x => x.IsDirty);
                var candidates = tweaks.Where(x =>
                    x.IsLoaded && !x.IsBusy && !x.IsDirty && !x.Current).ToArray();
                appliedCount = candidates.Length;
                if (appliedCount == 0)
                    return Array.Empty<string>();
                foreach (var tweak in candidates) tweak.Pending = true;
                return await _engine.ApplyAsync(candidates, token);
            },
            result => result.Count == 0,
            result => result.Count == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("tasks", "Task Scheduler Recommended cancelled by user.");
            bool verified = await ReconcileCancelledApplyAsync(
                tweaks, "components:Task Scheduler Recommended");
            TaskSchedulerService.InvalidateCache();
            InvalidateComponentsPageCache("Task Scheduler");
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            if (!verified && _section == "components")
                ShowActionFeedback("Task Scheduler", ActionFeedbackKind.Failed,
                    "Dávka byla přerušena; stav naplánovaných úloh nelze spolehlivě potvrdit.");
            if (_section == "components")
                await ShowSectionAsync("components");
            return;
        }
        var errors = progressResult.Result;
        if (appliedCount > 0)
        {
            // No-op Recommended must not throw away the cached task page
            // or trigger a new native scheduler enumeration.
            TaskSchedulerService.InvalidateCache();
            InvalidateComponentsPageCache("Task Scheduler");
        }
        string resultMessage = appliedCount == 0
            ? "Žádná nová doporučená úloha nevyžadovala změnu."
            : $"Doporučené nastavení bylo ověřeno pro {appliedCount} úloh.";
        if (preservedPendingCount > 0)
            resultMessage += $" Rozpracovaných voleb ponecháno: {preservedPendingCount}.";
        ShowActionFeedback("Task Scheduler",
            errors.Count != 0 ? ActionFeedbackKind.Failed
                : appliedCount == 0 ? ActionFeedbackKind.Info : ActionFeedbackKind.Verified,
            errors.Count == 0 ? resultMessage : string.Join(" • ", errors));
        if (appliedCount > 0 && _section == "components")
            await ShowSectionAsync("components");
    }
    private void OnTaskRowChanged(TaskRowViewModel vm, bool enabled) =>
        SafeTaskService.Forget(ChangeTaskRowAsync(vm, enabled), "tasks", "Virtual task change");

    private async Task ChangeTaskRowAsync(TaskRowViewModel vm, bool enabled)
    {
        bool original = vm.Source.Enabled;
        if (!enabled && !TaskSafetyService.CanDisable(vm.Source.Path))
        {
            vm.Confirm(original);
            await ShowMessageAsync("Úloha je chráněná", TaskSafetyService.Classify(vm.Source.Path).Reason);
            return;
        }
        if (!await ShowStyledConfirmAsync(enabled ? "Zapnout úlohu?" : "Vypnout úlohu?",
            vm.Source.Path, "Použít", "Zrušit", "Task Scheduler"))
        {
            vm.Confirm(original);
            return;
        }
        try
        {
            var scheduler = new TaskSchedulerService();
            await scheduler.SetEnabledAsync(vm.Source.Path, enabled);
            vm.Confirm(enabled);
            ShowActionFeedback(vm.Source.Name, ActionFeedbackKind.Verified,
                enabled ? "Úloha je zapnutá a stav byl ověřen." : "Úloha je vypnutá a stav byl ověřen.");
        }
        catch (Exception ex)
        {
            vm.Confirm(original);
            ShowActionFeedback(vm.Source.Name, ActionFeedbackKind.Failed, ex.Message);
            await ShowMessageAsync("Task Scheduler", ex.Message);
        }
    }
}
