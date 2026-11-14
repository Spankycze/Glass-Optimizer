using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void AddNicAdvancedCapabilityCard(StackPanel host, IReadOnlyDictionary<string, string> caps,
        string title, string description, string[] aliases, bool? recommendedOn = null, bool allowAbTest = false)
    {
        string current = CapabilityValue(caps, title);
        if (current.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info("network", $"NIC capability hidden-unsupported={title}");
            return;
        }

        bool isOn = NicCapabilityIsOn(current);

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        string recommendation = recommendedOn.HasValue
            ? $"Recommended: {(recommendedOn.Value ? "ON" : "OFF")}."
            : allowAbTest ? "Doporučené: TEST / A/B." : "Recommended: driver default.";
        var detail = new TextBlock
        {
            Text = $"Aktuální: {current} • {recommendation} Důvod: {description}",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        textStack.Children.Add(detail);
        row.Children.Add(textStack);

        var stateText = new TextBlock
        {
            Text = isOn ? "ON" : "OFF",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = isOn ? SuccessBrush() : TextSecondaryBrush()
        };
        var switchTrack = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = isOn ? UnifiedToggleOnTrack() : UnifiedToggleOffTrack(),
            BorderBrush = isOn
                ? ThemeColor(158, 116, 207, 250)
                : ThemeColor(96, 130, 171, 209),
            BorderThickness = new Thickness(1)
        };
        var switchKnob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = ThemeColor(252, 252, 255),
            HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(1)
        };
        var switchVisual = new Grid { Width = 44, Height = 22 };
        switchVisual.Children.Add(switchTrack);
        switchVisual.Children.Add(switchKnob);
        var switchButton = new Button
        {
            Content = switchVisual,
            Padding = new Thickness(0),
            MinWidth = 44,
            MinHeight = 22,
            Width = 44,
            Height = 22,
            Background = null,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // This Button is a transparent HIT TARGET around a custom 44x22 switch,
        // not an action button. Prevent global blue Button visual-state resources
        // from covering the glass track during hover/press.
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            switchButton.Resources["ButtonBackground" + state] = ThemeColor(0, 0, 0, 0);
            switchButton.Resources["ButtonBorderBrush" + state] = ThemeColor(0, 0, 0, 0);
        }
        ToolTipService.SetToolTip(switchButton, title);
        EnsureAccessibleButton(switchButton, title);

        bool busy = false;
        bool currentState = isOn;

        void SetToggleState(bool value)
        {
            currentState = value;
            switchTrack.Background = value ? UnifiedToggleOnTrack() : UnifiedToggleOffTrack();
            switchTrack.BorderBrush = value
                ? ThemeColor(158, 116, 207, 250)
                : ThemeColor(96, 130, 171, 209);
            switchKnob.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            stateText.Text = value ? "ON" : "OFF";
            stateText.Foreground = value ? SuccessBrush() : TextSecondaryBrush();
        }

        async Task ApplyToggleAsync(bool requested)
        {
            try
            {
                switchButton.IsEnabled = false;
                detail.Text = requested ? "Zapínám a ověřuji skutečný stav…" : "Vypínám a ověřuji skutečný stav…";
                LogService.Info("network", $"NIC_TOGGLE_CLICK name={title} requested={requested}");
                string result = await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, requested);
                PerformanceProfilesService.InvalidateNicCache();

                var freshCaps = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                string freshValue = CapabilityValue(freshCaps, title);
                bool actual = NicCapabilityIsOn(freshValue);
                SetToggleState(actual);

                if (actual != requested)
                    throw new InvalidOperationException($"{title}: driver změnu nepotvrdil ({freshValue}).");

                detail.Text = $"Aktuální: {freshValue} • {recommendation} Důvod: {description}";
                ShowActionFeedback(title, ActionFeedbackKind.Verified, result + " • Ověřeno: " + freshValue);
                LogService.Info("network", $"NIC_TOGGLE_VERIFIED name={title} requested={requested} actual={actual} value={freshValue}");
            }
            catch (Exception ex)
            {
                LogService.Error("network", $"Advanced NIC toggle failed: {title}", ex);
                PerformanceProfilesService.InvalidateNicCache();
                try
                {
                    var freshCaps = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                    string freshValue = CapabilityValue(freshCaps, title);
                    SetToggleState(NicCapabilityIsOn(freshValue));
                    detail.Text = $"Aktuální: {freshValue} • změna se nepodařila: {ex.Message}";
                }
                catch
                {
                    SetToggleState(isOn);
                    detail.Text = "Změna se nepodařila: " + ex.Message;
                }
                ShowActionFeedback(title, ActionFeedbackKind.Failed, ex.Message);
            }
            finally
            {
                switchButton.IsEnabled = true;
                busy = false;
            }
        }

        switchButton.Click += (_, _) =>
        {
            if (busy) return;
            bool requested = !currentState;
            SetToggleState(requested);
            busy = true;
            SafeTaskService.Forget(ApplyToggleAsync(requested), "network", "Advanced NIC toggle " + title);
        };

        var rightActions = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        rightActions.Children.Add(switchButton);
        rightActions.Children.Add(stateText);
        if (allowAbTest)
        {
            rightActions.Children.Add(QuickButton("A/B test", async () =>
            {
                if (busy) return;
                bool original = currentState;
                var mutation = new NicAbMutationGuard();
                if (!await ShowStyledConfirmAsync(
                    "NIC A/B test",
                    $"{title}\n\nOptimizer změří baseline, přepne pouze tuto vlastnost, restartuje síťový adaptér a změří latency/jitter/loss znovu. Pokud výsledek nebude lepší, původní hodnota se automaticky obnoví.",
                    "Spustit test", "Zrušit", "Síť může na několik sekund vypadnout.")) return;

                busy = true;
                switchButton.IsEnabled = false;
                try
                {
                    detail.Text = "A/B: měřím baseline…";
                    NetworkDiagnosticsService.Invalidate();
                    var baseline = await NetworkDiagnosticsService.MeasureAsync(samples: 10);

                    if (baseline.Received == 0)
                        throw new InvalidOperationException("ICMP baseline nemá odpovědi; A/B test nelze spolehlivě vyhodnotit.");

                    bool trialValue = !original;
                    detail.Text = $"A/B: přepínám na {(trialValue ? "ON" : "OFF")}…";
                    await mutation.ApplyTrialAsync(async () =>
                        { await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, trialValue); });
                    PerformanceProfilesService.InvalidateNicCache();
                    NetworkDiagnosticsService.Invalidate();
                    await Task.Delay(800);

                    detail.Text = "A/B: měřím testovací variantu…";
                    var trial = await NetworkDiagnosticsService.MeasureAsync(samples: 10);
                    if (trial.Received == 0)
                        throw new InvalidOperationException("Testovací varianta nemá ICMP odpovědi.");

                    double baselineScore = NicAbScore(baseline);
                    double trialScore = NicAbScore(trial);
                    double requiredGain = Math.Max(0.5, baselineScore * 0.03);
                    bool improved =
                        trial.LossPercent <= baseline.LossPercent &&
                        trialScore + requiredGain < baselineScore;

                    if (!improved)
                    {
                        detail.Text = "A/B: test nebyl lepší, obnovuji původní stav…";
                        await mutation.RestoreAsync(async () =>
                            { await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, original); });
                        PerformanceProfilesService.InvalidateNicCache();
                    }

                    NetworkDiagnosticsService.Invalidate();
                    var fresh = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                    string freshValue = CapabilityValue(fresh, title);
                    if (string.IsNullOrWhiteSpace(freshValue) ||
                        freshValue.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                        freshValue.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"{title}: NIC driver read-back is unavailable.");
                    bool actual = NicCapabilityIsOn(freshValue);
                    mutation.ConfirmReadback(actual, expected: improved ? !original : original,
                        restoredOriginal: !improved);
                    SetToggleState(actual);

                    string result = improved
                        ? $"TEST LEPŠÍ — ponecháno {(actual ? "ON" : "OFF")}. Baseline {baseline.AvgMs:0.0} ms / jitter {baseline.JitterMs:0.0} / loss {baseline.LossPercent:0.0}% → test {trial.AvgMs:0.0} ms / jitter {trial.JitterMs:0.0} / loss {trial.LossPercent:0.0}%."
                        : $"TEST HORŠÍ/STEJNÝ — vráceno na {(actual ? "ON" : "OFF")}. Baseline score {baselineScore:0.0}, test score {trialScore:0.0}.";
                    detail.Text = result;
                    ShowActionFeedback(title + " A/B", ActionFeedbackKind.Verified, result);
                    LogService.Info("network", $"NIC_AB_TEST name={title} original={original} keptTrial={improved} baselineScore={baselineScore:0.00} trialScore={trialScore:0.00}");
                }
                catch (Exception ex)
                {
                    Exception? rollbackError = null;
                    if (mutation.MayHaveChanged)
                    {
                        try
                        {
                            await mutation.RestoreAsync(async () =>
                                { await PerformanceProfilesService.SetNicAdvancedToggleAsync(aliases, original); });
                        }
                        catch (Exception restoreEx)
                        {
                            rollbackError = restoreEx;
                            LogService.Error("network", "NIC A/B rollback failed: " + title, restoreEx);
                        }
                    }

                    PerformanceProfilesService.InvalidateNicCache();
                    NetworkDiagnosticsService.Invalidate();
                    string readbackResult;
                    try
                    {
                        var verifiedCaps = await PerformanceProfilesService.GetNicCapabilitiesAsync();
                        string verifiedValue = CapabilityValue(verifiedCaps, title);
                        if (string.IsNullOrWhiteSpace(verifiedValue) ||
                            verifiedValue.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                            verifiedValue.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Driver value cannot be verified.");
                        bool verifiedOn = NicCapabilityIsOn(verifiedValue);
                        SetToggleState(verifiedOn);
                        if (verifiedOn == original)
                        {
                            mutation.ConfirmReadback(verifiedOn, original, restoredOriginal: true);
                            readbackResult = "Původní stav ověřen.";
                        }
                        else
                            readbackResult = "VAROVÁNÍ: původní stav síťové karty nebyl obnoven.";
                    }
                    catch (Exception readbackEx)
                    {
                        LogService.Error("network", "NIC A/B recovery read-back failed: " + title, readbackEx);
                        readbackResult = "VAROVÁNÍ: stav síťové karty nelze ověřit.";
                    }
                    if (rollbackError != null)
                        readbackResult += " Obnovení ovladače selhalo: " + rollbackError.Message;
                    detail.Text = "A/B test selhal: " + ex.Message + " " + readbackResult;
                    ShowActionFeedback(title + " A/B", ActionFeedbackKind.Failed, detail.Text);
                }
                finally
                {
                    switchButton.IsEnabled = true;
                    busy = false;
                }
            }));
        }

        Grid.SetColumn(rightActions, 1);
        row.Children.Add(rightActions);

        host.Children.Add(new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = row
        });
    }
    private void AddNicValueAdvisor(
        StackPanel host,
        string title,
        string current,
        string recommended,
        string reason,
        OptimizationAdvisorKind kind = OptimizationAdvisorKind.Test)
    {
        if (current.Equals("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            kind = OptimizationAdvisorKind.Info;
            recommended = "Driver nepodporuje";
        }
        host.Children.Add(BuildOptimizationAdvisorRow(title, current, recommended, reason, kind));
    }

    private static bool NicCapabilityIsOn(string value) =>
        !value.Contains("Disabled", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Off", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("No", StringComparison.OrdinalIgnoreCase);

    // NIC property mutations may succeed at driver level and then throw during
    // verification. Mark the attempt *before* awaiting, and retain that state
    // until the original setting has been confirmed by an independent read.
    private sealed class NicAbMutationGuard
    {
        internal bool MayHaveChanged { get; private set; }

        internal async Task ApplyTrialAsync(Func<Task> apply)
        {
            MayHaveChanged = true;
            await apply();
        }

        internal async Task RestoreAsync(Func<Task> restore)
        {
            if (MayHaveChanged)
                await restore();
        }

        internal void ConfirmReadback(bool actual, bool expected, bool restoredOriginal)
        {
            if (actual != expected)
                throw new InvalidOperationException(
                    "NIC A/B read-back did not confirm the expected driver setting.");
            if (restoredOriginal)
                MayHaveChanged = false;
        }
    }

    private static async Task RunNicAbSafetySmokeAsync()
    {
        var guard = new NicAbMutationGuard();
        bool driverState = false;
        try
        {
            await guard.ApplyTrialAsync(() =>
            {
                driverState = true;
                throw new InvalidOperationException("synthetic post-write driver failure");
            });
            throw new InvalidOperationException("Synthetic post-write failure was ignored.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic post-write driver failure") { }
        if (!guard.MayHaveChanged || !driverState)
            throw new InvalidOperationException("NIC A/B missed partially applied driver mutation.");

        bool simulatedRollbackFailed = false;
        try
        {
            await guard.RestoreAsync(() =>
            {
                simulatedRollbackFailed = true;
                throw new InvalidOperationException("synthetic rollback failure");
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic rollback failure") { }
        if (!simulatedRollbackFailed || !guard.MayHaveChanged)
            throw new InvalidOperationException("NIC A/B lost pending rollback after restore failure.");
        await guard.RestoreAsync(() => { driverState = false; return Task.CompletedTask; });
        guard.ConfirmReadback(driverState, expected: false, restoredOriginal: true);
        if (guard.MayHaveChanged)
            throw new InvalidOperationException("NIC A/B retained a confirmed completed rollback.");

        await guard.ApplyTrialAsync(() => { driverState = true; return Task.CompletedTask; });
        bool mismatchRejected = false;
        try { guard.ConfirmReadback(driverState, expected: false, restoredOriginal: true); }
        catch (InvalidOperationException) { mismatchRejected = true; }
        if (!mismatchRejected || !guard.MayHaveChanged)
            throw new InvalidOperationException("NIC A/B accepted incorrect driver read-back.");

        await guard.RestoreAsync(() => { driverState = false; return Task.CompletedTask; });
        guard.ConfirmReadback(driverState, expected: false, restoredOriginal: true);
        LogService.Info("network",
            "NIC_AB_SAFETY_SMOKE_OK partialWriteRollback=true rollbackRetry=true readbackRequired=true nicWrites=0");
    }

    private static double NicAbScore(NetworkLatencyReport report) =>
        report.AvgMs + (report.JitterMs * 1.5) + (report.LossPercent * 10.0);
    private async Task LoadNetworkLiveStatusAsync(StackPanel live, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            var nicTask = SystemInsightService.GetActiveNetworkAsync(token);
            var capsTask = PerformanceProfilesService.GetNicCapabilitiesAsync(token);
            // Sdílí stejný 4-sample cache key jako hlavní network telemetry,
            // takže jedna návštěva stránky nespouští dvě oddělené ICMP série.
            var latencyTask = NetworkDiagnosticsService.MeasureAsync(samples: 4, token: token);
            await Task.WhenAll(nicTask, latencyTask);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "network" ||
                !ReferenceEquals(_networkLiveBody, live)) return;
            var nic = await nicTask;
            var latency = await latencyTask;
            IReadOnlyDictionary<string, string>? caps = null;
            try { caps = await capsTask; }
            catch (Exception capEx) { LogService.Error("network", "Live NIC capability read failed", capEx); }
            string liveRss = caps != null && caps.TryGetValue("RSS", out var rssState) ? rssState : "Unknown";
            string liveRsc = caps != null && caps.TryGetValue("RSC", out var rscState) ? rscState : "Unknown";
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "network" ||
                    !ReferenceEquals(_networkLiveBody, live)) return;
                live.Children.Clear();
                live.Children.Add(BuildPageSummaryStrip(
                    ("Link", nic == null ? "N/A" : $"{nic.SpeedMbps:0} Mb/s", nic == null ? "SpankyWarningBrush" : "SpankySuccessBrush"),
                    ("Latency", latency.Received == 0 ? "N/A" : $"{latency.AvgMs:0.0} ms", latency.Received == 0 ? "SpankyWarningBrush" : "SpankySuccessBrush"),
                    ("Jitter", latency.Received == 0 ? "N/A" : $"{latency.JitterMs:0.0} ms", latency.JitterMs > 8 ? "SpankyWarningBrush" : "SpankySuccessBrush"),
                    ("Loss", latency.Received == 0 ? "ICMP blokováno" : $"{latency.LossPercent:0.0} %", latency.LossPercent > 0 ? "SpankyWarningBrush" : "SpankySuccessBrush")));
                if (nic != null)
                    live.Children.Add(new TextBlock { Text = $"{nic.Description} • MTU {nic.Mtu} • RSS {liveRss} • RSC {liveRsc} • {nic.DriverProvider} {nic.DriverVersion}", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Network V3 live status failed", ex);
            if (!token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested ||
                        generation != _navigationGeneration ||
                        _section != "network" ||
                        !ReferenceEquals(_networkLiveBody, live)) return;
                    live.Children.Clear();
                    live.Children.Add(new TextBlock
                    {
                        Text = "Aktuální stav sítě se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    live.Children.Add(QuickButton("Načíst znovu", () =>
                        LoadNetworkLiveStatusAsync(live, _loadCts?.Token ?? _windowLifetimeCts.Token)));
                    ShowActionFeedback("Síť — live stav", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }
    }

    private async Task ApplyNicPerformanceAsync()
    {
        string current = await PerformanceProfilesService.GetNicPerformanceSummaryAsync();
        if (!await ShowStyledConfirmAsync("NIC Max Performance", current + "\n\nSíťový adaptér se krátce restartuje a spojení může na několik sekund vypadnout.", "Použít profil", "Zrušit", "Realtek / Intel síťový profil")) return;
        int exitCode = await RunNicProgressPopupAsync();
        if (exitCode == 0)
        {
            InvalidateUiDataCache("network");
            RefreshNetworkLiveAfterMutation(_loadCts?.Token ?? _windowLifetimeCts.Token);
            if (_section == "network" && _networkAdvancedBody != null)
            {
                _networkAdvancedLoadGeneration++;
                _networkAdvancedLoadQueued = false;
                _networkAdvancedBody.Children.Clear();
                ScheduleNetworkAdvancedControls(_networkAdvancedBody);
            }
            try
            {
                string readback = await PerformanceProfilesService.GetNicTuningReadbackAsync();
                LogService.Info("NIC Max Performance completed OK");
                ShowActionFeedback("NIC Recommended", ActionFeedbackKind.Verified, "Profil byl aplikován a ověřen.");
                await ShowStyledInfoAsync("NIC Max Performance — ověřeno", readback);
            }
            catch (Exception ex)
            {
                LogService.Error("network", "NIC post-apply read-back failed", ex);
                ShowActionFeedback("NIC Recommended", ActionFeedbackKind.Failed, "Profil byl aplikován, ale finální ověření selhalo.");
                await ShowStyledInfoAsync("NIC Max Performance — ověření selhalo", ex.Message);
            }
        }
        else if (exitCode == -2)
        {
            LogService.Info("network", "NIC Max Performance was cancelled from the progress popup.");
        }
        else
        {
            LogService.Error("NIC Max Performance failed | ExitCode=" + exitCode);
            ShowActionFeedback("NIC Recommended", ActionFeedbackKind.Failed, "Profil se nepodařilo aplikovat nebo ověřit.");
        }
    }

    internal async Task RunNetworkDeferredRenderSmokeAsync()
    {
        await ShowSectionAsync("network");
        // Force navigation-generation churn while low-priority DNS and tweak
        // callbacks may still be queued. Cached hosts must recover without
        // stale callbacks clearing the new generation's queue ownership.
        for (int hop = 0; hop < 3; hop++)
        {
            await ShowSectionAsync("settings");
            await ShowSectionAsync("network");
        }
        int expectedTweaks = _tweaks.Count;
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            bool advancedReady = _networkAdvancedBody != null &&
                !(_networkAdvancedBody.Children.Count == 1 &&
                  _networkAdvancedBody.Children[0] is TextBlock loading &&
                  loading.Text.StartsWith("Načítám", StringComparison.OrdinalIgnoreCase));
            if (advancedReady &&
                (_networkDnsPanelHost?.Children.Count ?? 0) == 1 &&
                (_networkTelemetryShellHost?.Children.Count ?? 0) == 1 &&
                _networkToolsCard != null && ContentStack.Children.Contains(_networkToolsCard) &&
                _networkToolsRow?.Children.Count == 3 &&
                (UseCompactSectionChrome || _networkHeroArtHost?.Children.Count == 1) &&
                CountClassicTweakCards(_networkTweakHost) == expectedTweaks &&
                !_networkDnsPanelQueued && !_networkTweakQueued &&
                !_networkTelemetryShellQueued && !_networkToolsQueued && !_networkHeroArtQueued)
                break;
            await Task.Delay(50);
        }
        if (_networkAdvancedBody == null ||
            (_networkAdvancedBody.Children.Count == 1 &&
             _networkAdvancedBody.Children[0] is TextBlock pending &&
             pending.Text.StartsWith("Načítám", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Network deferred Advanced NIC controls did not complete.");
        if ((_networkDnsPanelHost?.Children.Count ?? 0) != 1 ||
            (_networkTelemetryShellHost?.Children.Count ?? 0) != 1 ||
            _networkToolsCard == null || !ContentStack.Children.Contains(_networkToolsCard) ||
            _networkToolsRow?.Children.Count != 3 ||
            (!UseCompactSectionChrome && _networkHeroArtHost?.Children.Count != 1) ||
            CountClassicTweakCards(_networkTweakHost) != expectedTweaks ||
            _networkDnsPanelQueued || _networkTweakQueued ||
            _networkTelemetryShellQueued || _networkToolsQueued || _networkHeroArtQueued)
            throw new InvalidOperationException(
                $"Network generation handoff incomplete: dns={_networkDnsPanelHost?.Children.Count ?? -1} telemetry={_networkTelemetryShellHost?.Children.Count ?? -1} tools={_networkToolsCard != null && ContentStack.Children.Contains(_networkToolsCard)} toolButtons={_networkToolsRow?.Children.Count ?? -1} hero={_networkHeroArtHost?.Children.Count ?? -1} tweaks={CountClassicTweakCards(_networkTweakHost)}/{expectedTweaks} queued={_networkDnsPanelQueued}/{_networkTweakQueued}/{_networkTelemetryShellQueued}/{_networkToolsQueued}/{_networkHeroArtQueued}.");
        LogService.Info("NETWORK_DEFERRED_SMOKE_OK hops=3 dns=1 telemetry=1 tools=1 queued=false");
    }
}
