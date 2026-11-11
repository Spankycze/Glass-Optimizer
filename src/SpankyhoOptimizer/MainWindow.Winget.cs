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
    private readonly SemaphoreSlim _softwareUpdaterMutationGate = new(1, 1);

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
        // Build only the interface users actually see. The previous block
        // constructed a second complete list and toolbar that were never
        // attached to ContentStack, then blocked first paint on a Winget scan
        // if no cached result existed.
        var displaySnapshot = WingetUpdateService.TryGetDisplayUpgrades();
        IReadOnlyList<WingetUpgradeItem> initialItems =
            displaySnapshot?.Items ?? Array.Empty<WingetUpgradeItem>();
        bool refreshStaleAfterRender = displaySnapshot == null ||
                                       displaySnapshot.Value.NeedsRefresh;
        string? displayedSignature = displaySnapshot == null ? null :
            string.Join("\n", initialItems
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Id + "|" + x.InstalledVersion + "|" +
                             x.AvailableVersion + "|" + x.Source));

        if (smokeOnly)
        {
            if (displaySnapshot == null)
                initialItems = await WingetUpdateService.GetUpgradesAsync(token);
            LogService.Info("winget",
                $"SOFTWARE_UPDATER_SMOKE_OK rows={initialItems.Count} cache={displaySnapshot != null}");
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
            refreshStaleAfterRender ? "Kontroluji aktualizace na pozadí…" :
                initialItems.Count == 0 ? "Všechny aplikace jsou aktuální" : $"{initialItems.Count} aktualizací k dispozici",
            headerActions);
        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=hero-created");
        ContentStack.Children.Add(hero);
        ContentStack.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Aktualizace", refreshStaleAfterRender ? "…" : initialItems.Count.ToString(),
                refreshStaleAfterRender ? "Ověřuji aktualizace" : initialItems.Count == 0 ? "Vše aktuální" : "Dostupné upgrady",
                refreshStaleAfterRender ? "SpankyInfoBrush" : initialItems.Count == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric("Zdroj", "Winget", "Microsoft package manager"),
            BuildDashboardMetric("Výběr", "Per-app", "Update selected / all", "SpankySuccessBrush"),
            BuildDashboardMetric("Refresh", refreshStaleAfterRender ? "Ověřuji" : "Fresh", refreshStaleAfterRender ? "Stale cache → background check" : "Aktuální snapshot", refreshStaleAfterRender ? "SpankyWarningBrush" : "SpankySuccessBrush")));
        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=hero-added");
        var safeRoot = new StackPanel { Spacing = 10 };
        var inlineSearch = new TextBox { PlaceholderText = "Filtrovat dostupné aktualizace…", MinWidth = 260 };
        var inlineRows = new List<(FrameworkElement Element, CheckBox Choice, WingetUpgradeItem Item)>();
        var inlineList = new StackPanel { Spacing = 8 };
        Border? updaterSurface = null;
        bool PageActive() => updaterSurface != null &&
            ContentStack.Children.Contains(updaterSurface) &&
            string.Equals(_section, returnSection, StringComparison.OrdinalIgnoreCase) &&
            (sessionId == 0 || sessionId == Volatile.Read(ref _softwareUpdaterSessionId));
        var inlineStatus = new TextBlock
        {
            Text = refreshStaleAfterRender ? "Kontroluji dostupné aktualizace na pozadí…" :
                initialItems.Count == 0 ? "Všechny Winget aplikace jsou aktuální." : $"Dostupné aktualizace: {initialItems.Count}",
            Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap
        };
        safeRoot.Children.Add(inlineSearch);
        safeRoot.Children.Add(inlineStatus);
        var safeActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        safeActions.Children.Add(QuickButton("Vybrat vše", () =>
        {
            foreach (var row in inlineRows) row.Choice.IsChecked = true;
            return Task.CompletedTask;
        }));
        safeActions.Children.Add(QuickButton("Zrušit výběr", () =>
        {
            foreach (var row in inlineRows) row.Choice.IsChecked = false;
            return Task.CompletedTask;
        }));
        safeActions.Children.Add(PrimaryQuickButton("Aktualizovat vybrané", async () =>
        {
            var selected = inlineRows.Where(x => x.Choice.IsChecked == true)
                .Select(x => x.Item).ToArray();
            if (selected.Length == 0)
            {
                inlineStatus.Text = "Vyber alespoň jednu aplikaci.";
                return;
            }
            if (!PageActive()) return;
            if (!await ShowStyledConfirmAsync(
                $"Aktualizovat {selected.Length} aplikací?",
                string.Join(Environment.NewLine, selected.Take(18)
                    .Select(x => "• " + x.Name + " → " + x.AvailableVersion)) +
                "\nAktualizace mohou zavřít běžící aplikace. Neuložená práce může být ztracena.",
                "Aktualizovat", "Zrušit", "Winget") || !PageActive()) return;
            if (!await _softwareUpdaterMutationGate.WaitAsync(0))
            {
                inlineStatus.Text = "Jiná operace aktualizace už probíhá.";
                return;
            }
            try
            {
                int ok = 0;
                var failed = new List<string>();
                foreach (var item in selected)
                {
                    inlineStatus.Text = $"Aktualizuji {item.Name} ({ok + failed.Count + 1}/{selected.Length})…";
                    var result = await WingetUpdateService.UpgradeAsync(item.Id, _windowLifetimeCts.Token);
                    if (result.Success) ok++;
                    else
                    {
                        failed.Add(item.Name);
                        LogService.Error("Winget upgrade failed: " + item.Id + " " + result.StdErr + result.StdOut);
                    }
                }
                if (PageActive())
                {
                    inlineStatus.Text = $"Hotovo: {ok}/{selected.Length}; chyby: {failed.Count}.";
                    if (failed.Count > 0)
                        await ShowStyledInfoAsync("Neúspěšné aktualizace",
                            string.Join(Environment.NewLine, failed));
                }
            }
            finally { _softwareUpdaterMutationGate.Release(); }
            if (PageActive())
                await RefreshVisibleAsync(forceRefresh: true);
        }));
        safeActions.Children.Add(QuickButton("Aktualizovat vše", async () =>
        {
            if (!PageActive()) return;
            if (!await ShowStyledConfirmAsync(
                "Aktualizovat všechny podporované aplikace?",
                "Winget může aktualizovat i právě spuštěné aplikace a některé z nich ukončit. Pokračovat?",
                "Aktualizovat vše", "Zrušit", "Software Updater") || !PageActive()) return;
            if (!await _softwareUpdaterMutationGate.WaitAsync(0))
            {
                inlineStatus.Text = "Jiná operace aktualizace už probíhá.";
                return;
            }
            try
            {
                inlineStatus.Text = "Winget aktualizuje všechny podporované aplikace…";
                var result = await WingetUpdateService.UpgradeAllAsync(_windowLifetimeCts.Token);
                if (PageActive())
                {
                    inlineStatus.Text = result.Success ? "Aktualizace dokončeny." : "Aktualizace skončily s chybou.";
                    ShowActionFeedback("Software Updater",
                        result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                        inlineStatus.Text);
                }
                if (!result.Success)
                    LogService.Error("Winget update all failed: " + result.StdErr + result.StdOut);
            }
            finally { _softwareUpdaterMutationGate.Release(); }
            if (PageActive())
                await RefreshVisibleAsync(forceRefresh: true);
        }));
        safeActions.Children.Add(QuickButton("Obnovit", () => RefreshVisibleAsync(true)));
        safeRoot.Children.Add(safeActions);
        var installToolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var installId = new TextBox { PlaceholderText = "ID balíčku Winget…", MinWidth = 200 };
        installToolbar.Children.Add(installId);
        installToolbar.Children.Add(QuickButton("Instalovat podle ID", async () =>
        {
            if (!PageActive()) return;
            if (!await _softwareUpdaterMutationGate.WaitAsync(0))
            {
                inlineStatus.Text = "Jiná operace Winget právě probíhá.";
                return;
            }
            try
            {
                await InstallWingetPackageAsync(
                    installId.Text, inlineStatus, _windowLifetimeCts.Token, PageActive);
            }
            finally { _softwareUpdaterMutationGate.Release(); }
            if (PageActive())
                await RefreshVisibleAsync(forceRefresh: true);
        }));
        safeRoot.Children.Add(BuildDashboardExpander(
            "Instalace podle ID", "Volitelné ruční instalování aplikací", installToolbar));
        safeRoot.Children.Add(inlineList);
        void RenderUpgradeRows(IReadOnlyList<WingetUpgradeItem> items)
        {
            // Rebuild only changed package rows. Preserve the user's explicit
            // selection and search filter across background Winget refreshes.
            var oldSelection = inlineRows
                .GroupBy(x => x.Item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Choice.IsChecked == true,
                    StringComparer.OrdinalIgnoreCase);
            inlineRows.Clear();
            inlineList.Children.Clear();
            foreach (var item in items)
            {
            var row = new StackPanel { Spacing = 3 };
            var choice = new CheckBox { IsChecked = true, Content = "Vybrat pro aktualizaci" };
            row.Children.Add(choice);
            row.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            row.Children.Add(new TextBlock { Text = item.Id + "  •  " + item.InstalledVersion + " → " + item.AvailableVersion, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
            var rowActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            rowActions.Children.Add(QuickButton("Aktualizovat", async () =>
            {
                if (!PageActive()) return;
                if (!await ShowStyledConfirmAsync(
                    "Aktualizovat " + item.Name + "?",
                    item.InstalledVersion + " → " + item.AvailableVersion +
                    "\nBěžící aplikace může být během aktualizace ukončena.",
                    "Aktualizovat", "Zrušit", "Winget") || !PageActive()) return;
                if (!await _softwareUpdaterMutationGate.WaitAsync(0))
                {
                    inlineStatus.Text = "Jiná aktualizace právě probíhá.";
                    return;
                }
                try
                {
                    inlineStatus.Text = "Aktualizuji " + item.Name + "…";
                    var result = await WingetUpdateService.UpgradeAsync(item.Id, _windowLifetimeCts.Token);
                    if (PageActive())
                    {
                        inlineStatus.Text = result.Success ? item.Name + " aktualizován." : item.Name + " se nepodařilo aktualizovat.";
                        ShowActionFeedback(item.Name,
                            result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                            inlineStatus.Text);
                    }
                    if (!result.Success)
                        LogService.Error("Winget upgrade failed: " + item.Id + " " + result.StdErr + result.StdOut);
                }
                finally { _softwareUpdaterMutationGate.Release(); }
                if (PageActive())
                    await RefreshVisibleAsync(forceRefresh: true);
            }));
            rowActions.Children.Add(QuickButton("Ignorovat", () =>
                ChangePinAsync(item, pin: true)));
            rowActions.Children.Add(QuickButton("Zrušit ignore", () =>
                ChangePinAsync(item, pin: false)));
            row.Children.Add(rowActions);
            var card = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = row };
            inlineList.Children.Add(card);
            inlineRows.Add((card, choice, item));
            if (oldSelection.TryGetValue(item.Id, out bool wasSelected))
                choice.IsChecked = wasSelected;
            }
            ApplySearchFilter();
        }
        void ApplySearchFilter()
        {
            string q = inlineSearch.Text.Trim();
            foreach (var entry in inlineRows)
                entry.Element.Visibility = q.Length == 0 ||
                    entry.Item.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                    entry.Item.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        inlineSearch.TextChanged += (_, _) => ApplySearchFilter();
        RenderUpgradeRows(initialItems);

        async Task RefreshVisibleAsync(bool forceRefresh)
        {
            if (!PageActive()) return;
            if (_softwareUpdaterMutationGate.CurrentCount == 0)
            {
                inlineStatus.Text = "Počkej na dokončení aktualizací aplikací.";
                return;
            }
            inlineStatus.Text = "Kontroluji dostupné aktualizace Winget…";
            IReadOnlyList<WingetUpgradeItem> fresh =
                await WingetUpdateService.GetUpgradesAsync(token, forceRefresh);
            // Do not resurrect the Updater after the user navigated elsewhere.
            if (token.IsCancellationRequested || !PageActive()) return;
            string signature = string.Join("\n", fresh
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Id + "|" + x.InstalledVersion + "|" +
                             x.AvailableVersion + "|" + x.Source));
            if (!string.Equals(displayedSignature, signature, StringComparison.Ordinal))
            {
                int previousCount = inlineRows.Count;
                RenderUpgradeRows(fresh);
                displayedSignature = signature;
                LogService.Info("winget",
                    $"WINGET_VISIBLE_ROWS_REFRESH before={previousCount} after={fresh.Count} " +
                    $"filter={inlineSearch.Text.Length}");
            }
            inlineStatus.Text = fresh.Count == 0
                ? "Všechny Winget aplikace jsou aktuální."
                : $"Dostupné aktualizace: {fresh.Count}";
        }

        async Task ChangePinAsync(WingetUpgradeItem item, bool pin)
        {
            if (!PageActive()) return;
            string verb = pin ? "Ignorovat aktualizaci" : "Zrušit ignorování";
            if (!await ShowStyledConfirmAsync(
                verb + "?",
                item.Name + " (" + item.Id + ")" + Environment.NewLine +
                (pin ? "Balíček bude přeskočen při běžné hromadné aktualizaci." :
                       "Balíček se znovu zahrne do dostupných aktualizací."),
                pin ? "Ignorovat" : "Zrušit ignore", "Zrušit", "Winget") || !PageActive())
                return;
            if (!await _softwareUpdaterMutationGate.WaitAsync(0))
            {
                inlineStatus.Text = "Jiná operace Winget právě probíhá.";
                return;
            }
            try
            {
                var result = pin
                    ? await WingetUpdateService.PinAsync(item.Id, _windowLifetimeCts.Token)
                    : await WingetUpdateService.UnpinAsync(item.Id, _windowLifetimeCts.Token);
                if (!result.Success)
                    LogService.Error("Winget pin change failed: " + item.Id + " " +
                                     result.StdErr + result.StdOut);
                if (!PageActive()) return;
                inlineStatus.Text = result.Success
                    ? item.Name + (pin ? " bude ignorován." : " už nebude ignorován.")
                    : "Winget změnu ignorování nedokončil.";
                ShowActionFeedback(item.Name,
                    result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                    inlineStatus.Text);
            }
            finally { _softwareUpdaterMutationGate.Release(); }
        }

        updaterSurface = new Border
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
                if (token.IsCancellationRequested || !PageActive()) return;
                await RefreshVisibleAsync(forceRefresh: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService.Error("winget", "Deferred Winget stale refresh failed", ex);
                if (PageActive())
                    inlineStatus.Text = "Kontrola aktualizací selhala. Použij Obnovit.";
            }
        }

        // Dedicated read-only CLI smoke calls this core method with sessionId=0.
        // Exercise the same real row renderer without any winget install/update.
        if (sessionId == 0)
        {
            var fixture = new[]
            {
                new WingetUpgradeItem("Smoke Primary", "smoke.primary", "1.0", "2.0", "smoke"),
                new WingetUpgradeItem("Smoke Secondary", "smoke.secondary", "1.0", "2.0", "smoke")
            };
            RenderUpgradeRows(fixture);
            inlineRows[0].Choice.IsChecked = false;
            inlineSearch.Text = "smoke.primary";
            RenderUpgradeRows(new[]
            {
                fixture[0] with { AvailableVersion = "2.1" },
                fixture[1]
            });
            if (inlineRows.Count != 2 ||
                inlineRows[0].Choice.IsChecked != false ||
                inlineRows[0].Element.Visibility != Visibility.Visible ||
                inlineRows[1].Element.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException(
                    "Software Updater lost selection/filter while refreshing package rows.");
            inlineSearch.Text = "";
            RenderUpgradeRows(initialItems);
            LogService.Info("winget",
                "SOFTWARE_UPDATER_SELECTION_REFRESH_SMOKE_OK selection=preserved filter=preserved");
        }

        LogService.Info("winget", "SOFTWARE_UPDATER_INLINE step=surface-created");
        ContentStack.Children.Add(updaterSurface);
        LogService.Info("winget", $"SOFTWARE_UPDATER_INLINE_READY rows={initialItems.Count} deferredScan={refreshStaleAfterRender}");
        if (refreshStaleAfterRender)
            SafeTaskService.Forget(RefreshStaleDisplayAfterRenderAsync(), "winget", "Deferred stale Winget refresh");
    }

    internal async Task RunSoftwareUpdaterSmokeAsync()
        => await ShowSoftwareManagerCoreAsync(_windowLifetimeCts.Token, smokeOnly: true);

    internal async Task RunSoftwareUpdaterInlineSmokeAsync()
        => await ShowSoftwareManagerCoreAsync(_windowLifetimeCts.Token, smokeOnly: false);

    private async Task InstallWingetPackageAsync(
        string id, TextBlock status, CancellationToken token, Func<bool>? stillActive = null)
    {
        id = (id ?? string.Empty).Trim();
        if (id.Length == 0) { status.Text = "Zadej Winget package ID."; return; }
        if (!await ShowStyledConfirmAsync("Instalovat aplikaci?", id, "Instalovat", "Zrušit", "Winget") ||
            (stillActive != null && !stillActive())) return;
        status.Text = "Instaluji " + id + "…";
        var result = await SpankyExtensionService.WingetInstallAsync(id, token);
        if (result.Success) WingetUpdateService.InvalidateStatus();
        status.Text = result.Success ? "Instalace dokončena." : "Instalace se nezdařila.";
        if (!result.Success) LogService.Error("Winget install failed: " + id + " " + result.StdErr + result.StdOut);
    }
}
