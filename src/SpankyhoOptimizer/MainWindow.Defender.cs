using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
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
        if (TryAttachDefenderPageCache())
        {
            if (_defenderModulesHost != null && _defenderModulesHost.Children.Count < _tweaks.Count)
            {
                _defenderModulesIndex = _defenderModulesHost.Children.Count;
                ScheduleDefenderModules(token);
            }
            if (_defenderActionsHost is { Children.Count: 0 })
                ScheduleDefenderActions(WindowsActions.IsTamperProtectionOn(), token);
            return;
        }
        var phase = System.Diagnostics.Stopwatch.StartNew();

        bool tamper = WindowsActions.IsTamperProtectionOn();
        long tamperMs = phase.ElapsedMilliseconds;
        int loaded = _tweaks.Count(x => x.IsLoaded);
        int active = _tweaks.Count(x => x.IsLoaded && x.Current);
        long countsEndMs = phase.ElapsedMilliseconds;

        long heroMs = 0;
        long metricsMs = 0;
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
                EnsureDefenderStaticShell(tamper, loaded, active);
                staticShellHost.Children.Add(_defenderStaticHero!);
                long deferredHeroMs = shellPerf.ElapsedMilliseconds;
                staticShellHost.Children.Add(_defenderStaticMetrics!);
                long deferredMetricsMs = shellPerf.ElapsedMilliseconds - deferredHeroMs;
                LogService.Info("performance",
                    $"DEFENDER_STATIC_SHELL_DEFERRED heroMs={deferredHeroMs} metricsMs={deferredMetricsMs} totalMs={shellPerf.ElapsedMilliseconds}");
            });
        }
        else
        {
            AttachDefenderStaticShell(tamper, loaded, active, out heroMs, out metricsMs);
        }
        long shellMs = phase.ElapsedMilliseconds - tamperMs;

        _defenderModulesHost = new StackPanel { Spacing = 6 };
        ContentStack.Children.Add(BuildDashboardExpander(
            "Ochranné moduly",
            "Real-time • behavior • cloud • PUA • Network Protection • CFA • SmartScreen • Firewall",
            _defenderModulesHost));
        long modulesMs = phase.ElapsedMilliseconds - tamperMs - shellMs;

        _defenderActionsHost = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(BuildDashboardExpander(
            "Disable / Restore",
            "Vratné akce • bod obnovení • následná kontrola",
            _defenderActionsHost));
        long actionsMs = phase.ElapsedMilliseconds - tamperMs - shellMs - modulesMs;
        long tailStartMs = phase.ElapsedMilliseconds;
        UpdateDirtyState();
        long dirtyMs = phase.ElapsedMilliseconds - tailStartMs;
        CaptureDefenderPageCache();
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
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "defender") return;
                SafeTaskService.Forget(
                    DetectDefenderAndRefreshAsync(detectSnapshot, token),
                    "defender",
                    "Background Defender detect");
            }))
            {
                SafeTaskService.Forget(
                    DetectDefenderAndRefreshAsync(detectSnapshot, token),
                    "defender",
                    "Background Defender detect fallback");
            }
        }
        long kickoffMs = phase.ElapsedMilliseconds - tailStartMs - dirtyMs - captureMs - scheduleMs - staleMs;
        LogService.Info("performance",
            $"DEFENDER_UI_PHASE tamperMs={tamperMs} countsMs={countsEndMs - tamperMs} heroMs={heroMs} metricsMs={metricsMs} shellMs={shellMs} modulesMs={modulesMs} actionsMs={actionsMs} dirtyMs={dirtyMs} captureMs={captureMs} scheduleMs={scheduleMs} staleMs={staleMs} kickoffMs={kickoffMs} totalMs={phase.ElapsedMilliseconds} tweaks={_tweaks.Count}");
    }

    private void EnsureDefenderStaticShell(bool tamper, int loaded, int active)
    {
        if (_defenderStaticHero == null || _defenderStaticTamper != tamper)
        {
            _defenderStaticHero = null;
            _defenderStaticMetrics = null;
            _defenderTamperValue = null;
            _defenderTamperDetail = null;
            _defenderModulesValue = null;
            _defenderReadBackValue = null;
            _defenderActiveValue = null;
            _defenderStaticTamper = tamper;

            var heroActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (tamper)
                heroActions.Children.Add(QuickButton("Windows Security", () =>
                {
                    ProcessRunner.Open("windowsdefender://threatsettings");
                    return Task.CompletedTask;
                }));
            heroActions.Children.Add(QuickButton("Vypnout Defender AV", RunDefenderDisableAsync));

            _defenderStaticHero = BuildModernSectionHero(
                "defender",
                "Microsoft Defender",
                "Stav ochrany, moduly a vratné změny s následným ověřením. Tamper Protection se neobchází.",
                "SECURITY",
                tamper ? "Tamper Protection zapnutý" : "Tamper Protection vypnutý / nezjištěný",
                heroActions);

            var tamperMetric = BuildDashboardMetric(
                "Tamper",
                tamper ? "ON" : "OFF / N/A",
                tamper ? "Změny mohou být blokované" : "Bez aktivního Tamper locku",
                tamper ? "SpankyWarningBrush" : "SpankySuccessBrush");
            var modulesMetric = BuildDashboardMetric("Moduly", _tweaks.Count.ToString(), "Defender + security nastavení");
            var readBackMetric = BuildDashboardMetric("Ověření", $"{loaded}/{_tweaks.Count}", "Načtený skutečný stav");
            var activeMetric = BuildDashboardMetric("Změny", active.ToString(), "Aktivní tweak hodnoty");

            _defenderStaticMetrics = BuildDashboardMetricGrid(
                tamperMetric,
                modulesMetric,
                readBackMetric,
                activeMetric);

            if (!TryGetDashboardMetricText(tamperMetric, 1, out _defenderTamperValue) ||
                !TryGetDashboardMetricText(tamperMetric, 2, out _defenderTamperDetail) ||
                !TryGetDashboardMetricText(modulesMetric, 1, out _defenderModulesValue) ||
                !TryGetDashboardMetricText(readBackMetric, 1, out _defenderReadBackValue) ||
                !TryGetDashboardMetricText(activeMetric, 1, out _defenderActiveValue))
                throw new InvalidOperationException("Defender static metric refs could not be captured.");
        }

        UpdateDefenderStaticMetrics(tamper, loaded, active);
    }

    private void UpdateDefenderStaticMetrics(bool tamper, int loaded, int active)
    {
        if (_defenderTamperValue == null ||
            _defenderTamperDetail == null ||
            _defenderModulesValue == null ||
            _defenderReadBackValue == null ||
            _defenderActiveValue == null)
            return;

        _defenderTamperValue.Text = tamper ? "ON" : "OFF / N/A";
        _defenderTamperValue.Foreground = tamper
            ? UiBrush("SpankyWarningBrush")
            : UiBrush("SpankySuccessBrush");
        _defenderTamperDetail.Text = tamper
            ? "Změny mohou být blokované"
            : "Bez aktivního Tamper locku";
        _defenderModulesValue.Text = _tweaks.Count.ToString();
        _defenderReadBackValue.Text = $"{loaded}/{_tweaks.Count}";
        _defenderActiveValue.Text = active.ToString();
    }

    private void PrewarmDefenderStaticShell(bool tamper)
    {
        const string defenderCatalogKey = "defender|";
        if (!_tweakCache.TryGetValue(defenderCatalogKey, out var tweaks))
            _tweakCache[defenderCatalogKey] = tweaks = CatalogService.GetTweaks("defender");
        int loaded = tweaks.Count(x => x.IsLoaded);
        int active = tweaks.Count(x => x.IsLoaded && x.Current);
        var previousTweaks = _tweaks;
        try
        {
            _tweaks = tweaks;
            EnsureDefenderStaticShell(tamper, loaded, active);
            _defenderStaticHero!.Measure(new Windows.Foundation.Size(1100, 190));
            _defenderStaticMetrics!.Measure(new Windows.Foundation.Size(1100, 180));
        }
        finally
        {
            _tweaks = previousTweaks;
        }
    }

    private void AttachDefenderStaticShell(
        bool tamper,
        int loaded,
        int active,
        out long heroMs,
        out long metricsMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EnsureDefenderStaticShell(tamper, loaded, active);

        ContentStack.Children.Add(_defenderStaticHero!);
        heroMs = sw.ElapsedMilliseconds;

        ContentStack.Children.Add(_defenderStaticMetrics!);
        metricsMs = sw.ElapsedMilliseconds - heroMs;
    }

    private void ScheduleDefenderModules(CancellationToken token)
    {
        if (_defenderModulesQueued || _defenderModulesHost == null || _defenderModulesHost.Children.Count >= _tweaks.Count) return;
        _defenderModulesIndex = Math.Max(_defenderModulesIndex, _defenderModulesHost.Children.Count);
        _defenderModulesQueued = true;
        QueueDefenderModuleBatch(token);
    }

    private void QueueDefenderModuleBatch(CancellationToken token)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "defender" || _defenderModulesHost == null)
            {
                _defenderModulesQueued = false;
                return;
            }

            _defenderModulesIndex = Math.Max(_defenderModulesIndex, _defenderModulesHost.Children.Count);
            if (_defenderModulesIndex >= _tweaks.Count)
            {
                _defenderModulesQueued = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int start = _defenderModulesIndex;
            int end = Math.Min(start + 1, _tweaks.Count);
            for (int i = start; i < end; i++)
                _defenderModulesHost.Children.Add(CreateTweakCard(_tweaks[i]));
            _defenderModulesIndex = end;

            LogService.Info("performance",
                $"DEFENDER_MODULES_BATCH start={start} added={end - start} ms={sw.ElapsedMilliseconds} count={_defenderModulesHost.Children.Count}");

            if (_defenderModulesIndex < _tweaks.Count)
                SafeTaskService.Forget(QueueNextDefenderModuleBatchAsync(token), "defender", "Deferred Defender module pacing");
            else
                _defenderModulesQueued = false;
        });
    }

    private async Task QueueNextDefenderModuleBatchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(8, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => QueueDefenderModuleBatch(token));
        }
        catch (OperationCanceledException) { }
    }

    private void ScheduleDefenderActions(bool tamper, CancellationToken token)
    {
        if (_defenderActionsQueued || _defenderActionsHost == null || _defenderActionsHost.Children.Count > 0) return;
        _defenderActionsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _defenderActionsQueued = false;
            if (token.IsCancellationRequested || _defenderActionsHost == null || _defenderActionsHost.Children.Count > 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (tamper)
                _defenderActionsHost.Children.Add(ActionCard("Tamper Protection je zapnutý", "Windows blokuje část změn Defenderu. Otevři Windows Security a vypni Tamper Protection ručně, pokud to systém dovolí.", "Otevřít Windows Security", () => { ProcessRunner.Open("windowsdefender://threatsettings"); return Task.CompletedTask; }));
            _defenderActionsHost.Children.Add(ActionCard("Vypnout Defender AV", "Vytvoří bod obnovení a použije podporované policy/service/task změny. Tamper Protection může část změn odmítnout.", "Vypnout", RunDefenderDisableAsync));
            _defenderActionsHost.Children.Add(ActionCard("Obnovit Defender", "Vrátí policy, služby a naplánované úlohy Defenderu.", "Obnovit", RunDefenderRestoreAsync));
            LogService.Info("performance", $"DEFENDER_ACTIONS_DEFERRED ms={sw.ElapsedMilliseconds} tamper={tamper}");
        });
    }

    internal async Task RunDefenderDeferredActionsSmokeAsync()
    {
        InvalidateDefenderPageCache();
        await ShowSectionAsync("defender");
        int expectedActions = WindowsActions.IsTamperProtectionOn() ? 3 : 2;
        int expectedModules = _tweaks.Count;
        for (int i = 0; i < 40 &&
             ((_defenderActionsHost?.Children.Count ?? 0) < expectedActions ||
              (_defenderModulesHost?.Children.Count ?? 0) < expectedModules); i++)
            await Task.Delay(25);

        int actualActions = _defenderActionsHost?.Children.Count ?? 0;
        int actualModules = _defenderModulesHost?.Children.Count ?? 0;
        if (actualActions < expectedActions)
            throw new InvalidOperationException($"Deferred Defender actions incomplete: expected={expectedActions} actual={actualActions}.");
        if (actualModules < expectedModules)
            throw new InvalidOperationException($"Deferred Defender modules incomplete: expected={expectedModules} actual={actualModules}.");
        LogService.Info("performance",
            $"DEFENDER_DEFERRED_ACTIONS_SMOKE_OK actions={actualActions} modules={actualModules}");
    }

    private async Task DetectDefenderAndRefreshAsync(Models.TweakDefinition[] tweaks, CancellationToken token)
    {
        try
        {
            await _engine.DetectFreshAsync(tweaks, TimeSpan.FromSeconds(20), token);
            if (token.IsCancellationRequested || _section != "defender") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "defender") return;
                RefreshCards();
                if (_defenderPageCache != null)
                    _defenderPageRevisionStamp = CurrentTweakRevisionStamp();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Background Defender detect failed", ex);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "defender") return;
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
            await ShowMessageAsync("Defender", "Tamper Protection je zapnutý. Optimizer ho nebude obcházet. Pokud chceš měnit Defender, proveď to přes podporované Windows Security nastavení.");
            return;
        }
        if (!await ShowStyledConfirmAsync("Vypnout Defender AV?", "Sníží se ochrana proti malwaru. Firewall zůstane beze změny. Pokračovat?", "Vypnout", "Zrušit", "Advanced Defender")) return;
        await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - Before Defender changes");
        await WindowsActions.MaxDisableDefenderAsync();
        InvalidateDefenderPageCache();
        var verify = CatalogService.GetDefenderTweaks().Where(x => x.Id != "def.firewall").ToList();
        await _engine.DetectAsync(verify);
        int confirmed = verify.Count(x => x.IsLoaded && x.Current);
        int unknown = verify.Count(x => !x.IsLoaded);
        string resultText = $"Ověření dokončeno: {confirmed}/{verify.Count} požadovaných změn je potvrzených. Nezjištěno: {unknown}.";
        ShowActionFeedback(
            "Defender AV",
            confirmed == verify.Count && unknown == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
            resultText);
        await ShowMessageAsync("Defender", resultText + " UI se obnoví podle skutečného stavu Windows.");
        await ShowSectionAsync("defender");
    }

    private async Task RunDefenderRestoreAsync()
    {
        await WindowsActions.RestoreDefenderAsync();
        InvalidateDefenderPageCache();
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
        await ShowMessageAsync("Defender", resultText + " UI zobrazí skutečný stav Windows.");
        await ShowSectionAsync("defender");
    }

}

