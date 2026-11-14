using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Both "Refresh processes" and "Retry" must use the same guarded
    // asynchronous path. An error-row button from an obsolete scan cannot
    // start a new scan or replace the currently displayed panel.
    private Task RefreshBackgroundActivityFromUiAsync(
        StackPanel host, IReadOnlyList<StartupEntry> startup, int expectedScan)
    {
        CancellationToken token = _loadCts?.Token ?? _windowLifetimeCts.Token;
        int generation = _navigationGeneration;
        if (!CanStartBackgroundActivityRefresh(expectedScan, host.Tag,
                !token.IsCancellationRequested &&
                generation == _navigationGeneration &&
                _section == "startup" && host.Parent != null &&
                ReferenceEquals(_backgroundActivityExpander?.Content, host),
                IsBackgroundActivityScanLoading(host, _backgroundActivityLoadingHost,
                    _backgroundActivityLoadingGeneration, generation)))
            return Task.CompletedTask;

        _backgroundActivityLoadingGeneration = generation;
        _backgroundActivityLoadingHost = host;
        _backgroundActivityLoadedAt = default;
        _backgroundActivityLoadedHost = null;
        host.Children.Clear();
        host.Children.Add(new TextBlock
        {
            Text = "Aktualizuji procesy…",
            Foreground = TextSecondaryBrush()
        });
        return RefreshAndFinishAsync();

        async Task RefreshAndFinishAsync()
        {
            try
            {
                await LoadBackgroundActivityPanelAsync(host, startup, generation,
                    token, _backgroundActivityRunningOnly);
            }
            finally
            {
                FinishBackgroundActivityScan(host, generation);
            }
        }
    }

    // A scan token is captured when the control is created, not on click.
    // The same check is shared by normal refresh and the failure retry.
    private static bool CanStartBackgroundActivityRefresh(
        int expectedScan, object? currentScan,
        bool viewActive, bool alreadyLoading) =>
        !alreadyLoading &&
        CanActOnBackgroundScan(expectedScan, currentScan, viewActive);

    // Only mutate WinUI cards if this is STILL the same scan and a new
    // preference actually changed a row. This prevents slow async saves
    // replacing a fresher process measurement or rebuilding all cards on
    // a click of the already selected preference.
    private static bool ShouldRedrawBackgroundPreference(
        int expectedScan, object? currentScan,
        IReadOnlyList<BackgroundActivityState> before,
        IReadOnlyList<BackgroundActivityState> after)
    {
        if (currentScan is not int actual || expectedScan < 0 ||
            actual != expectedScan)
            return false;
        if (before.Count != after.Count) return true;
        for (int i = 0; i < before.Count; i++)
            if (!ReferenceEquals(before[i], after[i]))
                return true;
        return false;
    }

    // An old click must not commit a preference after a newer background
    // process scan has superseded its card. Re-check AFTER the semaphore,
    // because a scan can finish while the click is waiting for an earlier
    // preference commit. The check itself never reads/writes Windows state.
    private static bool CanActOnBackgroundScan(
        int originalScan, object? currentScan, bool viewActive) =>
        viewActive && originalScan >= 0 &&
        currentScan is int activeScan && activeScan == originalScan;

    // A scan may remain unchanged after a preference redraw. A button from
    // a removed Border must never mutate a different, newly rendered card.
    // Check the current host collection, not a stale XAML Parent link.
    private static bool IsCurrentBackgroundActivityCard(
        StackPanel host, StackPanel card)
    {
        foreach (UIElement child in host.Children)
            if (child is Border border && ReferenceEquals(border.Child, card))
                return true;
        return false;
    }

    // Serialize preference button commits in click order without blocking the
    // WinUI dispatcher on JSON disk writes and read-back verification.
    private readonly SemaphoreSlim _backgroundPreferenceUiGate = new(1, 1);

    // Navigating away while a preference button waits in the queue is
    // expected user activity, not an actionable UI error. Never release
    // a semaphore slot that was NOT acquired due to cancellation.
    private static async Task<bool> TryEnterBackgroundPreferenceGateAsync(
        SemaphoreSlim gate, CancellationToken token)
    {
        try
        {
            await gate.WaitAsync(token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SaveBackgroundPreferenceFromUiAsync(
        StackPanel host, StackPanel renderedCard,
        IReadOnlyList<StartupEntry> startup,
        IReadOnlyList<BackgroundActivityState> states,
        string key, string label, BackgroundAppPreference preference,
        CancellationToken token, long scanMs, int generation, bool runningOnly,
        int renderedScan)
    {
        // User choices must remain actionable after reattaching the cached
        // Startup page. The original scan token was canceled on navigation.
        token = _loadCts?.Token ?? _windowLifetimeCts.Token;
        generation = _navigationGeneration;
        if (!ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
            token.IsCancellationRequested || _section != "startup") return;
        int originalScan = renderedScan;
        if (!CanActOnBackgroundScan(originalScan, host.Tag,
                viewActive: true) ||
            !IsCurrentBackgroundActivityCard(host, renderedCard))
            return;
        if (!await TryEnterBackgroundPreferenceGateAsync(
                _backgroundPreferenceUiGate, token))
            return;
        try
        {
            if (token.IsCancellationRequested ||
                !CanActOnBackgroundScan(originalScan, host.Tag,
                    generation == _navigationGeneration &&
                    _section == "startup" &&
                    host.Parent != null &&
                    ReferenceEquals(_backgroundActivityExpander?.Content, host)) ||
                !IsCurrentBackgroundActivityCard(host, renderedCard))
                return;
            await BackgroundActivityService.SetPreferenceAsync(key, preference, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Navigation cancelled an in-flight save. The underlying profile
            // service still owns atomic persistence and read-back checks.
            return;
        }
        catch (Exception ex)
        {
            LogService.Error("background-app", "Preference save failed", ex);
            // Never claim success after a failed or contended profile write.
            // The old preference is still active; another click can retry.
            if (!token.IsCancellationRequested && _section == "startup" &&
                generation == _navigationGeneration &&
                ReferenceEquals(_backgroundActivityExpander?.Content, host) &&
                IsCurrentBackgroundActivityCard(host, renderedCard))
                ShowActionFeedback(label, ActionFeedbackKind.Failed,
                    "Předvolba se neuložila. Zkus to znovu. " + ex.Message);
            return;
        }
        finally
        {
            _backgroundPreferenceUiGate.Release();
        }
        if (token.IsCancellationRequested || generation != _navigationGeneration ||
            _section != "startup" || host.Parent == null ||
            !ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
            !CanActOnBackgroundScan(originalScan, host.Tag, viewActive: true) ||
            !IsCurrentBackgroundActivityCard(host, renderedCard))
            return;
        ShowActionFeedback(label, preference switch
        {
            BackgroundAppPreference.Keep => ActionFeedbackKind.Verified,
            BackgroundAppPreference.Unused => ActionFeedbackKind.Verified,
            _ => ActionFeedbackKind.Info
        }, preference switch
        {
            BackgroundAppPreference.Keep => "Preference uložena: Používám.",
            BackgroundAppPreference.Unused => "Preference uložena: Nepoužívám.",
            _ => "Preference byla resetována."
        });
        // The card, host and scan were revalidated BEFORE feedback. A newer
        // refresh must not show stale confirmation or overwrite its snapshot.
        // A second click may have changed another card while this queued.
        // Refresh effective values from the verified persisted profile, but
        // a no-op click should not destroy/recreate the same WinUI controls.
        var effectiveStates = BackgroundActivityService.ApplyCurrentPreferences(states);
        if (ShouldRedrawBackgroundPreference(
                originalScan, host.Tag, states, effectiveStates))
            RenderBackgroundActivityPanel(host, startup,
                effectiveStates, token, scanMs, generation,
                _backgroundActivityRunningOnly);
    }

    private void RenderBackgroundActivityPanel(
        StackPanel host,
        IReadOnlyList<StartupEntry> startup,
        IReadOnlyList<BackgroundActivityState> states,
        CancellationToken token,
        long scanMs,
        int generation,
        bool runningOnly)
    {
        host.Children.Clear();

        int refreshSourceScan = host.Tag is int currentScan ? currentScan : -1;
        host.Children.Add(QuickButton("Obnovit procesy", () =>
            RefreshBackgroundActivityFromUiAsync(host, startup, refreshSourceScan)));

        // Filtering never changes process measurements or preferences.
        // Preserve all WinUI cards/buttons: only toggle visibility in place.
        bool showRunningOnly = runningOnly;
        TextBlock? noRunningNotice = null;
        Button? filterButton = null;
        filterButton = QuickButton(showRunningOnly ? "Zobrazit všechny" : "Jen běžící", () =>
        {
            if (_section != "startup" ||
                !ReferenceEquals(_backgroundActivityExpander?.Content, host))
                return Task.CompletedTask;
            showRunningOnly = !showRunningOnly;
            _backgroundActivityRunningOnly = showRunningOnly;
            // QuickButton restores its content after the click task completes.
            // Queue the new label after that shared action wrapper unwinds.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (filterButton != null && filterButton.Parent != null)
                        filterButton.Content = showRunningOnly ? "Zobrazit všechny" : "Jen běžící";
                });
            foreach (UIElement child in host.Children)
            {
                if (child is Border { Tag: BackgroundActivityState entry } card)
                    card.Visibility = showRunningOnly && entry.ProcessCount == 0
                        ? Visibility.Collapsed : Visibility.Visible;
            }
            if (noRunningNotice != null)
                noRunningNotice.Visibility = showRunningOnly ? Visibility.Visible : Visibility.Collapsed;
            return Task.CompletedTask;
        });
        host.Children.Add(filterButton);

        int liveProcesses = 0;
        double trackedRamMb = 0;
        foreach (var state in states)
        {
            liveProcesses += state.ProcessCount;
            trackedRamMb += state.WorkingSetMb;
        }
        host.Children.Add(new TextBlock
        {
            Text = $"Sken {scanMs} ms • sledováno {states.Count} aplikací • {liveProcesses} procesů • RAM {trackedRamMb:0} MB (součet sledovaných)",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        if (states.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "Žádná sledovaná background aplikace neběží ani nemá aktivní startup položku.",
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        if (liveProcesses == 0)
        {
            noRunningNotice = new TextBlock
            {
                Text = "Momentálně neběží žádná sledovaná aplikace.",
                Foreground = TextSecondaryBrush(),
                Visibility = showRunningOnly ? Visibility.Visible : Visibility.Collapsed
            };
            host.Children.Add(noRunningNotice);
        }

        // Manual process/startup actions also require this exact snapshot
        // after the user confirmation dialog. A newer scan invalidates the
        // old card's process count and startup inventory.
        int renderedScan = host.Tag is int scan ? scan : -1;
        foreach (var state in states)
        {
            string current =
                $"{state.ProcessCount} proc. • {state.WorkingSetMb:0} MB • CPU {state.CpuSeconds:0.0}s • startup {state.EnabledStartupMatches}/{state.TotalStartupMatches}";

            string recommended = state.Preference switch
            {
                BackgroundAppPreference.Keep => "PONECHAT — označeno Používám",
                BackgroundAppPreference.Unused => state.EnabledStartupMatches > 0 || state.ProcessCount > 0
                    ? "VYPNOUT / ukončit podle potřeby"
                    : "OK — nepoužívané a neběží",
                _ => "Vyber Používám / Nepoužívám"
            };

            OptimizationAdvisorKind kind = state.Preference switch
            {
                BackgroundAppPreference.Keep => OptimizationAdvisorKind.Ok,
                BackgroundAppPreference.Unused when state.EnabledStartupMatches > 0 || state.ProcessCount > 0
                    => OptimizationAdvisorKind.Recommended,
                BackgroundAppPreference.Unused => OptimizationAdvisorKind.Ok,
                _ => OptimizationAdvisorKind.Info
            };

            var card = new StackPanel { Spacing = 7 };
            card.Children.Add(BuildOptimizationAdvisorRow(
                state.Label,
                current,
                recommended,
                "Doporučení vychází z toho, zda aplikaci skutečně používáš; Optimizer nevypíná background aplikace plošně.",
                kind));

            var preference = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7
            };
            preference.Children.Add(QuickButton(
                state.Preference == BackgroundAppPreference.Keep ? "Používám ✓" : "Používám",
                () => SaveBackgroundPreferenceFromUiAsync(host, card, startup, states,
                    state.Key, state.Label, BackgroundAppPreference.Keep,
                    token, scanMs, generation, runningOnly, renderedScan)));
            preference.Children.Add(QuickButton(
                state.Preference == BackgroundAppPreference.Unused ? "Nepoužívám ✓" : "Nepoužívám",
                () => SaveBackgroundPreferenceFromUiAsync(host, card, startup, states,
                    state.Key, state.Label, BackgroundAppPreference.Unused,
                    token, scanMs, generation, runningOnly, renderedScan)));
            preference.Children.Add(QuickButton("Reset volby", () =>
                SaveBackgroundPreferenceFromUiAsync(host, card, startup, states,
                    state.Key, state.Label, BackgroundAppPreference.Unknown,
                    token, scanMs, generation, runningOnly, renderedScan)));
            card.Children.Add(preference);

            if (state.Preference == BackgroundAppPreference.Unused)
            {
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7
                };

                if (state.EnabledStartupMatches > 0)
                {
                    actions.Children.Add(PrimaryQuickButton("Vypnout autostart", async () =>
                    {
                        CancellationToken actionToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                        int actionGeneration = _navigationGeneration;
                        if (actionToken.IsCancellationRequested || _section != "startup" ||
                            !ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag, viewActive: true) ||
                            !IsCurrentBackgroundActivityCard(host, card)) return;
                        if (!await ShowStyledConfirmAsync(
                            "Vypnout automatické spouštění?",
                            $"Zakázat {state.EnabledStartupMatches} položek pro {state.Label} po startu Windows? Běžící procesy se tím neukončí.",
                            "Vypnout autostart", "Zrušit", "Po spuštění")) return;
                        if (actionToken.IsCancellationRequested ||
                            actionGeneration != _navigationGeneration ||
                            _section != "startup" ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag,
                                ReferenceEquals(_backgroundActivityExpander?.Content, host) &&
                                host.Parent != null) ||
                            !IsCurrentBackgroundActivityCard(host, card))
                            return;
                        int changed = await BackgroundActivityService.DisableStartupMatchesAsync(
                            state.Key, startup, actionToken);
                        // Invalidate even after leaving the page: the service may
                        // have changed Startup entries before cancellation.
                        InvalidateStartupPageCache();
                        if (actionToken.IsCancellationRequested || _section != "startup") return;
                        ShowActionFeedback(
                            state.Label,
                            changed > 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                            changed > 0
                                ? $"Vypnuto startup položek: {changed}."
                                : "Nebyla nalezena žádná aktivní startup položka k vypnutí.");
                        await ShowSectionAsync("startup");
                    }));
                }

                if (state.ProcessCount > 0)
                {
                    actions.Children.Add(QuickButton("Ukončit teď", async () =>
                    {
                        CancellationToken actionToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                        int actionGeneration = _navigationGeneration;
                        if (actionToken.IsCancellationRequested || _section != "startup" ||
                            !ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag, viewActive: true) ||
                            !IsCurrentBackgroundActivityCard(host, card)) return;
                        if (!await ShowStyledConfirmAsync(
                            "Ukončit běžící aplikaci?",
                            $"Ukončit až {state.ProcessCount} procesů aplikace {state.Label}? Neuložená práce může být ztracena.",
                            "Ukončit procesy", "Zrušit", "Background Activity")) return;
                        if (actionToken.IsCancellationRequested ||
                            actionGeneration != _navigationGeneration ||
                            _section != "startup" ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag,
                                ReferenceEquals(_backgroundActivityExpander?.Content, host) &&
                                host.Parent != null) ||
                            !IsCurrentBackgroundActivityCard(host, card))
                            return;
                        int stopped = await BackgroundActivityService.StopProcessesAsync(
                            state.Key, actionToken);
                        if (actionToken.IsCancellationRequested ||
                            actionGeneration != _navigationGeneration || _section != "startup") return;
                        ShowActionFeedback(
                            state.Label,
                            stopped > 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                            stopped > 0
                                ? $"Ukončeno procesů: {stopped}."
                                : "Neběží žádný proces, který by bylo potřeba ukončit.");
                        _backgroundActivityLoadedAt = default;
                        await LoadBackgroundActivityPanelAsync(
                            host, startup, actionGeneration, actionToken,
                            _backgroundActivityRunningOnly);
                    }));
                }

                if (actions.Children.Count > 0)
                    card.Children.Add(actions);
            }

            host.Children.Add(new Border
            {
                Tag = state,
                Visibility = showRunningOnly && state.ProcessCount == 0
                    ? Visibility.Collapsed : Visibility.Visible,
                Background = SectionCardBackgroundBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Child = card
            });
        }
    }
}
