using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static readonly (string Section, string? Sub)[] ExtensionCatalogs =
    {
        ("system", null), ("gpu", null), ("memory", null), ("storage", null),
        ("network", null), ("ui", null), ("update", null), ("privacy", null),
        ("ai", null), ("defender", null),
        ("components", "Služby"), ("components", "Ovladače"), ("components", "Task Scheduler")
    };
    private Task BuildExtensionsPageAsync(CancellationToken token)
    {
        int generation = _navigationGeneration;
        token.ThrowIfCancellationRequested();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var shellHost = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(shellHost);
        LogService.Info("performance", $"EXTENSIONS_UI_PHASE shellMs={sw.ElapsedMilliseconds} deferred=true");

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "extensions" || !ContentStack.Children.Contains(shellHost) || shellHost.Children.Count > 0)
                return;

            var deferred = System.Diagnostics.Stopwatch.StartNew();
            var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heroActions.Children.Add(QuickButton("Plugin složka", () => { System.IO.Directory.CreateDirectory(SpankyExtensionService.PluginRoot); ProcessRunner.Open(SpankyExtensionService.PluginRoot); return Task.CompletedTask; }));
            heroActions.Children.Add(QuickButton("Zkontrolovat zdroje", () => ShowUpstreamSourcesAsync(token, force: true)));
            shellHost.Children.Add(BuildSectionHero("extensions", "PORTABLE SETUP", "Rozšíření / Nástroje", "Winget • Pluginy • Volitelné funkce • Zdrojové aktualizace", heroActions));

            var body = new StackPanel { Spacing = 10 };
            shellHost.Children.Add(body);
            LogService.Info("performance", $"EXTENSIONS_SHELL_DEFERRED ms={deferred.ElapsedMilliseconds}");
            SafeTaskService.Forget(LoadExtensionsBodyAsync(body, token, generation), "extensions", "Deferred extensions body");
        });
        return Task.CompletedTask;
    }

    private async Task LoadExtensionsBodyAsync(StackPanel body, CancellationToken token, int generation)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var plugins = SpankyExtensionService.LoadPlugins();
                var upstream = UpstreamSourceUpdateService.ReadCachedStatuses();
                return (plugins, upstream);
            }, token);
            long dataMs = sw.ElapsedMilliseconds;
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "extensions" || body.Parent == null) return;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "extensions" || body.Parent == null) return;
                var render = System.Diagnostics.Stopwatch.StartNew();
                body.Children.Clear();
                body.Children.Add(ActionCard("Aktualizace programů", "Grafická správa aktualizací přes Winget s výběrem jednotlivých aplikací, verzemi a hromadnou aktualizací.", "Otevřít", () => ShowSoftwareManagerAsync(token)));
                body.Children.Add(ActionCard("Volitelné funkce Windows", "Správa funkcí Windows používaných Optimizerem bez zbytečného kompletního DISM skenu.", "Otevřít", () => ShowOptionalFeaturesAsync(token)));
                string upstreamSummary = string.Join(" • ", data.upstream.Select(x => $"{x.Name}: {(x.UpdateDownloaded ? "archiv " + x.Commit[..Math.Min(8, x.Commit.Length)] : "kopie neověřena")} ({x.IntegrationMode})"));
                body.Children.Add(ActionCard("Aktualizace zdrojů", upstreamSummary, "Zkontrolovat", () => ShowUpstreamSourcesAsync(token, force: true)));
                body.Children.Add(ActionCard("Spanky pluginy", $"Bezpečné JSON manifesty mohou skládat existující vestavěné tweaky. Nalezeno: {data.plugins.Count}.", "Spravovat", () => ShowPluginManagerAsync(token)));
                body.Children.Add(ActionCard("Lokální skripty", "Pokročilá kompatibilita pro lokální .ps1, .bat, .cmd a .reg soubory. Spuštění je vždy ručně potvrzené.", "Otevřít", () => ShowGTweakLocalScriptsAsync(token)));
                body.Children.Add(ActionCard("Pokročilé nástroje", "Katalog užitečných systémových nástrojů a nástrojů pro ovladače; stahuje je do samostatné složky a nemění vzhled aplikace.", "Otevřít", () => ShowGTweakToolsetAsync(token)));
                LogService.Info("performance", $"EXTENSIONS_DEFERRED_PHASE dataMs={dataMs} renderMs={render.ElapsedMilliseconds} plugins={data.plugins.Count}");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("extensions", "Deferred extensions body failed", ex); }
    }
    private List<TweakDefinition> GetAllExtensionTweaks()
    {
        var map = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in ExtensionCatalogs)
            foreach (var tweak in CatalogService.GetTweaks(x.Section, x.Sub)) map.TryAdd(tweak.Id, tweak);
        return map.Values.ToList();
    }

    private async Task ShowOptionalFeaturesAsync(CancellationToken token)
    {
        try
        {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sys.hyperv", "sys.vmp", "sys.whp", "sec.smb1" };
        var tweaks = GetAllExtensionTweaks().Where(x => ids.Contains(x.Id)).ToArray();
        await _engine.DetectAsync(tweaks, token);
        var root = new StackPanel { Spacing = 9 };
        var toggles = new List<(ToggleSwitch Toggle, TweakDefinition Tweak)>();
        foreach (var tweak in tweaks)
        {
            var toggle = new ToggleSwitch { Header = tweak.Name, IsOn = tweak.Current, OnContent = "Vypnuto", OffContent = "Zapnuto / default" };
            NormalizeToggleAppearance(toggle);
            toggles.Add((toggle, tweak)); root.Children.Add(toggle);
        }
        if (!await ShowStyledContentAsync("Optional Features", "Přepínač ON znamená, že daná Windows Feature má být vypnutá. Změny mohou vyžadovat restart.", root, "Použít")) return;
        var dirty = new List<TweakDefinition>();
        foreach (var item in toggles) { item.Tweak.Pending = item.Toggle.IsOn; if (item.Tweak.IsDirty) dirty.Add(item.Tweak); }
        if (dirty.Count == 0) return;
        await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before optional feature changes");
        var progressResult = await RunCancellableWithProgressAsync(
            "Optional Features",
            $"Aplikuji {dirty.Count} změn…",
            async progressToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                return await _engine.ApplyAsync(dirty, linked.Token);
            },
            result => result.Count == 0,
            result => result.Count == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("extensions", "Optional Features apply cancelled by user/navigation.");
            bool verified = await ReconcileCancelledApplyAsync(dirty, "extensions:optional-features");
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            if (!verified && !token.IsCancellationRequested)
                ShowActionFeedback("Optional Features", ActionFeedbackKind.Failed,
                    "Operace byla přerušena; skutečný stav Windows Features se nepodařilo ověřit.");
            return;
        }
        var errors = progressResult.Result;
        if (errors.Count > 0) await ShowMessageAsync("Optional Features", string.Join(Environment.NewLine, errors.Take(10)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Info("extensions", "Optional Features cancelled by navigation.");
        }
    }

    private async Task ShowPluginManagerAsync(CancellationToken token)
    {
        try
        {
        var plugins = SpankyExtensionService.LoadPlugins();
        if (plugins.Count == 0)
        {
            System.IO.Directory.CreateDirectory(SpankyExtensionService.PluginRoot);
            await ShowStyledInfoAsync("Spanky Plugins", "Ve složce Plugins zatím není žádný JSON manifest. Systém spouští pouze vestavěná tweak ID, ne cizí skripty.");
            ProcessRunner.Open(SpankyExtensionService.PluginRoot); return;
        }
        var combo = new ComboBox { MinWidth = 420, ItemsSource = plugins.Select(x => x.Name).ToArray(), SelectedIndex = 0 };
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush() };
        void RefreshInfo()
        {
            int i = Math.Max(0, combo.SelectedIndex);
            var p = plugins[i];
            info.Text = $"{p.Description}\nAutor: {p.Author} • verze {p.Version} • build {p.MinBuild}-{p.MaxBuild}\nTweaky: {p.Tweaks.Length}";
        }
        combo.SelectionChanged += (_, _) => RefreshInfo(); RefreshInfo();
        var root = new StackPanel { Spacing = 10, Children = { combo, info } };
        if (!await ShowStyledContentAsync("Spanky Plugins", "Plugin je bezpečný manifest, který skládá pouze existující tweak ID Optimizeru.", root, "Použít plugin")) return;
        var plugin = plugins[Math.Max(0, combo.SelectedIndex)];
        int build = Environment.OSVersion.Version.Build;
        if (build < plugin.MinBuild || build > plugin.MaxBuild) { await ShowStyledInfoAsync("Plugin není kompatibilní", $"Build {build} není v rozsahu {plugin.MinBuild}-{plugin.MaxBuild}."); return; }
        var all = GetAllExtensionTweaks().ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var selected = plugin.Tweaks.Where(all.ContainsKey).Select(id => all[id]).ToArray();
        if (selected.Length == 0) { await ShowStyledInfoAsync("Spanky Plugins", "Manifest neobsahuje žádná známá tweak ID."); return; }
        await _engine.DetectAsync(selected, token);
        // Stage the desired ON state without changing shared catalog definitions before confirmation.
        var dirty = selected.Where(x => x.IsLoaded && !x.Current).ToArray();
        if (dirty.Length == 0) { await ShowStyledInfoAsync("Spanky Plugins", "Všechny položky pluginu už jsou aktivní."); return; }
        if (!await ShowStyledConfirmAsync($"Použít plugin {plugin.Name}?", string.Join(Environment.NewLine, dirty.Take(24).Select(x => "• " + x.Name)), "Použít", "Zrušit", $"{dirty.Length} změn")) return;
        await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before plugin changes");
        token.ThrowIfCancellationRequested();
        foreach (var tweak in dirty) tweak.Pending = true;
        var progressResult = await RunCancellableWithProgressAsync(
            plugin.Name,
            $"Aplikuji {dirty.Length} změn…",
            async progressToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                return await _engine.ApplyAsync(dirty, linked.Token);
            },
            result => result.Count == 0,
            result => result.Count == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("extensions", $"Plugin apply cancelled: {plugin.Name}");
            bool verified = await ReconcileCancelledApplyAsync(dirty, "extensions:plugin:" + plugin.Name);
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            if (!verified && !token.IsCancellationRequested)
                ShowActionFeedback(plugin.Name, ActionFeedbackKind.Failed,
                    "Operace byla přerušena; stav změněných funkcí nebylo možné ověřit.");
            return;
        }
        var errors = progressResult.Result;
        if (errors.Count > 0) await ShowMessageAsync(plugin.Name, string.Join(Environment.NewLine, errors.Take(10)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Info("extensions", "Spanky Plugins cancelled by navigation.");
        }
    }
}
