using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Microsoft.Win32;
using SpankyhoOptimizer.Services;
using SpankyhoOptimizer.ViewModels;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Task? _startupHydrationTask;
    private ListView? _startupVirtualList;
    private ListView? _startupSystemList;
    private Expander? _startupSystemExpander;
    private static readonly SemaphoreSlim StartupDelayGate = new(1, 1);

    private static bool IsWindowsSystemStartupEntry(StartupEntry item)
    {
        string windows = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        return IsWindowsSystemStartupEntry(item, windows);
    }

    private static bool IsWindowsSystemStartupEntry(StartupEntry item, string windowsRoot)
    {
        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(item.SourceId) &&
            item.SourceId.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase))
            return true;

        static string ExpandIfNeeded(string? value)
        {
            string text = value ?? string.Empty;
            return text.IndexOf('%') >= 0 ? Environment.ExpandEnvironmentVariables(text) : text;
        }

        static bool IsSystemPath(string path, string windowsRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string normalized = path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            string root = windowsRoot.TrimEnd(Path.DirectorySeparatorChar);
            return normalized.StartsWith(root + @"\System32\", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(root + @"\SysWOW64\", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(root + @"\SystemApps\", StringComparison.OrdinalIgnoreCase);
        }

        string filePath = ExpandIfNeeded(item.FilePath);
        string command = ExpandIfNeeded(item.Command);
        if (IsSystemPath(filePath, windowsRoot) || IsSystemPath(command, windowsRoot))
            return true;

        string name = item.Name ?? string.Empty;
        return name.Equals("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Windows Security notification", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreStartupInventoriesEquivalent(
        IReadOnlyList<StartupEntry>? previous, IReadOnlyList<StartupEntry> current)
    {
        if (previous == null) return false;
        if (ReferenceEquals(previous, current)) return true;
        if (previous.Count != current.Count) return false;
        // The Startup inventory is already sorted by display name; compare
        // complete records, including Enabled, Command and CanToggle.
        for (int i = 0; i < current.Count; i++)
            if (previous[i] != current[i]) return false;
        return true;
    }

    internal static void RunStartupHydrationPolicySmoke()
    {
        var entry = new StartupEntry("startup-smoke", "Smoke", "smoke.exe",
            RegistryHive.CurrentUser, @"Software\Smoke", true, "Smoke",
            "smoke.exe");
        StartupEntry[] original = [entry];
        StartupEntry[] unchanged = [entry with { }];
        if (!AreStartupInventoriesEquivalent(original, unchanged) ||
            !AreStartupInventoriesEquivalent(original, original) ||
            AreStartupInventoriesEquivalent(null, original) ||
            AreStartupInventoriesEquivalent(original, Array.Empty<StartupEntry>()) ||
            AreStartupInventoriesEquivalent(original, [entry with { Enabled = false }]) ||
            AreStartupInventoriesEquivalent(original, [entry with { Command = "new.exe" }]) ||
            AreStartupInventoriesEquivalent(original, [entry with { CanToggle = false }]))
            throw new InvalidOperationException("Startup refresh policy failed to detect inventory drift.");
        LogService.Info("startup",
            "STARTUP_HYDRATION_POLICY_SMOKE_OK unchanged=skip enabledChange=rerender commandChange=rerender canToggleChange=rerender");
    }

    internal async Task RunStartupRenderSmokeAsync()
    {
        RunStartupHydrationPolicySmoke();
        LogService.Info("performance", StartupManagerService.RunOrphanedRegistryCommandSmoke());
        // Check the UI integration separately: Core has no ViewModel reference.
        // The missing target is synthetic; this never touches the real Run key.
        string orphanPath = Path.Combine(Path.GetTempPath(),
            "SpankyMissingStartup" + Guid.NewGuid().ToString("N"), "Notion.exe");
        var orphan = new StartupRowViewModel(
            new StartupEntry("orphan-smoke", "Notion", $"\"{orphanPath}\" --processStart",
                RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run",
                true, "Neznámý vydavatel", orphanPath),
            insight: null, static (_, _) => { });
        if (!orphan.IsOrphaned || !orphan.Subtitle.Contains("CHYBÍ SOUBOR", StringComparison.Ordinal))
            throw new InvalidOperationException("Startup missing target was not shown as orphaned in its actual UI ViewModel.");
        LogService.Info("performance", StartupRowViewModel.RunOrphanPathReadOnlyBenchmark());
        LogService.Info("performance", SystemInsightService.RunStartupInsightPreparationBenchmark());
        LogService.Info("performance", SystemInsightService.RunStartupSignatureCacheSmoke());
        // Rebuild Startup inventory, but reuse Task Scheduler's own fresh/persistent cache.
        // Task Scheduler force-refresh/single-flight is covered by its dedicated smoke gate.
        var items = await StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: false);
        _ = await SystemInsightService.GetStartupInsightsAsync(items, CancellationToken.None);
        if (SystemInsightService.TryGetStartupInsightsForExactSnapshot(items) is not { Count: > 0 })
            throw new InvalidOperationException("Precomputed startup insights were not cached for their exact inventory.");
        var alteredInventory = items.ToArray();
        alteredInventory[0] = alteredInventory[0] with { Enabled = !alteredInventory[0].Enabled };
        if (SystemInsightService.TryGetStartupInsightsForExactSnapshot(alteredInventory) != null)
            throw new InvalidOperationException("Cached UI insights accepted a changed inventory instance.");
        InvalidateStartupPageCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("startup");
        sw.Stop();
        if (sw.ElapsedMilliseconds > 140)
            throw new InvalidOperationException($"Startup render too slow with warm data: {sw.ElapsedMilliseconds} ms.");
        int expectedSystem = items.Count(IsWindowsSystemStartupEntry);
        int expectedApps = items.Count - expectedSystem;
        // The lists are intentionally attached at Low dispatcher priority after first paint.
        // Allow enough time for a busy release-smoke run without turning scheduler jitter into a false failure.
        for (int i = 0; i < 120 && _startupVirtualList == null; i++)
            await Task.Delay(25);

        if (_startupVirtualList?.ItemsSource is not StartupRowViewModel[] rows || rows.Length != expectedApps)
            throw new InvalidOperationException(
                $"Startup app filter mismatch rows={(_startupVirtualList?.ItemsSource as StartupRowViewModel[])?.Length ?? -1} expectedApps={expectedApps}.");

        StartupRowViewModel[] systemRows = Array.Empty<StartupRowViewModel>();
        if (expectedSystem > 0)
        {
            for (int i = 0; i < 120 && _startupSystemExpander == null; i++)
                await Task.Delay(25);
            if (_startupSystemExpander == null || _startupSystemList != null)
                throw new InvalidOperationException("System startup list must remain lazy until expanded.");
            _startupSystemExpander.IsExpanded = true;
            for (int i = 0; i < 120 && _startupSystemList == null; i++)
                await Task.Delay(25);

            if (_startupSystemList?.ItemsSource is not StartupRowViewModel[] renderedSystemRows || renderedSystemRows.Length != expectedSystem)
                throw new InvalidOperationException(
                    $"Startup system filter mismatch rows={(_startupSystemList?.ItemsSource as StartupRowViewModel[])?.Length ?? -1} expectedSystem={expectedSystem}.");
            systemRows = renderedSystemRows;
        }

        if (_startupVirtualList.ItemTemplate == null ||
            (expectedSystem > 0 && _startupSystemList?.ItemTemplate == null))
            throw new InvalidOperationException("Startup render is missing its DataTemplate.");

        if (rows.Any(x => x.Impact.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) ||
            systemRows.Any(x => x.Impact.Equals("Unknown", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Warm Startup view did not apply same-inventory insights immediately.");
        if (rows.Length > 0)
        {
            for (int i = 0; i < 40 && rows.All(x => x.Impact.Equals("Unknown", StringComparison.OrdinalIgnoreCase)); i++)
                await Task.Delay(25);
            if (rows.All(x => x.Impact.Equals("Unknown", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Startup deferred insights did not enrich any rendered app row.");
        }

        LogService.Info("performance",
            $"STARTUP_RENDER_SMOKE_OK renderMs={sw.ElapsedMilliseconds} apps={expectedApps} system={expectedSystem} total={items.Count} filtered=true virtualized=true");
    }

    private async Task BuildStartupManagerAsync(CancellationToken token = default)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        var cachedItems = StartupManagerService.TryGetMemoryDisplaySnapshot();
        if (cachedItems == null)
        {
            var coldActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            coldActions.Children.Add(QuickButton("Obnovit", () => { StartupManagerService.InvalidateCache(invalidateTaskScheduler: true); InvalidateStartupPageCache(); return ShowSectionAsync("startup"); }));
            coldActions.Children.Add(QuickButton("Launchery", ShowLauncherStartupDialogAsync));
            ContentStack.Children.Add(BuildModernSectionHero(
                "startup",
                "Po spuštění",
                "Startup aplikace, launchery a Windows Startup Delay v jednom přehledu.",
                "STARTUP",
                "Načítám položky po spuštění…",
                coldActions));

            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
            loading.Children.Add(new TextBlock { Text = "Načítám běžné startup aplikace; Windows/System položky budou zvlášť dole…", VerticalAlignment = VerticalAlignment.Center, Foreground = TextSecondaryBrush() });
            ContentStack.Children.Add(BuildSectionSurface("startup", "Startup aplikace", "Běžné aplikace nahoře • Windows/System odfiltrováno", loading));

            int coldGeneration = _navigationGeneration;
            _startupHydrationTask ??= HydrateStartupManagerAfterFirstFrameAsync(token, coldGeneration);
            SafeTaskService.Forget(_startupHydrationTask, "startup", "Startup inventory hydration");
            LogService.Info("performance", $"STARTUP_UI_PHASE coldShell=1 totalMs={perf.ElapsedMilliseconds}");
            return;
        }

        int generation = _navigationGeneration;
        var items = cachedItems;
        long dataMs = perf.ElapsedMilliseconds;
        token.ThrowIfCancellationRequested();
        var immediateInsights = SystemInsightService.TryGetStartupInsightsForExactSnapshot(items);
        int immediateInsightRows = 0;

        // Main list shows user/third-party startup entries. Windows/System entries stay in
        // a separate collapsed expander lower on the page.
        long insightMs = 0;
        var sorted = items.ToArray();
        Array.Sort(sorted, static (a, b) =>
        {
            int enabled = b.Enabled.CompareTo(a.Enabled);
            return enabled != 0 ? enabled : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        long sortMs = perf.ElapsedMilliseconds - dataMs - insightMs;

        string windowsRoot = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        var appItems = new List<StartupEntry>(sorted.Length);
        var systemItems = new List<StartupEntry>();
        int enabledCount = 0;
        foreach (var item in sorted)
        {
            if (IsWindowsSystemStartupEntry(item, windowsRoot))
            {
                systemItems.Add(item);
            }
            else
            {
                appItems.Add(item);
                if (item.Enabled) enabledCount++;
            }
        }
        long splitMs = perf.ElapsedMilliseconds - dataMs - insightMs - sortMs;
        long listSetupMs = sortMs + splitMs;

        StartupRowViewModel[] BuildRows(IReadOnlyList<StartupEntry> entries, bool allowDelete)
        {
            var result = new StartupRowViewModel[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                StartupEntry item = entries[i];
                StartupInsight? cachedInsight = null;
                if (immediateInsights != null &&
                    immediateInsights.TryGetValue(item.Id, out var known))
                {
                    cachedInsight = known;
                    immediateInsightRows++;
                }
                result[i] = new StartupRowViewModel(
                    item,
                    insight: cachedInsight,
                    (vm, enabled) => SafeTaskService.Forget(
                        ToggleStartupRowAsync(vm, enabled),
                        "startup",
                        "Toggle startup item"),
                    allowDelete ? DeleteStartupRowAsync : null);
            }
            return result;
        }

        var rows = BuildRows(appItems, allowDelete: true);
        // Most visits need only the regular apps. Building view-models for
        // protected Windows/system entries waits until the user expands them.
        _startupVirtualList = null;
        _startupSystemList = null;
        _startupSystemExpander = null;
        _startupSystemRowsLoad = null;

        long rowsMs = perf.ElapsedMilliseconds - dataMs - insightMs - listSetupMs;
        LogService.Info("performance",
            $"STARTUP_INSIGHTS_IMMEDIATE rows={immediateInsightRows}/{rows.Length} " +
            $"sameSnapshot={immediateInsights != null} extraUiBatchesStillVerify=true");
        int disabledCount = appItems.Count - enabledCount;
        int orphanedCount = rows.Count(x => x.IsOrphaned);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Obnovit", () => { StartupManagerService.InvalidateCache(invalidateTaskScheduler: true); InvalidateStartupPageCache(); return ShowSectionAsync("startup"); }));
        actions.Children.Add(QuickButton("Launchery", ShowLauncherStartupDialogAsync));

        var delta = StartupManagerService.LastDelta;
        string deltaText = delta.HasChanges ? $" • změny {delta.Summary}" : " • bez nových změn";
        // This is a single HKCU registry read. Running it through Task.Run adds a cold
        // scheduler hop to first Startup navigation that costs more than the read itself.
        bool startupDelayDisabled = IsStartupDelayDisabled();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "startup") return;

        var startupHeroHost = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(startupHeroHost);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "startup" ||
                !ContentStack.Children.Contains(startupHeroHost) ||
                startupHeroHost.Children.Count > 0) return;

            var deferredHeroPerf = System.Diagnostics.Stopwatch.StartNew();
            startupHeroHost.Children.Add(BuildModernSectionHero(
                "startup",
                "Po spuštění",
                "Běžné aplikace jsou nahoře; Windows a systémové položky mají vlastní oddělenou sekci.",
                "STARTUP MANAGER",
                $"{appItems.Count} aplikací • {systemItems.Count} systémových • {enabledCount} zapnuto • {orphanedCount} chybějících souborů{deltaText}",
                actions));
            startupHeroHost.Children.Add(BuildDashboardMetricGrid(
                BuildDashboardMetric("Aplikace", appItems.Count.ToString(), orphanedCount == 0 ? "Běžné startup položky" : $"{orphanedCount} položek odkazuje na chybějící soubor"),
                BuildDashboardMetric("Zapnuto", enabledCount.ToString(), "Běžné aplikace při startu", "SpankySuccessBrush"),
                BuildDashboardMetric("Systém", systemItems.Count.ToString(), "Oddělená systémová sekce"),
                BuildDashboardMetric("Prodleva", startupDelayDisabled ? "Vypnuto" : "Výchozí", startupDelayDisabled ? "Bez prodlevy" : "Výchozí Windows")));
            LogService.Info("performance", $"STARTUP_HERO_DEFERRED totalMs={deferredHeroPerf.ElapsedMilliseconds}");
        });

        long heroMs = perf.ElapsedMilliseconds - dataMs - insightMs - listSetupMs - rowsMs;
        var delayActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        delayActions.Children.Add(PrimaryQuickButton("Vypnout prodlevu po spuštění", async () => { await SetStartupDelayAsync(true); InvalidateStartupPageCache(); await ShowSectionAsync("startup"); }));
        delayActions.Children.Add(QuickButton("Výchozí Windows", async () => { await SetStartupDelayAsync(false); InvalidateStartupPageCache(); await ShowSectionAsync("startup"); }));
        long delayReadMs = perf.ElapsedMilliseconds - dataMs - insightMs - listSetupMs - rowsMs - heroMs;
        token.ThrowIfCancellationRequested();

        ContentStack.Children.Add(BuildFlatSectionHeader(
            "Ostatní aplikace",
            "Běžné aplikace, launchery a další nesystémové položky po spuštění"));
        var appHost = new StackPanel();
        appHost.Children.Add(new TextBlock
        {
            Text = "Načítám aplikace po spuštění…",
            Foreground = TextSecondaryBrush()
        });
        ContentStack.Children.Add(BuildSectionSurface(
            "startup",
            "Aplikace po spuštění",
            $"{appItems.Count} běžných položek • systémové věci jsou oddělené níže",
            appHost));

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "startup" ||
                appHost.Parent == null) return;

            var deferredAppList = new ListView
            {
                MaxHeight = 650,
                SelectionMode = ListViewSelectionMode.None,
                ItemsPanel = UiResourceService.Get<ItemsPanelTemplate>("SpankyVirtualizingItemsPanel"),
                ItemTemplate = UiResourceService.Get<DataTemplate>("StartupRowTemplateV209"),
                ItemsSource = rows
            };
            _startupVirtualList = deferredAppList;
            appHost.Children.Clear();
            appHost.Children.Add(deferredAppList);
            LogService.Info("performance",
                $"STARTUP_APP_LIST_DEFERRED rows={rows.Length}");
        });

        var backgroundSlot = new StackPanel();
        ContentStack.Children.Add(backgroundSlot);
        ScheduleBackgroundActivityPanel(appItems, token, backgroundSlot);

        if (systemItems.Count > 0)
        {
            var systemHost = new StackPanel();
            systemHost.Children.Add(new TextBlock
            {
                Text = "Rozbal pro zobrazení systémových položek. Běžné aplikace zůstávají oddělené.",
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            var systemCard = BuildDashboardExpander(
                "Windows / systém",
                $"{systemItems.Count} chráněných a systémových položek • pouze na vyžádání",
                systemHost);
            ContentStack.Children.Add(systemCard);
            if (systemCard.Child is Expander systemExpander)
            {
                _startupSystemExpander = systemExpander;
                bool populated = false;
                void PopulateSystemRows()
                {
                    if (populated) return;
                    CancellationToken currentToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                    int currentGeneration = _navigationGeneration;
                    if (currentToken.IsCancellationRequested || _section != "startup") return;
                    populated = true;
                    if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (currentToken.IsCancellationRequested ||
                            currentGeneration != _navigationGeneration || _section != "startup")
                        {
                            populated = false;
                            return;
                        }
                        var systemRows = BuildRows(systemItems, allowDelete: false);
                        var list = new ListView
                        {
                            MaxHeight = 460,
                            SelectionMode = ListViewSelectionMode.None,
                            ItemsPanel = UiResourceService.Get<ItemsPanelTemplate>("SpankyVirtualizingItemsPanel"),
                            ItemTemplate = UiResourceService.Get<DataTemplate>("StartupRowTemplateV209"),
                            ItemsSource = systemRows
                        };
                        _startupSystemList = list;
                        systemHost.Children.Clear();
                        systemHost.Children.Add(list);
                        LogService.Info("performance",
                            $"STARTUP_SYSTEM_LIST_ON_EXPAND rows={systemRows.Length}");
                    }))
                        populated = false;
                }
                _startupSystemRowsLoad = PopulateSystemRows;
                systemExpander.Expanding += (_, _) => PopulateSystemRows();
            }
        }
        else
        {
            ContentStack.Children.Add(BuildSectionSurface(
                "startup",
                "Windows / systém",
                "Nebyly nalezeny žádné systémové položky",
                new TextBlock { Text = "Žádné systémové položky po spuštění.", Foreground = TextSecondaryBrush() }));
        }

        ContentStack.Children.Add(BuildFlatSectionHeader(
            "Správa startupu",
            "Systémové nastavení a Startup Delay"));

        ContentStack.Children.Add(BuildSectionSurface(
            "startup",
            "Startup Delay",
            startupDelayDisabled ? "Prodleva vypnutá" : "Windows default",
            delayActions));

        long attachMs = perf.ElapsedMilliseconds - dataMs - insightMs - listSetupMs - rowsMs - heroMs - delayReadMs;

        // Always fingerprint the COMPLETE inventory, including the hidden
        // system rows. Hydration already verified this same snapshot; asking
        // the shared insight cache for just the 26 visible app rows generated
        // a second unrelated fingerprint and recomputed every signer.
        // Presentation below still applies insights only to visible app rows.
        SafeTaskService.Forget(
            EnrichStartupInsightsAsync(rows, items, immediateInsights, generation, token),
            "startup",
            "Deferred startup insights");

        if (StartupManagerService.NeedsBackgroundRefresh && _startupHydrationTask == null)
        {
            _startupHydrationTask = HydrateStartupManagerAsync(token, forceRefresh: true);
            SafeTaskService.Forget(_startupHydrationTask, "startup", "Startup persistent snapshot refresh");
        }

        LogService.Info("performance", $"STARTUP_UI_PHASE apps={appItems.Count} system={systemItems.Count} total={sorted.Length} dataMs={dataMs} insightMs={insightMs} sortMs={sortMs} splitMs={splitMs} listSetupMs={listSetupMs} rowsMs={rowsMs} heroMs={heroMs} delayReadMs={delayReadMs} attachMs={attachMs} totalMs={perf.ElapsedMilliseconds}");
    }

    private async Task EnrichStartupInsightsAsync(
        StartupRowViewModel[] rows,
        IReadOnlyList<StartupEntry> items,
        IReadOnlyDictionary<string, StartupInsight>? initiallyRenderedInsights,
        int generation,
        CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var insights = await Task.Run(
                () => SystemInsightService.GetStartupInsightsAsync(items, token),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // The rows were initialized from these exact verified insights.
            // Reapplying an identical dictionary would schedule three more
            // dispatcher turns for 26 unchanged startup buttons. The verifier
            // above still runs so changes to executable stamps are discovered.
            if (initiallyRenderedInsights != null &&
                ReferenceEquals(initiallyRenderedInsights, insights))
            {
                LogService.Info("performance",
                    $"STARTUP_INSIGHTS_DEFERRED reused={rows.Length}/{rows.Length} ms={sw.ElapsedMilliseconds} batches=0 verified=true");
                return;
            }

            int applied = 0;
            void QueueInsightBatch(int start)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (token.IsCancellationRequested ||
                        generation != _navigationGeneration ||
                        _section != "startup" ||
                        _startupVirtualList?.ItemsSource is not StartupRowViewModel[] currentRows ||
                        !ReferenceEquals(currentRows, rows))
                        return;

                    var batch = System.Diagnostics.Stopwatch.StartNew();
                    int end = Math.Min(start + 12, rows.Length);
                    for (int i = start; i < end; i++)
                    {
                        var row = rows[i];
                        insights.TryGetValue(row.Source.Id, out var insight);
                        row.ApplyInsight(insight);
                        if (insight != null) applied++;
                    }

                    LogService.Info("performance",
                        $"STARTUP_INSIGHTS_BATCH start={start} added={end - start} ms={batch.ElapsedMilliseconds} applied={applied}");

                    if (end < rows.Length)
                    {
                        QueueInsightBatch(end);
                        return;
                    }

                    LogService.Info("performance",
                        $"STARTUP_INSIGHTS_DEFERRED applied={applied}/{rows.Length} ms={sw.ElapsedMilliseconds} batched=true");
                });
            }

            for (int i = 0; i < 40 && _startupVirtualList == null &&
                 !token.IsCancellationRequested &&
                 generation == _navigationGeneration &&
                 _section == "startup"; i++)
                await Task.Delay(25, token).ConfigureAwait(false);

            QueueInsightBatch(0);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("startup", "Deferred startup insights failed", ex); }
    }

    private async Task ToggleStartupRowAsync(StartupRowViewModel vm, bool enabled)
    {
        StartupEntry before = vm.Source;
        if (before.Enabled == enabled) return;

        vm.BeginBusy();
        try
        {
            await StartupManagerService.SetEnabledAsync(before, enabled);
            StartupEntry updated;
            if (before.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(before.SourceId))
            {
                string fileName = Path.GetFileName(before.SourceId);
                string newPath = enabled
                    ? Path.Combine(before.RunPath, fileName)
                    : Path.Combine(before.RunPath, "SpankyhoDisabled", fileName);
                updated = before with
                {
                    Enabled = enabled,
                    SourceId = newPath,
                    FilePath = newPath,
                    Command = newPath
                };
            }
            else
            {
                updated = before with { Enabled = enabled };
            }

            vm.Confirm(updated);
            InvalidateStartupPageCache();
            ShowActionFeedback("Po spuštění", ActionFeedbackKind.Verified, $"{updated.Name}: {(enabled ? "zapnuto" : "vypnuto")}");
        }
        catch (Exception ex)
        {
            LogService.Error("startup", $"Startup toggle failed: {before.Name}", ex);
            vm.Revert(before.Enabled);
            ShowActionFeedback("Po spuštění", ActionFeedbackKind.Failed, ex.Message);
        }
    }

    private async Task DeleteStartupRowAsync(StartupRowViewModel vm)
    {
        StartupEntry item = vm.Source;
        if (!await ShowStyledConfirmAsync(
                "Odstranit ze spuštění?",
                $"{item.Name}\n\nAplikace zůstane nainstalovaná, ale už se nebude spouštět při přihlášení. Tuto akci nelze vrátit tlačítkem Zpět.",
                "Odstranit", "Zrušit", "Po spuštění"))
            return;

        vm.BeginBusy();
        try
        {
            LogService.Info("startup", $"STARTUP_DELETE_REQUEST name={item.Name} kind={item.SourceKind}");
            await StartupManagerService.DeleteAsync(item, _windowLifetimeCts.Token);
            StartupManagerService.InvalidateCache(invalidateTaskScheduler: true);
            InvalidateStartupPageCache();
            LogService.Info("startup", $"STARTUP_DELETE_OK name={item.Name} kind={item.SourceKind}");
            ShowActionFeedback("Po spuštění", ActionFeedbackKind.Verified,
                $"{item.Name}: odstraněno ze spuštění");
            await ShowSectionAsync("startup");
        }
        catch (Exception ex)
        {
            LogService.Error("startup", $"Startup delete failed: {item.Name}", ex);
            vm.Revert(item.Enabled);
            ShowActionFeedback("Po spuštění", ActionFeedbackKind.Failed, ex.Message);
        }
    }

    private async Task HydrateStartupManagerAfterFirstFrameAsync(CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "startup")
                return;
            await HydrateStartupManagerAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation != _navigationGeneration || _section != "startup")
                _startupHydrationTask = null;
        }
    }

    private async Task HydrateStartupManagerAsync(CancellationToken token, bool forceRefresh = false)
    {
        int generation = _navigationGeneration;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!forceRefresh)
            {
                bool displayHydrated = await StartupManagerService.TryWarmDisplaySnapshotAsync(token).ConfigureAwait(false);
                if (displayHydrated && !token.IsCancellationRequested &&
                    generation == _navigationGeneration && _section == "startup")
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested ||
                            generation != _navigationGeneration ||
                            _section != "startup")
                            return;
                        InvalidateStartupPageCache();
                        SafeTaskService.Forget(ShowSectionAsync("startup"), "startup", "Startup display-cache rerender");
                    });
                }
                if (displayHydrated)
                {
                    LogService.Info("performance",
                        "STARTUP_DISPLAY_HYDRATION queued=true deferredRefreshOwnedByPage=true");
                    return;
                }
            }

            IReadOnlyList<StartupEntry>? previousSnapshot =
                StartupManagerService.TryGetMemoryDisplaySnapshot();
            var items = await StartupManagerService.GetAsync(
                forceRefresh || StartupManagerService.NeedsBackgroundRefresh).ConfigureAwait(false);
            long inventoryMs = sw.ElapsedMilliseconds;

            // Prewarm impact/signature analysis before asking WinUI to render the rows.
            // This keeps the second navigation pass focused on UI materialization only.
            await SystemInsightService.GetStartupInsightsAsync(items, token).ConfigureAwait(false);
            long insightMs = sw.ElapsedMilliseconds - inventoryMs;

            LogService.Info("performance",
                $"STARTUP_HYDRATION_PHASE items={items.Count} inventoryMs={inventoryMs} insightMs={insightMs} totalMs={sw.ElapsedMilliseconds}");

            if (AreStartupInventoriesEquivalent(previousSnapshot, items))
            {
                LogService.Info("performance",
                    $"STARTUP_REDUNDANT_RERENDER_SKIPPED count={items.Count} refreshMs={inventoryMs} sameRecords=true");
                return;
            }

            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "startup") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "startup")
                    return;
                InvalidateStartupPageCache();
                SafeTaskService.Forget(ShowSectionAsync("startup"), "startup", "Startup hydrated rerender");
            });
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                LogService.Error("Startup inventory hydration failed", ex);
        }
        finally
        {
            _startupHydrationTask = null;
        }
    }

    private static bool IsStartupDelayDisabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize");
        return k?.GetValue("StartupDelayInMSec") is int v && v == 0;
    }

    private static async Task SetStartupDelayAsync(bool disable)
    {
        await StartupDelayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                const string path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
                const string name = "StartupDelayInMSec";

                using RegistryKey? beforeKey = Registry.CurrentUser.OpenSubKey(path, writable: false);
                bool existed = beforeKey?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
                object? originalValue = existed
                    ? beforeKey!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    : null;
                RegistryValueKind? originalKind = existed ? beforeKey!.GetValueKind(name) : null;

                try
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, true)
                        ?? throw new InvalidOperationException("Nelze otevřít Startup registry.");
                    if (disable)
                        key.SetValue(name, 0, RegistryValueKind.DWord);
                    else
                        key.DeleteValue(name, false);

                    bool actual = IsStartupDelayDisabled();
                    if (actual != disable)
                        throw new InvalidOperationException("Startup Delay read-back nepotvrdil změnu.");
                }
                catch
                {
                    using RegistryKey rollback = Registry.CurrentUser.CreateSubKey(path, true)
                        ?? throw new InvalidOperationException("Startup Delay rollback nemůže otevřít registry.");
                    if (existed)
                        rollback.SetValue(name, originalValue!, originalKind!.Value);
                    else
                        rollback.DeleteValue(name, false);

                    bool rollbackExists = rollback.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
                    if (rollbackExists != existed)
                        throw new InvalidOperationException("Startup Delay rollback read-back nepotvrdil původní existenci hodnoty.");
                    if (existed)
                    {
                        object? actualValue = rollback.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        RegistryValueKind actualKind = rollback.GetValueKind(name);
                        if (actualKind != originalKind || !RegistryValueEquals(actualValue, originalValue))
                            throw new InvalidOperationException("Startup Delay rollback read-back nepotvrdil původní hodnotu.");
                    }
                    throw;
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            StartupDelayGate.Release();
        }
    }

    private static bool RegistryValueEquals(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is byte[] lb && right is byte[] rb) return lb.AsSpan().SequenceEqual(rb);
        if (left is string[] ls && right is string[] rs) return ls.SequenceEqual(rs, StringComparer.Ordinal);
        return Equals(left, right);
    }
}
