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
            try
            {
                WindowsModernizerSnapshot current = await Task.Run(WindowsModernizerService.ReadSnapshot, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _section != "ui") return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (!token.IsCancellationRequested && _section == "ui")
                    {
                        status.Text = FormatUiState(current);
                        status.Foreground = TextSecondaryBrush();
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                LogService.Error("ui", "Windows Modernizer UI state read failed", ex);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_section != "ui" || token.IsCancellationRequested) return;
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
        var adaptiveMeta = new TextBlock
        {
            Text = "Automatická kontrola čeká na dokončení načtení…",
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
        async Task<AdaptiveScanReport> RunAdaptiveScanAsync(bool forceRefresh)
        {
            var sw = Stopwatch.StartNew();
            AdaptiveScanReport report = await WindowsModernizerService.ScanAdaptiveSystemAsync(CancellationToken.None, forceRefresh);
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
                .ToArray();
            adaptiveStatus.Text = $"{report.Total} nalezeno • {report.Needed} doporučeno • {report.AlreadyOptimal} už OK • {report.ManualReview} ruční kontrola • {report.NotApplicable} N/A • zobrazeno {visibleItems.Length}";
            adaptiveStatus.Foreground = TextSecondaryBrush();
            RefreshFilterButtons();
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
                applyAdaptiveButton.IsEnabled = report.Findings.Any(x => x.SafeAutoApply && x.State == AdaptiveTweakState.Needed);
        }

        var adaptiveActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        adaptiveActions.Children.Add(PrimaryQuickButton("Proskenovat celý Windows", async () =>
        {
            try
            {
                adaptiveStatus.Text = "Kontroluji Průzkumník, Vyhledávání, kontextové menu, Po spuštění, úlohy, služby, síť, úložiště, Firefox a čištění…";
                adaptiveReport = await RunAdaptiveScanAsync(forceRefresh: true);
                RenderAdaptiveReport(adaptiveReport);
                ShowActionFeedback("Adaptivní kontrola Windows", ActionFeedbackKind.Verified, adaptiveStatus.Text);
            }
            catch (Exception ex)
            {
                adaptiveStatus.Text = ex.Message;
                ShowActionFeedback("Adaptivní kontrola Windows", ActionFeedbackKind.Failed, ex.Message);
            }
        }));

        applyAdaptiveButton = QuickButton("Použít doporučené", async () =>
        {
            if (adaptiveReport == null) return;
            try
            {
                adaptiveStatus.Text = "Aplikuji jen bezpečné položky označené POTŘEBA…";
                var beforeNeeded = adaptiveReport.Findings
                    .Where(x => x.State == AdaptiveTweakState.Needed && x.SafeAutoApply)
                    .ToDictionary(x => x.Id, x => x.Title, StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<string> applied = await WindowsModernizerService.ApplyAdaptiveRecommendedAsync(adaptiveReport, CancellationToken.None);
                StartupManagerService.InvalidateCache(invalidateTaskScheduler: true);
                ServiceCatalogService.InvalidateCache();
                TaskSchedulerService.InvalidateCache();
                SystemInsightService.InvalidateNetwork();
                WindowsModernizerService.InvalidateAdaptiveScanCache();
                adaptiveReport = await RunAdaptiveScanAsync(forceRefresh: true);
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
            catch (Exception ex)
            {
                adaptiveStatus.Text = ex.Message;
                ShowActionFeedback("Adaptivní kontrola Windows", ActionFeedbackKind.Failed, ex.Message);
            }
        });
        applyAdaptiveButton.IsEnabled = false;
        adaptiveActions.Children.Add(applyAdaptiveButton);

        var filterActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
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
        // Detailed adaptive diagnostics stay backend-only. They are intentionally not rendered
        // in the normal Windows UI page; the page should contain controls, not an audit dump.
        _ = adaptivePanel;
        long adaptiveShellMs = perf.ElapsedMilliseconds;

        async Task RunAdaptiveAutoScanAsync()
        {
            try
            {
                await Task.Delay(350, token);
                if (!string.Equals(_section, "ui", StringComparison.OrdinalIgnoreCase)) return;
                adaptiveStatus.Text = "Automatická kontrola systému…";
                adaptiveReport = await RunAdaptiveScanAsync(forceRefresh: false);
                if (!string.Equals(_section, "ui", StringComparison.OrdinalIgnoreCase)) return;
                RenderAdaptiveReport(adaptiveReport);
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                adaptiveStatus.Text = "Automatická kontrola systému selhala.";
                adaptiveStatus.Foreground = WarningBrush();
                adaptiveMeta.Text = "Chyba: " + ex.Message + " • použij „Proskenovat celý Windows“ pro opakování.";
            }
        }

        // No automatic adaptive scan in the foreground UI. Keep this expensive audit on demand
        // so the Windows UI page stays clean and does not show internal diagnostic findings.
        _ = (Func<Task>)RunAdaptiveAutoScanAsync;

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
                        await WindowsModernizerService.ApplyUiBaselineAsync(CancellationToken.None);
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
                        await WindowsModernizerService.RestoreUiBaselineAsync(CancellationToken.None);
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
                    status.Text = "Historie dialogů Otevřít/Uložit byla vyčištěna.";
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
                        ShowActionFeedback("Kontrola rozšíření",
                            stale == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Applied,
                            status.Text);
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
                if (token.IsCancellationRequested || _section != "ui") return;
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
        QueueNextActionCard(0);
        ContentStack.Children.Add(BuildSectionSurface(
            "ui",
            "Práce ve Windows / Dialogy souborů",
            "Přenesené WindowsModernizer úpravy bez externího launcheru",
            host));
        long totalMs = perf.ElapsedMilliseconds;
        LogService.Info("performance", $"WINDOWS_MODERNIZER_UI_PHASE adaptiveShellMs={adaptiveShellMs} actionCardsMs={actionCardsMs} surfaceMs={totalMs - adaptiveShellMs - actionCardsMs} totalMs={totalMs}");
    }

    private void BuildWindowsModernizerStartupPanel(CancellationToken token)
    {
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
                await WindowsModernizerService.ApplyStartupProfileAsync(CancellationToken.None);
                StartupManagerService.InvalidateCache(invalidateTaskScheduler: true);
                RefreshStartupState();
                if (!WindowsModernizerService.ReadSnapshot().StartupDelayOff)
                    throw new InvalidOperationException("Ověřený stav zpoždění Startu neodpovídá cílovému nastavení.");
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
                await WindowsModernizerService.RestoreStartupProfileAsync(CancellationToken.None);
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
                    status.Text = await WindowsModernizerService.RunPostLoginAuditAsync(60, CancellationToken.None);
                    ShowActionFeedback("Kontrola po přihlášení", ActionFeedbackKind.Verified, status.Text);
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
                if (token.IsCancellationRequested || _section != "startup") return;
                status.Text = $"Zpoždění Startu={(state.StartupDelayOff ? "0 ms" : "Výchozí")} • Battle.net odklad={(state.BattleNetDelayed ? "ON" : "OFF")}";
            });
        }, token), "startup", "Startup modernizer state");
    }

    private void BuildWindowsModernizerVendorPanel(CancellationToken token)
    {
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
                await WindowsModernizerService.ApplyVendorIdleAsync(CancellationToken.None);
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
                await WindowsModernizerService.RestoreVendorIdleAsync(CancellationToken.None);
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
                if (token.IsCancellationRequested || _section != "components") return;
                status.Text = $"Služby výrobců ručně: {state.VendorServicesTuned}/{state.VendorServicesPresent} • Armoury Download Tool={(state.ArmouryDownloadPresent ? (state.ArmouryDownloadDisabled ? "Vypnuto" : "Aktivní / ručně") : "není nainstalovaný")}";
            });
        }, token), "components", "Stav služeb výrobců");
    }

    private void BuildWindowsModernizerStoragePanel(CancellationToken token)
    {
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
            if (token.IsCancellationRequested || _section != "storage" || actions.Children.Count > 0) return;
            var deferred = System.Diagnostics.Stopwatch.StartNew();
            actions.Children.Add(QuickButton("Analyzovat disky", async () =>
            {
                try
                {
                    status.Text = "Analyzuji pevné disky…";
                    status.Text = await WindowsModernizerService.AnalyzeFixedDrivesAsync(CancellationToken.None);
                    ShowActionFeedback("Analýza úložiště", ActionFeedbackKind.Verified, status.Text);
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
                    status.Text = await WindowsModernizerService.RetrimFixedDrivesAsync(CancellationToken.None);
                    SystemInsightService.InvalidateStorage(
                        preserveStableDriverMetadata: true,
                        invalidateTemperatures: false,
                        invalidateTrim: true);
                    ShowActionFeedback("Retrim", ActionFeedbackKind.Verified, status.Text);
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

    private void BuildWindowsModernizerFirefoxPanel(CancellationToken token)
    {
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var host = new StackPanel { Spacing = 8 };
        var status = new TextBlock
        {
            Text = "Načítám stav Firefox RAM…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };

        static string FormatFirefoxState(WindowsModernizerSnapshot current) =>
            $"Dynamic RAM={(current.FirefoxDynamicRamConfigured ? "ON" : "OFF")} • Default Browser Agent={(current.FirefoxDefaultAgentEnabled.HasValue ? (current.FirefoxDefaultAgentEnabled.Value ? "ON" : "OFF") : "N/A")}";

        async Task<WindowsModernizerSnapshot> ReadFirefoxStateAsync(CancellationToken ct = default)
        {
            return await Task.Run(WindowsModernizerService.ReadSnapshot, ct).ConfigureAwait(false);
        }

        async Task RefreshFirefoxStateAsync()
        {
            try
            {
                WindowsModernizerSnapshot current = await ReadFirefoxStateAsync(token);
                if (token.IsCancellationRequested || _section != "browsers" || _sub != "Firefox") return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (!token.IsCancellationRequested && _section == "browsers" && _sub == "Firefox")
                        status.Text = FormatFirefoxState(current);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    LogService.Error("browsers", "Firefox WindowsModernizer state read failed", ex);
            }
        }

        long setupMs = phase.ElapsedMilliseconds;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void PopulatePrimaryFirefoxActions()
        {
            actions.Children.Add(PrimaryQuickButton("Použít Dynamic RAM", async () =>
            {
                try
                {
                    string detail = await Task.Run(WindowsModernizerService.ApplyFirefoxDynamicRam);
                    WindowsModernizerSnapshot current = await ReadFirefoxStateAsync();
                    status.Text = FormatFirefoxState(current);
                    if (!current.FirefoxDynamicRamConfigured)
                        throw new InvalidOperationException("Ověření Firefox Dynamic RAM nepotvrdilo aplikovaný profil.");
                    ShowActionFeedback("Firefox RAM", ActionFeedbackKind.Verified, detail + " • " + status.Text);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    ShowActionFeedback("Firefox RAM", ActionFeedbackKind.Failed, ex.Message);
                }
            }));
            actions.Children.Add(QuickButton("Obnovit user.js", async () =>
            {
                bool ok = await Task.Run(WindowsModernizerService.RestoreFirefoxDynamicRam);
                WindowsModernizerSnapshot current = await ReadFirefoxStateAsync();
                status.Text = FormatFirefoxState(current);
                ShowActionFeedback("Firefox RAM", ok ? ActionFeedbackKind.Verified : ActionFeedbackKind.Applied,
                    ok ? "Firefox RAM backup obnoven. • " + status.Text : "Firefox RAM backup nebyl nalezen.");
            }));
        }

        host.Children.Add(actions);
        long actionsMs = phase.ElapsedMilliseconds;

        var firefoxTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        host.Children.Add(firefoxTools);
        long toolsMs = phase.ElapsedMilliseconds;
        host.Children.Add(new TextBlock
        {
            Text = "Profil je dynamický podle fyzické RAM: unloadOnLowMemory, inactive unload delay, memory cache, session restore-on-demand a low-commit threshold. Aktivní stream/media taby Firefox chrání vlastním tab-unload chováním.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        host.Children.Add(status);
        long contentMs = phase.ElapsedMilliseconds;
        ContentStack.Children.Add(BuildSectionSurface(
            "browsers",
            "Firefox Dynamic RAM",
            "Přenesený RAM profil • bez background watcher procesu",
            host));
        long surfaceMs = phase.ElapsedMilliseconds;

        LogService.Info("performance",
            $"FIREFOX_MODERNIZER_SHELL setupMs={setupMs} actionsMs={actionsMs - setupMs} toolsMs={toolsMs - actionsMs} contentMs={contentMs - toolsMs} surfaceMs={surfaceMs - contentMs} totalMs={surfaceMs}");

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "browsers" || _sub != "Firefox") return;
            var deferred = System.Diagnostics.Stopwatch.StartNew();

            firefoxTools.Children.Add(QuickButton("Vypnout agenta výchozího prohlížeče", async () =>
            {
                try
                {
                    string detail = await WindowsModernizerService.SetFirefoxDefaultAgentEnabledAsync(false, CancellationToken.None);
                    WindowsModernizerSnapshot current = await ReadFirefoxStateAsync();
                    status.Text = FormatFirefoxState(current);
                    if (current.FirefoxDefaultAgentEnabled == true)
                        throw new InvalidOperationException("Ověření agenta výchozího prohlížeče Firefoxu ukazuje, že je stále zapnutý.");
                    ShowActionFeedback("Firefox agent", ActionFeedbackKind.Verified, detail + " • " + status.Text);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    ShowActionFeedback("Firefox agent", ActionFeedbackKind.Failed, ex.Message);
                }
            }));
            firefoxTools.Children.Add(QuickButton("Obnovit agenta", async () =>
            {
                try
                {
                    string detail = await WindowsModernizerService.RestoreFirefoxDefaultAgentAsync(CancellationToken.None);
                    WindowsModernizerSnapshot current = await ReadFirefoxStateAsync();
                    status.Text = FormatFirefoxState(current);
                    ShowActionFeedback("Firefox agent", ActionFeedbackKind.Verified, detail + " • " + status.Text);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    ShowActionFeedback("Firefox agent", ActionFeedbackKind.Failed, ex.Message);
                }
            }));
            firefoxTools.Children.Add(QuickButton("60s RAM soak", async () =>
            {
                try
                {
                    status.Text = "Měřím Firefox RAM 60 sekund…";
                    status.Text = await WindowsModernizerService.RunFirefoxSoakAuditAsync(60, CancellationToken.None);
                    ShowActionFeedback("Firefox soak", ActionFeedbackKind.Verified, status.Text);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    ShowActionFeedback("Firefox soak", ActionFeedbackKind.Failed, ex.Message);
                }
            }));

            PopulatePrimaryFirefoxActions();
            LogService.Info("performance", $"FIREFOX_MODERNIZER_ACTIONS_DEFERRED ms={deferred.ElapsedMilliseconds} primary={actions.Children.Count} tools={firefoxTools.Children.Count}");
        });

        SafeTaskService.Forget(RefreshFirefoxStateAsync(), "browsers", "Firefox modernizer state");
    }
}

