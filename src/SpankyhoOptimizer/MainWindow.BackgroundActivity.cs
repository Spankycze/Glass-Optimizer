using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void ScheduleBackgroundActivityPanel(
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token,
        StackPanel? attachHost = null)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "startup") return;
            BuildBackgroundActivityPanel(startup, token, attachHost);
        });
    }

    private void BuildBackgroundActivityPanel(
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token,
        StackPanel? attachHost = null)
    {
        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(new TextBlock
        {
            Text = "Měřím běžící background aplikace…",
            Foreground = TextSecondaryBrush()
        });

        var expander = BuildDashboardExpander(
            "Background Activity",
            "Reálné procesy • RAM • CPU time • autostart • tvoje Používám/Nepoužívám",
            host);
        if (attachHost != null)
            attachHost.Children.Add(expander);
        else
            ContentStack.Children.Add(expander);

        SafeTaskService.Forget(
            LoadBackgroundActivityPanelAsync(host, startup, token),
            "startup",
            "Background activity");
    }

    private async Task LoadBackgroundActivityPanelAsync(
        StackPanel host,
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token)
    {
        try
        {
            var states = await BackgroundActivityService.ReadAsync(startup, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _section != "startup") return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "startup" || token.IsCancellationRequested) return;
                RenderBackgroundActivityPanel(host, startup, states, token);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("startup", "Background activity load failed", ex);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "startup" || token.IsCancellationRequested) return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Background Activity se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                host.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadBackgroundActivityPanelAsync(host, startup, token)));
            });
        }
    }

    private void RenderBackgroundActivityPanel(
        StackPanel host,
        IReadOnlyList<StartupEntry> startup,
        IReadOnlyList<BackgroundActivityState> states,
        CancellationToken token)
    {
        host.Children.Clear();

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
                async () =>
                {
                    BackgroundActivityService.SetPreference(state.Key, BackgroundAppPreference.Keep);
                    ShowActionFeedback(state.Label, ActionFeedbackKind.Verified, "Preference uložena: Používám.");
                    await LoadBackgroundActivityPanelAsync(host, startup, token);
                }));
            preference.Children.Add(QuickButton(
                state.Preference == BackgroundAppPreference.Unused ? "Nepoužívám ✓" : "Nepoužívám",
                async () =>
                {
                    BackgroundActivityService.SetPreference(state.Key, BackgroundAppPreference.Unused);
                    ShowActionFeedback(state.Label, ActionFeedbackKind.Verified, "Preference uložena: Nepoužívám.");
                    await LoadBackgroundActivityPanelAsync(host, startup, token);
                }));
            preference.Children.Add(QuickButton("Reset volby", async () =>
            {
                BackgroundActivityService.SetPreference(state.Key, BackgroundAppPreference.Unknown);
                ShowActionFeedback(state.Label, ActionFeedbackKind.Info, "Preference byla resetována.");
                await LoadBackgroundActivityPanelAsync(host, startup, token);
            }));
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
                        int changed = await BackgroundActivityService.DisableStartupMatchesAsync(
                            state.Key, startup, _loadCts?.Token ?? CancellationToken.None);
                        StartupManagerService.InvalidateCache(invalidateTaskScheduler: false);
                        InvalidateStartupPageCache();
                        ShowActionFeedback(
                            state.Label,
                            changed > 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                            changed > 0
                                ? $"Vypnuto startup položek: {changed}."
                                : "Nebyla nalezena žádná aktivní startup položka k vypnutí.");
                        if (_section == "startup") await ShowSectionAsync("startup");
                    }));
                }

                if (state.ProcessCount > 0)
                {
                    actions.Children.Add(QuickButton("Ukončit teď", async () =>
                    {
                        int stopped = await BackgroundActivityService.StopProcessesAsync(
                            state.Key, _loadCts?.Token ?? CancellationToken.None);
                        ShowActionFeedback(
                            state.Label,
                            stopped > 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                            stopped > 0
                                ? $"Ukončeno procesů: {stopped}."
                                : "Neběží žádný proces, který by bylo potřeba ukončit.");
                        await LoadBackgroundActivityPanelAsync(host, startup, token);
                    }));
                }

                if (actions.Children.Count > 0)
                    card.Children.Add(actions);
            }

            host.Children.Add(new Border
            {
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
