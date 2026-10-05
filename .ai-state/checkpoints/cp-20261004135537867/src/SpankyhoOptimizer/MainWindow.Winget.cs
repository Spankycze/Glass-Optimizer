using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _softwareUpdaterSessionId;

    private async Task ShowSoftwareManagerAsync(CancellationToken token)
    {
        int sessionId = Interlocked.Increment(ref _softwareUpdaterSessionId);
        string returnSection = _section;
        try { await ShowSoftwareManagerCoreAsync(token, smokeOnly: false, sessionId, returnSection); }
        catch (OperationCanceledException) { LogService.Info("winget", "Software Updater cancelled."); }
        catch (Exception ex)
        {
            LogService.Error("winget", "Software Updater failed", ex);
            await ShowMessageAsync("Software Updater", "Updater se nepodařilo otevřít: " + ex.Message);
        }
    }

    private async Task ShowSoftwareManagerCoreAsync(CancellationToken token, bool smokeOnly, int sessionId = 0, string returnSection = "apps")
    {
        var root = new StackPanel { Spacing = 12 };
        var status = new TextBlock { Text = "Načítám dostupné aktualizace…", Foreground = TextSecondaryBrush() };
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 5,
            Visibility = Visibility.Collapsed,
            Foreground = TextSecondaryBrush(),
            Background = SurfaceAltBrush()
        };
        var listHost = new StackPanel { Spacing = 7 };
        var rows = new List<(CheckBox Box, WingetUpgradeItem Item)>();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var installId = new TextBox { PlaceholderText = "Winget package ID…", MinWidth = 280 };
        var installButton = QuickButton("Instalovat ID", async () => await InstallWingetPackageAsync(installId.Text, status, token));
        toolbar.Children.Add(installId);
        toolbar.Children.Add(installButton);
        root.Children.Add(toolbar);
        root.Children.Add(status);
        root.Children.Add(progress);
        void PopulateRows(IReadOnlyList<WingetUpgradeItem> items, bool staleDisplay = false)
        {
            listHost.Children.Clear(); rows.Clear();
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 8, 10, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
                text.Children.Add(new TextBlock { Text = item.Id, FontSize = 11, Foreground = TextSecondaryBrush() });
                var version = new TextBlock { Text = $"{item.InstalledVersion}  →  {item.AvailableVersion}", VerticalAlignment = VerticalAlignment.Center, Foreground = AccentBrush() };
                Grid.SetColumn(box, 0); Grid.SetColumn(text, 1); Grid.SetColumn(version, 2);
                row.Children.Add(box); row.Children.Add(text); row.Children.Add(version);
                listHost.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Child = row });
                rows.Add((box, item));
            }
            string baseStatus = items.Count == 0 ? "Všechny Winget aplikace jsou aktuální." : $"Dostupné aktualizace: {items.Count}";
            status.Text = staleDisplay ? baseStatus + " • ověřuji na pozadí…" : baseStatus;
        }

        async Task RefreshAsync(bool forceRefresh = false)
        {
            status.Text = "Kontroluji Winget aktualizace…";
            var items = await WingetUpdateService.GetUpgradesAsync(token, forceRefresh);
            PopulateRows(items);
        }
        async Task UpdateSelectedAsync()
        {
            var selected = rows.Where(x => x.Box.IsChecked == true).Select(x => x.Item).ToArray();
            if (selected.Length == 0) { status.Text = "Není vybraná žádná aktualizace."; return; }
            if (!await ShowStyledConfirmAsync($"Aktualizovat {selected.Length} aplikací?",
                string.Join(Environment.NewLine, selected.Take(18).Select(x => $"• {x.Name} → {x.AvailableVersion}")),
                "Aktualizovat", "Zrušit", "Winget")) return;

            progress.Visibility = Visibility.Visible;
            progress.Maximum = selected.Length; progress.Value = 0;
            int ok = 0;
            var failed = new List<string>();
            foreach (var item in selected)
            {
                token.ThrowIfCancellationRequested();
                status.Text = $"Aktualizuji {item.Name}…";
                var result = await WingetUpdateService.UpgradeAsync(item.Id, token);
                if (result.Success) ok++;
                else
                {
                    failed.Add(item.Name);
                    LogService.Error("Winget upgrade failed: " + item.Id + " " + result.StdErr + result.StdOut);
                }
                progress.Value++;
            }
            progress.Visibility = Visibility.Collapsed;
            status.Text = failed.Count == 0
                ? $"Hotovo: {ok}/{selected.Length} aktualizací úspěšně."
                : $"Hotovo: {ok}/{selected.Length}. Selhalo: {failed.Count}.";
            if (failed.Count > 0)
                await ShowStyledInfoAsync("Winget — neúspěšné aktualizace", string.Join(Environment.NewLine, failed.Select(x => "• " + x)));
            await RefreshAsync(true);
        }

        async Task UpdateAllAsync()
        {
            if (!await ShowStyledConfirmAsync("Aktualizovat všechny aplikace?", "Winget zpracuje všechny dostupné aktualizace.", "Update all", "Zrušit", "Software Manager")) return;
            progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true;
            status.Text = "Winget aktualizuje všechny podporované aplikace…";
            var result = await WingetUpdateService.UpgradeAllAsync(token);
            progress.IsIndeterminate = false; progress.Visibility = Visibility.Collapsed;
            status.Text = result.Success ? "Update all dokončen." : "Update all skončil s chybou.";
            if (!result.Success) LogService.Error("Winget update all failed: " + result.StdErr + result.StdOut);
            await RefreshAsync(true);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Obnovit", () => RefreshAsync(true)));
        actions.Children.Add(QuickButton("Vybrat vše", () => { foreach (var row in rows) row.Box.IsChecked = true; return Task.CompletedTask; }));
        actions.Children.Add(QuickButton("Zrušit výběr", () => { foreach (var row in rows) row.Box.IsChecked = false; return Task.CompletedTask; }));
        actions.Children.Add(PrimaryQuickButton("Update selected", UpdateSelectedAsync));
        actions.Children.Add(QuickButton("Update all", UpdateAllAsync));
        root.Children.Add(actions);
        root.Children.Add(listHost);

        bool refreshStaleAfterRender = false;
        string? displayedSignature = null;
        var displaySnapshot = WingetUpdateService.TryGetDisplayUpgrades();
        if (displaySnapshot is { } display)
        {
            PopulateRows(display.Items, display.NeedsRefresh);
            refreshStaleAfterRender = display.NeedsRefresh;
            displayedSignature = string.Join("\n", display.Items
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Id + "|" + x.InstalledVersion + "|" + x.AvailableVersion + "|" + x.Source));
        }
        else
        {
            await RefreshAsync();
        }

        if (smokeOnly)
        {
            LogService.Info("winget", $"SOFTWARE_UPDATER_SMOKE_OK rows={rows.Count} status={status.Text}");
            return;
        }

        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=header-actions");
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string backLabel = string.Equals(returnSection, "extensions", StringComparison.OrdinalIgnoreCase)
            ? "Zpět na Rozšíření"
            : "Zpět na Programy / Launchery";
        headerActions.Children.Add(QuickButton(backLabel, async () =>
        {
            if (sessionId != 0)
                Interlocked.Increment(ref _softwareUpdaterSessionId);
            await ShowSectionAsync(returnSection);
        }));
        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=before-clear");
        ContentStack.Children.Clear();
        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=after-clear");
        var hero = BuildModernSectionHero(
            "apps",
            "Software Updater",
            "Winget aktualizace s přesnými Installed → Available verzemi a per-app read-backem.",
            "WINGET",
            rows.Count == 0 ? "Všechny aplikace jsou aktuální" : $"{rows.Count} aktualizací k dispozici",
            headerActions);
        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=hero-created");
        ContentStack.Children.Add(hero);
        ContentStack.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Aktualizace", rows.Count.ToString(), rows.Count == 0 ? "Vše aktuální" : "Dostupné upgrady", rows.Count == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric("Zdroj", "Winget", "Microsoft package manager"),
            BuildDashboardMetric("Výběr", "Per-app", "Update selected / all", "SpankySuccessBrush"),
            BuildDashboardMetric("Refresh", refreshStaleAfterRender ? "Ověřuji" : "Fresh", refreshStaleAfterRender ? "Stale cache → background check" : "Aktuální snapshot", refreshStaleAfterRender ? "SpankyWarningBrush" : "SpankySuccessBrush")));
        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=hero-added");
        var safeRoot = new StackPanel { Spacing = 10 };
        var inlineSearch = new TextBox { PlaceholderText = "Filtrovat dostupné aktualizace…", MinWidth = 260 };
        var inlineRows = new List<(FrameworkElement Element, WingetUpgradeItem Item)>();
        var inlineStatus = new TextBlock { Text = status.Text, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        safeRoot.Children.Add(inlineSearch);
        safeRoot.Children.Add(inlineStatus);
        var safeActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        safeActions.Children.Add(PrimaryQuickButton("Update all", async () =>
        {
            inlineStatus.Text = "Winget aktualizuje všechny podporované aplikace…";
            var result = await WingetUpdateService.UpgradeAllAsync(CancellationToken.None);
            inlineStatus.Text = result.Success ? "Update all dokončen." : "Update all skončil s chybou.";
            ShowActionFeedback("Software Updater", result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed, inlineStatus.Text);
            if (!result.Success) LogService.Error("Winget update all failed: " + result.StdErr + result.StdOut);
        }));
        safeActions.Children.Add(QuickButton("Obnovit", () => ShowSoftwareManagerAsync(CancellationToken.None)));
        safeRoot.Children.Add(safeActions);
        foreach (var item in rows.Select(x => x.Item))
        {
            var row = new StackPanel { Spacing = 3 };
            row.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            row.Children.Add(new TextBlock { Text = item.Id + "  •  " + item.InstalledVersion + " → " + item.AvailableVersion, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
            var rowActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            rowActions.Children.Add(QuickButton("Aktualizovat", async () =>
            {
                inlineStatus.Text = "Aktualizuji " + item.Name + "…";
                var result = await WingetUpdateService.UpgradeAsync(item.Id, CancellationToken.None);
                inlineStatus.Text = result.Success ? item.Name + " aktualizován." : item.Name + " se nepodařilo aktualizovat.";
                ShowActionFeedback(item.Name, result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed, inlineStatus.Text);
                if (!result.Success) LogService.Error("Winget upgrade failed: " + item.Id + " " + result.StdErr + result.StdOut);
            }));
            rowActions.Children.Add(QuickButton("Ignorovat", async () =>
            {
                var result = await WingetUpdateService.PinAsync(item.Id, CancellationToken.None);
                inlineStatus.Text = result.Success ? item.Name + " připnutý — běžný Update all ho přeskočí." : "Winget pin selhal.";
                ShowActionFeedback(item.Name, result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed, inlineStatus.Text);
                if (!result.Success) LogService.Error("Winget pin failed: " + item.Id + " " + result.StdErr + result.StdOut);
            }));
            rowActions.Children.Add(QuickButton("Zrušit ignore", async () =>
            {
                var result = await WingetUpdateService.UnpinAsync(item.Id, CancellationToken.None);
                inlineStatus.Text = result.Success ? item.Name + " už není připnutý." : "Winget unpin selhal.";
                ShowActionFeedback(item.Name, result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed, inlineStatus.Text);
                if (!result.Success) LogService.Error("Winget unpin failed: " + item.Id + " " + result.StdErr + result.StdOut);
            }));
            row.Children.Add(rowActions);
            var card = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = row };
            safeRoot.Children.Add(card);
            inlineRows.Add((card, item));
        }
        inlineSearch.TextChanged += (_, _) =>
        {
            string q = inlineSearch.Text.Trim();
            foreach (var entry in inlineRows)
                entry.Element.Visibility = q.Length == 0 || entry.Item.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || entry.Item.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        var updaterSurface = new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.25 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = safeRoot
        };
        async Task RefreshStaleDisplayAfterRenderAsync()
        {
            try
            {
                await Task.Delay(350, token);
                token.ThrowIfCancellationRequested();
                if (sessionId != 0 && sessionId != Volatile.Read(ref _softwareUpdaterSessionId)) return;
                if (!string.Equals(_section, returnSection, StringComparison.OrdinalIgnoreCase)) return;
                var fresh = await WingetUpdateService.GetUpgradesAsync(token, forceRefresh: true);
                token.ThrowIfCancellationRequested();
                if (sessionId != 0 && sessionId != Volatile.Read(ref _softwareUpdaterSessionId)) return;
                if (!string.Equals(_section, returnSection, StringComparison.OrdinalIgnoreCase)) return;

                string freshSignature = string.Join("\n", fresh
                    .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Id + "|" + x.InstalledVersion + "|" + x.AvailableVersion + "|" + x.Source));
                if (!string.Equals(displayedSignature, freshSignature, StringComparison.Ordinal))
                {
                    LogService.Info("winget", $"WINGET_STALE_REFRESH_CHANGED oldRows={rows.Count} newRows={fresh.Count}");
                    await ShowSoftwareManagerCoreAsync(CancellationToken.None, smokeOnly: false, sessionId, returnSection);
                    return;
                }

                inlineStatus.Text = fresh.Count == 0
                    ? "Všechny Winget aplikace jsou aktuální."
                    : $"Dostupné aktualizace: {fresh.Count}";
                LogService.Info("winget", $"WINGET_STALE_REFRESH_UNCHANGED rows={fresh.Count}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService.Error("winget", "Deferred Winget stale refresh failed", ex); }
        }

        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=surface-created");
        ContentStack.Children.Add(updaterSurface);
        LogService.Info("winget", $"SOFTWARE_UPDATER_INLINE_READY rows={rows.Count}");
        if (refreshStaleAfterRender)
            SafeTaskService.Forget(RefreshStaleDisplayAfterRenderAsync(), "winget", "Deferred stale Winget refresh");
    }

    internal async Task RunSoftwareUpdaterSmokeAsync()
        => await ShowSoftwareManagerCoreAsync(CancellationToken.None, smokeOnly: true);

    internal async Task RunSoftwareUpdaterInlineSmokeAsync()
        => await ShowSoftwareManagerCoreAsync(CancellationToken.None, smokeOnly: false);

    private async Task InstallWingetPackageAsync(string id, TextBlock status, CancellationToken token)
    {
        id = (id ?? string.Empty).Trim();
        if (id.Length == 0) { status.Text = "Zadej Winget package ID."; return; }
        if (!await ShowStyledConfirmAsync("Instalovat aplikaci?", id, "Instalovat", "Zrušit", "Winget")) return;
        status.Text = "Instaluji " + id + "…";
        var result = await SpankyExtensionService.WingetInstallAsync(id, token);
        if (result.Success) WingetUpdateService.InvalidateStatus();
        status.Text = result.Success ? "Instalace dokončena." : "Instalace se nezdařila.";
        if (!result.Success) LogService.Error("Winget install failed: " + id + " " + result.StdErr + result.StdOut);
    }
}
