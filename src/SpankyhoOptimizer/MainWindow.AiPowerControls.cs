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
    private StackPanel? _aiPowerControlsHost;
    private readonly AiPowerControlsRefreshState _aiPowerControlsRefreshState = new();
    private readonly AiPowerDetailsSnapshotCache _aiPowerDetailsSnapshotCache = new();
    // Retain just the controls needed for in-place status updates while the
    // same Power page and toggle setting stay current.
    private sealed record AiPowerLiveView(
        StackPanel Host, bool ToggleEnabled, TextBlock Status, TextBlock Pressure,
        Expander Disclosure, Func<bool> IsDetailsLoading, Func<bool> IsDetailsLoaded);
    private AiPowerLiveView? _aiPowerLiveView;

    private static string AiPowerEvidenceText(string? eppEvidence) =>
        "Automatické režimy Idle / Balanced / Performance. " +
        (eppEvidence ?? "") + " " +
        "AI upravuje hodnoty pouze ve vlastním Spankyho plánu.";

    private static readonly SemaphoreSlim AiScopeSaveGate = new(1, 1);

    private async Task SaveAiMasterAsync(bool enable)
    {
        await AiScopeSaveGate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                FanProfile originalProfile = FanProfileService.Load();
                AiSystemRuntimeConfig? originalConfig = null;
                bool restoreStarted = false;
                try
                {
                    FanProfileService.Save(originalProfile with { AiSmartRuntimeEnabled = enable });
                    AiSystemResourceManagerService.UpdateConfig(current =>
                    {
                        originalConfig = current;
                        return current with { Enabled = enable };
                    });

                    FanProfile persistedProfile = FanProfileService.Load();
                    AiSystemRuntimeConfig persistedConfig = AiSystemResourceManagerService.LoadConfig();
                    if (persistedProfile.AiSmartRuntimeEnabled != enable || persistedConfig.Enabled != enable)
                        throw new InvalidOperationException("AI master read-back nepotvrdil požadovaný stav.");

                    if (!enable)
                    {
                        restoreStarted = true;
                        await AiSystemResourceManagerService.RestoreAllNowAsync();
                    }

                    FanProfileService.StartAgentIfNeeded();
                }
                catch
                {
                    if (!enable && restoreStarted)
                    {
                        // A partial restore MUST NOT reactivate the AI after
                        // its master switch has been durably turned off.
                        // Keep the OFF state; restore attempts are tracked
                        // in the ownership journal and can be retried.
                        if (AiSystemResourceManagerService.LoadConfig().Enabled ||
                            FanProfileService.Load().AiSmartRuntimeEnabled)
                            throw new InvalidOperationException(
                                "Obnova AI nebyla úplná a nelze potvrdit vypnuté AI řízení.");
                        throw;
                    }

                    FanProfileService.Save(originalProfile);
                    if (originalConfig is not null)
                    {
                        // Undo only our master flag. Never overwrite a
                        // different AI scope saved by another GUI meantime.
                        AiSystemResourceManagerService.UpdateConfig(current =>
                            current.Enabled == enable
                                ? current with { Enabled = originalConfig.Enabled }
                                : current);
                    }
                    FanProfile rollbackProfile = FanProfileService.Load();
                    AiSystemRuntimeConfig rollbackConfig = AiSystemResourceManagerService.LoadConfig();
                    if (rollbackProfile != originalProfile ||
                        (originalConfig is not null &&
                         rollbackConfig.Enabled != originalConfig.Enabled))
                        throw new InvalidOperationException("AI master rollback read-back nepotvrdil původní stav.");
                    throw;
                }
            });
            InvalidatePowerPageCache();
        }
        finally { AiScopeSaveGate.Release(); }
    }

    // The persisted FanProfile and AI config may be on a slow/profile-locked
    // volume. Never synchronously read those JSON files on WinUI dispatcher
    // in either success or rollback/error paths of the master toggle.
    private static Task<bool> ReadAiMasterPersistedAsync() => Task.Run(() =>
    {
        FanProfile profile = FanProfileService.Load();
        AiSystemRuntimeConfig config = AiSystemResourceManagerService.LoadConfig();
        return profile.AiSmartRuntimeEnabled && config.Enabled;
    });

    private async Task SaveAiMasterToggleVerifiedAsync(ToggleSwitch toggle)
    {
        if (!toggle.IsEnabled) return;
        bool requested = toggle.IsOn;
        toggle.IsEnabled = false;
        try
        {
            await SaveAiMasterAsync(requested);
            bool actual = await ReadAiMasterPersistedAsync();
            if (actual != requested)
                throw new InvalidOperationException($"AI System Runtime read-back nesouhlasí. Požadováno={requested}, skutečnost={actual}.");
            if (toggle.IsOn != actual) toggle.IsOn = actual;
            LogService.Info("ai-runtime", $"AI_MASTER_TOGGLE_OK requested={requested} actual={actual}");
        }
        catch (Exception ex)
        {
            bool? actual = null;
            try { actual = await ReadAiMasterPersistedAsync(); }
            catch (Exception readEx)
            {
                LogService.Error("ai-runtime", "AI master failure read-back failed", readEx);
            }
            // If neither settings file can be read reliably, revert the
            // visible request rather than advertise unverified success.
            bool visualState = actual ?? !requested;
            if (toggle.IsOn != visualState) toggle.IsOn = visualState;
            LogService.Error("ai-runtime",
                $"AI_MASTER_TOGGLE_FAIL requested={requested} actual={(actual.HasValue ? actual.Value.ToString() : "UNKNOWN")}", ex);
            await ShowMessageAsync("AI System Runtime",
                actual.HasValue ? ex.Message :
                "Stav AI se nepodařilo ověřit z uložených nastavení. " + ex.Message);
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    private async Task SaveAiScopeAsync(Func<AiSystemRuntimeConfig, AiSystemRuntimeConfig> update, bool enable)
    {
        await AiScopeSaveGate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                AiSystemRuntimeConfig? originalConfig = null;
                AiSystemRuntimeConfig? next = null;
                FanProfile originalProfile = FanProfileService.Load();

                try
                {
                    next = AiSystemResourceManagerService.UpdateConfig(current =>
                    {
                        originalConfig = current;
                        return update(current) with { Enabled = enable || current.Enabled };
                    });
                    AiSystemRuntimeConfig persisted = AiSystemResourceManagerService.LoadConfig();
                    if (persisted != next)
                        throw new InvalidOperationException("AI Runtime config se po zápisu neshoduje s požadovaným stavem.");

                    if (originalConfig!.TweaksEnabled && !next.TweaksEnabled)
                        await new AiTweakRuntimeService().RestoreAllAsync();
                    if (originalConfig.SecurityEnabled && !next.SecurityEnabled)
                        await new AiSecurityHardeningService().RestoreAllAsync();
                    if (originalConfig.PowerPlanEnabled && !next.PowerPlanEnabled)
                        await AdaptivePowerRuntimeService.RestoreAllAiSettingsAsync();

                    if (enable)
                    {
                        FanProfileService.Save(originalProfile with { AiSmartRuntimeEnabled = true });
                        if (!FanProfileService.Load().AiSmartRuntimeEnabled)
                            throw new InvalidOperationException("AI fan profile read-back nepotvrdil zapnutý runtime.");
                    }

                    FanProfileService.StartAgentIfNeeded();
                }
                catch
                {
                    if (originalConfig is not null && next is not null)
                    {
                        // This scope currently controls the power plan. Undo
                        // only the flags we touched if another GUI has not
                        // changed those exact flags in the meantime.
                        AiSystemResourceManagerService.UpdateConfig(current =>
                            current with
                            {
                                PowerPlanEnabled = current.PowerPlanEnabled == next.PowerPlanEnabled
                                    ? originalConfig.PowerPlanEnabled : current.PowerPlanEnabled,
                                Enabled = current.Enabled == next.Enabled
                                    ? originalConfig.Enabled : current.Enabled
                            });
                    }
                    FanProfileService.Save(originalProfile);

                    AiSystemRuntimeConfig rollbackConfig = AiSystemResourceManagerService.LoadConfig();
                    FanProfile rollbackProfile = FanProfileService.Load();
                    if ((originalConfig is not null && next is not null &&
                         rollbackConfig.PowerPlanEnabled == next.PowerPlanEnabled &&
                         next.PowerPlanEnabled != originalConfig.PowerPlanEnabled) ||
                        rollbackProfile != originalProfile)
                        throw new InvalidOperationException("AI scope rollback read-back nepotvrdil původní stav.");
                    throw;
                }
            });
            InvalidatePowerPageCache();
        }
        finally { AiScopeSaveGate.Release(); }
    }

    private void RenderAiPowerDetails(
        StackPanel details, AiPowerDetailsSnapshot snapshot, string? eppEvidence)
    {
        details.Children.Clear();
        details.Children.Add(new TextBlock
        {
            Text = AiPowerEvidenceText(eppEvidence),

            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = snapshot.ManagedSummary,
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = snapshot.SystemSummary,
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
    }
    // Defer the heavier detail-only probes until the built-in disclosure
    // is opened. Keep all Windows power settings untouched.
    private async Task LoadAiPowerDetailsAsync(
        StackPanel host, Expander disclosure, StackPanel details,
        int generation, string? eppEvidence, CancellationToken token, Action<bool> completed)
    {
        bool success = false;
        bool IsCurrent() =>
            !token.IsCancellationRequested &&
            generation == _navigationGeneration &&
            _section == "power" &&
            ReferenceEquals(host, _aiPowerControlsHost) &&
            _aiPowerControlsRefreshState.IsActiveDisclosure(disclosure) &&
            disclosure.IsExpanded;

        try
        {
            if (!IsCurrent()) return;
            // The expensive probes are kept off the UI thread. Cache a
            // small immutable summary instead of large setting collections.
            AiPowerDetailsSnapshot data = await Task.Run(() =>
            {
                var system = AiSystemResourceManagerService.ReadHealth();
                var plan = PowerPlanStateService.TryGetFreshSnapshot();
                var managed = AdaptivePowerRuntimeService.ReadManagedSettings(plan?.ActiveGuid);
                string managedText = AiPowerCompactPresentationPolicy.ManagedSummary(
                    managed.Count,
                    string.Join(" • ", managed.Take(4)
                        .Select(x => $"{x.Name} {x.Original}→{x.Current}")));
                DateTime sampledUtc = DateTime.UtcNow;
                string systemText = system is { } health &&
                    AiSystemSmoothnessGuardService.IsFresh(
                        health.UpdatedUtc, sampledUtc, TimeSpan.FromMinutes(5))
                    ? $"Spravované: služby {health.ServiceManaged}, úlohy {health.TaskManaged}, ovladače {health.DeviceManaged}."
                    : "Správa služeb, úloh a ovladačů se aktualizuje při vhodném klidovém okně.";
                return new AiPowerDetailsSnapshot(
                    plan?.ActiveGuid ?? "", managedText, systemText, sampledUtc);
            }, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            // Never display a snapshot sampled from another active power
            // scheme after a user changed the plan during this worker read.
            if (_powerSummarySnapshot?.ActiveGuid is { } currentGuid &&
                !string.Equals(data.PlanGuid, currentGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Napájecí plán se změnil během načítání AI podrobností.");

            _aiPowerDetailsSnapshotCache.Store(data);
            RenderAiPowerDetails(details, data, eppEvidence);
            SchedulePageScrollPolicyRefresh();
            success = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "Expanded power details load failed", ex);
            if (IsCurrent())
            {
                details.Children.Clear();
                details.Children.Add(new TextBlock
                {
                    Text = "Podrobnosti se nepodařilo načíst. Zkus je znovu rozbalit.",
                    FontSize = 11,
                    Foreground = TextSecondaryBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        finally { completed(success); }
    }
    private async Task LoadAiPowerControlsAsync(StackPanel host, int generation, CancellationToken token)
    {
        if (!_aiPowerControlsRefreshState.TryBegin(host, generation)) return;
        try
        {
            var data = await Task.Run(() =>
            {
                var config = AiSystemResourceManagerService.LoadConfig();
                var profile = FanProfileService.Load();
                var agent = FanProfileService.ReadHealthStatus();
                var runtime = AdaptivePowerRuntimeService.ReadStatus();
                var pressure = AiSystemSmoothnessGuardService.ReadCurrent();
                return (config, profile, agent, runtime, pressure);
            }, token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "power" ||
                !_aiPowerControlsRefreshState.IsCurrent(host, generation)) return;
            var config = data.config;
            bool agentLive = data.agent is not null &&
                AiSystemSmoothnessGuardService.IsFresh(
                    data.agent.UpdatedUtc, DateTime.UtcNow, TimeSpan.FromSeconds(12));
            bool runtimeLive = data.runtime is not null &&
                AdaptivePowerRuntimeService.IsStatusFresh(data.runtime, DateTime.UtcNow);
            bool aiControlsPower = config.Enabled && config.PowerPlanEnabled &&
                data.profile.AiSmartRuntimeEnabled && agentLive && runtimeLive &&
                !data.runtime!.Mode.StartsWith("Observation", StringComparison.OrdinalIgnoreCase);
            // A routine 3-second status refresh should update existing
            // TextBlocks, not recreate the interactive WinUI control tree.
            bool toggleEnabled = config.Enabled && config.PowerPlanEnabled;
            string statusText = AiPowerCompactPresentationPolicy.Status(
                agentLive, runtimeLive, aiControlsPower,
                data.agent?.ProcessId, data.runtime?.Mode,
                data.runtime?.Epp, data.runtime?.Boost);
            bool hasPressure = data.pressure is not null;
            string pressureText = data.pressure is { } currentPressure
                ? AiPowerCompactPresentationPolicy.Pressure(
                    currentPressure.CpuLoadPercent,
                    currentPressure.UsedRamPercent, currentPressure.FreeRamGiB)
                : "";
            bool pressureWarning = data.pressure?.State is "CPU pressure" or
                "Memory pressure" or "Disk pressure" or "Disk latency";
            var previous = _aiPowerLiveView;
            bool detailsFresh = _aiPowerDetailsSnapshotCache.TryGet(
                _powerSummarySnapshot?.ActiveGuid, DateTime.UtcNow, out _);
            if (previous is not null &&
                AiPowerCompactLivePolicy.CanReuse(
                    ReferenceEquals(previous.Host, host),
                    host.Children.Count > 0,
                    previous.ToggleEnabled == toggleEnabled,
                    previous.Disclosure.IsExpanded,
                    previous.IsDetailsLoading(), previous.IsDetailsLoaded(),
                    detailsFresh))
            {
                previous.Status.Text = statusText;
                previous.Status.Foreground = aiControlsPower ? SuccessBrush() : TextSecondaryBrush();
                previous.Pressure.Text = pressureText;
                previous.Pressure.Visibility = hasPressure ? Visibility.Visible : Visibility.Collapsed;
                previous.Pressure.Foreground = pressureWarning ? WarningBrush() : TextSecondaryBrush();
                // Only a successfully rendered detail block is evidence.
                // Never overwrite "Loading..." or a retry/error state.
                if (previous.IsDetailsLoaded() && !previous.IsDetailsLoading() &&
                    previous.Disclosure.Content is StackPanel currentDetails &&
                    currentDetails.Children.Count > 0 &&
                    currentDetails.Children[0] is TextBlock evidence)
                {
                    evidence.Text = AiPowerEvidenceText(data.runtime?.EppEvidence);
                }
                _aiPowerControlsLastRefreshUtc = DateTime.UtcNow;
                return;
            }

            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(BuildRuntimeToggleRow(
                "Automatické řízení přes AI",
                "FanAgent řídí aktivní Spankyho plán i po zavření okna. AI používá výkonový profil jen při zapnutí a změny ověřuje s možností obnovení.",
                toggleEnabled,
                enabled => SaveAiScopeAsync(c => c with { PowerPlanEnabled = enabled }, enabled)));

            var statusBlock = new TextBlock
            {
                Text = statusText,
                Foreground = aiControlsPower ? SuccessBrush() : TextSecondaryBrush(),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(statusBlock);
            var pressureBlock = new TextBlock
            {
                Text = pressureText,
                Visibility = hasPressure ? Visibility.Visible : Visibility.Collapsed,
                Foreground = pressureWarning ? WarningBrush() : TextSecondaryBrush(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(pressureBlock);
            // Optional managed-power/system details are fetched only when
            // the user expands the existing compact panel, not on each
            // sidebar reattach or 3-second cached refresh.
            var details = new StackPanel { Spacing = 5 };
            var disclosure = new Expander
            {
                Header = "Podrobnosti AI Power Plan",
                IsExpanded = _aiPowerControlsRefreshState.DetailsExpanded,
                Content = details
            };
            _aiPowerControlsRefreshState.ActivateDisclosure(disclosure);
            bool detailsLoading = false;
            bool detailsLoaded = false;
            void LoadExpandedDetails()
            {
                bool current = generation == _navigationGeneration &&
                    _section == "power" &&
                    ReferenceEquals(host, _aiPowerControlsHost) &&
                    !token.IsCancellationRequested;
                if (!AiPowerDetailsLoadPolicy.ShouldLoad(
                    disclosure.IsExpanded,
                    _aiPowerControlsRefreshState.IsActiveDisclosure(disclosure),
                    detailsLoading, detailsLoaded, current)) return;

                // A just-loaded expanded panel need not resample managed
                // registry/power settings on every rapid 3s redraw.
                if (_aiPowerDetailsSnapshotCache.TryGet(
                    _powerSummarySnapshot?.ActiveGuid, DateTime.UtcNow, out var cached))
                {
                    RenderAiPowerDetails(details, cached!, data.runtime?.EppEvidence);
                    detailsLoaded = true;
                    SchedulePageScrollPolicyRefresh();
                    return;
                }

                detailsLoading = true;
                details.Children.Clear();
                details.Children.Add(new TextBlock
                {
                    Text = "Načítám podrobnosti…",
                    FontSize = 11,
                    Foreground = TextSecondaryBrush()
                });
                SafeTaskService.Forget(
                    LoadAiPowerDetailsAsync(host, disclosure, details, generation, data.runtime?.EppEvidence, token,
                        success =>
                        {
                            detailsLoading = false;
                            detailsLoaded = success;
                        }),
                    "ai-runtime", "Read expanded AI power details");
            }
            disclosure.RegisterPropertyChangedCallback(
                Expander.IsExpandedProperty,
                (sender, _) =>
                {
                    if (_aiPowerControlsRefreshState.TryUpdateDisclosure(
                        sender, ((Expander)sender).IsExpanded))
                        LoadExpandedDetails();
                });
            panel.Children.Add(disclosure);
            _aiPowerLiveView = new AiPowerLiveView(host, toggleEnabled,
                statusBlock, pressureBlock, disclosure,
                () => detailsLoading, () => detailsLoaded);
            host.Children.Clear();
            host.Children.Add(BuildSectionSurface("power", "AI Power Plan", "Automatický režim i ruční volby na jednom místě.", panel));
            LoadExpandedDetails();
            if (ReferenceEquals(host, _aiPowerControlsHost))
                _aiPowerControlsLastRefreshUtc = DateTime.UtcNow;
            SchedulePageScrollPolicyRefresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "Power controls load failed", ex);
        }
        finally
        {
            _aiPowerControlsRefreshState.Complete(host, generation);
        }
    }
}
