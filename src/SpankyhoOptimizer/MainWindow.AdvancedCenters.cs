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
    private int _updateStatusLoadGeneration;
    private Task BuildWindowsUpdateCenterAsync(CancellationToken token)
    {
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _updateActionsHost = actions;
        _updateActionsRendered = 0;
        _updateActionsQueued = false;
        _updateActionsGeneration++;
        long actionsMs = phase.ElapsedMilliseconds;
        ContentStack.Children.Add(BuildSectionHero("update", "Windows servicing", "Windows Update Center", "Načítám stav Windows Update…", actions));
        long heroMs = phase.ElapsedMilliseconds - actionsMs;
        var body = new StackPanel { Spacing = 7 };
        _updateStatusBody = body;
        body.Children.Add(new TextBlock { Text = "Načítám pending restart, služby a poslední chyby…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("update", "Stav Windows Update", "Aktuální stav + čekající restart + poslední chyby", body));
        long surfaceMs = phase.ElapsedMilliseconds - actionsMs - heroMs;
        ScheduleWindowsUpdateActions(token);
        long kickoffMs = phase.ElapsedMilliseconds - actionsMs - heroMs - surfaceMs;
        LogService.Info("performance", $"UPDATE_UI_SHELL_PHASE actionsMs={actionsMs} heroMs={heroMs} surfaceMs={surfaceMs} kickoffMs={kickoffMs} totalMs={phase.ElapsedMilliseconds}");
        return Task.CompletedTask;
    }

    private void ScheduleWindowsUpdateActions(CancellationToken token, bool restartQueue = false)
    {
        if (_updateActionsHost == null || _updateActionsRendered >= 3) return;
        if (restartQueue)
        {
            _updateActionsGeneration++;
            _updateActionsQueued = false;
        }
        if (_updateActionsQueued) return;
        int generation = _updateActionsGeneration;
        _updateActionsQueued = true;
        QueueWindowsUpdateAction(token, generation);
    }

    private void QueueWindowsUpdateAction(CancellationToken token, int generation)
    {
        // A rejected low-priority enqueue must release the pending bit so the
        // Windows Update action batches can be scheduled again.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _updateActionsGeneration) return;
            if (token.IsCancellationRequested || _section != "update" || _updateActionsHost == null)
            {
                _updateActionsQueued = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            switch (_updateActionsRendered)
            {
                case 0:
                {
                    var pauseButton = PrimaryQuickButton("Načítám stav…", async () =>
                    {
                        int actionGeneration = _updateActionsGeneration;
                        await AdvancedCenterService.SetWindowsUpdatePauseAsync(!_updatePausedState);
                        bool refresh = actionGeneration == _updateActionsGeneration && _section == "update";
                        InvalidateUpdatePageCache();
                        if (refresh) await ShowSectionAsync("update");
                    });
                    pauseButton.IsEnabled = false;
                    _updatePauseButton = pauseButton;
                    _updateActionsHost.Children.Add(pauseButton);
                    if (_updateStatusBody != null)
                        SafeTaskService.Forget(
                            LoadWindowsUpdateCenterAsync(_updateStatusBody, pauseButton, token),
                            "update", "Windows Update status");
                    break;
                }
                case 1:
                    _updateActionsHost.Children.Add(QuickButton("Vyhledat aktualizace", () =>
                    {
                        ProcessRunner.Open("ms-settings:windowsupdate");
                        return Task.CompletedTask;
                    }));
                    break;
                case 2:
                    _updateActionsHost.Children.Add(QuickButton("Opravit komponenty", async () =>
                    {
                        int actionGeneration = _updateActionsGeneration;
                        bool completed = await RunCancellableWithProgressAsync(
                            "Windows Update repair",
                            "DISM RestoreHealth + SFC…",
                            token => AdvancedCenterService.RepairWindowsUpdateAsync(token));
                        if (!completed)
                        {
                            LogService.Info("update", "Windows Update repair cancelled by user.");
                            return;
                        }
                        ShowActionFeedback(
                            "Windows Update repair",
                            ActionFeedbackKind.Applied,
                            "DISM/SFC oprava komponent doběhla. Obnovuji skutečný stav Windows Update.");
                        bool refresh = actionGeneration == _updateActionsGeneration && _section == "update";
                        InvalidateUpdatePageCache();
                        if (refresh) await ShowSectionAsync("update");
                    }));
                    break;
            }

            _updateActionsRendered++;
            LogService.Info("performance",
                $"UPDATE_ACTION_BATCH index={_updateActionsRendered - 1} ms={sw.ElapsedMilliseconds} total={_updateActionsRendered}");

            if (_updateActionsRendered < 3)
                QueueWindowsUpdateAction(token, generation);
            else
                _updateActionsQueued = false;
        })) _updateActionsQueued = false;
    }

    private async Task LoadWindowsUpdateCenterAsync(StackPanel body, Button pauseButton, CancellationToken token)
    {
        int generation = _updateActionsGeneration;
        if (_updateStatusLoadPending && _updateStatusLoadGeneration == generation) return;
        _updateStatusLoadGeneration = generation;
        _updateStatusLoadPending = true;
        try
        {
            var s = await AdvancedCenterService.GetWindowsUpdateStatusAsync(token);
            var drift = await Task.Run(TweakDriftWatchdogService.LoadLast, token);
            if (token.IsCancellationRequested || _section != "update") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _updateActionsGeneration || _section != "update" || !ReferenceEquals(_updateStatusBody, body)) return;
                _updatePausedState = s.Paused;
                _updateStatusUiAt = DateTime.UtcNow;
                pauseButton.Content = s.Paused ? "Obnovit aktualizace" : "Pozastavit 7 dní";
                pauseButton.IsEnabled = true;
                body.Children.Clear();
                body.Children.Add(InsightRow("RESTART", s.RebootPending ? "Čeká restart" : "Není vyžadován"));
                body.Children.Add(InsightRow("SLUŽBY", s.Services));
                body.Children.Add(InsightRow("CHYBY", s.RecentErrorCount == 0 ? "Bez nedávných chyb" : s.RecentErrorCount + " nedávných chyb"));
                body.Children.Add(InsightRow("ODCHYLKY NASTAVENÍ", drift == null
                    ? "Zatím nekontrolováno"
                    : TweakDriftWatchdogService.FormatDriftSummary(drift)));
                if (drift?.Drifted.Length > 0)
                    body.Children.Add(new TextBlock
                    {
                        Text = "Windows změnil: " + string.Join(", ", drift.Drifted.Take(6).Select(x => x.Name)),
                        Foreground = UiBrush("SpankyWarningBrush"),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 3
                    });
                if (!string.IsNullOrWhiteSpace(s.LastError)) body.Children.Add(new TextBlock { Text = s.LastError, Foreground = UiBrush("SpankyWarningBrush"), TextWrapping = TextWrapping.Wrap, MaxLines = 4 });
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("update", "Windows Update Center status failed", ex);
            if (!token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || generation != _updateActionsGeneration || _section != "update" || !ReferenceEquals(_updateStatusBody, body)) return;
                    pauseButton.Content = "Stav nedostupný";
                    pauseButton.IsEnabled = false;
                    body.Children.Clear();
                    body.Children.Add(new TextBlock
                    {
                        Text = "Stav Windows Update se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    body.Children.Add(QuickButton("Načíst znovu", async () =>
                    {
                        pauseButton.Content = "Načítám stav…";
                        await LoadWindowsUpdateCenterAsync(body, pauseButton, token);
                    }));
                    ShowActionFeedback("Windows Update", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }
        finally
        {
            if (_updateStatusLoadGeneration == generation)
                _updateStatusLoadPending = false;
        }
    }
    private Task BuildStorageAdvancedAsync(CancellationToken token)
    {
        var body = new StackPanel { Spacing = 7 };
        _storageAdvancedBody = body;
        body.Children.Add(new TextBlock { Text = "Načítám firmware a volné místo…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("storage", "Detail úložiště", "Firmware • teplota pokud ji disk poskytuje • volné místo", body));
        ContentStack.Children.Add(ActionCard("Windows temp", "Bezpečný úklid uživatelské TEMP: pouze soubory starší 24 hodin, bez symbolických odkazů a shader cache. Před smazáním zobrazí náhled.", "Zkontrolovat temp", async () =>
            {
                var preview = await SafeTempCleanupService.InspectUserTempAsync();
                if (preview.Truncated)
                {
                    ShowActionFeedback("Windows temp", ActionFeedbackKind.Info, "TEMP obsahuje příliš mnoho položek; bezpečnostní limit byl dosažen. Nic nebylo smazáno.");
                    return;
                }
                if (preview.EligibleFiles == 0)
                {
                    ShowActionFeedback("Windows temp", ActionFeedbackKind.Info, "Žádné vhodné starší soubory. Nic nebylo smazáno.");
                    return;
                }
                string estimate = $"K odstranění: {preview.EligibleFiles} souborů (~{preview.EligibleBytes / 1048576.0:0.0} MB). " +
                                  "Nedávno změněné soubory, systémové položky, odkazy a shader cache nebudou smazány.";
                if (!await ShowStyledConfirmAsync("Vyčistit uživatelskou TEMP?", estimate, "Vyčistit", "Zrušit", "Windows temp")) return;
                var result = await SafeTempCleanupService.CleanUserTempAsync();
                string summary = $"Odstraněno {result.RemovedFiles} souborů (součet velikostí {result.RemovedBytes / 1048576.0:0.0} MB)." +
                                 $" Přeskočeno {result.SkippedFiles}, chyby {result.FailedFiles}." +
                                 (result.Truncated ? " Dosažen bezpečnostní limit; část souborů nebyla zpracována." : "");
                ShowActionFeedback("Windows temp", result.FailedFiles > 0 || result.Truncated ? ActionFeedbackKind.Info : ActionFeedbackKind.Verified, summary);
                await ShowMessageAsync("Úložiště", summary);
            }));
        SafeTaskService.Forget(LoadStorageAdvancedAsync(body, token), "storage", "Lazy storage advanced");
        return Task.CompletedTask;
    }

    private async Task LoadStorageAdvancedAsync(StackPanel body, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            var s = await AdvancedCenterService.GetStorageAdvancedAsync(token);
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageAdvancedBody, body)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration ||
                    _section != "storage" || !ReferenceEquals(_storageAdvancedBody, body)) return;
                body.Children.Clear();
                body.Children.Add(InsightRow("Oddíly a volné místo", s.Volumes));
                body.Children.Add(InsightRow("Fyzické disky", s.Physical));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Lazy storage advanced failed", ex);
            if (!token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || generation != _navigationGeneration ||
                        _section != "storage" || !ReferenceEquals(_storageAdvancedBody, body)) return;
                    body.Children.Clear();
                    body.Children.Add(new TextBlock
                    {
                        Text = "Detail úložiště se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    body.Children.Add(QuickButton("Načíst znovu", () =>
                        LoadStorageAdvancedAsync(body, token)));
                    ShowActionFeedback("Úložiště — detail", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }
    }


    private TextBlock? _componentAdvisorCount;
    private string? _componentAdvisorSub;

    private void RefreshComponentAdvisorCount(string sub, UserFeatureProfile profile)
    {
        if (_componentAdvisorCount is not { } label || label.Parent == null ||
            !string.Equals(_componentAdvisorSub, sub, StringComparison.OrdinalIgnoreCase))
            return;
        var options = ContextOptionsForCurrentSub();
        int count = options.Count(option => profile.Keeps(option.Key));
        string text = $"Chráníš {count} z {options.Length} volitelných funkcí v této sekci.";
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
            label.Text = text;
    }

    private void BuildComponentAdvisor()
    {
        var profile = UserFeatureProfileService.Load();
        var options = ContextOptionsForCurrentSub();
        int kept = 0;
        for (int i = 0; i < options.Length; i++)
            if (profile.Keeps(options[i].Key)) kept++;

        var body = new StackPanel { Spacing = 7 };
        var countLabel = new TextBlock
        {
            Text = $"Chráníš {kept} z {options.Length} volitelných funkcí v této sekci.",
            FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush()
        };
        _componentAdvisorCount = countLabel;
        _componentAdvisorSub = _sub ?? "Služby";
        body.Children.Add(countLabel);
        body.Children.Add(new TextBlock { Text = "Doporučení nikdy automaticky nevypíná hardware nebo službu označenou jako používanou. Rizikové a systémově chráněné položky zůstávají blokované.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Upravit co používám", ShowComponentsContextDialogAsync));
        body.Children.Add(actions);

        ContentStack.Children.Add(BuildSectionSurface("components", "Doporučení podle hardwaru", "Služby • ovladače • úlohy podle toho, co reálně používáš", body));
    }

    private Task BuildAppHealthAsync(CancellationToken token)
    {
        var body = new StackPanel { Spacing = 7 };
        _fixerAppHealthHost = body;
        body.Children.Add(new TextBlock { Text = "Načítám stav aplikace…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("fixer", "Stav aplikace", "Aplikace • Winget • úložiště • služby Windows Update", body));


        return Task.CompletedTask;
    }

    private async Task LoadAppHealthAsync(StackPanel body, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            var checks = await AdvancedCenterService.GetAppHealthAsync(token);
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "fixer" || !ReferenceEquals(_fixerAppHealthHost, body)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration ||
                    _section != "fixer" || !ReferenceEquals(_fixerAppHealthHost, body)) return;
                body.Children.Clear();
                foreach (var c in checks)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(Badge(c.Ok ? "OK" : "PROBLÉM", c.Ok ? "SpankySuccessBrush" : "SpankyWarningBrush"));
                    row.Children.Add(new TextBlock { Text = c.Name + " — " + c.Detail, Foreground = c.Ok ? TextPrimaryBrush() : UiBrush("SpankyWarningBrush"), TextWrapping = TextWrapping.Wrap });
                    body.Children.Add(row);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("fixer", "App Health failed", ex);
            if (!token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || generation != _navigationGeneration ||
                        _section != "fixer" || !ReferenceEquals(_fixerAppHealthHost, body)) return;
                    body.Children.Clear();
                    body.Children.Add(new TextBlock
                    {
                        Text = "Stav aplikace se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    body.Children.Add(QuickButton("Načíst znovu", () =>
                        LoadAppHealthAsync(body, token)));
                });
            }
        }
    }
}
