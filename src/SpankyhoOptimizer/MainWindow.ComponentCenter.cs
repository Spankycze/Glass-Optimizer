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

    internal void PrewarmServiceUi() { }

    private Button ComponentRecommendedCtaButton(string text, Func<Task> action)
    {
        var button = QuickButton(text, action);
        button.MinWidth = 220;
        button.MinHeight = 34;
        button.Padding = new Thickness(13, 6, 13, 6);
        button.CornerRadius = new CornerRadius(10);
        button.Background = AccentBrush();
        button.BorderBrush = AccentBrush();
        button.BorderThickness = new Thickness(1);
        button.Foreground = OnAccentTextBrush();
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
        var perf = System.Diagnostics.Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(PrimaryQuickButton("Použít doporučené", ApplyWinUtilRecommendedServicesAsync));
        actions.Children.Add(ComponentRecommendedCtaButton("Podle používání", ShowComponentsContextDialogAsync));
        actions.Children.Add(QuickButton("Obnovit stav", async () =>
        {
            ServiceCatalogService.InvalidateCache();
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
            LoadManagedServiceStatusAsync(statusHost, token),
            "services",
            "Managed service read-back");

        LogService.Info("performance", $"COMPONENT_SERVICE_UI_PHASE totalMs={perf.ElapsedMilliseconds}");
        return Task.CompletedTask;
    }

    private async Task LoadManagedServiceStatusAsync(StackPanel host, CancellationToken token)
    {
        try
        {
            var services = await new ServiceCatalogService().GetAsync(forceRefresh: false, token).ConfigureAwait(false);
            if (token.IsCancellationRequested ||
                !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_sub, "Služby", StringComparison.Ordinal))
                return;

            var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["CscService"] = 4,
                ["DiagTrack"] = 4,
                ["MapsBroker"] = 3,
                ["StorSvc"] = 3
            };
            var map = services.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            int matched = expected.Count(x => map.TryGetValue(x.Key, out var svc) && svc.StartType == x.Value);
            int missing = expected.Count(x => !map.ContainsKey(x.Key));

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "components" || _sub != "Služby") return;
                host.Children.Clear();
                host.Children.Add(BuildDashboardMetricGrid(
                    BuildDashboardMetric("Shoda", $"{matched}/{expected.Count}", "Doporučený Start", matched == expected.Count ? "SpankySuccessBrush" : "SpankyWarningBrush"),
                    BuildDashboardMetric("Chybí", missing.ToString(), "Nedetekované služby", missing == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush"),
                    BuildDashboardMetric("Read-back", "ON", "Skutečný stav Windows", "SpankySuccessBrush"),
                    BuildDashboardMetric("Režim", "Safe", "Jen 4 řízené služby")));

                foreach (var item in expected)
                {
                    bool found = map.TryGetValue(item.Key, out var svc);
                    string actual = found ? ServiceStartTypeLabel(svc!.StartType) : "Nenalezeno";
                    string wanted = ServiceStartTypeLabel(item.Value);
                    bool ok = found && svc!.StartType == item.Value;
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
                if (_section != "components" || _sub != "Služby") return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Skutečný stav služeb se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                host.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadManagedServiceStatusAsync(host, token)));
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
        var changes = new[]
        {
            new ServiceChangeRequest("CscService", 4),
            new ServiceChangeRequest("DiagTrack", 4),
            new ServiceChangeRequest("MapsBroker", 3),
            new ServiceChangeRequest("StorSvc", 3)
        };

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
                        WorkerPayloads.Encode(new ServiceBatchRequest(changes)),
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
        LogService.Info("performance", $"SERVICES_RECOMMENDED_PHASE changes={changes.Length} errors={errors.Length} totalMs={sw.ElapsedMilliseconds}");

        if (errors.Length == 0)
        {
            ShowActionFeedback("Služby", ActionFeedbackKind.Verified, "Doporučené nastavení bylo aplikováno a ověřeno.");
            await ShowStyledInfoAsync("Služby — Doporučené",
                "Doporučené nastavení služeb bylo aplikováno a následná kontrola proběhla úspěšně. Systémově spravované služby zůstaly beze změny.");
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

    private static string TaskSummary(IReadOnlyCollection<TaskRowViewModel> source) =>
        $"Aktivní {source.Count(x => x.Enabled)} • vypnuté {source.Count(x => !x.Enabled)} • chráněné {source.Count(x => !x.CanEdit)}";

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

            if (!forceRefresh)
            {
                bool hydrated = await TaskSchedulerService.TryWarmBasicDisplaySnapshotAsync(token).ConfigureAwait(false);
                tasks = TaskSchedulerService.TryGetBasicFreshSnapshot();
                if (hydrated && tasks != null)
                    QueueTaskRowsApply(list, info, tasks, token, pageGeneration);

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
        var progressResult = await RunCancellableWithProgressAsync(
            "Task Scheduler — Recommended",
            $"Detekuji a aplikuji {tweaks.Length} Recommended úloh…",
            async token =>
            {
                await _engine.DetectAsync(tweaks, token);
                foreach (var tweak in tweaks) tweak.Pending = true;
                return await _engine.ApplyAsync(tweaks, token);
            },
            result => result.Count == 0,
            result => result.Count == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("tasks", "Task Scheduler Recommended cancelled by user.");
            await ReconcileCancelledApplyAsync(tweaks, "components:Task Scheduler Recommended");
            TaskSchedulerService.InvalidateCache();
            InvalidateComponentsPageCache("Task Scheduler");
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            await ShowSectionAsync("components");
            return;
        }
        var errors = progressResult.Result;
        TaskSchedulerService.InvalidateCache();
        InvalidateComponentsPageCache("Task Scheduler");
        ShowActionFeedback("Task Scheduler",
            errors.Count == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
            errors.Count == 0 ? "Doporučené nastavení bylo aplikováno." : string.Join(" • ", errors));
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
