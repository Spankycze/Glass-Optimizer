using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task ShowComponentDisableSelectorAsync(string sub)
    {
        int operationGeneration = _navigationGeneration;
        var tweaks = CatalogService.GetTweaks("components", sub);
        if (tweaks.Count == 0) return;
        await _engine.DetectAsync(tweaks);
        var checks = new List<(CheckBox Box, TweakDefinition Tweak)>();
        var root = new StackPanel { Spacing = 9 };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        root.Children.Add(toolbar);
        foreach (var tweak in tweaks)
        {
            var cb = new CheckBox { IsChecked = tweak.Recommended && tweak.Risk != TweakRisk.Advanced, VerticalAlignment = VerticalAlignment.Top };
            checks.Add((cb, tweak));
            root.Children.Add(BuildComponentChoiceRow(cb, tweak));
        }
        toolbar.Children.Add(QuickButton("Recommended", () => { foreach (var x in checks) x.Box.IsChecked = x.Tweak.Recommended && x.Tweak.Risk != TweakRisk.Advanced; return Task.CompletedTask; }));
        toolbar.Children.Add(QuickButton("Vybrat Safe", () => { foreach (var x in checks) x.Box.IsChecked = x.Tweak.Risk == TweakRisk.Safe; return Task.CompletedTask; }));
        toolbar.Children.Add(QuickButton("Zrušit vše", () => { foreach (var x in checks) x.Box.IsChecked = false; return Task.CompletedTask; }));
        var scroll = new ScrollViewer { Content = root, MaxHeight = 590, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        if (!await ShowStyledContentAsync($"{sub} — co vypnout", "Zaškrtni jen položky, které chceš změnit. Advanced nejsou předvybrané.", scroll, "Použít vybrané")) return;
        var selected = checks.Where(x => x.Box.IsChecked == true).Select(x => x.Tweak).ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(x => x.Risk == TweakRisk.Advanced) &&
            !await ShowStyledConfirmAsync("Advanced změny", "Výběr obsahuje Advanced položky. Mohou ovlivnit kompatibilitu zařízení nebo funkcí Windows.", "Pokračovat", "Zrušit", sub)) return;
        // Stage the desired ON state without changing shared catalog definitions before confirmation.
        var dirty = selected.Where(x => x.IsLoaded && !x.Current).ToArray();
        if (dirty.Length == 0) { await ShowStyledInfoAsync(sub, "Vybrané položky už jsou v požadovaném stavu."); return; }
        string preview = string.Join(Environment.NewLine, dirty.Take(24).Select(x => "• " + x.Name));
        if (!await ShowStyledConfirmAsync($"Použít {dirty.Length} změn?", preview, "Použít", "Zrušit", "Před změnou se vytvoří Restore Point")) return;
        await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before component selection");
        foreach (var tweak in dirty) tweak.Pending = true;
        var progressResult = await RunCancellableWithProgressAsync(
            sub,
            $"Aplikuji {dirty.Length} vybraných změn…",
            progressToken => _engine.ApplyAsync(dirty, progressToken),
            result => result.Count == 0,
            result => result.Count == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("components", $"Component selection apply cancelled: {sub}");
            bool verified = await ReconcileCancelledApplyAsync(dirty, "components:" + sub);
            _tweakCacheTime.TryRemove("components|" + sub, out _);
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            if (!verified && operationGeneration == _navigationGeneration &&
                _section == "components")
                ShowActionFeedback(sub, ActionFeedbackKind.Failed,
                    "Operace byla přerušena a skutečný stav vybraných položek není ověřen.");
            return;
        }
        var errors = progressResult.Result;
        if (errors.Count > 0) await ShowMessageAsync(sub, string.Join(Environment.NewLine, errors.Take(10)));
        _tweakCacheTime.TryRemove("components|" + sub, out _);
        if (operationGeneration == _navigationGeneration && _section == "components") await ShowSectionAsync("components");
    }
    private FrameworkElement BuildComponentChoiceRow(CheckBox box, TweakDefinition tweak)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(box);
        var text = new StackPanel { Spacing = 2 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new TextBlock { Text = tweak.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        if (tweak.Recommended) title.Children.Add(Badge("Recommended", "SpankySuccessBrush"));
        if (tweak.Risk == TweakRisk.Advanced) title.Children.Add(Badge("Advanced", "SpankyWarningBrush"));
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = tweak.Description, FontSize = 11.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxWidth = 680 });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        return new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Child = row };
    }
}

