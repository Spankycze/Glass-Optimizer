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

    private Task SaveDebloatKeepStoreAsync()
    {
        // Capture the current choice on the UI dispatcher; disk I/O and
        // read-back verification must never block the checkbox interaction.
        string[] expected = _debloatKeep
            .Where(x => x.Value)
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string path = DebloatKeepPath;
        return Task.Run(() => PersistDebloatKeepStore(path, expected));
    }

    private static void PersistDebloatKeepStore(string path, string[] expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFileService.WriteText(path, string.Join(Environment.NewLine, expected));
        string[] persisted = File.Exists(path)
            ? File.ReadAllLines(path)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        if (!persisted.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Debloater keep-list read-back nepotvrdil uložené výjimky.");
    }
    private async Task OpenDebloatKeepManagerAsync(CancellationToken token)
    {
        int operationGeneration = _navigationGeneration;
        var apps = await InstalledAppService.GetInstalledAsync(token, includeLogos: false);
        token.ThrowIfCancellationRequested();
        var candidates = apps.Where(x => x.Recommended && IsDebloatSelectable(x))
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (candidates.Length == 0) { await ShowStyledInfoAsync("Debloater", "Nebyly nalezeny žádné Recommended položky k odebrání."); return; }
        var keep = candidates.ToDictionary(x => x.PackageFullName, x => _debloatKeep.TryGetValue(x.PackageFullName, out var saved) && saved, StringComparer.OrdinalIgnoreCase);
        // Controls are scoped to this dialog; do not retain a closed dialog in MainWindow.
        var keepBoxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(new TextBlock { Text = "Zaškrtni pouze aplikace, které chceš PONECHAT. Nezaškrtnuté Recommended položky budou připravené k odebrání.", TextWrapping = TextWrapping.Wrap, Foreground = TextPrimaryBrush() });
        root.Children.Add(new TextBlock
        {
            Text = $"Trvale uložené výjimky: {keep.Count(x => x.Value)}",
            Foreground = TextSecondaryBrush(),
            FontSize = 12
        });
        bool suppressKeepEvents = false;
        bool saveInProgress = false;
        Task pendingSave = Task.CompletedTask;
        Button? resetButton = null;
        void SetSaving(bool saving)
        {
            saveInProgress = saving;
            foreach (var box in keepBoxes.Values) box.IsEnabled = !saving;
            if (resetButton is not null) resetButton.IsEnabled = !saving;
        }
        async Task UpdateKeepAsync(CheckBox box, string package, bool shouldKeep)
        {
            if (suppressKeepEvents || saveInProgress) return;
            bool previous = keep[package];
            keep[package] = shouldKeep;
            _debloatKeep[package] = shouldKeep;
            SetSaving(true);
            try
            {
                await SaveDebloatKeepStoreAsync();
            }
            catch (Exception ex)
            {
                LogService.Error("debloat", "Debloat keep-list save failed", ex);
                keep[package] = previous;
                _debloatKeep[package] = previous;
                suppressKeepEvents = true;
                try { box.IsChecked = previous; }
                finally { suppressKeepEvents = false; }
                ShowActionFeedback("Debloater", ActionFeedbackKind.Failed, ex.Message);
            }
            finally { SetSaving(false); }
        }
        foreach (var app in candidates)
        {
            var cb = new CheckBox { Content = app.DisplayName, IsChecked = keep[app.PackageFullName], Tag = app.PackageFullName };
            cb.Checked += (_, _) => pendingSave = UpdateKeepAsync(cb, app.PackageFullName, true);
            cb.Unchecked += (_, _) => pendingSave = UpdateKeepAsync(cb, app.PackageFullName, false);
            keepBoxes[app.PackageFullName] = cb;
            root.Children.Add(cb);
        }

        async Task ResetKeepAsync()
        {
            if (saveInProgress) return;
            SetSaving(true);
            var previous = _debloatKeep.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var key in _debloatKeep.Keys.ToArray()) _debloatKeep[key] = false;
                await SaveDebloatKeepStoreAsync();
                foreach (var key in keep.Keys.ToArray()) keep[key] = false;
                suppressKeepEvents = true;
                try
                {
                    foreach (var box in keepBoxes.Values) box.IsChecked = false;
                }
                finally { suppressKeepEvents = false; }
                ShowActionFeedback("Debloater", ActionFeedbackKind.Verified, "Uložené výjimky byly resetovány a ověřeny.");
            }
            catch (Exception ex)
            {
                foreach (var pair in previous) _debloatKeep[pair.Key] = pair.Value;
                LogService.Error("debloat", "Debloat keep-list reset failed", ex);
                ShowActionFeedback("Debloater", ActionFeedbackKind.Failed, ex.Message);
            }
            finally { SetSaving(false); }
        }
        resetButton = QuickButton("Reset uložených výjimek", () =>
        {
            pendingSave = ResetKeepAsync();
            return pendingSave;
        });
        root.Children.Add(resetButton);

        if (!await ShowStyledContentAsync("Co chceš ponechat?", $"Recommended kandidáti: {candidates.Length}", new ScrollViewer { Content = root, MaxHeight = 560 }, "Pokračovat")) return;
        // A user can click Continue while a save is in flight: wait for
        // verified persistence (including any rollback) before choosing apps.
        await pendingSave;
        var chosen = candidates.Where(x => !keep[x.PackageFullName]).ToArray();
        if (chosen.Length == 0) return;
        string preview = string.Join("\n", chosen.Take(24).Select(x => "• " + x.DisplayName));
        if (chosen.Length > 24) preview += $"\n… a dalších {chosen.Length - 24}";
        if (!await ShowStyledConfirmAsync($"Odebrat {chosen.Length} Recommended aplikací?", preview, "Spustit", "Zrušit", "Před změnou se vytvoří Restore Point")) return;
        if (token.IsCancellationRequested || operationGeneration != _navigationGeneration ||
            _section != "debloat") return;
        // A failed restore point must never be silently ignored before a
        // potentially destructive removal.
        if (!await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before Debloater"))
        {
            ShowActionFeedback("Debloater", ActionFeedbackKind.Failed,
                "Bod obnovení nebyl vytvořen. Žádné aplikace nebyly odebrány.");
            await ShowStyledInfoAsync("Debloater — odebrání zastaveno",
                "Windows nepotvrdil vytvoření bodu obnovení. Odebrání aplikací bylo bezpečně zrušeno.");
            return;
        }
        if (token.IsCancellationRequested || operationGeneration != _navigationGeneration ||
            _section != "debloat") return;
        bool refreshCompleted = await RunDebloatProgressAsync(chosen, token);
        // Canceled or unverified operations leave their result visible.
        // The progress dialog already invalidates the inventory/page caches;
        // manual Obnovit will perform a fresh scan when the user is ready.
        if (refreshCompleted && !token.IsCancellationRequested &&
            operationGeneration == _navigationGeneration && _section == "debloat")
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
            bool logoRequested = false;
            iconBox.Loaded += (_, _) =>
            {
                if (logoRequested) return;
                logoRequested = true;
                SafeTaskService.Forget(
                    LoadInstalledAppLogoAsync(), "debloat", "Lazy installed app logo");
            };
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

    internal Task RunDebloatDialogFilterSmokeAsync()
    {
        // Read-only: exercises the real manual selection controls without
        // ever displaying the destructive confirmation or uninstalling apps.
        InstalledAppInfo[] sample =
        [
            new("Smoke.Safe", "Smoke.Safe_1_test", "Smoke.Safe_family", "Smoke Safe",
                "Test", null, true, DebloatCategory.ThirdParty, DebloatRisk.Safe, false, "safe"),
            new("Smoke.Optional", "Smoke.Optional_1_test", "Smoke.Optional_family", "Smoke Optional",
                "Test", null, false, DebloatCategory.ThirdParty, DebloatRisk.Optional, false, "optional"),
            new("Smoke.Advanced", "Smoke.Advanced_1_test", "Smoke.Advanced_family", "Smoke Advanced",
                "Test", null, false, DebloatCategory.MicrosoftExtras, DebloatRisk.Advanced, false, "advanced"),
            new("Smoke.Protected", "Smoke.Protected_1_test", "Smoke.Protected_family", "Smoke Protected",
                "Test", null, false, DebloatCategory.System, DebloatRisk.Protected, true, "protected")
        ];
        return ShowInstalledDebloatDialogAsync(sample, _windowLifetimeCts.Token, smokeOnly: true);
    }

    private static bool IsDebloatSelectable(InstalledAppInfo app) =>
        !app.Protected && app.Risk != DebloatRisk.Protected &&
        !InstalledAppService.IsProtectedPackageName(app.PackageName) &&
        !string.IsNullOrWhiteSpace(app.PackageName) &&
        !string.IsNullOrWhiteSpace(app.PackageFullName) &&
        app.PackageFullName.StartsWith(app.PackageName + "_", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecommendedDebloatSelection(
        InstalledAppInfo app, IReadOnlyDictionary<string, bool> keep)
        => app.Recommended && IsDebloatSelectable(app) &&
           !(keep.TryGetValue(app.PackageFullName, out bool saved) && saved);

    private async Task ShowInstalledDebloatDialogAsync(
        IReadOnlyList<InstalledAppInfo> apps, CancellationToken token, bool smokeOnly = false)
    {
        int operationGeneration = _navigationGeneration;
        // The manual dialog must honor the SAME persisted Keep exceptions as
        // the Recommended removal flow. Explicit user checkboxes may override.
        var keep = _debloatKeep;
        var selected = apps.ToDictionary(x => x.PackageFullName,
            x => IsRecommendedDebloatSelection(x, keep), StringComparer.OrdinalIgnoreCase);
        // Dialog-scoped controls survive category/risk filter changes.
        // Avoid rebuilding hundreds of cards and re-registering logo loaders
        // whenever the user changes a filter or selects a preset.
        var cachedRows = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
        var cachedChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        bool syncingSelection = false;
        var category = new ComboBox { MinWidth = 150 };
        foreach (var v in new[] { "Všechny kategorie", "Microsoft Extras", "Média", "Gaming", "Komunikace", "Nástroje", "Third-party", "Systém" }) category.Items.Add(v);
        category.SelectedIndex = 0;
        var risk = new ComboBox { MinWidth = 130 };
        foreach (var v in new[] { "Všechna rizika", "Bezpečné", "Volitelné", "Pokročilé", "Chráněné" }) risk.Items.Add(v);
        risk.SelectedIndex = 0;
        var scope = new ComboBox { MinWidth = 150, ItemsSource = new[] { "Aktuální uživatel", "Všichni uživatelé" }, SelectedIndex = 0 };
        var count = new TextBlock { Foreground = TextSecondaryBrush(), FontSize = 12.5 };
        var listHost = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 520,
            ItemsPanel = UiResourceService.Get<ItemsPanelTemplate>("SpankyVirtualizingItemsPanel")
        };
        List<InstalledAppInfo> filtered = apps.ToList();

        bool CategoryMatches(InstalledAppInfo a) => category.SelectedIndex switch
        {
            1 => a.Category == DebloatCategory.MicrosoftExtras,
            2 => a.Category == DebloatCategory.Media,
            3 => a.Category == DebloatCategory.Gaming,
            4 => a.Category == DebloatCategory.Communication,
            5 => a.Category == DebloatCategory.Tools,
            6 => a.Category == DebloatCategory.ThirdParty,
            7 => a.Category == DebloatCategory.System,
            _ => true
        };
        // The visible labels are Czech, while InstalledAppInfo.RiskLabel
        // is English (Safe/Optional/Advanced/Protected). Compare the enum,
        // not translated presentation strings.
        bool RiskMatches(InstalledAppInfo a) => risk.SelectedIndex switch
        {
            1 => a.Risk == DebloatRisk.Safe,
            2 => a.Risk == DebloatRisk.Optional,
            3 => a.Risk == DebloatRisk.Advanced,
            4 => a.Protected || a.Risk == DebloatRisk.Protected,
            _ => true
        };
        void Render()
        {
            // Keep one ListViewItem per package for the entire dialog lifetime.
            // Filtering only collapses existing items: clearing/re-adding live
            // WinUI children can reparent visuals and repeatedly trigger Loaded.
            filtered = apps.Where(a => CategoryMatches(a) && RiskMatches(a)).ToList();
            var visible = new HashSet<string>(
                filtered.Select(app => app.PackageFullName), StringComparer.OrdinalIgnoreCase);
            foreach (var app in filtered)
            {
                string package = app.PackageFullName;
                if (cachedRows.ContainsKey(package)) continue;
                var cb = new CheckBox
                {
                    IsChecked = selected[package],
                    IsEnabled = IsDebloatSelectable(app),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = app
                };
                cb.Checked += (_, _) =>
                {
                    if (syncingSelection) return;
                    selected[package] = true;
                    UpdateCount();
                };
                cb.Unchecked += (_, _) =>
                {
                    if (syncingSelection) return;
                    selected[package] = false;
                    UpdateCount();
                };
                cachedChecks[package] = cb;
                var row = new ListViewItem
                {
                    Content = BuildInstalledAppRow(app, cb),
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                cachedRows[package] = row;
                listHost.Items.Add(row);
            }
            foreach (var pair in cachedRows)
                pair.Value.Visibility = visible.Contains(pair.Key)
                    ? Visibility.Visible : Visibility.Collapsed;
            UpdateCount();
        }

        void ApplySelection(Func<InstalledAppInfo, bool> choose)
        {
            foreach (var app in apps)
                // Bulk presets respect persisted Keep exceptions. Only an
                // explicit checkbox click can opt an excluded package back in.
                selected[app.PackageFullName] = IsDebloatSelectable(app) &&
                    !(keep.TryGetValue(app.PackageFullName, out bool keepSaved) && keepSaved) &&
                    choose(app);
            syncingSelection = true;
            try
            {
                // Presets never need to rebuild the visible list.
                foreach (var entry in cachedChecks)
                    entry.Value.IsChecked = selected[entry.Key];
            }
            finally { syncingSelection = false; }
            UpdateCount();
        }

        void UpdateCount()
        {
            int n = selected.Count(x => x.Value);
            int exclusions = apps.Count(a => keep.TryGetValue(a.PackageFullName, out bool saved) && saved);
            count.Text = $"Zobrazeno: {filtered.Count}/{apps.Count}  •  Vybráno: {n}  •  Ponechat: {exclusions}";
        }
        category.SelectionChanged += (_, _) => Render();
        risk.SelectionChanged += (_, _) => Render();

        var root = new StackPanel { Spacing = 10 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filters.Children.Add(category); filters.Children.Add(risk); filters.Children.Add(scope);
        root.Children.Add(filters); root.Children.Add(count);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Doporučené", () =>
        {
            ApplySelection(app => IsRecommendedDebloatSelection(app, keep));
            return Task.CompletedTask;
        }));
        actions.Children.Add(QuickButton("Bezpečné", () =>
        {
            ApplySelection(app => app.Risk == DebloatRisk.Safe);
            return Task.CompletedTask;
        }));
        actions.Children.Add(QuickButton("Vybrat filtrované", () =>
        {
            var visible = new HashSet<string>(
                filtered.Select(app => app.PackageFullName), StringComparer.OrdinalIgnoreCase);
            ApplySelection(app => selected[app.PackageFullName] || visible.Contains(app.PackageFullName));
            return Task.CompletedTask;
        }));
        actions.Children.Add(QuickButton("Zrušit vše", () =>
        {
            ApplySelection(_ => false);
            return Task.CompletedTask;
        }));
        root.Children.Add(actions);
        root.Children.Add(listHost);
        Render();

        if (smokeOnly)
        {
            if (cachedRows.Count != apps.Count || listHost.Items.Count != apps.Count)
                throw new InvalidOperationException("Debloat dialog failed initial row creation.");
            var originals = cachedRows.ToDictionary(x => x.Key, x => x.Value,
                StringComparer.OrdinalIgnoreCase);
            for (int option = 1; option <= 4; option++)
            {
                risk.SelectedIndex = option;
                int visible = cachedRows.Count(x => x.Value.Visibility == Visibility.Visible);
                if (visible != 1 || cachedRows.Count != apps.Count ||
                    originals.Any(x => !ReferenceEquals(x.Value, cachedRows[x.Key])))
                    throw new InvalidOperationException(
                        $"Debloat risk filter did not reuse its rows, option={option} visible={visible}.");
            }
            risk.SelectedIndex = 0;
            category.SelectedIndex = 6; // Third-party
            if (cachedRows.Count(x => x.Value.Visibility == Visibility.Visible) != 2)
                throw new InvalidOperationException("Debloat category filter produced incorrect visible rows.");
            ApplySelection(a => a.Risk == DebloatRisk.Safe);
            if (!selected[apps[0].PackageFullName] ||
                selected.Where(x => x.Key != apps[0].PackageFullName).Any(x => x.Value) ||
                cachedChecks.Any(x => x.Value.IsChecked != selected[x.Key]))
                throw new InvalidOperationException("Debloat selection preset became inconsistent.");
            // Combining category and risk filters must not reparent any card,
            // and a filtered additive selection must retain hidden choices.
            risk.SelectedIndex = 2; // Optional
            if (cachedRows.Count(x => x.Value.Visibility == Visibility.Visible) != 1)
                throw new InvalidOperationException("Debloat combined filters selected the wrong apps.");
            ApplySelection(app => selected[app.PackageFullName] ||
                filtered.Any(visible => visible.PackageFullName == app.PackageFullName));
            if (!selected[apps[0].PackageFullName] || !selected[apps[1].PackageFullName] ||
                selected[apps[2].PackageFullName] || selected[apps[3].PackageFullName])
                throw new InvalidOperationException("Debloat filtered selection lost hidden safe selection.");
            // No preset, including select-all, is allowed to enable protected packages.
            category.SelectedIndex = 0;
            risk.SelectedIndex = 4; // Protected
            if (cachedRows.Count(x => x.Value.Visibility == Visibility.Visible) != 1)
                throw new InvalidOperationException("Protected risk filter is not stable.");
            ApplySelection(_ => true);
            if (selected[apps[3].PackageFullName] ||
                cachedChecks[apps[3].PackageFullName].IsEnabled ||
                cachedChecks[apps[3].PackageFullName].IsChecked != false ||
                !count.Text.Contains("Vybráno: 3", StringComparison.Ordinal))
                throw new InvalidOperationException("Protected app was selected by a preset.");
            ApplySelection(_ => false);
            if (selected.Values.Any(value => value) ||
                cachedChecks.Any(entry => entry.Value.IsChecked != false))
                throw new InvalidOperationException("Debloat clear-all did not clear every checkbox.");
            risk.SelectedIndex = 0;
            if (cachedRows.Values.Any(row => row.Visibility != Visibility.Visible) ||
                listHost.Items.Count != apps.Count ||
                originals.Any(entry => !ReferenceEquals(entry.Value, cachedRows[entry.Key])))
                throw new InvalidOperationException("Debloat filters did not restore stable original rows.");
            LogService.Info("debloat",
                $"DEBLOAT_DIALOG_FILTER_SMOKE_OK rows={apps.Count} risk=4 category=true combined=true additive=true protected=true clearAll=true reused=true");
            return;
        }

        if (!await ShowStyledContentAsync("Debloater",
            "Chráněné aplikace nejdou vybrat. Uložené výjimky Ponechat respektují všechny hromadné předvolby; pouze ruční výběr je může změnit. Pokročilé položky vyžadují další potvrzení.",
            root, "Pokračovat")) return;
        var chosen = apps.Where(a => selected.TryGetValue(a.PackageFullName, out bool yes) &&
            yes && IsDebloatSelectable(a)).ToArray();
        if (chosen.Length == 0) return;
        bool removeAllUsers = scope.SelectedIndex == 1;
        if (removeAllUsers && !await ShowStyledConfirmAsync(
            "Odebrat aplikace všem uživatelům?",
            $"Vybráno {chosen.Length} balíčků. Tento zásah ovlivní také ostatní účty Windows. " +
            "Standardně se odebírá pouze aktuálnímu uživateli.",
            "Potvrdit všem uživatelům", "Zrušit", "Rozsah odebrání: všechny účty")) return;
        if (chosen.Any(x => x.Risk == DebloatRisk.Advanced) && !await ShowStyledConfirmAsync("Advanced Appx balíky", "Výběr obsahuje Microsoft balíky mimo Recommended. Ty mohou být navázané na další funkce Windows.", "Pokračovat", "Zrušit", "Debloater V3")) return;
        string preview = string.Join("\n", chosen.Take(24).Select(x => "• " + x.DisplayName));
        if (!await ShowStyledConfirmAsync(
            $"Odebrat {chosen.Length} aplikací ({(removeAllUsers ? "všichni uživatelé" : "aktuální uživatel")})?",
            preview + "\n\nOdeberou se jen vybrané Appx balíčky. Žádné další zásahy do registru.",
            "Spustit", "Zrušit", "Před změnou se vytvoří Restore Point")) return;
        if (token.IsCancellationRequested || operationGeneration != _navigationGeneration ||
            _section != "debloat") return;
        if (!await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before Debloater V3"))
        {
            ShowActionFeedback("Debloater", ActionFeedbackKind.Failed,
                "Bod obnovení nebyl vytvořen. Žádné aplikace nebyly odebrány.");
            await ShowStyledInfoAsync("Debloater — odebrání zastaveno",
                "Windows nepotvrdil vytvoření bodu obnovení. Odebrání aplikací bylo bezpečně zrušeno.");
            return;
        }
        if (token.IsCancellationRequested || operationGeneration != _navigationGeneration ||
            _section != "debloat") return;
        bool refreshCompleted = await RunDebloatProgressAsync(chosen, token, allUsers: removeAllUsers);
        if (refreshCompleted && !token.IsCancellationRequested &&
            operationGeneration == _navigationGeneration && _section == "debloat")
            await ShowSectionAsync("debloat");
    }
}
