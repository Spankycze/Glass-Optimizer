using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void BuildWindowsModernizerUiPanel(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var perf = Stopwatch.StartNew();
        var host = new StackPanel { Spacing = 8 };
        var status = new TextBlock
        {
            Text = "Načítám stav Windows UI…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };

        static string FormatUiState(WindowsModernizerSnapshot current) =>
            $"Vzhled Windows={(current.UiBaselineConfigured ? "ON" : "OFF")} • Vyhledávání={(current.SearchCleanupConfigured ? "ON" : "OFF")} • Dialogy souborů={(current.FileDialogProfileConfigured ? "ON" : "OFF")}";

        async Task RefreshUiStateAsync()
        {
            // Controls are cached with the Windows UI page. Resolve a fresh
            // navigation token on every click instead of retaining the token
            // that was canceled when this page first went off-screen.
            CancellationToken refreshToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
            int refreshGeneration = _navigationGeneration;
            try
            {
                WindowsModernizerSnapshot current = await Task.Run(WindowsModernizerService.ReadSnapshot, refreshToken).ConfigureAwait(false);
                if (refreshToken.IsCancellationRequested ||
                    refreshGeneration != _navigationGeneration ||
                    _section != "ui") return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (!refreshToken.IsCancellationRequested &&
                        refreshGeneration == _navigationGeneration &&
                        _section == "ui")
                    {
                        status.Text = FormatUiState(current);
                        status.Foreground = TextSecondaryBrush();
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (refreshToken.IsCancellationRequested) return;
                LogService.Error("ui", "Windows Modernizer UI state read failed", ex);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (refreshToken.IsCancellationRequested ||
                        refreshGeneration != _navigationGeneration ||
                        _section != "ui") return;
                    status.Text = "Stav Windows UI se nepodařilo načíst: " + ex.Message;
                    status.Foreground = WarningBrush();
                });
            }
        }

        SafeTaskService.Forget(RefreshUiStateAsync(), "ui", "Windows Modernizer UI state");

        AdaptiveScanReport? adaptiveReport = null;
        var adaptiveStatus = new TextBlock
        {
            Text = "Ještě nebyla provedena kompletní kontrola systému.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var adaptiveFindings = new StackPanel { Spacing = 4 };
        bool adaptiveBusy = false;
        var adaptiveMeta = new TextBlock
        {
            Text = "Kontrola poběží až po stisknutí „Najít doporučení“.",
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            Opacity = .78,
            TextWrapping = TextWrapping.Wrap
        };
        Button? applyAdaptiveButton = null;
        string adaptiveFilter = "recommended";
        Button? filterRecommended = null;
        Button? filterManual = null;
        Button? filterAll = null;
        var filterActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            Visibility = Visibility.Collapsed };

        string StateText(AdaptiveTweakState value) => value switch
        {
            AdaptiveTweakState.Needed => "POTŘEBA",
            AdaptiveTweakState.AlreadyOptimal => "UŽ OK",
            AdaptiveTweakState.NotApplicable => "N/A",
            _ => "RUČNÍ KONTROLA"
        };
        string ImpactText(AdaptiveImpact value) => value switch
        {
            AdaptiveImpact.High => "HIGH",
            AdaptiveImpact.Medium => "MEDIUM",
            _ => "LOW"
        };
        int StateOrder(AdaptiveTweakState value) => value switch
        {
            AdaptiveTweakState.Needed => 0,
            AdaptiveTweakState.ManualReview => 1,
            AdaptiveTweakState.AlreadyOptimal => 2,
            _ => 3
        };
        async Task<AdaptiveScanReport> RunAdaptiveScanAsync(bool forceRefresh, CancellationToken scanToken)
        {
            var sw = Stopwatch.StartNew();
            AdaptiveScanReport report = await WindowsModernizerService.ScanAdaptiveSystemAsync(scanToken, forceRefresh);
            scanToken.ThrowIfCancellationRequested();
            sw.Stop();
            string areaSummary = string.Join(" • ", report.Findings
                .Where(x => x.State == AdaptiveTweakState.Needed)
                .GroupBy(x => x.Area)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(x => $"{x.Key} {x.Count()}"));
            adaptiveMeta.Text = $"Poslední kontrola {DateTime.Now:HH:mm:ss} • {sw.ElapsedMilliseconds} ms" +
                (string.IsNullOrWhiteSpace(areaSummary) ? " • bez bezpečných doporučení" : " • " + areaSummary);
            return report;
        }
        void RefreshFilterButtons()
        {
            if (filterRecommended != null) filterRecommended.Content = adaptiveFilter == "recommended" ? "✓ Doporučené" : "Doporučené";
            if (filterManual != null) filterManual.Content = adaptiveFilter == "manual" ? "✓ Ruční kontrola" : "Ruční kontrola";
            if (filterAll != null) filterAll.Content = adaptiveFilter == "all" ? "✓ Vše" : "Vše";
        }

        void RenderAdaptiveReport(AdaptiveScanReport report)
        {
            adaptiveFindings.Children.Clear();
            IEnumerable<AdaptiveTweakFinding> visible = adaptiveFilter switch
            {
                "manual" => report.Findings.Where(x => x.State == AdaptiveTweakState.ManualReview),
                "all" => report.Findings,
                _ => report.Findings.Where(x => x.State == AdaptiveTweakState.Needed)
            };
            AdaptiveTweakFinding[] visibleItems = visible
                .OrderBy(x => StateOrder(x.State))
                .ThenByDescending(x => x.Impact)
                .ThenBy(x => x.Area, StringComparer.OrdinalIgnoreCase)
                .Take(adaptiveFilter == "all" ? 24 : 16)
                .ToArray();
            adaptiveStatus.Text = $"{report.Total} nalezeno • {report.Needed} doporučeno • {report.AlreadyOptimal} už OK • {report.ManualReview} ruční kontrola • {report.NotApplicable} N/A • zobrazeno {visibleItems.Length}";
            adaptiveStatus.Foreground = TextSecondaryBrush();
            RefreshFilterButtons();
            filterActions.Visibility = Visibility.Visible;
            if (visibleItems.Length == 0)
            {
                adaptiveFindings.Children.Add(new TextBlock
                {
                    Text = adaptiveFilter == "recommended"
                        ? "Žádné bezpečné automatické doporučení. Pro další nálezy otevři Ruční kontrola."
                        : "V tomto filtru nejsou žádné položky.",
                    FontSize = 11.5,
                    Foreground = TextSecondaryBrush(),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 2)
                });
            }
            foreach (var group in visibleItems.GroupBy(x => x.Area))
            {
                adaptiveFindings.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 13.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = TextPrimaryBrush(),
                    Margin = new Thickness(0, 6, 0, 1)
                });
                foreach (AdaptiveTweakFinding finding in group
                    .OrderBy(x => StateOrder(x.State))
                    .ThenByDescending(x => x.Impact)
                    .Take(20))
                {
                    adaptiveFindings.Children.Add(new TextBlock
                    {
                        Text = $"[{StateText(finding.State)} • {ImpactText(finding.Impact)}] {finding.Title} — {finding.Detail}",
                        FontSize = finding.State == AdaptiveTweakState.AlreadyOptimal ? 10.8 : 11.5,
                        Opacity = finding.State == AdaptiveTweakState.AlreadyOptimal ? .68 : 1,
                        Foreground = finding.State == AdaptiveTweakState.Needed ? UiBrush("SpankyAccentBrush") : TextSecondaryBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }
            if (applyAdaptiveButton != null)
                applyAdaptiveButton.IsEnabled = !adaptiveBusy && report.Findings.Any(x => x.SafeAutoApply && x.State == AdaptiveTweakState.Needed);
        }

        var adaptiveActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button? scanAdaptiveButton = null;
        scanAdaptiveButton = PrimaryQuickButton("Najít doporučení", async () =>
        {
            if (adaptiveBusy) return;
            // Cached UI panels survive navigation. Use the CURRENT navigation's
            // cancellation and generation, not those captured at initial build.
            CancellationToken scanToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
            int scanGeneration = _navigationGeneration;
            adaptiveBusy = true;
            if (scanAdaptiveButton != null) scanAdaptiveButton.IsEnabled = false;
            if (applyAdaptiveButton != null) applyAdaptiveButton.IsEnabled = false;
            try
            {
                adaptiveStatus.Text = "Kontroluji Průzkumník, Vyhledávání, kontextové menu, Po spuštění, úlohy, služby, síť, úložiště a čištění…";
                adaptiveReport = await RunAdaptiveScanAsync(forceRefresh: true, scanToken);
                if (scanToken.IsCancellationRequested || scanGeneration != _navigationGeneration ||
                    _section != "ui" || host.Parent == null) return;
                RenderAdaptiveReport(adaptiveReport);
                ShowActionFeedback("Adaptivní kontrola Windows", ActionFeedbackKind.Info, adaptiveStatus.Text);
            }
            catch (OperationCanceledException) when (scanToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (scanGeneration == _navigationGeneration && _section == "ui")
                {
                    adaptiveStatus.Text = ex.Message;
                    ShowActionFeedback("Adaptivní kontrola Windows", ActionFeedbackKind.Failed, ex.Message);
                }
            }
            finally
            {
                adaptiveBusy = false;
                if (scanAdaptiveButton != null) scanAdaptiveButton.IsEnabled = true;
                if (applyAdaptiveButton != null && adaptiveReport != null)
                    applyAdaptiveButton.IsEnabled = adaptiveReport.Findings.Any(x => x.SafeAutoApply && x.State == AdaptiveTweakState.Needed);
            }
        });
        adaptiveActions.Children.Add(scanAdaptiveButton);

        applyAdaptiveButton = QuickButton("Použít bezpečné návrhy", async () =>
        {
            if (adaptiveReport == null || adaptiveBusy) return;
            // Mutations are explicit and remain transactional after navigation;
            // UI updates target only the generation that initiated the action.
            int applyGeneration = _navigationGeneration;
            adaptiveBusy = true;
            if (applyAdaptiveButton != null) applyAdaptiveButton.IsEnabled = false;
            if (scanAdaptiveButton != null) scanAdaptiveButton.IsEnabled = false;
            try
            {
                if (!await ShowStyledConfirmAsync(
                    "Použít bezpečné návrhy Windows",
                    "Použít všechny nalezené bezpečné návrhy označené POTŘEBA? Změny nastavení Windows mohou vyžadovat obnovení Průzkumníku.",
                    "Použít", "Zrušit", "Windows UI")) return;
                adaptiveStatus.Text = "Ověřuji aktuální bezpečná doporučení Windows…";
                // The report may be minutes old after cached navigation.
                // Recheck actual Windows state before any modification.
                AdaptiveScanReport currentReport = await RunAdaptiveScanAsync(
                    forceRefresh: true, _windowLifetimeCts.Token);
                if (applyGeneration != _navigationGeneration || _section != "ui") return;
                adaptiveStatus.Text = "Aplikuji jen bezpečné položky označené POTŘEBA…";
                var beforeNeeded = currentReport.Findings
                    .Where(x => x.State == AdaptiveTweakState.Needed && x.SafeAutoApply)
                    .ToDictionary(x => x.Id, x => x.Title, StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<string> applied = await WindowsModernizerService.ApplyAdaptiveRecommendedAsync(currentReport, _windowLifetimeCts.Token);
                StartupManagerService.InvalidateCache(invalidateTaskScheduler: true);
                ServiceCatalogService.InvalidateCache();
                TaskSchedulerService.InvalidateCache();
                SystemInsightService.InvalidateNetwork();
                WindowsModernizerService.InvalidateAdaptiveScanCache();
                // Navigation may have switched while Windows changes finished;
                // never refresh a detached page or show feedback for an old one.
                if (applyGeneration != _navigationGeneration || _section != "ui") return;
                CancellationToken verifyToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                adaptiveReport = await RunAdaptiveScanAsync(forceRefresh: true, verifyToken);
                if (verifyToken.IsCancellationRequested || applyGeneration != _navigationGeneration ||
                    _section != "ui" || host.Parent == null) return;
                RenderAdaptiveReport(adaptiveReport);
                var stillNeeded = adaptiveReport.Findings
                    .Where(x => x.State == AdaptiveTweakState.Needed)
                    .Select(x => x.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                string[] resolved = beforeNeeded
                    .Where(x => !stillNeeded.Contains(x.Key))
                    .Select(x => x.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string detail = applied.Count == 0
                    ? "Nebyla potřeba žádná bezpečná automatická změna."
                    : $"Aplikováno {applied.Count} • ověřeno {resolved.Length}: {string.Join(", ", resolved.Take(10))}";
                ShowActionFeedback("Adaptivní kontrola Windows", ActionFeedbackKind.Verified, detail);
            }
            catch (OperationCanceledException) when (_loadCts?.IsCancellationRequested == true || _windowLifetimeCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (applyGeneration == _navigationGeneration && _section == "ui")
                {
                    adaptiveStatus.Text = ex.Message;
                    ShowActionFeedback("Adaptivní kontrola Windows", ActionFeedbackKind.Failed, ex.Message);
                }
            }
            finally
            {
                adaptiveBusy = false;
                if (scanAdaptiveButton != null) scanAdaptiveButton.IsEnabled = true;
                if (applyAdaptiveButton != null && adaptiveReport != null)
                    applyAdaptiveButton.IsEnabled = adaptiveReport.Findings.Any(x => x.SafeAutoApply && x.State == AdaptiveTweakState.Needed);
            }
        });
        applyAdaptiveButton.IsEnabled = false;
        adaptiveActions.Children.Add(applyAdaptiveButton);

        filterRecommended = QuickButton("Doporučené", () =>
        {
            adaptiveFilter = "recommended";
            if (adaptiveReport != null) RenderAdaptiveReport(adaptiveReport);
            return Task.CompletedTask;
        });
        filterManual = QuickButton("Ruční kontrola", () =>
        {
            adaptiveFilter = "manual";
            if (adaptiveReport != null) RenderAdaptiveReport(adaptiveReport);
            return Task.CompletedTask;
        });
        filterAll = QuickButton("Vše", () =>
        {
            adaptiveFilter = "all";
            if (adaptiveReport != null) RenderAdaptiveReport(adaptiveReport);
            return Task.CompletedTask;
        });
        filterActions.Children.Add(filterRecommended);
        filterActions.Children.Add(filterManual);
        filterActions.Children.Add(filterAll);

        var adaptivePanel = new StackPanel { Spacing = 8 };
        adaptivePanel.Children.Add(adaptiveActions);
        adaptivePanel.Children.Add(filterActions);
        adaptivePanel.Children.Add(adaptiveStatus);
        adaptivePanel.Children.Add(adaptiveMeta);
        adaptivePanel.Children.Add(adaptiveFindings);
        // Keep the primary Windows controls visible and uncluttered. Users can
        // explicitly expand this section and request a bounded, read-only scan;
        // Windows mutations still require a second explicit button press.
        var adaptiveExpander = BuildDashboardExpander(
            "Další doporučení Windows",
            "Volitelná kontrola systému • žádné automatické změny",
            adaptivePanel);
        long adaptiveShellMs = perf.ElapsedMilliseconds;

        Func<Border>[] actionCardFactories =
        {
            () => ActionCard(
                "Výchozí vzhled Windows / Průzkumníku",
                "Tento počítač • přípony • méně doporučení • Vyhledávání bez webového/cloud obsahu • Přehled úloh/Widgety/Chat vypnuté • Snap/Ukončit úlohu • náhledy • 100 ms hover • bez accentního okraje aktivních oken.",
                "Použít",
                async () =>
                {
                    try
                    {
                        await WindowsModernizerService.ApplyUiBaselineAsync(_windowLifetimeCts.Token);
                        await RefreshUiStateAsync();
                        ShowActionFeedback("Windows UI", ActionFeedbackKind.Verified, status.Text);
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                        ShowActionFeedback("Windows UI", ActionFeedbackKind.Failed, ex.Message);
                    }
                }),
            () => ActionCard(
                "Obnovit výchozí vzhled Windows",
                "Vrátí přesné původní registry hodnoty uložené před první aplikací tohoto profilu.",
                "Obnovit",
                async () =>
                {
                    try
                    {
                        await WindowsModernizerService.RestoreUiBaselineAsync(_windowLifetimeCts.Token);
                        await RefreshUiStateAsync();
                        ShowActionFeedback("Windows UI", ActionFeedbackKind.Verified, status.Text);
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                        ShowActionFeedback("Windows UI", ActionFeedbackKind.Failed, ex.Message);
                    }
                }),
            () => ActionCard(
                "Dialogy souborů — rychlá místa",
                "Nastaví Plochu, Stažené soubory, Projekty, Dokumenty a Tento počítač pro klasické dialogy Otevřít/Uložit.",
                "Použít",
                async () =>
                {
                    try
                    {
                        string s = WindowsModernizerService.ApplyFileDialogProfile();
                        await RefreshUiStateAsync();
                        ShowActionFeedback("Dialogy souborů", ActionFeedbackKind.Verified, s);
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                        ShowActionFeedback("Dialogy souborů", ActionFeedbackKind.Failed, ex.Message);
                    }
                    await Task.CompletedTask;
                }),
            () => ActionCard(
                "Vyčistit historii Otevřít / Uložit",
                "Smaže jen historii naposledy navštívených cest klasických dialogů, ne soubory.",
                "Vyčistit",
                async () =>
                {
                    if (!await ShowStyledConfirmAsync(
                            "Historie Otevřít / Uložit",
                            "Vyčistit historii klasických dialogů Otevřít/Uložit?",
                            "Vyčistit", "Zrušit", "UI / Explorer"))
                        return;
                    WindowsModernizerService.ClearFileDialogHistory();
                    status.Text = "Historie dialogů Otevřít/Uložit byla vyčištěna a ověřena.";
                    ShowActionFeedback("Dialogy souborů", ActionFeedbackKind.Verified, status.Text);
                }),
            () => ActionCard(
                "Kontrola rozšíření Průzkumníku",
                "Najde registrovaná rozšíření kontextového menu, jejichž DLL už na disku neexistuje.",
                "Kontrola",
                async () =>
                {
                    try
                    {
                        int stale = await Task.Run(WindowsModernizerService.CountStaleShellHandlers);
                        status.Text = stale == 0
                            ? "Kontrola rozšíření: žádná neplatná položka."
                            : $"Kontrola rozšíření: {stale} neplatných nebo chybějících položek.";
                        ShowActionFeedback("Kontrola rozšíření", ActionFeedbackKind.Info, status.Text);
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                        ShowActionFeedback("Kontrola rozšíření", ActionFeedbackKind.Failed, ex.Message);
                    }
                })
        };

        long maxActionCardMs = 0;
        void QueueNextActionCard(int index)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "ui") return;
                if (index >= actionCardFactories.Length)
                {
                    LogService.Info("performance", $"WINDOWS_MODERNIZER_ACTIONS_DEFERRED count={host.Children.Count} maxCardMs={maxActionCardMs}");
                    return;
                }

                var cardSw = Stopwatch.StartNew();
                host.Children.Add(actionCardFactories[index]());
                maxActionCardMs = System.Math.Max(maxActionCardMs, cardSw.ElapsedMilliseconds);
                QueueNextActionCard(index + 1);
            });
        }

        long actionCardsMs = perf.ElapsedMilliseconds - adaptiveShellMs;
        host.Children.Add(status);
        host.Children.Add(adaptiveExpander);
        QueueNextActionCard(0);
        var modernizerSurface = BuildSectionSurface(
            "ui",
            "Průzkumník, dialogy a doporučení",
            "Volitelné nástroje Windows bez automatických změn",
            BuildDashboardExpander(
                "Další nástroje Windows",
                "Průzkumník • rychlá místa dialogů • kontrola rozšíření • doporučení",
                host));
        modernizerSurface.Tag = "windows-modernizer-ui";
        ContentStack.Children.Add(modernizerSurface);
        long totalMs = perf.ElapsedMilliseconds;
        LogService.Info("performance", $"WINDOWS_MODERNIZER_UI_PHASE adaptiveShellMs={adaptiveShellMs} actionCardsMs={actionCardsMs} surfaceMs={totalMs - adaptiveShellMs - actionCardsMs} totalMs={totalMs}");
    }

    private void BuildWindowsModernizerStartupPanel(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var host = new StackPanel { Spacing = 8 };
        var status = new TextBlock
        {
            Text = "Načítám stav řízeného spuštění…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        void RefreshStartupState()
        {
            WindowsModernizerSnapshot current = WindowsModernizerService.ReadSnapshot();
            status.Text = $"Zpoždění Startu={(current.StartupDelayOff ? "0 ms" : "Výchozí")} • Battle.net odklad={(current.BattleNetDelayed ? "ON" : "OFF")}";
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Použít řízené spuštění", async () =>
        {
            try
            {
                await WindowsModernizerService.ApplyStartupProfileAsync(_windowLifetimeCts.Token);
                StartupManagerService.InvalidateCache(invalidateTaskScheduler: true);
                RefreshStartupState();
                WindowsModernizerSnapshot verified = WindowsModernizerService.ReadSnapshot();
                if (!verified.StartupDelayOff || !verified.BattleNetDelayed)
                    throw new InvalidOperationException("Ověřený stav řízeného spuštění neodpovídá cíli (StartupDelay/Battle.net).");
                ShowActionFeedback("Řízené spuštění", ActionFeedbackKind.Verified, status.Text);
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                ShowActionFeedback("Řízené spuštění", ActionFeedbackKind.Failed, ex.Message);
            }
        }));
        actions.Children.Add(QuickButton("Obnovit původní", async () =>
        {
            try
            {
                await WindowsModernizerService.RestoreStartupProfileAsync(_windowLifetimeCts.Token);
                StartupManagerService.InvalidateCache(invalidateTaskScheduler: true);
                RefreshStartupState();
                ShowActionFeedback("Řízené spuštění", ActionFeedbackKind.Verified, status.Text);
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                ShowActionFeedback("Řízené spuštění", ActionFeedbackKind.Failed, ex.Message);
            }
        }));
        host.Children.Add(actions);
        host.Children.Add(ActionCard(
            "60s kontrola po přihlášení",
            "Změří po 60 sekund CPU a private RAM procesů a ukáže nejaktivnější položky bez změn systému.",
            "Spustit kontrolu",
            async () =>
            {
                try
                {
                    status.Text = "Probíhá 60s kontrola po přihlášení…";
                    status.Text = await WindowsModernizerService.RunPostLoginAuditAsync(60, _windowLifetimeCts.Token);
                    ShowActionFeedback("Kontrola po přihlášení", ActionFeedbackKind.Info, status.Text);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    ShowActionFeedback("Kontrola po přihlášení", ActionFeedbackKind.Failed, ex.Message);
                }
            }));
        host.Children.Add(status);
        ContentStack.Children.Add(BuildSectionSurface(
            "startup",
            "Řízené spuštění",
            "Battle.net 20 s • Spotify 35 s • bez zpoždění Startu • vratné změny • kontrola po přihlášení",
            host));

        SafeTaskService.Forget(Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            WindowsModernizerSnapshot state = await Task.Run(
                WindowsModernizerService.ReadSnapshot,
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            LogService.Info("performance", $"STARTUP_MODERNIZER_DEFERRED_READ ms={sw.ElapsedMilliseconds}");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "startup" ||
                    host.Parent == null) return;
                status.Text = $"Zpoždění Startu={(state.StartupDelayOff ? "0 ms" : "Výchozí")} • Battle.net odklad={(state.BattleNetDelayed ? "ON" : "OFF")}";
            });
        }, token), "startup", "Startup modernizer state");
    }

    private void BuildWindowsModernizerVendorPanel(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var host = new StackPanel { Spacing = 8 };
        var status = new TextBlock
        {
            Text = "Načítám stav služeb výrobců a aktualizátorů…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        WindowsModernizerSnapshot RefreshVendorState()
        {
            WindowsModernizerSnapshot current = WindowsModernizerService.ReadSnapshot();
            status.Text = $"Služby výrobců ručně: {current.VendorServicesTuned}/{current.VendorServicesPresent} • Armoury Download Tool={(current.ArmouryDownloadPresent ? (current.ArmouryDownloadDisabled ? "Vypnuto" : "Aktivní / ručně") : "není nainstalovaný")}";
            return current;
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Optimalizovat služby výrobců", async () =>
        {
            try
            {
                await WindowsModernizerService.ApplyVendorIdleAsync(_windowLifetimeCts.Token);
                ServiceCatalogService.InvalidateCache();
                TaskSchedulerService.InvalidateCache();
                WindowsModernizerSnapshot current = RefreshVendorState();
                if (current.VendorServicesTuned != current.VendorServicesPresent ||
                    (current.ArmouryDownloadPresent && !current.ArmouryDownloadDisabled))
                    throw new InvalidOperationException("Ověřený stav služeb výrobců neodpovídá cílovému nastavení.");
                ShowActionFeedback("Služby výrobců", ActionFeedbackKind.Verified, status.Text);
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                ShowActionFeedback("Služby výrobců", ActionFeedbackKind.Failed, ex.Message);
            }
        }));
        actions.Children.Add(QuickButton("Obnovit původní", async () =>
        {
            try
            {
                await WindowsModernizerService.RestoreVendorIdleAsync(_windowLifetimeCts.Token);
                ServiceCatalogService.InvalidateCache();
                TaskSchedulerService.InvalidateCache();
                RefreshVendorState();
                ShowActionFeedback("Služby výrobců", ActionFeedbackKind.Verified, status.Text);
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                ShowActionFeedback("Služby výrobců", ActionFeedbackKind.Failed, ex.Message);
            }
        }));
        host.Children.Add(actions);
        host.Children.Add(status);
        ContentStack.Children.Add(BuildSectionSurface(
            "components",
            "Služby výrobců / aktualizátory",
            "ASUS • Armoury Crate • Google aktualizátor • neplatná ASUS úloha",
            host));

        SafeTaskService.Forget(Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            WindowsModernizerSnapshot state = WindowsModernizerService.ReadSnapshot();
            token.ThrowIfCancellationRequested();
            LogService.Info("performance", $"VENDOR_PANEL_DEFERRED_READ ms={sw.ElapsedMilliseconds}");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "components" ||
                    host.Parent == null) return;
                status.Text = $"Služby výrobců ručně: {state.VendorServicesTuned}/{state.VendorServicesPresent} • Armoury Download Tool={(state.ArmouryDownloadPresent ? (state.ArmouryDownloadDisabled ? "Vypnuto" : "Aktivní / ručně") : "není nainstalovaný")}";
            });
        }, token), "components", "Stav služeb výrobců");
    }

    private void BuildWindowsModernizerStoragePanel(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var host = new StackPanel { Spacing = 8 };
        var status = new TextBlock
        {
            Text = "Analýza a Retrim běží jen na dostupných pevných discích.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        host.Children.Add(actions);
        host.Children.Add(status);
        ContentStack.Children.Add(BuildSectionSurface(
            "storage",
            "Údržba NVMe / SSD",
            "Analýza • Windows Retrim • bez vypínání ochrany zápisové cache",
            host));

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "storage" ||
                host.Parent == null ||
                actions.Children.Count > 0) return;
            var deferred = System.Diagnostics.Stopwatch.StartNew();
            actions.Children.Add(QuickButton("Analyzovat disky", async () =>
            {
                try
                {
                    status.Text = "Analyzuji pevné disky…";
                    status.Text = await WindowsModernizerService.AnalyzeFixedDrivesAsync(_windowLifetimeCts.Token);
                    ShowActionFeedback("Analýza úložiště", ActionFeedbackKind.Info, status.Text);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    ShowActionFeedback("Analýza úložiště", ActionFeedbackKind.Failed, ex.Message);
                }
            }));
            actions.Children.Add(PrimaryQuickButton("Retrim SSD/NVMe", async () =>
            {
                if (!await ShowStyledConfirmAsync(
                        "Retrim SSD/NVMe",
                        "Spustit Windows retrim na všech dostupných fixed drives? Nejde o klasickou defragmentaci SSD.",
                        "Retrim", "Zrušit", "Storage"))
                    return;
                try
                {
                    status.Text = "Probíhá retrim…";
                    status.Text = await WindowsModernizerService.RetrimFixedDrivesAsync(_windowLifetimeCts.Token);
                    SystemInsightService.InvalidateStorage(
                        preserveStableDriverMetadata: true,
                        invalidateTemperatures: false,
                        invalidateTrim: true);
                    ShowActionFeedback("Retrim", ActionFeedbackKind.Applied, status.Text);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    ShowActionFeedback("Retrim", ActionFeedbackKind.Failed, ex.Message);
                }
            }));
            LogService.Info("performance", $"STORAGE_MODERNIZER_ACTIONS_DEFERRED ms={deferred.ElapsedMilliseconds} buttons={actions.Children.Count}");
        });
    }

}
