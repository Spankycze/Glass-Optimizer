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

    internal async Task RunStartupRenderSmokeAsync()
    {
        // Rebuild Startup inventory, but reuse Task Scheduler's own fresh/persistent cache.
        // Task Scheduler force-refresh/single-flight is covered by its dedicated smoke gate.
        var items = await StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: false);
        _ = await SystemInsightService.GetStartupInsightsAsync(items, CancellationToken.None);
        InvalidateStartupPageCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("startup");
        sw.Stop();
        if (sw.ElapsedMilliseconds > 140)
            throw new InvalidOperationException($"Startup render too slow with warm data: {sw.ElapsedMilliseconds} ms.");
        int expectedSystem = items.Count(IsWindowsSystemStartupEntry);
        int expectedApps = items.Count - expectedSystem;
        for (int i = 0; i < 40 && _startupVirtualList == null; i++)
            await Task.Delay(25);

        if (_startupVirtualList?.ItemsSource is not StartupRowViewModel[] rows || rows.Length != expectedApps)
            throw new InvalidOperationException(
                $"Startup app filter mismatch rows={(_startupVirtualList?.ItemsSource as StartupRowViewModel[])?.Length ?? -1} expectedApps={expectedApps}.");
        if (expectedSystem > 0)
        {
            for (int i = 0; i < 40 && _startupSystemList == null; i++)
                await Task.Delay(25);
        }

        if (_startupSystemList?.ItemsSource is not StartupRowViewModel[] systemRows || systemRows.Length != expectedSystem)
            throw new InvalidOperationException(
                $"Startup system filter mismatch rows={(_startupSystemList?.ItemsSource as StartupRowViewModel[])?.Length ?? -1} expectedSystem={expectedSystem}.");
        if (_startupVirtualList.ItemTemplate == null || _startupSystemList.ItemTemplate == null)
            throw new InvalidOperationException("Startup render is missing its DataTemplate.");

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

            _startupHydrationTask ??= HydrateStartupManagerAsync(token);
            SafeTaskService.Forget(_startupHydrationTask, "startup", "Startup inventory hydration");
            LogService.Info("performance", $"STARTUP_UI_PHASE coldShell=1 totalMs={perf.ElapsedMilliseconds}");
            return;
        }

        var items = cachedItems;
        long dataMs = perf.ElapsedMilliseconds;
        token.ThrowIfCancellationRequested();

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
        foreach (var item in sorted)
        {
            if (IsWindowsSystemStartupEntry(item, windowsRoot)) systemItems.Add(item);
            else appItems.Add(item);
        }
        var source = appItems.ToArray();
        var systemSource = systemItems.ToArray();
        long splitMs = perf.ElapsedMilliseconds - dataMs - insightMs - sortMs;
        long listSetupMs = sortMs + splitMs;

        StartupRowViewModel[] BuildRows(StartupEntry[] entries)
        {
            var result = new StartupRowViewModel[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                StartupEntry item = entries[i];
                result[i] = new StartupRowViewModel(
                    item,
                    insight: null,
                    (vm, enabled) => SafeTaskService.Forget(
                        ToggleStartupRowAsync(vm, enabled),
                        "startup",
                        "Toggle startup item"));
            }
            return result;
        }

        var rows = BuildRows(source);
        var systemRows = BuildRows(systemSource);
        _startupVirtualList = null;
        _startupSystemList = null;

        long rowsMs = perf.ElapsedMilliseconds - dataMs - insightMs - listSetupMs;
        int enabledCount = source.Count(x => x.Enabled);
        int disabledCount = source.Length - enabledCount;
        int orphanedCount = rows.Count(x => x.IsOrphaned);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Obnovit", () => { StartupManagerService.InvalidateCache(invalidateTaskScheduler: true); InvalidateStartupPageCache(); return ShowSectionAsync("startup"); }));
        actions.Children.Add(QuickButton("Launchery", ShowLauncherStartupDialogAsync));

        var delta = StartupManagerService.LastDelta;
        string deltaText = delta.HasChanges ? $" • změny {delta.Summary}" : " • bez nových změn";
        bool startupDelayDisabled = IsStartupDelayDisabled();

        var startupHeroHost = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(startupHeroHost);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "startup" || startupHeroHost.Children.Count > 0) return;

            var deferredHeroPerf = System.Diagnostics.Stopwatch.StartNew();
            startupHeroHost.Children.Add(BuildModernSectionHero(
                "startup",
                "Po spuštění",
                "Běžné aplikace jsou nahoře; Windows a systémové položky mají vlastní oddělenou sekci.",
                "STARTUP MANAGER",
                $"{source.Length} aplikací • {systemSource.Length} systémových • {enabledCount} zapnuto • {orphanedCount} chybějících souborů{deltaText}",
                actions));
            startupHeroHost.Children.Add(BuildDashboardMetricGrid(
                BuildDashboardMetric("Aplikace", source.Length.ToString(), orphanedCount == 0 ? "Běžné startup položky" : $"{orphanedCount} položek odkazuje na chybějící soubor"),
                BuildDashboardMetric("Zapnuto", enabledCount.ToString(), "Běžné aplikace při startu", "SpankySuccessBrush"),
                BuildDashboardMetric("Systém", systemSource.Length.ToString(), "Oddělená systémová sekce"),
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
            $"{source.Length} běžných položek • systémové věci jsou oddělené níže",
            appHost));

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "startup") return;

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
        ScheduleBackgroundActivityPanel(source, token, backgroundSlot);

        ContentStack.Children.Add(BuildFlatSectionHeader(
            "Systémové položky",
            "Windows komponenty a systémové úlohy jsou oddělené od běžných aplikací"));
        if (systemSource.Length > 0)
        {
            var systemHost = new StackPanel();
            systemHost.Children.Add(new TextBlock
            {
                Text = "Načítám systémové položky…",
                Foreground = TextSecondaryBrush()
            });
            ContentStack.Children.Add(BuildSectionSurface(
                "startup",
                "Windows / systém",
                $"{systemSource.Length} systémových položek",
                systemHost));

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "startup") return;

                var deferredSystemList = new ListView
                {
                    MaxHeight = 460,
                    SelectionMode = ListViewSelectionMode.None,
                    ItemsPanel = UiResourceService.Get<ItemsPanelTemplate>("SpankyVirtualizingItemsPanel"),
                    ItemTemplate = UiResourceService.Get<DataTemplate>("StartupRowTemplateV209"),
                    ItemsSource = systemRows
                };
                _startupSystemList = deferredSystemList;
                systemHost.Children.Clear();
                systemHost.Children.Add(deferredSystemList);
                LogService.Info("performance",
                    $"STARTUP_SYSTEM_LIST_DEFERRED rows={systemRows.Length}");
            });
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

        SafeTaskService.Forget(
            EnrichStartupInsightsAsync(rows, source, token),
            "startup",
            "Deferred startup insights");

        if (StartupManagerService.NeedsBackgroundRefresh && _startupHydrationTask == null)
        {
            _startupHydrationTask = HydrateStartupManagerAsync(token, forceRefresh: true);
            SafeTaskService.Forget(_startupHydrationTask, "startup", "Startup persistent snapshot refresh");
        }

        LogService.Info("performance", $"STARTUP_UI_PHASE apps={source.Length} system={systemSource.Length} total={sorted.Length} dataMs={dataMs} insightMs={insightMs} sortMs={sortMs} splitMs={splitMs} listSetupMs={listSetupMs} rowsMs={rowsMs} heroMs={heroMs} delayReadMs={delayReadMs} attachMs={attachMs} totalMs={perf.ElapsedMilliseconds}");
    }

    private async Task EnrichStartupInsightsAsync(
        StartupRowViewModel[] rows,
        StartupEntry[] items,
        CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var insights = await Task.Run(
                () => SystemInsightService.GetStartupInsightsAsync(items, token),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            int applied = 0;
            void QueueInsightBatch(int start)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (token.IsCancellationRequested || _section != "startup" ||
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
                 !token.IsCancellationRequested && _section == "startup"; i++)
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

    private async Task HydrateStartupManagerAsync(CancellationToken token, bool forceRefresh = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!forceRefresh)
            {
                bool displayHydrated = await StartupManagerService.TryWarmDisplaySnapshotAsync(token).ConfigureAwait(false);
                if (displayHydrated && !token.IsCancellationRequested && _section == "startup")
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested || _section != "startup") return;
                        InvalidateStartupPageCache();
                        SafeTaskService.Forget(ShowSectionAsync("startup"), "startup", "Startup display-cache rerender");
                    });
                }
            }

            var items = await StartupManagerService.GetAsync(
                forceRefresh || StartupManagerService.NeedsBackgroundRefresh).ConfigureAwait(false);
            long inventoryMs = sw.ElapsedMilliseconds;

            // Prewarm impact/signature analysis before asking WinUI to render the rows.
            // This keeps the second navigation pass focused on UI materialization only.
            await SystemInsightService.GetStartupInsightsAsync(items, token).ConfigureAwait(false);
            long insightMs = sw.ElapsedMilliseconds - inventoryMs;

            LogService.Info("performance",
                $"STARTUP_HYDRATION_PHASE items={items.Count} inventoryMs={inventoryMs} insightMs={insightMs} totalMs={sw.ElapsedMilliseconds}");

            if (token.IsCancellationRequested || _section != "startup") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _section != "startup") return;
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

    private static Task SetStartupDelayAsync(bool disable) => Task.Run(() =>
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
        using var k = Registry.CurrentUser.CreateSubKey(path, true) ?? throw new InvalidOperationException("Nelze otevřít Startup registry.");
        if (disable) k.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord); else k.DeleteValue("StartupDelayInMSec", false);
        bool actual = IsStartupDelayDisabled();
        if (actual != disable) throw new InvalidOperationException("Startup Delay read-back nepotvrdil změnu.");
    });
}
