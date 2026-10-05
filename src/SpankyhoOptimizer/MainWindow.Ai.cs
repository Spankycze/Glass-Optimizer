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
    private System.Collections.Generic.List<UIElement>? _aiPageCache;
    private DateTime _aiPageCacheAt;
    private sealed record AiChoice(string Key, string Label, string Description, bool Recommended, bool Advanced);

    private static readonly AiChoice[] AiChoices =
    {
        new("DisableCopilotPolicies", "Windows Copilot", "Vypne podporované integrace Copilotu ve Windows.", true, false),
        new("DisableRegKeys", "AI integrace Windows", "Vypne běžné systémové AI integrace.", true, false),
        new("RemoveRecallFeature", "Recall", "Vypne nebo odebere volitelnou funkci Recall.", true, false),
        new("RemoveAppxPackages", "AI / Copilot aplikace", "Odebere nainstalované AI a Copilot aplikace.", true, false),
        new("RemoveWindowsAITasks", "AI naplánované úlohy", "Vypne nebo odebere podporované naplánované AI úlohy.", true, false),
        new("UpdateCleanupCheck", "AI cleanup po Windows Update", "Přidá kontrolu návratu podporovaných AI komponent po aktualizacích Windows.", false, false),
        new("DisableDefenderAI", "Defender AI Host", "Vypne podporovanou AI Host část Defenderu přes RemoveWindowsAI. Není součástí doporučeného výběru.", false, true),
        new("HideAIComponents", "AI Components stránka", "Skryje AI Components v Nastavení Windows.", true, false),
        new("DisableRewrite", "Notepad Rewrite", "Vypne AI Rewrite integraci v Poznámkovém bloku.", true, false),
        new("PreventAIPackageReinstall", "Blokovat návrat AI balíčků", "Omezí opětovnou instalaci podporovaných AI balíčků po update.", true, false),
        new("RemoveCBSPackages", "Systémové AI balíčky", "Pokročilý zásah do systémových AI komponent.", false, true),
        new("RemoveAIFiles", "AI systémové soubory", "Pokročilé odebrání AI souborů s nejvyšším rizikem kompatibility.", false, true)
    };
    private Task BuildAiPageAsync(CancellationToken token)
    {
        const string cacheKey = "ai|";
        if (!_tweakCache.TryGetValue(cacheKey, out _tweaks))
            _tweakCache[cacheKey] = _tweaks = CatalogService.GetTweaks("ai");

        bool staleDetection = !_tweakCacheTime.TryGetValue(cacheKey, out var detectedAt) || DateTime.UtcNow - detectedAt > TimeSpan.FromSeconds(90);
        if (_aiPageCache != null)
        {
            _aiPageCacheAt = DateTime.UtcNow;
            foreach (var element in _aiPageCache) AttachCachedElement(element);
            UpdateDirtyState();
            if (staleDetection) SafeTaskService.Forget(DetectAiStateAsync(cacheKey, _tweaks.ToArray(), token), "ai", "Background AI detect");
            return Task.CompletedTask;
        }

        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(PrimaryQuickButton("Vybrat AI moduly", ShowAiSelectionAsync));
        heroActions.Children.Add(QuickButton("Obnovit modul", ShowAiRevertDialogAsync));
        ContentStack.Children.Add(BuildModernSectionHero(
            "ai",
            "AI Debloater",
            "Windows AI komponenty s přesným výběrem a vratnými moduly.",
            "OVLÁDÁNÍ AI",
            "Copilot • Recall • AI úlohy • aplikace • systémové komponenty",
            heroActions));
        ContentStack.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Moduly", AiChoices.Length.ToString(), "Dostupné operace"),
            BuildDashboardMetric("Doporučené", AiChoices.Count(x => x.Recommended).ToString(), "Běžné vratné zásahy", "SpankySuccessBrush"),
            BuildDashboardMetric("Pokročilé", AiChoices.Count(x => x.Advanced).ToString(), "Systémové komponenty a soubory", "SpankyWarningBrush"),
            BuildDashboardMetric("Obnova", "ON", "Bod obnovení před pokročilými změnami", "SpankySuccessBrush")));
        var aiActions = new StackPanel { Spacing = 8 };
        aiActions.Children.Add(ActionCard("Vybrat AI moduly", "Copilot, Recall, AI úlohy, aplikace, AI Components, Notepad Rewrite a pokročilé moduly.", "Otevřít výběr", ShowAiSelectionAsync));
        aiActions.Children.Add(ActionCard("Obnovit AI modul", "Vrátí konkrétní modul do původního stavu.", "Obnovit", ShowAiRevertDialogAsync));
        ContentStack.Children.Add(BuildDashboardExpander("Další AI nástroje", "Výběr, obnova a údržba AI komponent", aiActions, expanded: true));
        ContentStack.Children.Add(ActionCard("Aktualizovat RemoveWindowsAI", "Stáhne poslední verzi skriptu z oficiálního zoicware repozitáře.", "Aktualizovat", async () =>
        {
            await ZoicwareToolsService.RefreshRemoveWindowsAiAsync(token);
            await ShowMessageAsync("Windows AI", "RemoveWindowsAI byl aktualizován.");
        }));
        _aiPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _aiPageCacheAt = DateTime.UtcNow;
        UpdateDirtyState();
        if (staleDetection) SafeTaskService.Forget(DetectAiStateAsync(cacheKey, _tweaks.ToArray(), token), "ai", "Background AI detect");
        return Task.CompletedTask;
    }

    private void InvalidateAiPageCache()
    {
        _aiPageCache = null;
        _aiPageCacheAt = default;
    }

    private async Task DetectAiStateAsync(string cacheKey, TweakDefinition[] tweaks, CancellationToken token)
    {
        try
        {
            await _engine.DetectAsync(tweaks, token);
            _tweakCacheTime[cacheKey] = DateTime.UtcNow;
            if (!token.IsCancellationRequested && _section == "ai") DispatcherQueue.TryEnqueue(UpdateDirtyState);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("ai", "Background AI detect failed", ex); }
    }

    private async Task ShowAiSelectionAsync()
    {
        var checks = new List<(CheckBox Box, AiChoice Item)>();
        var root = new StackPanel { Spacing = 10 };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(QuickButton("Recommended", () => { foreach (var x in checks) x.Box.IsChecked = x.Item.Recommended; return Task.CompletedTask; }));
        toolbar.Children.Add(QuickButton("Vybrat vše", () => { foreach (var x in checks) x.Box.IsChecked = true; return Task.CompletedTask; }));
        toolbar.Children.Add(QuickButton("Zrušit vše", () => { foreach (var x in checks) x.Box.IsChecked = false; return Task.CompletedTask; }));
        root.Children.Add(toolbar);
        foreach (var item in AiChoices)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var cb = new CheckBox { IsChecked = item.Recommended, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
            var text = new StackPanel { Spacing = 2 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            title.Children.Add(new TextBlock { Text = item.Label, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            if (item.Advanced) title.Children.Add(Badge("Pokročilé", "SpankyWarningBrush"));
            text.Children.Add(title);
            text.Children.Add(new TextBlock { Text = item.Description, FontSize = 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxWidth = 650 });
            row.Children.Add(cb); Grid.SetColumn(text, 1); row.Children.Add(text);
            root.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Child = row });
            checks.Add((cb, item));
        }
        var scroll = new ScrollViewer { Content = root, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        if (!await ShowStyledContentAsync("AI Debloater — co upravit", "Vyber konkrétní moduly. Nic dalšího se nespustí.", scroll, "Použít vybrané")) return;
        var selected = checks.Where(x => x.Box.IsChecked == true).Select(x => x.Item).ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(x => x.Advanced) && !await ShowStyledConfirmAsync("Pokročilý AI zásah", "Vybral jsi systémové AI komponenty nebo systémové soubory. Tyto zásahy mohou zhoršit kompatibilitu po Windows Update.", "Pokračovat", "Zrušit", "Před změnou se vytvoří bod obnovení")) return;
        await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before AI changes");
        await RunAiModulesProgressAsync(selected, revert: false);
        await ShowMessageAsync("Windows AI", "AI operace byla dokončena.");
    }

    private async Task ShowAiRevertDialogAsync()
    {
        var combo = new ComboBox { MinWidth = 420 };
        foreach (var ai in AiChoices) combo.Items.Add(ai.Key);
        combo.SelectedIndex = 0;
        if (!await ShowStyledContentAsync("Obnovit AI modul", "Vyber modul, který chceš vrátit do původního stavu.", combo, "Obnovit")) return;
        if (combo.SelectedItem is not string option) return;
        var choice = AiChoices.First(x => x.Key == option);
        await RunAiModulesProgressAsync(new[] { choice }, revert: true);
        await ShowMessageAsync("Obnova AI", "Obnova modulu byla dokončena.");
    }
}
