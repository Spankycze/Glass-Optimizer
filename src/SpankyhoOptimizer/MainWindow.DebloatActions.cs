using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Dictionary<string, bool>? _debloatKeepStore;
    private Dictionary<string, CheckBox>? _debloatKeepBoxesStore;

    private Dictionary<string, bool> _debloatKeep =>
        _debloatKeepStore ??= LoadDebloatKeepStore();

    private static string DebloatKeepPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "State", "debloat-keep.txt");

    private static Dictionary<string, bool> LoadDebloatKeepStore()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(DebloatKeepPath)) return result;
            foreach (string line in File.ReadAllLines(DebloatKeepPath))
            {
                string package = line.Trim();
                if (package.Length > 0) result[package] = true;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("debloat", "Debloat keep-list load failed", ex);
        }
        return result;
    }

    private void SaveDebloatKeepStore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebloatKeepPath)!);
            string text = string.Join(
                Environment.NewLine,
                _debloatKeep.Where(x => x.Value).Select(x => x.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            AtomicFileService.WriteText(DebloatKeepPath, text);
        }
        catch (Exception ex)
        {
            LogService.Error("debloat", "Debloat keep-list save failed", ex);
        }
    }
    private Dictionary<string, CheckBox> _debloatKeepBoxes =>
        _debloatKeepBoxesStore ??= new(StringComparer.OrdinalIgnoreCase);

    private async Task OpenDebloatKeepManagerAsync(CancellationToken token)
    {
        var apps = await InstalledAppService.GetInstalledAsync(token, includeLogos: false);
        token.ThrowIfCancellationRequested();
        var candidates = apps.Where(x => x.Recommended && !x.Protected).OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (candidates.Length == 0) { await ShowStyledInfoAsync("Debloater", "Nebyly nalezeny žádné Recommended položky k odebrání."); return; }
        var keep = candidates.ToDictionary(x => x.PackageFullName, x => _debloatKeep.TryGetValue(x.PackageFullName, out var saved) && saved, StringComparer.OrdinalIgnoreCase);
        _debloatKeepBoxes.Clear();
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(new TextBlock { Text = "Zaškrtni pouze aplikace, které chceš PONECHAT. Nezaškrtnuté Recommended položky budou připravené k odebrání.", TextWrapping = TextWrapping.Wrap, Foreground = TextPrimaryBrush() });
        root.Children.Add(new TextBlock
        {
            Text = $"Trvale uložené výjimky: {keep.Count(x => x.Value)}",
            Foreground = TextSecondaryBrush(),
            FontSize = 12
        });
        foreach (var app in candidates)
        {
            var cb = new CheckBox { Content = app.DisplayName, IsChecked = keep[app.PackageFullName], Tag = app.PackageFullName };
            cb.Checked += (_, _) =>
            {
                keep[app.PackageFullName] = true;
                _debloatKeep[app.PackageFullName] = true;
                SaveDebloatKeepStore();
            };
            cb.Unchecked += (_, _) =>
            {
                keep[app.PackageFullName] = false;
                _debloatKeep[app.PackageFullName] = false;
                SaveDebloatKeepStore();
            };
            _debloatKeepBoxes[app.PackageFullName] = cb;
            root.Children.Add(cb);
        }

        root.Children.Add(QuickButton("Reset uložených výjimek", () =>
        {
            foreach (var key in _debloatKeep.Keys.ToArray()) _debloatKeep[key] = false;
            SaveDebloatKeepStore();
            foreach (var cb in _debloatKeepBoxes.Values) cb.IsChecked = false;
            ShowActionFeedback("Debloater", ActionFeedbackKind.Info, "Uložené výjimky byly resetovány.");
            return Task.CompletedTask;
        }));

        if (!await ShowStyledContentAsync("Co chceš ponechat?", $"Recommended kandidáti: {candidates.Length}", new ScrollViewer { Content = root, MaxHeight = 560 }, "Pokračovat")) return;
        var chosen = candidates.Where(x => !keep[x.PackageFullName]).ToArray();
        if (chosen.Length == 0) return;
        string preview = string.Join("\n", chosen.Take(24).Select(x => "• " + x.DisplayName));
        if (chosen.Length > 24) preview += $"\n… a dalších {chosen.Length - 24}";
        if (!await ShowStyledConfirmAsync($"Odebrat {chosen.Length} Recommended aplikací?", preview, "Spustit", "Zrušit", "Před změnou se vytvoří Restore Point")) return;
        await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before Debloater");
        await RunDebloatProgressAsync(chosen, token);
        InstalledAppService.InvalidateCache();
        InvalidateDebloatPageCache();
        await ShowSectionAsync("debloat");
    }

    private async Task OpenDebloatManagerAsync(CancellationToken token)
    {
        var apps = await InstalledAppService.GetInstalledAsync(token, includeLogos: false);
        token.ThrowIfCancellationRequested();
        await ShowInstalledDebloatDialogAsync(apps, token);
    }

    private FrameworkElement BuildInstalledAppRow(InstalledAppInfo app, CheckBox check)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(check);
        var iconBox = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(10), Background = AccentSoftBrush() };
        if (!string.IsNullOrWhiteSpace(app.LogoPath) && File.Exists(app.LogoPath))
        {
            try { var img = new Image { Width = 28, Height = 28 }; img.Source = new BitmapImage(new Uri(app.LogoPath)) { DecodePixelWidth = 28 }; iconBox.Child = img; }
            catch { iconBox.Child = new FontIcon { Glyph = "\uE71D", Foreground = AccentBrush() }; }
        }
        else
        {
            iconBox.Child = new FontIcon { Glyph = "\uE71D", Foreground = AccentBrush() };
            iconBox.Loaded += (_, _) => SafeTaskService.Forget(
                LoadInstalledAppLogoAsync(), "debloat", "Lazy installed app logo");
            async Task LoadInstalledAppLogoAsync()
            {
                try
                {
                    string? lazy = await InstalledAppService.GetLogoPathAsync(app.PackageFullName);
                    if (!string.IsNullOrWhiteSpace(lazy) && File.Exists(lazy))
                    {
                        var img = new Image { Width = 28, Height = 28, Source = new BitmapImage(new Uri(lazy)) { DecodePixelWidth = 28 } };
                        iconBox.Child = img;
                    }
                }
                catch { }
            }
        }
        Grid.SetColumn(iconBox, 1); grid.Children.Add(iconBox);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new TextBlock { Text = app.DisplayName, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        if (app.Recommended) title.Children.Add(Badge("Doporučené", "SpankySuccessBrush"));
        if (app.Protected) title.Children.Add(Badge("Chráněné", "SpankyWarningBrush"));
        else if (app.Risk == DebloatRisk.Advanced) title.Children.Add(Badge("Pokročilé", "SpankyWarningBrush"));
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = $"{app.CategoryLabel} • {app.RiskLabel} • {app.Publisher}", FontSize = 11.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = app.Reason, FontSize = 11, Foreground = TextSecondaryBrush(), Opacity = .82, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 2); grid.Children.Add(text);
        var row = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(0, 0, 0, _darkTheme ? 1.1 : 1), CornerRadius = new CornerRadius(0), Padding = new Thickness(10, 8, 10, 8), Child = grid }; AttachHoverMotion(row, 1.004f); return row;
    }

    private async Task ShowInstalledDebloatDialogAsync(IReadOnlyList<InstalledAppInfo> apps, CancellationToken token)
    {
        var selected = apps.ToDictionary(x => x.PackageFullName, x => x.Recommended && !x.Protected, StringComparer.OrdinalIgnoreCase);
        var category = new ComboBox { MinWidth = 150 };
        foreach (var v in new[] { "Všechny kategorie", "Microsoft Extras", "Média", "Gaming", "Komunikace", "Nástroje", "Third-party", "Systém" }) category.Items.Add(v);
        category.SelectedIndex = 0;
        var risk = new ComboBox { MinWidth = 130 };
        foreach (var v in new[] { "Všechna rizika", "Bezpečné", "Volitelné", "Pokročilé", "Chráněné" }) risk.Items.Add(v);
        risk.SelectedIndex = 0;
        var scope = new ComboBox { MinWidth = 150, ItemsSource = new[] { "Aktuální uživatel", "Všichni uživatelé" }, SelectedIndex = 0 };
        var count = new TextBlock { Foreground = TextSecondaryBrush(), FontSize = 12.5 };
        var listHost = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 520 };
        List<InstalledAppInfo> filtered = apps.ToList();

        bool CategoryMatches(InstalledAppInfo a) => category.SelectedIndex == 0 || a.CategoryLabel == category.SelectedItem?.ToString();
        bool RiskMatches(InstalledAppInfo a) => risk.SelectedIndex == 0 || a.RiskLabel == risk.SelectedItem?.ToString();
        void Render()
        {
            listHost.Items.Clear();
            filtered = apps.Where(a => CategoryMatches(a) && RiskMatches(a)).ToList();
            foreach (var app in filtered)
            {
                var cb = new CheckBox { IsChecked = selected[app.PackageFullName], IsEnabled = !app.Protected, VerticalAlignment = VerticalAlignment.Center, Tag = app };
                cb.Checked += (_, _) => { selected[app.PackageFullName] = true; UpdateCount(); };
                cb.Unchecked += (_, _) => { selected[app.PackageFullName] = false; UpdateCount(); };
                listHost.Items.Add(BuildInstalledAppRow(app, cb));
            }
            UpdateCount();
        }

        void UpdateCount()
        {
            int n = selected.Count(x => x.Value);
            count.Text = $"Zobrazeno: {filtered.Count}/{apps.Count}  •  Vybráno: {n}";
        }
        category.SelectionChanged += (_, _) => Render();
        risk.SelectionChanged += (_, _) => Render();

        var root = new StackPanel { Spacing = 10 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filters.Children.Add(category); filters.Children.Add(risk); filters.Children.Add(scope);
        root.Children.Add(filters); root.Children.Add(count);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Doporučené", () => { foreach (var a in apps) selected[a.PackageFullName] = a.Recommended && !a.Protected; Render(); return Task.CompletedTask; }));
        actions.Children.Add(QuickButton("Bezpečné", () => { foreach (var a in apps) selected[a.PackageFullName] = a.Risk == DebloatRisk.Safe && !a.Protected; Render(); return Task.CompletedTask; }));
        actions.Children.Add(QuickButton("Vybrat filtrované", () => { foreach (var a in filtered.Where(x => !x.Protected)) selected[a.PackageFullName] = true; Render(); return Task.CompletedTask; }));
        actions.Children.Add(QuickButton("Zrušit vše", () => { foreach (var a in apps) selected[a.PackageFullName] = false; Render(); return Task.CompletedTask; }));
        root.Children.Add(actions);
        root.Children.Add(listHost);
        Render();

        if (!await ShowStyledContentAsync("Debloater", "Chráněné aplikace nejdou vybrat. Pokročilé položky vyžadují další potvrzení.", root, "Pokračovat")) return;
        var chosen = apps.Where(a => selected.TryGetValue(a.PackageFullName, out bool yes) && yes && !a.Protected).ToArray();
        if (chosen.Length == 0) return;
        if (chosen.Any(x => x.Risk == DebloatRisk.Advanced) && !await ShowStyledConfirmAsync("Advanced Appx balíky", "Výběr obsahuje Microsoft balíky mimo Recommended. Ty mohou být navázané na další funkce Windows.", "Pokračovat", "Zrušit", "Debloater V3")) return;
        string preview = string.Join("\n", chosen.Take(24).Select(x => "• " + x.DisplayName));
        if (!await ShowStyledConfirmAsync($"Zpracovat {chosen.Length} aplikací?", preview, "Spustit", "Zrušit", "Před změnou se vytvoří Restore Point")) return;
        await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before Debloater V3");
        await RunDebloatProgressAsync(chosen, token, allUsers: scope.SelectedIndex == 1);
        InstalledAppService.InvalidateCache();
        InvalidateDebloatPageCache();
        await ShowSectionAsync("debloat");
    }
}
