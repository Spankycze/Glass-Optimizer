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
    private static readonly (string Id, string Label)[] DefenderStatusModules =
    {
        ("def.rt", "Real-time ochrana"),
        ("def.cloud", "Cloud protection"),
        ("def.net", "Network Protection"),
        ("def.smartscreen", "SmartScreen"),
        ("def.firewall", "Firewall")
    };

    private StackPanel? _defenderModulesHost;
    private bool _defenderModulesQueued;
    private int _defenderModulesIndex;
    private StackPanel? _defenderActionsHost;
    private bool _defenderActionsQueued;

    private async Task BuildDefenderPageAsync(CancellationToken token)
    {
        const string defenderCatalogKey = "defender|";
        if (!_tweakCache.TryGetValue(defenderCatalogKey, out _tweaks))
            _tweakCache[defenderCatalogKey] = _tweaks = CatalogService.GetTweaks("defender");

        var phase = System.Diagnostics.Stopwatch.StartNew();
        bool tamper = WindowsActions.IsTamperProtectionOn();
        long tamperMs = phase.ElapsedMilliseconds;
        if (TryAttachDefenderPageCache(tamper))
        {
            if (_defenderModulesHost != null && _defenderModulesHost.Children.Count < _tweaks.Count)
            {
                _defenderModulesIndex = _defenderModulesHost.Children.Count;
                ScheduleDefenderModules(token);
            }
            if (_defenderActionsHost is { Children.Count: 0 })
                ScheduleDefenderActions(tamper, token);
            UpdateDefenderStaticStatus();
            return;
        }

        long heroMs = 0;
        long statusMs = 0;
        bool deferStaticShell = _defenderStaticHero == null || _defenderStaticTamper != tamper;
        if (deferStaticShell)
        {
            var staticShellHost = new StackPanel { Spacing = 10 };
            ContentStack.Children.Add(staticShellHost);
            int shellGeneration = _navigationGeneration;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested ||
                    shellGeneration != _navigationGeneration ||
                    _section != "defender" ||
                    staticShellHost.Children.Count > 0)
                    return;

                var shellPerf = System.Diagnostics.Stopwatch.StartNew();
                EnsureDefenderStaticShell(tamper);
                staticShellHost.Children.Add(_defenderStaticHero!);
                long deferredHeroMs = shellPerf.ElapsedMilliseconds;
                staticShellHost.Children.Add(_defenderStaticStatus!);
                long deferredStatusMs = shellPerf.ElapsedMilliseconds - deferredHeroMs;
                LogService.Info("performance",
                    $"DEFENDER_STATIC_SHELL_DEFERRED heroMs={deferredHeroMs} statusMs={deferredStatusMs} totalMs={shellPerf.ElapsedMilliseconds}");
            });
        }
        else
        {
            AttachDefenderStaticShell(tamper, out heroMs, out statusMs);
        }
        long shellMs = phase.ElapsedMilliseconds - tamperMs;

        _defenderModulesHost = new StackPanel { Spacing = 6 };
        var defenderModulesShellHost = new StackPanel();
        ContentStack.Children.Add(defenderModulesShellHost);
        int modulesGeneration = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                modulesGeneration != _navigationGeneration ||
                _section != "defender" ||
                defenderModulesShellHost.Children.Count > 0)
                return;

            _defenderModulesCard = BuildDashboardExpander(
                "Pokročilé moduly",
                "Real-time • behavior • cloud • PUA • Network Protection • CFA • SmartScreen • Firewall",
                _defenderModulesHost);
            defenderModulesShellHost.Children.Add(_defenderModulesCard);
        });
        long modulesMs = phase.ElapsedMilliseconds - tamperMs - shellMs;

        _defenderActionsHost = new StackPanel { Spacing = 10 };
        var defenderActionsShellHost = new StackPanel();
        ContentStack.Children.Add(defenderActionsShellHost);
        int actionsGeneration = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                actionsGeneration != _navigationGeneration ||
                _section != "defender" ||
                defenderActionsShellHost.Children.Count > 0)
                return;

            _defenderActionsCard = BuildDashboardExpander(
                "Pokročilé akce",
                "Vratné akce • bod obnovení • následná kontrola",
                _defenderActionsHost);
            defenderActionsShellHost.Children.Add(_defenderActionsCard);
        });
        long actionsMs = phase.ElapsedMilliseconds - tamperMs - shellMs - modulesMs;
        long tailStartMs = phase.ElapsedMilliseconds;
        UpdateDirtyState();
        long dirtyMs = phase.ElapsedMilliseconds - tailStartMs;
        CaptureDefenderPageCache(tamper);
        long captureMs = phase.ElapsedMilliseconds - tailStartMs - dirtyMs;
        ScheduleDefenderModules(token);
        ScheduleDefenderActions(tamper, token);
        long scheduleMs = phase.ElapsedMilliseconds - tailStartMs - dirtyMs - captureMs;
        var defenderCutoff = DateTime.UtcNow - TimeSpan.FromSeconds(20);
        bool defenderStale = _tweaks.Any(x => !x.IsLoaded || !x.LastVerifiedUtc.HasValue || x.LastVerifiedUtc.Value < defenderCutoff);
        long staleMs = phase.ElapsedMilliseconds - tailStartMs - dirtyMs - captureMs - scheduleMs;
        if (defenderStale)
        {
            var detectSnapshot = _tweaks.ToArray();
            int detectGeneration = _navigationGeneration;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested ||
                    detectGeneration != _navigationGeneration ||
                    _section != "defender") return;
                SafeTaskService.Forget(
                    DetectDefenderAndRefreshAsync(detectSnapshot, detectGeneration, token),
                    "defender",
                    "Background Defender detect");
            }))
            {
                SafeTaskService.Forget(
                    DetectDefenderAndRefreshAsync(detectSnapshot, detectGeneration, token),
                    "defender",
                    "Background Defender detect fallback");
            }
        }
        long kickoffMs = phase.ElapsedMilliseconds - tailStartMs - dirtyMs - captureMs - scheduleMs - staleMs;
        LogService.Info("performance",
            $"DEFENDER_UI_PHASE tamperMs={tamperMs} heroMs={heroMs} statusMs={statusMs} shellMs={shellMs} modulesMs={modulesMs} actionsMs={actionsMs} dirtyMs={dirtyMs} captureMs={captureMs} scheduleMs={scheduleMs} staleMs={staleMs} kickoffMs={kickoffMs} totalMs={phase.ElapsedMilliseconds} tweaks={_tweaks.Count}");
    }

    private void EnsureDefenderStaticShell(bool tamper)
    {
        if (_defenderStaticHero == null || _defenderStaticStatus == null || _defenderStaticTamper != tamper)
        {
            if (_defenderStaticHero?.Parent is Panel p1) p1.Children.Remove(_defenderStaticHero);
            if (_defenderStaticStatus?.Parent is Panel p2) p2.Children.Remove(_defenderStaticStatus);
            _defenderStaticHero = null;
            _defenderStaticStatus = null;
            _defenderStaticTamper = tamper;

            var heroActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            heroActions.Children.Add(QuickButton("Otevřít Windows Security", () =>
            {
                ProcessRunner.Open("windowsdefender://threatsettings");
                return Task.CompletedTask;
            }));

            _defenderStaticHero = BuildModernSectionHero(
                "defender",
                "Microsoft Defender",
                "Stav ochrany z reálného read-backu Windows. Pokročilé moduly i vratné akce jsou níže.",
                "SECURITY",
                tamper ? "Tamper Protection zapnutý" : "Tamper Protection nezjištěný",
                heroActions);

            _defenderStaticStatus = BuildDefenderProtectionCard();
        }

        UpdateDefenderStaticStatus();
    }

    private Border BuildDefenderProtectionCard()
    {
        _defenderStatusRows.Clear();
        var stack = new StackPanel { Spacing = 6 };

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Stav ochrany",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        _defenderStatusSummary = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_defenderStatusSummary, 1);
        header.Children.Add(_defenderStatusSummary);
        stack.Children.Add(header);

        foreach (var module in DefenderStatusModules)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = module.Label,
                FontSize = 12.5,
                Foreground = TextPrimaryBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var value = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextSecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            _defenderStatusRows[module.Id] = value;
            stack.Children.Add(row);
        }

        return BuildDashboardCard(stack, new Thickness(16, 12, 16, 12));
    }

    private void UpdateDefenderStaticStatus()
    {
        if (_defenderStatusRows.Count == 0) return;
        int off = 0;
        int unknown = 0;
        foreach (var module in DefenderStatusModules)
        {
            if (!_defenderStatusRows.TryGetValue(module.Id, out var value)) continue;
            var tweak = _tweaks.FirstOrDefault(x => x.Id == module.Id);
            if (tweak is not { IsLoaded: true })
            {
                unknown++;
                value.Text = "Nezjištěno";
                value.Foreground = TextSecondaryBrush();
            }
            else if (tweak.Current)
            {
                off++;
                value.Text = "Vypnuto";
                value.Foreground = UiBrush("SpankyWarningBrush");
            }
            else
            {
                value.Text = "Zapnuto";
                value.Foreground = UiBrush("SpankySuccessBrush");
            }
        }

        if (_defenderStatusSummary == null) return;
        _defenderStatusSummary.Text = unknown > 0
            ? $"Nezjištěno: {unknown}/{DefenderStatusModules.Length}"
            : off > 0
                ? $"{off} z {DefenderStatusModules.Length} vypnuto"
                : "Vše zapnuto";
        _defenderStatusSummary.Foreground = off > 0
            ? UiBrush("SpankyWarningBrush")
            : unknown > 0 ? TextSecondaryBrush() : UiBrush("SpankySuccessBrush");
    }

    private void AttachDefenderStaticShell(bool tamper, out long heroMs, out long statusMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EnsureDefenderStaticShell(tamper);

        ContentStack.Children.Add(_defenderStaticHero!);
        heroMs = sw.ElapsedMilliseconds;

        ContentStack.Children.Add(_defenderStaticStatus!);
        statusMs = sw.ElapsedMilliseconds - heroMs;
    }

    private void ScheduleDefenderModules(CancellationToken token)
    {
        StackPanel? host = _defenderModulesHost;
        if (_defenderModulesQueued || host == null || host.Children.Count >= _tweaks.Count) return;
        int generation = _navigationGeneration;
        _defenderModulesIndex = Math.Max(_defenderModulesIndex, host.Children.Count);
        _defenderModulesQueued = true;
        QueueDefenderModuleBatch(token, generation, host);
    }

    private void QueueDefenderModuleBatch(CancellationToken token, int generation, StackPanel host)
    {
        // A rejected low-priority enqueue must release the queued flag, or the
        // deferred module list can never be scheduled again in this session.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "defender" ||
                !ReferenceEquals(_defenderModulesHost, host))
            {
                _defenderModulesQueued = false;
                return;
            }

            _defenderModulesIndex = Math.Max(_defenderModulesIndex, host.Children.Count);
            if (_defenderModulesIndex >= _tweaks.Count)
            {
                _defenderModulesQueued = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int start = _defenderModulesIndex;
            int end = Math.Min(start + 1, _tweaks.Count);
            for (int i = start; i < end; i++)
                host.Children.Add(CreateTweakCard(_tweaks[i]));
            _defenderModulesIndex = end;

            LogService.Info("performance",
                $"DEFENDER_MODULES_BATCH start={start} added={end - start} ms={sw.ElapsedMilliseconds} count={host.Children.Count}");

            if (_defenderModulesIndex < _tweaks.Count)
                SafeTaskService.Forget(
                    QueueNextDefenderModuleBatchAsync(token, generation, host),
                    "defender",
                    "Deferred Defender module pacing");
            else
                _defenderModulesQueued = false;
        })) _defenderModulesQueued = false;
    }

    private async Task QueueNextDefenderModuleBatchAsync(CancellationToken token, int generation, StackPanel host)
    {
        try
        {
            await Task.Delay(8, token).ConfigureAwait(false);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "defender" ||
                !ReferenceEquals(_defenderModulesHost, host))
                return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => QueueDefenderModuleBatch(token, generation, host));
        }
        catch (OperationCanceledException) { }
    }

    private void ScheduleDefenderActions(bool tamper, CancellationToken token)
    {
        StackPanel? host = _defenderActionsHost;
        if (_defenderActionsQueued || host == null || host.Children.Count > 0) return;
        int generation = _navigationGeneration;
        _defenderActionsQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _defenderActionsQueued = false;
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "defender" ||
                !ReferenceEquals(_defenderActionsHost, host) ||
                host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (tamper)
                host.Children.Add(ActionCard("Tamper Protection je zapnutý", "Windows blokuje část změn Defenderu. Otevři Windows Security a vypni Tamper Protection ručně, pokud to systém dovolí.", "Otevřít Windows Security", () => { ProcessRunner.Open("windowsdefender://threatsettings"); return Task.CompletedTask; }));
            host.Children.Add(ActionCard("Vypnout Defender AV", "Vytvoří bod obnovení a použije podporované policy/service/task změny. Tamper Protection může část změn odmítnout.", "Vypnout", RunDefenderDisableAsync));
            host.Children.Add(ActionCard("Obnovit Defender", "Vrátí policy, služby a naplánované úlohy Defenderu.", "Obnovit", RunDefenderRestoreAsync));
            LogService.Info("performance", $"DEFENDER_ACTIONS_DEFERRED ms={sw.ElapsedMilliseconds} tamper={tamper}");
        })) _defenderActionsQueued = false;
    }

    internal async Task RunDefenderDeferredActionsSmokeAsync()
    {
        InvalidateDefenderPageCache(includeStaticShell: true);
        await ShowSectionAsync("defender");
        bool tamper = WindowsActions.IsTamperProtectionOn();
        int expectedActions = tamper ? 3 : 2;
        int expectedModules = _tweaks.Count;
        for (int i = 0; i < 40 &&
             ((_defenderActionsHost?.Children.Count ?? 0) < expectedActions ||
              (_defenderModulesHost?.Children.Count ?? 0) < expectedModules ||
              _defenderModulesCard == null ||
              _defenderActionsCard == null); i++)
            await Task.Delay(25);

        int actualActions = _defenderActionsHost?.Children.Count ?? 0;
        int actualModules = _defenderModulesHost?.Children.Count ?? 0;
        if (actualActions < expectedActions)
            throw new InvalidOperationException($"Deferred Defender actions incomplete: expected={expectedActions} actual={actualActions}.");
        if (actualModules < expectedModules)
            throw new InvalidOperationException($"Deferred Defender modules incomplete: expected={expectedModules} actual={actualModules}.");
        if (_defenderStatusRows.Count != DefenderStatusModules.Length ||
            _defenderStatusRows.Values.Any(x => string.IsNullOrWhiteSpace(x.Text)))
            throw new InvalidOperationException("Defender protection status rows are incomplete.");
        AssertDefenderAdvancedCard(_defenderModulesCard, "modules");
        AssertDefenderAdvancedCard(_defenderActionsCard, "actions");
        LogService.Info("performance",
            $"DEFENDER_DEFERRED_ACTIONS_SMOKE_OK actions={actualActions} modules={actualModules} statusRows={_defenderStatusRows.Count}");
    }

    private static void AssertDefenderAdvancedCard(Border? card, string name)
    {
        if (card?.Child is not Expander expander)
            throw new InvalidOperationException($"Defender advanced {name} expander is missing.");
        if (expander.IsExpanded)
            throw new InvalidOperationException($"Defender advanced {name} expander must start collapsed.");
    }

    private async Task DetectDefenderAndRefreshAsync(
        Models.TweakDefinition[] tweaks,
        int generation,
        CancellationToken token)
    {
        try
        {
            await _engine.DetectFreshAsync(tweaks, TimeSpan.FromSeconds(20), token);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "defender") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "defender") return;
                RefreshCards();
                UpdateDefenderStaticStatus();
                if (_defenderPageCache != null)
                    _defenderPageRevisionStamp = CurrentTweakRevisionStamp();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Background Defender detect failed", ex);
            if (token.IsCancellationRequested || generation != _navigationGeneration) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "defender") return;
                ShowActionFeedback(
                    "Defender — ověření stavu",
                    ActionFeedbackKind.Failed,
                    "Nepodařilo se načíst skutečný stav Defenderu: " + ex.Message);
            });
        }
    }

    private async Task RunDefenderDisableAsync()
    {
        if (WindowsActions.IsTamperProtectionOn())
        {
            // The warning card already explains the block; do not open a modal.
            ShowActionFeedback(
                "Defender AV",
                ActionFeedbackKind.Unsupported,
                "Tamper Protection je zapnutý. Optimizer ho neobchází; změny proveď přes Windows Security.");
            return;
        }
        if (!await ShowStyledConfirmAsync("Vypnout Defender AV?", "Sníží se ochrana proti malwaru. Firewall zůstane beze změny. Pokračovat?", "Vypnout", "Zrušit", "Advanced Defender")) return;
        try
        {
            if (!await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before Defender changes"))
            {
                ShowActionFeedback(
                    "Defender AV",
                    ActionFeedbackKind.Failed,
                    "Bod obnovení se nepodařilo vytvořit, takže se Defender nemění.");
                return;
            }

            await WindowsActions.MaxDisableDefenderAsync();
            var verify = CatalogService.GetDefenderTweaks().Where(x => x.Id != "def.firewall").ToList();
            await _engine.DetectAsync(verify);
            int confirmed = verify.Count(x => x.IsLoaded && x.Current);
            int unknown = verify.Count(x => !x.IsLoaded);
            string resultText = $"Ověření dokončeno: {confirmed}/{verify.Count} požadovaných změn je potvrzených. Nezjištěno: {unknown}.";
            ShowActionFeedback(
                "Defender AV",
                confirmed == verify.Count && unknown == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                resultText);
        }
        catch (Exception ex)
        {
            LogService.Error("Defender disable failed", ex);
            ShowActionFeedback("Defender AV", ActionFeedbackKind.Failed, "Vypnutí Defenderu selhalo: " + ex.Message);
        }
        finally
        {
            await RefreshDefenderPageAsync();
        }
    }

    private async Task RunDefenderRestoreAsync()
    {
        try
        {
            await WindowsActions.RestoreDefenderAsync();
            var verify = CatalogService.GetDefenderTweaks();
            await _engine.DetectAsync(verify);
            int remaining = verify.Count(x => x.IsLoaded && x.Current);
            int unknown = verify.Count(x => !x.IsLoaded);
            string resultText = remaining == 0 && unknown == 0
                ? "Nastavení Defenderu bylo vráceno a ověřeno."
                : $"Obnova proběhla, ale aktivních zůstává {remaining} položek a nezjištěných je {unknown}.";
            ShowActionFeedback(
                "Defender — obnova",
                remaining == 0 && unknown == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                resultText);
        }
        catch (Exception ex)
        {
            LogService.Error("Defender restore failed", ex);
            ShowActionFeedback("Defender — obnova", ActionFeedbackKind.Failed, "Obnova Defenderu selhala: " + ex.Message);
        }
        finally
        {
            await RefreshDefenderPageAsync();
        }
    }

    private async Task RefreshDefenderPageAsync()
    {
        InvalidateDefenderPageCache(includeStaticShell: true);
        if (_section == "defender")
            await ShowSectionAsync("defender");
    }
}
