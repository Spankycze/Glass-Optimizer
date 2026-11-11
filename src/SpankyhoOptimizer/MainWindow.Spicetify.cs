using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Task BuildSpicetifyPageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TryAttachSpicetifyPageCache(token)) return Task.CompletedTask;

        var perf = System.Diagnostics.Stopwatch.StartNew();
        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(QuickButton("Obnovit", () => { InvalidateSpicetifyPageCache(); return ShowSectionAsync("spicetify"); }));
        var configButton = QuickButton("Otevřít config", () => { SpicetifyService.OpenConfigRoot(); return Task.CompletedTask; });
        configButton.IsEnabled = false;
        heroActions.Children.Add(configButton);

        ContentStack.Children.Add(BuildModernSectionHero(
            "spicetify",
            "Spotify / Spicetify",
            "Spotify, Spicetify, Marketplace a vzhled v jednom dashboardu.",
            "SPOTIFY MOD",
            "Načítám Spotify • Spicetify • Marketplace…",
            heroActions));
        var host = new StackPanel { Spacing = 10 };
        host.Children.Add(new TextBlock { Text = "Načítám stav…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(host);
        long shellMs = perf.ElapsedMilliseconds;
        LogService.Info("performance", $"SPICETIFY_BUILD statusMs=deferred baseUiMs={shellMs} tweaksMs=0 extensionsMs=0 themeMs=deferred totalMs={shellMs} deferredStatus=true");
        SafeTaskService.Forget(LoadSpicetifyStatusAndHydrateAsync(host, configButton, token), "spicetify", "Deferred Spicetify status");
        return Task.CompletedTask;
    }

    private async Task LoadSpicetifyStatusAndHydrateAsync(StackPanel host, Button configButton, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            var perf = System.Diagnostics.Stopwatch.StartNew();
            SpicetifyStatus status = await SpicetifyService.GetStatusAsync(token);
            long statusMs = perf.ElapsedMilliseconds;
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "spicetify") return;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "spicetify" ||
                    host.Parent == null) return;
                var render = System.Diagnostics.Stopwatch.StartNew();
                host.Children.Clear();
                configButton.IsEnabled = status.SpicetifyInstalled;
                host.Children.Add(BuildSpicetifyStatusCard(status));
                host.Children.Add(BuildSpotifyInstallCard(status, token));
                _spicetifyCachedStatus = status;
                _spicetifyActionsBody = new StackPanel { Spacing = 10 };
                host.Children.Add(_spicetifyActionsBody);

                if (status.SpicetifyInstalled)
                {
                    _spicetifyTweaksBody = new StackPanel { Spacing = 12 };
                    _spicetifyExtensionsBody = new StackPanel { Spacing = 10 };
                    _spicetifyThemeBody = new StackPanel { Spacing = 10 };
                    host.Children.Add(BuildFlatSectionHeader(
                        "Spotify / Spicetify tweaky",
                        "Recommended + ruční nastavení přes Spicetify CLI"));
                    host.Children.Add(_spicetifyTweaksBody);
                    host.Children.Add(BuildDashboardExpander(
                        $"Extensions ({status.Extensions.Count})",
                        "Rozšíření Spicetify",
                        _spicetifyExtensionsBody));
                    host.Children.Add(BuildDashboardExpander(
                        "Themes",
                        "Theme a color scheme přes Spicetify CLI",
                        _spicetifyThemeBody));
                }

                CaptureSpicetifyPageCache();
                ScheduleDeferredSpicetifyActions(status, token);
                if (status.SpicetifyInstalled)
                {
                    ScheduleDeferredSpicetifySecondary(status, token);
                    ScheduleDeferredSpicetifyTheme(status, _spicetifyThemeBody!, token);
                }
                LogService.Info("performance", $"SPICETIFY_STATUS_DEFERRED statusMs={statusMs} renderMs={render.ElapsedMilliseconds} installed={status.SpicetifyInstalled}");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("spicetify", "Deferred Spicetify status failed", ex);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "spicetify" ||
                    host.Parent == null) return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Stav Spotify / Spicetify se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                host.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadSpicetifyStatusAndHydrateAsync(host, configButton, token)));
            });
        }
    }
    private void ScheduleDeferredSpicetifyActions(SpicetifyStatus status, CancellationToken token)
    {
        StackPanel? host = _spicetifyActionsBody;
        if (_spicetifyActionsQueued || host == null || host.Children.Count > 0) return;
        int generation = _navigationGeneration;
        _spicetifyActionsQueued = true;
        // A rejected low-priority enqueue must release the pending bit, or the
        // Spicetify action batches can never be scheduled again.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _spicetifyActionsQueued = false;
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "spicetify" || !ReferenceEquals(_spicetifyActionsBody, host) || host.Children.Count > 0) return;
            var phase = System.Diagnostics.Stopwatch.StartNew();
            BuildSpicetifyPrimaryActions(status, token, host);
            LogService.Info("performance", $"SPICETIFY_ACTIONS_BATCH1 ms={phase.ElapsedMilliseconds} count={host.Children.Count}");
            if (!status.SpicetifyInstalled) return;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration ||
                    _section != "spicetify" || !ReferenceEquals(_spicetifyActionsBody, host)) return;
                var batch2 = System.Diagnostics.Stopwatch.StartNew();
                BuildSpicetifyTailBatch2(status, token, host);
                LogService.Info("performance", $"SPICETIFY_ACTIONS_BATCH2 ms={batch2.ElapsedMilliseconds} count={host.Children.Count}");

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (token.IsCancellationRequested || generation != _navigationGeneration ||
                        _section != "spicetify" || !ReferenceEquals(_spicetifyActionsBody, host)) return;
                    var batch3 = System.Diagnostics.Stopwatch.StartNew();
                    BuildSpicetifyTailBatch3(status, token, host);
                    LogService.Info("performance", $"SPICETIFY_ACTIONS_BATCH3 ms={batch3.ElapsedMilliseconds} count={host.Children.Count}");

                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (token.IsCancellationRequested || generation != _navigationGeneration ||
                            _section != "spicetify" || !ReferenceEquals(_spicetifyActionsBody, host)) return;
                        var batch4 = System.Diagnostics.Stopwatch.StartNew();
                        BuildSpicetifyTailBatch4(token, host);
                        LogService.Info("performance", $"SPICETIFY_ACTIONS_BATCH4 ms={batch4.ElapsedMilliseconds} count={host.Children.Count}");
                    });
                });
            });
        })) _spicetifyActionsQueued = false;
    }

    private void ScheduleDeferredSpicetifySecondary(SpicetifyStatus status, CancellationToken token)
    {
        if (_spicetifySecondaryQueued) return;
        StackPanel? tweaksHost = _spicetifyTweaksBody;
        StackPanel? extensionsHost = _spicetifyExtensionsBody;
        if ((tweaksHost == null || tweaksHost.Children.Count > 0) &&
            (extensionsHost == null || extensionsHost.Children.Count > 0)) return;
        int generation = _navigationGeneration;
        _spicetifySecondaryQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _spicetifySecondaryQueued = false;
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "spicetify" ||
                !ReferenceEquals(_spicetifyTweaksBody, tweaksHost) ||
                !ReferenceEquals(_spicetifyExtensionsBody, extensionsHost))
                return;
            SafeTaskService.Forget(
                BuildDeferredSpicetifySecondaryAsync(status, tweaksHost, extensionsHost, generation, token),
                "spicetify",
                "Deferred tweak/extension UI");
        })) _spicetifySecondaryQueued = false;
    }

    private async Task BuildDeferredSpicetifySecondaryAsync(
        SpicetifyStatus status,
        StackPanel? tweaksHost,
        StackPanel? extensionsHost,
        int generation,
        CancellationToken token)
    {
        if (tweaksHost == null || extensionsHost == null) return;
        if (generation != _navigationGeneration || _section != "spicetify" ||
            !ReferenceEquals(_spicetifyTweaksBody, tweaksHost) ||
            !ReferenceEquals(_spicetifyExtensionsBody, extensionsHost)) return;
        var phase = System.Diagnostics.Stopwatch.StartNew();
        Task<IReadOnlySet<string>> enabledExtensionsTask = Task.Run(
            () => SpicetifyService.GetEnabledExtensions(),
            token);

        long tweaksMs = 0;
        if (tweaksHost.Children.Count == 0)
        {
            await BuildSpicetifyTweaksAsync(tweaksHost, token);
            tweaksMs = phase.ElapsedMilliseconds;
        }

        long beforeExtensionRead = phase.ElapsedMilliseconds;
        IReadOnlySet<string> enabledExtensions = await enabledExtensionsTask;
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "spicetify" ||
            !ReferenceEquals(_spicetifyTweaksBody, tweaksHost) ||
            !ReferenceEquals(_spicetifyExtensionsBody, extensionsHost))
            return;
        long extensionReadWaitMs = phase.ElapsedMilliseconds - beforeExtensionRead;

        long beforeExtensionsUi = phase.ElapsedMilliseconds;
        if (extensionsHost.Children.Count == 0)
            BuildSpicetifyExtensionManager(status, extensionsHost, token, enabledExtensions);
        long extensionsUiMs = phase.ElapsedMilliseconds - beforeExtensionsUi;

        LogService.Info("performance",
            $"SPICETIFY_SECONDARY_DEFERRED tweaksMs={tweaksMs} extensionReadWaitMs={extensionReadWaitMs} extensionsUiMs={extensionsUiMs} totalMs={phase.ElapsedMilliseconds}");
    }

    private void ScheduleDeferredSpicetifyTheme(SpicetifyStatus status, StackPanel host, CancellationToken token)
    {
        if (host.Children.Count > 0) return;
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "spicetify" ||
                !ReferenceEquals(_spicetifyThemeBody, host) ||
                host.Children.Count > 0)
                return;
            var phase = System.Diagnostics.Stopwatch.StartNew();
            BuildSpicetifyThemeCard(status, host, token);
            LogService.Info("performance", $"SPICETIFY_THEME_DEFERRED themeMs={phase.ElapsedMilliseconds}");
        });
    }
    internal async Task RunSpicetifyDeferredRenderSmokeAsync()
    {
        await ShowSectionAsync("spicetify");
        await Task.Delay(400);
        if (_spicetifyPageCache == null || _spicetifyActionsBody == null || _spicetifyActionsBody.Children.Count < 2 ||
            _spicetifyTweaksBody == null || _spicetifyTweaksBody.Children.Count < 10 ||
            _spicetifyExtensionsBody == null || _spicetifyExtensionsBody.Children.Count < 1 ||
            _spicetifyThemeBody == null || _spicetifyThemeBody.Children.Count < 3)
            throw new InvalidOperationException("Spicetify deferred secondary/theme/cache did not complete.");
        LogService.Info("SPICETIFY_DEFERRED_SMOKE_OK");
    }

    private Border BuildSpicetifyStatusCard(SpicetifyStatus status)
    {
        var body = new StackPanel { Spacing = 9 };
        body.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Spotify", status.SpotifyInstalled ? "Nainstalováno" : "Chybí", "Oficiální klient", status.SpotifyInstalled ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric("Spicetify", status.SpicetifyInstalled ? status.Version : "Chybí", "CLI", status.SpicetifyInstalled ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric("Marketplace", status.MarketplaceInstalled ? "Nainstalováno" : "Chybí", "Custom app", status.MarketplaceInstalled ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric("Add-ons", $"{status.Themes.Count}/{status.Extensions.Count}", "Themes / Extensions")));
        body.Children.Add(new TextBlock
        {
            Text = status.ExecutablePath,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush()
        });
        return BuildSectionSurface("spicetify", "Stav", "Spotify • Spicetify • Marketplace • add-ons", body);
    }

    private Border BuildSpotifyInstallCard(SpicetifyStatus status, CancellationToken token)
    {
        var root = new StackPanel { Spacing = 10 };
        var statusText = new TextBlock { Text = status.SpotifyInstalled ? "Spotify je nainstalované." : "Spotify není nainstalované.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = status.SpotifyInstalled ? 100 : 0,
            Height = 7,
            IsIndeterminate = false,
            Foreground = TextSecondaryBrush(),
            Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush()
        };
        var install = new Button { Content = status.SpotifyInstalled ? "Aktualizovat Spotify" : "Stáhnout a nainstalovat" };
        ApplyPillButtonAppearance(install);
        install.Padding = new Thickness(14, 7, 14, 7);
        install.Click += (_, _) => SafeTaskService.Forget(
            InstallOrUpdateSpotifyAsync(status.SpotifyInstalled, install, progress, statusText, token),
            "spicetify", "Spotify install/update");
        root.Children.Add(statusText); root.Children.Add(progress); root.Children.Add(install);
        return BuildSectionSurface("spicetify", "Spotify", "Oficiální Spotify ze Spotify CDN • ověření podpisu • tichá instalace", root);
    }

    private void BuildSpicetifyPrimaryActions(SpicetifyStatus status, CancellationToken token, StackPanel root)
    {
        if (!status.SpicetifyInstalled)
        {
            root.Children.Add(ActionCard("Spicetify CLI", "Nainstaluje oficiální Spicetify CLI přes Winget jako běžný uživatel.", "Nainstalovat", () => RunSpicetifyActionAsync("Instalace Spicetify", SpicetifyService.InstallAsync, _spicetifyActionToken)));
            root.Children.Add(ActionCard("Spicetify Marketplace", "Nainstaluje Spicetify CLI (pokud chybí) a potom oficiální Marketplace jako běžný uživatel.", "Nainstalovat Marketplace", () => RunSpicetifyActionAsync("Spicetify Marketplace", SpicetifyService.InstallMarketplaceWithPrerequisitesAsync, _spicetifyActionToken)));
            return;
        }

        root.Children.Add(ActionCard("Spicetify CLI", "Aktualizace CLI přes Winget. Neprovádí změny Spotify, dokud nepoužiješ Apply.", "Aktualizovat", () => RunSpicetifyActionAsync("Aktualizace Spicetify", SpicetifyService.UpdateAsync, _spicetifyActionToken)));
        root.Children.Add(ActionCard("Backup + Apply", "Vytvoří backup Spotify a aplikuje Spicetify. Doporučený první krok po instalaci nebo větší aktualizaci Spotify.", "Spustit", () => RunSpicetifyActionAsync("Backup + Apply", SpicetifyService.BackupApplyAsync, _spicetifyActionToken)));
        root.Children.Add(ActionCard("Apply", "Znovu aplikuje aktuální theme, extensions a custom apps.", "Použít", () => RunSpicetifyActionAsync("Spicetify Apply", SpicetifyService.ApplyAsync, _spicetifyActionToken)));
    }

    private void BuildSpicetifyTailBatch2(SpicetifyStatus status, CancellationToken token, StackPanel root)
    {
        root.Children.Add(ActionCard("Restore Spotify", "Vrátí Spotify do původního stavu přes příkaz spicetify restore.", "Obnovit", () => RunSpicetifyActionAsync("Restore Spotify", SpicetifyService.RestoreAsync, _spicetifyActionToken)));
        root.Children.Add(ActionCard("Spicetify Marketplace", status.MarketplaceInstalled ? "Marketplace je nainstalovaný. Installer můžeš spustit znovu pro opravu/aktualizaci." : "Nainstaluje oficiální Spicetify Marketplace odděleně od CLI installeru.", status.MarketplaceInstalled ? "Opravit / aktualizovat" : "Nainstalovat", () => RunSpicetifyActionAsync("Spicetify Marketplace", SpicetifyService.InstallMarketplaceAsync, _spicetifyActionToken)));
    }

    private void BuildSpicetifyTailBatch3(SpicetifyStatus status, CancellationToken token, StackPanel root)
    {
        root.Children.Add(ActionCard("Restart Spotify", "Restartuje Spotify přes Spicetify CLI.", "Restartovat", () => RunSpicetifyActionAsync("Restart Spotify", SpicetifyService.RestartAsync, _spicetifyActionToken, refresh: false)));

        var folders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folders.Children.Add(QuickButton("Themes", () => { SpicetifyService.OpenThemesRoot(); return Task.CompletedTask; }));
        folders.Children.Add(QuickButton("Extensions", () => { SpicetifyService.OpenExtensionsRoot(); return Task.CompletedTask; }));
        folders.Children.Add(QuickButton("Custom Apps", () => { SpicetifyService.OpenCustomAppsRoot(); return Task.CompletedTask; }));
        root.Children.Add(BuildSectionSurface("spicetify", "Složky Spicetify", "Rychlý přístup k themes, extensions a custom apps", folders));
    }

    private void BuildSpicetifyTailBatch4(CancellationToken token, StackPanel root)
    {
        root.Children.Add(ActionCard("Odinstalovat Spicetify", "Odstraní Spicetify CLI přes Winget. Před odinstalací je vhodné nejdřív použít Restore Spotify.", "Odinstalovat", async () =>
        {
            if (!await ShowStyledConfirmAsync("Odinstalovat Spicetify?", "Nejdřív doporučuji Restore Spotify. Samotný uninstall odstraní CLI.", "Odinstalovat", "Zrušit")) return;
            await RunSpicetifyActionAsync("Odinstalace Spicetify", SpicetifyService.UninstallAsync, token);
        }));
    }

    private async Task InstallOrUpdateSpotifyAsync(bool update, Button button, ProgressBar progress, TextBlock statusText, CancellationToken token)
    {
        int operationGeneration = _navigationGeneration;
        bool IsCurrentOperation() => operationGeneration == _navigationGeneration && _section == "spicetify" && !token.IsCancellationRequested;
        button.IsEnabled = false; progress.Value = 0; progress.IsIndeterminate = true; statusText.Text = update ? "Kontroluji aktualizaci Spotify…" : "Připravuji stažení Spotify…";
        try
        {
            void OnOutput(string line) { if (!string.IsNullOrWhiteSpace(line)) DispatcherQueue.TryEnqueue(() => { if (IsCurrentOperation()) UpdateSpotifyInstallProgress(line, progress, statusText); }); }
            var result = update ? await SpicetifyService.UpdateSpotifyAsync(token, OnOutput) : await SpicetifyService.InstallSpotifyAsync(token, OnOutput);
            if (!IsCurrentOperation()) return;
            progress.IsIndeterminate = false;
            if (!result.Success)
            {
                progress.Value = 0; statusText.Text = "Instalace Spotify selhala.";
                string error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                await ShowStyledInfoAsync("Spotify", string.IsNullOrWhiteSpace(error) ? $"Spotify installer skončil s kódem {result.ExitCode}." : error.Trim());
                return;
            }
            progress.Value = 100; statusText.Text = "Instalátor Spotify dokončen. Obnovuji stav…";
            await Task.Delay(350, token); if (IsCurrentOperation()) { InvalidateSpicetifyPageCache(); await ShowSectionAsync("spicetify"); }
        }
        catch (OperationCanceledException) { progress.IsIndeterminate = false; if (IsCurrentOperation()) statusText.Text = "Operace byla zrušena."; }
        catch (Exception ex) { progress.IsIndeterminate = false; progress.Value = 0; statusText.Text = "Instalace Spotify selhala."; LogService.Error("spotify", "Spotify install/update failed", ex); if (IsCurrentOperation()) await ShowStyledInfoAsync("Spotify", ex.Message); }
        finally { progress.IsIndeterminate = false; button.IsEnabled = true; }
    }

    private static void UpdateSpotifyInstallProgress(string line, ProgressBar progress, TextBlock statusText)
    {
        string text = line.Trim();
        var match = Regex.Match(text, @"(?<!\d)(100|[1-9]?\d)%");
        if (match.Success && double.TryParse(match.Groups[1].Value, out double pct)) { progress.IsIndeterminate = false; progress.Value = Math.Clamp(pct, 0, 100); }
        else if (text.Contains("Downloading", StringComparison.OrdinalIgnoreCase) || text.Contains("Stahov", StringComparison.OrdinalIgnoreCase)) progress.IsIndeterminate = true;
        else if (text.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) || text.Contains("Installing", StringComparison.OrdinalIgnoreCase) || text.Contains("Instal", StringComparison.OrdinalIgnoreCase)) { progress.IsIndeterminate = false; progress.Value = Math.Max(progress.Value, 75); }
        if (text.Length > 160) text = text[..160] + "…";
        statusText.Text = text;
    }

    private void BuildSpicetifyThemeCard(SpicetifyStatus status, StackPanel root, CancellationToken token)
    {
        var theme = new ComboBox { Header = "Theme", MinWidth = 280, ItemsSource = status.Themes };
        var scheme = new ComboBox { Header = "Color scheme", MinWidth = 280, IsEnabled = false };
        theme.SelectionChanged += (_, _) =>
        {
            string? selected = theme.SelectedItem as string;
            IReadOnlyList<string> schemes = selected == null
                ? Array.Empty<string>()
                : SpicetifyService.GetColorSchemes(selected);
            scheme.ItemsSource = schemes;
            scheme.SelectedIndex = schemes.Count > 0 ? 0 : -1;
            scheme.IsEnabled = schemes.Count > 0;
        };
        root.Children.Add(theme);
        root.Children.Add(scheme);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Použít theme", async () =>
        {
            if (theme.SelectedItem is not string selected) { await ShowStyledInfoAsync("Spicetify", "Nejdřív vyber theme."); return; }
            await RunSpicetifyActionAsync("Použít theme", ct => SpicetifyService.ApplyThemeAsync(selected, scheme.SelectedItem as string, ct), token);
        }));
        actions.Children.Add(QuickButton("Obnovit seznam", () => { InvalidateSpicetifyPageCache(); return ShowSectionAsync("spicetify"); }));
        root.Children.Add(actions);
    }
    private async Task RunSpicetifyActionAsync(string title, Func<CancellationToken, Task<ProcessResult>> action, CancellationToken token, bool refresh = true)
    {
        int operationGeneration = _navigationGeneration;
        bool IsCurrentOperation() => operationGeneration == _navigationGeneration && _section == "spicetify" && !token.IsCancellationRequested;
        try
        {
            ProcessResult result = await action(token);
            if (!IsCurrentOperation()) return;
            string output = string.Join(Environment.NewLine, new[] { result.StdOut.Trim(), result.StdErr.Trim() }.Where(x => x.Length > 0));
            if (output.Length > 5000) output = output[^5000..];
            if (!result.Success)
            {
                string detail = string.IsNullOrWhiteSpace(output)
                    ? $"Operace selhala (exit {result.ExitCode})."
                    : output;
                ShowActionFeedback(title, ActionFeedbackKind.Failed, detail.Length > 220 ? detail[..220] + "…" : detail);
                await ShowStyledInfoAsync(title, detail);
                return;
            }
            ShowActionFeedback(title, ActionFeedbackKind.Applied, "Operace dokončena a stav bude znovu načten.");
            if (!string.IsNullOrWhiteSpace(output)) await ShowStyledInfoAsync(title, output);
            if (refresh && IsCurrentOperation()) { InvalidateSpicetifyPageCache(); await ShowSectionAsync("spicetify"); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("spicetify", title + " failed", ex);
            if (!IsCurrentOperation()) return;
            ShowActionFeedback(title, ActionFeedbackKind.Failed, ex.Message);
            await ShowStyledInfoAsync(title, ex.Message);
        }
    }
}
