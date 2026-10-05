using System;
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
    private int _powerPageRefreshInFlight;
    private async Task BuildPowerPlanAsync(CancellationToken token = default)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = PowerPlanStateService.TryGetFreshSnapshot();
        if (snapshot == null)
        {
            // The normal registry-backed path completes synchronously in only a few ms.
            // Use it immediately to avoid a visible loading-shell -> rebuild flash.
            // If powercfg fallback is required, keep the existing non-blocking loading path.
            var readTask = PowerPlanStateService.ReadAsync(CancellationToken.None);
            if (readTask.IsCompletedSuccessfully)
            {
                snapshot = await readTask;
            }
            else
            {
                BuildPowerLoading(token);
                if (Interlocked.CompareExchange(ref _powerPageRefreshInFlight, 1, 0) == 0)
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        SafeTaskService.Forget(LoadPowerSnapshotAndRefreshAsync(token), "power", "Lazy power snapshot"));
                if (perf.ElapsedMilliseconds >= 10)
                    LogService.Info("performance", $"POWER_UI_PHASE mode=loading totalMs={perf.ElapsedMilliseconds}");
                return;
            }
        }
        long snapshotReadyMs = perf.ElapsedMilliseconds;
        if (TryAttachPowerPageCache(token))
        {
            if (perf.ElapsedMilliseconds >= 5)
                LogService.Info("performance", $"POWER_UI_PHASE mode=cache attachMs={perf.ElapsedMilliseconds}");
            return;
        }
        token.ThrowIfCancellationRequested();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _powerActionsHost = actions;
        long dataReadyMs = perf.ElapsedMilliseconds;
        long actionMs = dataReadyMs - snapshotReadyMs;
        ContentStack.Children.Add(BuildModernSectionHero(
            "power",
            "Gaming Power Plan",
            "Výkonový profil, chování CPU a režim spánku s průběžným ověřením.",
            snapshot.ActivePlan,
            "Recommended • HW-aware • login guard"));

        var aiPowerHost = new StackPanel();
        _aiPowerControlsHost = aiPowerHost;
        ContentStack.Children.Add(aiPowerHost);
        SafeTaskService.Forget(LoadAiPowerControlsAsync(aiPowerHost, _navigationGeneration, token),
            "ai-runtime", "Deferred AI power controls");
        var summaryHost = new StackPanel();
        ContentStack.Children.Add(summaryHost);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (summaryHost.Children.Count == 0)
                summaryHost.Children.Add(PowerPlanSummary(snapshot, actions));
        });
        SchedulePowerActions(actions, snapshot);
        long summaryMs = perf.ElapsedMilliseconds - dataReadyMs;
        var recommended = snapshot.Settings.Where(x => !IsAdvancedPowerSetting(x.Id)).ToArray();
        var advanced = snapshot.Settings.Where(x => IsAdvancedPowerSetting(x.Id)).ToArray();
        long splitMs = perf.ElapsedMilliseconds - dataReadyMs - summaryMs;

        var primaryRecommended = recommended.Where(x => !IsAdaptiveDetailPowerSetting(x.Id)).ToArray();
        var adaptiveDetails = recommended.Where(x => IsAdaptiveDetailPowerSetting(x.Id)).ToArray();
        long recommendedHeaderMs = perf.ElapsedMilliseconds - dataReadyMs - summaryMs - splitMs;
        var recommendedDetails = new StackPanel { Spacing = 8 };
        var recommendedHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        _powerRecommendedHost = recommendedHost;
        _powerRecommendedSettings = primaryRecommended;
        _powerRecommendedRendered = 0;
        _powerRecommendedQueued = false;
        recommendedDetails.Children.Add(recommendedHost);
        SchedulePowerRecommendedCards(token);
        long primaryGridMs = perf.ElapsedMilliseconds - dataReadyMs - summaryMs - splitMs - recommendedHeaderMs;
        recommendedDetails.Children.Add(BuildAdaptivePowerDetails(adaptiveDetails));
        ContentStack.Children.Add(BuildFlatSectionHeader(
            $"Recommended detaily ({primaryRecommended.Length + adaptiveDetails.Length})",
            "CPU, EPP, parking, sleep, display a další HW-aware hodnoty"));
        ContentStack.Children.Add(recommendedDetails);
        long adaptiveMs = perf.ElapsedMilliseconds - dataReadyMs - summaryMs - splitMs - recommendedHeaderMs - primaryGridMs;
        long advancedHeaderMs = perf.ElapsedMilliseconds - dataReadyMs - summaryMs - splitMs - recommendedHeaderMs - primaryGridMs - adaptiveMs;
        ContentStack.Children.Add(BuildFlatSectionHeader(
            "Advanced power settings",
            "Ruční volby mimo Recommended profil"));
        var advancedHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        _powerAdvancedHost = advancedHost;
        _powerAdvancedSettings = advanced;
        _powerAdvancedRendered = 0;
        _powerAdvancedQueued = false;
        ContentStack.Children.Add(advancedHost);
        SchedulePowerAdvancedCards(token);
        long advancedGridMs = perf.ElapsedMilliseconds - dataReadyMs - summaryMs - splitMs - recommendedHeaderMs - primaryGridMs - adaptiveMs - advancedHeaderMs;
        CapturePowerPageCache();
        long cacheMs = perf.ElapsedMilliseconds - dataReadyMs - summaryMs - splitMs - recommendedHeaderMs - primaryGridMs - adaptiveMs - advancedHeaderMs - advancedGridMs;
        if (perf.ElapsedMilliseconds >= 5)
            LogService.Info("performance", $"POWER_UI_PHASE mode=rebuild snapshotMs={snapshotReadyMs} actionMs={actionMs} dataMs={dataReadyMs} summaryMs={summaryMs} splitMs={splitMs} recommendedHeaderMs={recommendedHeaderMs} primaryGridMs={primaryGridMs} adaptiveMs={adaptiveMs} advancedHeaderMs={advancedHeaderMs} advancedGridMs={advancedGridMs} cacheMs={cacheMs} totalMs={perf.ElapsedMilliseconds} settings={snapshot.Settings.Count} recommended={recommended.Length} advanced={advanced.Length}");
    }

    private void BuildPowerLoading(CancellationToken token)
    {
        var loading = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(4, 6, 4, 0)
        };
        loading.Children.Add(new TextBlock
        {
            Text = "Gaming Power Plan",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        loading.Children.Add(new TextBlock
        {
            Text = "Načítám aktuální plán napájení a ověřené hodnoty…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        ContentStack.Children.Add(loading);
    }

    private void SchedulePowerActions(StackPanel host, PowerPlanSnapshot snapshot)
    {
        if (host.Children.Count > 0) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (host.Children.Count > 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            host.Children.Add(PrimaryQuickButton("Použít Recommended", ApplyPerformancePowerAsync));
            if (snapshot.SpankyDuplicateCount > 0)
            {
                host.Children.Add(QuickButton("Vyčistit duplicity", async () =>
                {
                    if (!await ShowStyledConfirmAsync(
                            "Vyčistit duplicitní power plány",
                            $"Odstranit {snapshot.SpankyDuplicateCount} neaktivních duplicit Spankyho Performance? Aktivní plán ani jeho hodnoty se nezmění.",
                            "Vyčistit", "Zrušit"))
                        return;

                    string result = await PerformanceProfilesService.CleanupDuplicatePowerPlansAsync(CancellationToken.None);
                    InvalidatePowerPageCache();
                    await ShowStyledInfoAsync("Power Plan", result);
                    await ShowSectionAsync("power");
                }));
            }

            LogService.Info("performance", $"POWER_ACTIONS_DEFERRED ms={sw.ElapsedMilliseconds} buttons={host.Children.Count}");
        });
    }

    private async Task LoadPowerSnapshotAndRefreshAsync(CancellationToken token)
    {
        try
        {
            // Shared snapshot must survive navigation cancellation; refresh UI only when this load actually published fresh state.
            await PowerPlanStateService.ReadAsync(CancellationToken.None);
            if (PowerPlanStateService.TryGetFreshSnapshot() == null || _section != "power") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section == "power") SafeTaskService.Forget(ShowSectionAsync("power"), "power", "Refresh loaded power page");
            });
        }
        catch (Exception ex)
        {
            LogService.Error("power", "Lazy power snapshot failed", ex);
            if (!token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_section != "power") return;
                    ContentStack.Children.Clear();
                    var error = new StackPanel { Spacing = 9, Margin = new Thickness(4, 6, 4, 0) };
                    error.Children.Add(new TextBlock
                    {
                        Text = "Power Plan se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    error.Children.Add(QuickButton("Načíst znovu", async () =>
                    {
                        PowerPlanStateService.InvalidateCache();
                        InvalidatePowerPageCache();
                        await ShowSectionAsync("power");
                    }));
                    ContentStack.Children.Add(error);
                    ShowActionFeedback("Power Plan", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }
        finally { Interlocked.Exchange(ref _powerPageRefreshInFlight, 0); }
    }

    private static bool IsAdvancedPowerSetting(string id) => id is
        "core-park" or "idle-promote" or "idle-demote" or "boost-aggressive" or "epp-zero";

    private static bool IsAdaptiveDetailPowerSetting(string id) => id is
        "cppc-autonomous" or "perf-inc-policy" or "perf-dec-policy" or "perf-inc-threshold" or "perf-dec-threshold" or "perf-check" or
        "core-park-min" or "core-park-max" or "core-park-inc-policy" or "core-park-dec-policy" or "core-park-inc-time" or "core-park-dec-time" or
        "latency-unpark" or "latency-epp" or "latency-perf" or "duty-cycling" or "cpu-idle" or "wake-timers" or "hybrid-sleep" or "away-mode" or
        "video-bias" or "video-mode";

    private static (bool ActiveOk, int Drift, int Total) EvaluateCriticalPowerState(PowerPlanSnapshot snapshot)
    {
        bool activeOk = !string.IsNullOrWhiteSpace(snapshot.SpankyPlanGuid) &&
            string.Equals(snapshot.ActiveGuid, snapshot.SpankyPlanGuid, StringComparison.OrdinalIgnoreCase);
        var critical = snapshot.Settings
            .Where(x => !IsAdvancedPowerSetting(x.Id) && !IsAdaptiveDetailPowerSetting(x.Id) && x.CurrentValue is not null)
            .ToArray();
        return (activeOk, critical.Count(x => !x.IsApplied), critical.Length);
    }

    private UIElement BuildAdaptivePowerDetails(PowerSettingState[] settings)
    {
        var root = new StackPanel { Spacing = 8 };
        var detailsHost = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var toggle = new Button
        {
            Content = $"Adaptive CPU V3 — rozšířené volby ({settings.Length})  ▾",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8),
            Background = SectionHeaderBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            Foreground = TextPrimaryBrush()
        };
        bool loaded = false;
        bool expanded = false;
        toggle.Click += (_, _) =>
        {
            expanded = !expanded;
            if (expanded && !loaded)
            {
                loaded = true;
                detailsHost.Children.Add(PowerSettingsGrid(settings));
            }
            detailsHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = $"Adaptive CPU V3 — rozšířené volby ({settings.Length})  {(expanded ? "▴" : "▾")}";
        };
        root.Children.Add(toggle);
        root.Children.Add(detailsHost);
        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 8, 0, 0),
            Child = root
        };
    }
    private UIElement PowerPlanSummary(PowerPlanSnapshot snapshot, UIElement actions)
    {
        var adaptiveProfile = PerformanceProfilesService.GetAdaptivePowerProfile();
        var recommended = snapshot.Settings
            .Where(x => !IsAdvancedPowerSetting(x.Id) && x.CurrentValue is not null)
            .ToArray();

        int active = recommended.Count(x => x.IsApplied);
        int drift = recommended.Count(x => !x.IsApplied);
        int total = recommended.Length;
        bool spankyActive = !string.IsNullOrWhiteSpace(snapshot.SpankyPlanGuid) &&
            string.Equals(snapshot.ActiveGuid, snapshot.SpankyPlanGuid, StringComparison.OrdinalIgnoreCase);

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new Border
        {
            Padding = new Thickness(13, 10, 13, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = spankyActive ? ButtonVioletBorderBrush() : SectionBorderBrush(),
            Background = spankyActive ? ButtonVioletSoftBrush() : SurfaceAltBrush(),
            Child = new TextBlock
            {
                Text = spankyActive ? "Spankyho Performance — AKTIVNÍ" : $"Aktivní plán: {snapshot.ActivePlan}",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = spankyActive ? TextPrimaryBrush() : WarningBrush(),
                TextWrapping = TextWrapping.Wrap
            }
        });
        root.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric(
                "Active",
                spankyActive ? "Gaming" : snapshot.ActivePlan,
                spankyActive ? "Spankyho Performance je aktivní" : "Aktivní je jiný plán",
                spankyActive ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric(
                "Recommended",
                $"{active}/{Math.Max(1, total)}",
                total > 0 ? $"{Math.Round(active * 100d / total):0}% odpovídá profilu" : "Žádné dostupné hodnoty",
                drift == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric(
                "Drift",
                drift.ToString(),
                drift == 0 ? "Bez odchylek" : "Hodnot k opravě",
                drift == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric(
                "CPU profil",
                $"EPP {adaptiveProfile.Epp}",
                $"{adaptiveProfile.Architecture} • parking min {adaptiveProfile.CoreMin}%")));

        AdaptivePowerRuntimeStatus? runtime = AdaptivePowerRuntimeService.ReadStatus();
        if (runtime != null && DateTime.UtcNow - runtime.UpdatedUtc < TimeSpan.FromSeconds(20))
        {
            string temp = runtime.TemperatureC is double c ? $"{c:0.0} °C" : "— °C";
            string power = runtime.PackagePowerW is double w ? $"{w:0.0} W" : "— W";
            string clock = runtime.ClockMHz is double mhz ? $"{mhz:0} MHz" : "— MHz";
            string blocker = string.IsNullOrWhiteSpace(runtime.Blocker) ? "none" : runtime.Blocker;

            root.Children.Add(new Border
            {
                Padding = new Thickness(11, 8, 11, 8),
                CornerRadius = new CornerRadius(9),
                Background = SurfaceAltBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = runtime.GameActive
                        ? $"Adaptive runtime: PERFORMANCE • Diablo Immortal lock • load {runtime.LoadPercent:0.0}% • EPP {runtime.Epp} • boost {runtime.Boost} • {temp} • {clock} • {power}"
                        : $"Adaptive runtime: {runtime.Mode} • load {runtime.LoadPercent:0.0}% • EPP {runtime.Epp} • boost {runtime.Boost} • {temp} • {clock} • {power} • idle blocker: {blocker}",
                    FontSize = 10.5,
                    Foreground = TextSecondaryBrush(),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        var progressRow = new Grid { ColumnSpacing = 12 };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition());
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressRow.Children.Add(PowerProgressBar(active, Math.Max(1, total)));
        var pct = new TextBlock
        {
            Text = total > 0 ? $"{Math.Round(active * 100d / total):0}%" : "0%",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pct, 1);
        progressRow.Children.Add(pct);
        root.Children.Add(progressRow);

        var guardStatus = new TextBlock
        {
            Text = "Auto-ověření: kontroluji aktivní plán a ochranu po startu/přihlášení…",
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(guardStatus);
        ScheduleAutomaticPowerGuardVerification(snapshot, guardStatus);

        root.Children.Add(actions);

        return BuildDashboardCard(root, new Thickness(14, 12, 14, 12));
    }

    private void ScheduleAutomaticPowerGuardVerification(
        PowerPlanSnapshot snapshot,
        TextBlock statusText)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            SafeTaskService.Forget(
                VerifyAndRepairPowerGuardAsync(snapshot, statusText),
                "power",
                "Automatic power plan guard verification"));
    }

    private async Task VerifyAndRepairPowerGuardAsync(
        PowerPlanSnapshot snapshot,
        TextBlock statusText)
    {
        string? targetGuid = snapshot.SpankyPlanGuid;
        if (string.IsNullOrWhiteSpace(targetGuid))
        {
            if (_section == "power")
            {
                statusText.Text = "Auto-ověření: Spankyho Performance ještě není vytvořený.";
                statusText.Foreground = TextSecondaryBrush();
            }
            return;
        }

        try
        {
            var status = await PowerPlanGuardService.ReadStatusAsync(
                targetGuid, CancellationToken.None);
            bool targetActive = string.Equals(
                snapshot.ActiveGuid, targetGuid, StringComparison.OrdinalIgnoreCase);
            bool guardArmed =
                string.Equals(status.ConfiguredGuid, targetGuid, StringComparison.OrdinalIgnoreCase) &&
                (status.LogonConfigured || status.BootConfigured);

            if (targetActive && (!status.LogonConfigured || !status.BootConfigured))
            {
                await PowerPlanGuardService.EnsureAsync(targetGuid, CancellationToken.None);
                status = await PowerPlanGuardService.ReadStatusAsync(
                    targetGuid, CancellationToken.None);
                LogService.Info("power",
                    $"POWER_AUTO_GUARD_REPAIRED active=true boot={status.BootConfigured} logon={status.LogonConfigured}");
            }
            else if (!targetActive && guardArmed)
            {
                await PowerPlanGuardService.EnsureAsync(targetGuid, CancellationToken.None);
                await PowerPlanGuardService.RunNowAsync(CancellationToken.None);
                await Task.Delay(1100);
                PowerPlanStateService.InvalidateCache();
                var repaired = await PowerPlanStateService.ReadAsync(
                    CancellationToken.None, forceRefresh: true);
                targetActive = string.Equals(
                    repaired.ActiveGuid, targetGuid, StringComparison.OrdinalIgnoreCase);
                status = await PowerPlanGuardService.ReadStatusAsync(
                    targetGuid, CancellationToken.None);

                LogService.Info("power",
                    $"POWER_AUTO_GUARD_RESTORE active={targetActive} boot={status.BootConfigured} logon={status.LogonConfigured}");

                if (targetActive && _section == "power")
                {
                    InvalidatePowerPageCache();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_section == "power")
                            SafeTaskService.Forget(
                                ShowSectionAsync("power"),
                                "power",
                                "Refresh auto-restored power plan");
                    });
                }
            }

            if (_section != "power") return;

            string activeText = targetActive ? "plán aktivní" : "jiný plán aktivní";
            string bootText = status.BootConfigured ? "po startu OK" : "po startu chybí";
            string logonText = status.LogonConfigured ? "po přihlášení OK" : "po přihlášení chybí";
            string lastText = status.LastRunOk
                ? "poslední kontrola OK"
                : status.LastResult;

            statusText.Text =
                $"Auto-ověření: {activeText} • {bootText} • {logonText} • {lastText}";
            statusText.Foreground =
                targetActive && status.BootConfigured && status.LogonConfigured
                    ? SuccessBrush()
                    : WarningBrush();
        }
        catch (Exception ex)
        {
            LogService.Error("power", "Automatic power guard verification failed", ex);
            if (_section == "power")
            {
                statusText.Text = "Auto-ověření Power Planu selhalo: " + ex.Message;
                statusText.Foreground = WarningBrush();
            }
        }
    }

    private UIElement PowerProgressBar(int value, int maximum)
    {
        int safeMax = Math.Max(1, maximum);
        int safeValue = Math.Clamp(value, 0, safeMax);
        var grid = new Grid { Height = 7, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, safeValue), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, safeMax - safeValue), GridUnitType.Star) });
        var track = new Border { Background = BorderThemeBrush(), CornerRadius = new CornerRadius(4) };
        Grid.SetColumnSpan(track, 2); grid.Children.Add(track);
        var fill = new Border { Background = ButtonVioletBrush(), CornerRadius = new CornerRadius(4) };
        Grid.SetColumn(fill, 0); grid.Children.Add(fill);
        return grid;
    }

    private void SchedulePowerRecommendedCards(CancellationToken token)
    {
        if (_powerRecommendedQueued ||
            _powerRecommendedHost == null ||
            _powerRecommendedSettings == null ||
            _powerRecommendedRendered >= _powerRecommendedSettings.Length)
            return;

        _powerRecommendedQueued = true;
        QueuePowerRecommendedBatch(token);
    }

    private void QueuePowerRecommendedBatch(CancellationToken token)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                _section != "power" ||
                _powerRecommendedHost == null ||
                _powerRecommendedSettings == null)
            {
                _powerRecommendedQueued = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int start = _powerRecommendedRendered;
            int end = Math.Min(start + 3, _powerRecommendedSettings.Length);
            for (int i = start; i < end; i++)
            {
                var card = PowerSettingCard(_powerRecommendedSettings[i]);
                card.HorizontalAlignment = HorizontalAlignment.Stretch;
                _powerRecommendedHost.Children.Add(card);
            }
            _powerRecommendedRendered = end;
            LogService.Info("performance",
                $"POWER_RECOMMENDED_BATCH start={start} added={end - start} ms={sw.ElapsedMilliseconds} total={_powerRecommendedRendered}");

            if (end < _powerRecommendedSettings.Length)
                QueuePowerRecommendedBatch(token);
            else
                _powerRecommendedQueued = false;
        });
    }

    private void SchedulePowerAdvancedCards(CancellationToken token)
    {
        if (_powerAdvancedQueued ||
            _powerAdvancedHost == null ||
            _powerAdvancedSettings == null ||
            _powerAdvancedRendered >= _powerAdvancedSettings.Length)
            return;

        _powerAdvancedQueued = true;
        QueuePowerAdvancedBatch(token);
    }

    private void QueuePowerAdvancedBatch(CancellationToken token)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                _section != "power" ||
                _powerAdvancedHost == null ||
                _powerAdvancedSettings == null)
            {
                _powerAdvancedQueued = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int start = _powerAdvancedRendered;
            int end = Math.Min(start + 1, _powerAdvancedSettings.Length);
            for (int i = start; i < end; i++)
            {
                var card = PowerSettingCard(_powerAdvancedSettings[i]);
                card.HorizontalAlignment = HorizontalAlignment.Stretch;
                _powerAdvancedHost.Children.Add(card);
            }
            _powerAdvancedRendered = end;
            LogService.Info("performance",
                $"POWER_ADVANCED_BATCH start={start} added={end - start} ms={sw.ElapsedMilliseconds} total={_powerAdvancedRendered}");

            if (end < _powerAdvancedSettings.Length)
                QueuePowerAdvancedBatch(token);
            else
                _powerAdvancedQueued = false;
        });
    }

    private UIElement PowerSettingsGrid(PowerSettingState[] settings)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        long previousMs = 0;
        long firstCardMs = 0;
        long maxCardMs = 0;
        string maxCardId = string.Empty;
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < settings.Length; i++)
        {
            var setting = settings[i];
            var card = PowerSettingCard(setting);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(card);
            long now = perf.ElapsedMilliseconds;
            long cardMs = now - previousMs;
            previousMs = now;
            if (i == 0) firstCardMs = cardMs;
            if (cardMs > maxCardMs) { maxCardMs = cardMs; maxCardId = setting.Id; }
        }
        if (perf.ElapsedMilliseconds >= 8)
            LogService.Info("performance", $"POWER_GRID_PHASE count={settings.Length} firstCardMs={firstCardMs} maxCardMs={maxCardMs} maxCardId={maxCardId} totalMs={perf.ElapsedMilliseconds}");
        return stack;
    }

    private Border PowerSettingCard(PowerSettingState state)
    {
        var root = new StackPanel { Spacing = 8 };
        var top = new Grid { ColumnSpacing = 10 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = state.Title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        var badge = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 7, 3),
            Background = state.IsApplied ? SuccessBrush() : WarningBrush(),
            VerticalAlignment = VerticalAlignment.Top
        };
        badge.Child = new TextBlock
        {
            Text = state.CurrentValue is null ? "NEDOSTUPNÉ" : state.IsApplied ? "AKTIVNÍ" : "NEAKTIVNÍ",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = OnAccentTextBrush()
        };
        Grid.SetColumn(badge, 1);
        top.Children.Add(badge);
        root.Children.Add(top);

        root.Children.Add(new TextBlock
        {
            Text = state.Description,
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var current = PowerValueBlock("AKTUÁLNĚ", state.CurrentText);
        current.Width = 210;
        var target = PowerValueBlock("RECOMMENDED", state.RecommendedText);
        target.Width = 210;
        values.Children.Add(current);
        values.Children.Add(target);
        root.Children.Add(values);

        var apply = new Button
        {
            Content = state.IsApplied ? "Ověřeno" : "Nastavit cílovou hodnotu",
            IsEnabled = !state.IsApplied && state.CurrentValue is not null,
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(6),
            Background = state.IsApplied ? SurfaceBrush() : AccentBrush(),
            Foreground = TextPrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        apply.Click += (_, _) => SafeTaskService.Forget(
            ApplyPowerSettingAsync(), "power", "Apply setting " + state.Id);
        async Task ApplyPowerSettingAsync()
        {
            try
            {
                apply.IsEnabled = false;
                await WindowsActions.SetCurrentPowerOptionAsync(state.Subgroup, state.Setting, state.RecommendedValue);
                InvalidatePowerPageCache();
                PowerPlanStateService.InvalidateCache();
                var refreshed = await PowerPlanStateService.ReadAsync(forceRefresh: true);
                var verified = refreshed.Settings.FirstOrDefault(x => x.Id.Equals(state.Id, StringComparison.OrdinalIgnoreCase));
                if (verified?.IsApplied == true)
                    ShowActionFeedback(state.Title, ActionFeedbackKind.Verified, $"Read-back: {verified.CurrentText}.");
                else
                    ShowActionFeedback(state.Title, ActionFeedbackKind.Failed, "Read-back nepotvrdil cílovou hodnotu.");
                await ShowSectionAsync("power");
            }
            catch (Exception ex)
            {
                LogService.Error("Power setting apply failed: " + state.Id, ex);
                await ShowMessageAsync("Power Plan", ex.Message);
                apply.IsEnabled = true;
            }
        }
        root.Children.Add(apply);

        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 16, 12, 16),
            Child = root,
            MinHeight = 155
        };
    }
    private StackPanel PowerValueBlock(string label, string value)
    {
        var root = new StackPanel { Spacing = 2 };
        root.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            CharacterSpacing = 90,
            Foreground = TextSecondaryBrush(),
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            Foreground = TextPrimaryBrush(),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        return root;
    }
}
