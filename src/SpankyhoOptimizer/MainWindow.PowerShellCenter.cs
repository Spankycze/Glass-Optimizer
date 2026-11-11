using System;
using System.IO;
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
    private TextBlock? _psCenterStatus;
    private TextBlock? _shellHealthStatus;
    private TextBlock? _cmdOptimizerStatus;
    private TextBlock? _cttPowerShellProfileStatus;
    private TextBlock? _titusAiStatus;
    private int _psCenterLoadGeneration;

    private void BuildPowerShellCenterExtras(CancellationToken token)
    {
        int generation = unchecked(++_psCenterLoadGeneration);
        var phase = System.Diagnostics.Stopwatch.StartNew();

        var quick = new StackPanel { Spacing = 11 };

        _psCenterStatus = new TextBlock
        {
            Text = "Načítám PowerShell stav…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        quick.Children.Add(_psCenterStatus);

        _cmdOptimizerStatus = new TextBlock
        {
            Text = "Načítám CMD / Clink stav…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        quick.Children.Add(_cmdOptimizerStatus);

        var primary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        primary.Children.Add(PrimaryQuickButton("NAINSTALOVAT + NASTAVIT VŠE", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                "Shell Quick Setup",
                "Nainstalovat a nastavit doporučený PowerShell / CMD balík včetně CLI nástrojů, PSReadLine, Oh My Posh, CMD Recommended a Clink AutoRun?",
                "Nainstalovat vše",
                "Zrušit",
                "PowerShell / CMD"))
                return;

            var result = await RunCancellableWithProgressAsync(
                "Shell Quick Setup",
                "Instaluji a nastavuji PowerShell / CMD…",
                async ct => await ShellToolkitService.InstallAndApplyEverythingAsync(ct));
            if (!result.Completed) return;

            await LoadPowerShellCenterStatusAsync(token, generation);
            await LoadShellToolkitStatusAsync(token, generation);

            var setup = result.Result;
            string message = setup.ErrorCount == 0
                ? $"Shell setup dokončen.\n\n{setup.SuccessCount} / {setup.TotalCount} kroků OK."
                : $"Shell setup dokončen s {setup.ErrorCount} problémem/problémy.\n\n" +
                  $"{setup.SuccessCount} / {setup.TotalCount} kroků OK.\n\n" +
                  "Problémy:\n" +
                  string.Join(Environment.NewLine, setup.Errors.Select(x => $"• {x.Name}: {x.Message}"));

            ShowActionFeedback(
                "Shell Quick Setup",
                setup.ErrorCount == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Applied,
                setup.ErrorCount == 0
                    ? "PowerShell / CMD je připraven."
                    : $"Dokončeno s {setup.ErrorCount} problémem/problémy.");

            await ShowMessageAsync("Shell Quick Setup", message);
        }));
        primary.Children.Add(QuickButton("OBNOVIT STAV", async () =>
        {
            await LoadPowerShellCenterStatusAsync(token, generation);
            await LoadShellToolkitStatusAsync(token, generation);
        }));
        quick.Children.Add(primary);


        ContentStack.Children.Add(BuildSectionSurface(
            "powershell",
            "Shell Quick Setup",
            "Jedno místo pro PowerShell 7 • CMD • Clink • CLI nástroje • doporučené nastavení",
            quick));
        long quickSetupMs = phase.ElapsedMilliseconds;

        // Keep the actionable Quick Setup visible immediately. The secondary cards
        // perform additional WinUI construction; queue them after navigation completes.
        int navigationGeneration = _navigationGeneration;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _psCenterLoadGeneration ||
                navigationGeneration != _navigationGeneration || _section != "powershell") return;
            var deferred = System.Diagnostics.Stopwatch.StartNew();
            BuildShellHealthCard(token, generation);
            long shellHealthMs = deferred.ElapsedMilliseconds;
            BuildChrisTitusIntegrationsCard(token, generation);
            long integrationsMs = deferred.ElapsedMilliseconds - shellHealthMs;
            SafeTaskService.Forget(LoadPowerShellCenterStatusAsync(token, generation), "powershell", "PowerShell Center status");
            SafeTaskService.Forget(LoadShellToolkitStatusAsync(token, generation), "powershell", "CMD / CLI status");
            LogService.Info("performance", $"POWERSHELL_EXTRAS_DEFERRED quickSetupMs={quickSetupMs} shellHealthMs={shellHealthMs} integrationsMs={integrationsMs} totalMs={deferred.ElapsedMilliseconds}");
        }))
            LogService.Info("performance", "POWERSHELL_EXTRAS_DEFERRED queue=false");
    }

    private void BuildChrisTitusIntegrationsCard(CancellationToken token, int generation)
    {
        _cttPowerShellProfileStatus = new TextBlock
        {
            Foreground = TextSecondaryBrush(),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        _titusAiStatus = new TextBlock
        {
            Foreground = TextSecondaryBrush(),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };

        // File enumeration (including Codex skills and PowerShell backup
        // folders) and registry status reads used to run synchronously on
        // the WinUI dispatcher. Keep only presentation on the UI thread.
        int statusRefreshSequence = 0;
        async Task RefreshStatusesAsync(int refreshSequence)
        {
            if (token.IsCancellationRequested || generation != _psCenterLoadGeneration ||
                _section != "powershell") return;
            var profileLabel = _cttPowerShellProfileStatus;
            var aiLabel = _titusAiStatus;
            int navigationGeneration = _navigationGeneration;
            try
            {
                var statuses = await Task.Run(() =>
                {
                    var profile = ChrisTitusIntegrationService.GetPowerShellProfileStatus();
                    var ai = ChrisTitusIntegrationService.GetTitusAiStatus();
                    var profileAuto = ChrisTitusIntegrationService.GetAutoUpdateStatusLabel(false);
                    var aiAuto = ChrisTitusIntegrationService.GetAutoUpdateStatusLabel(true);
                    return (profile, ai, profileAuto, aiAuto);
                }, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || generation != _psCenterLoadGeneration ||
                        refreshSequence != statusRefreshSequence ||
                        navigationGeneration != _navigationGeneration || _section != "powershell" ||
                        !ReferenceEquals(profileLabel, _cttPowerShellProfileStatus) ||
                        !ReferenceEquals(aiLabel, _titusAiStatus)) return;
                    if (profileLabel != null)
                    {
                        profileLabel.Text = statuses.profile.Detail + "\n" + statuses.profileAuto;
                        profileLabel.Foreground = statuses.profile.Installed
                            ? UiBrush("SpankySuccessBrush") : TextSecondaryBrush();
                    }
                    if (aiLabel != null)
                    {
                        aiLabel.Text = statuses.ai.Detail + "\n" + statuses.aiAuto;
                        aiLabel.Foreground = statuses.ai.Installed
                            ? UiBrush("SpankySuccessBrush") : TextSecondaryBrush();
                    }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogService.Error("powershell", "Chris Titus integration status read failed", ex);
            }
        }
        void RefreshStatuses()
        {
            // A prior read from the same page must not overwrite the state
            // after an install, restore, or auto-update toggle.
            int refreshSequence = ++statusRefreshSequence;
            SafeTaskService.Forget(RefreshStatusesAsync(refreshSequence),
                "powershell", "Chris Titus integration status");
        }

        Border IntegrationCard(string title, string subtitle, TextBlock status, params Button[] actions)
        {
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush()
            });
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(status);

            var buttons = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            int actionRows = (actions.Length + 1) / 2;
            for (int row = 0; row < actionRows; row++)
                buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < actions.Length; i++)
            {
                Button action = actions[i];
                action.HorizontalAlignment = HorizontalAlignment.Stretch;
                Grid.SetColumn(action, i % 2);
                Grid.SetRow(action, i / 2);
                buttons.Children.Add(action);
            }
            stack.Children.Add(buttons);

            return new Border
            {
                Background = GlassSurfaceElevatedBrush(),
                BorderBrush = GlassBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14),
                Child = stack
            };
        }

        var profileInstall = PrimaryQuickButton("INSTALOVAT / AKTUALIZOVAT", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                "Chris Titus PowerShell Profile",
                "Stáhnout aktuální immutable commit z oficiálního repozitáře, ověřit setup.ps1 a spustit jeho instalaci? Existující PowerShell profil upstream zálohuje jako oldprofile-*.ps1.",
                "Pokračovat", "Zrušit", "PowerShell"))
                return;

            var result = await RunCancellableWithProgressAsync(
                "Chris Titus PowerShell Profile",
                "Stahuji a instaluji profil z oficiálního GitHub repozitáře…",
                ct => ChrisTitusIntegrationService.InstallOrUpdatePowerShellProfileAsync(ct));
            if (!result.Completed) return;
            RefreshStatuses();
            ShowActionFeedback("PowerShell Profile", ActionFeedbackKind.Verified, result.Result);
        });
        var profileRestore = QuickButton("OBNOVIT ZÁLOHU", async () =>
        {
            var result = await RunCancellableWithProgressAsync(
                "PowerShell Profile",
                "Obnovuji poslední oldprofile zálohu…",
                ct => ChrisTitusIntegrationService.RestoreLatestPowerShellProfileBackupAsync(ct));
            if (!result.Completed) return;
            RefreshStatuses();
            ShowActionFeedback("PowerShell Profile", ActionFeedbackKind.Verified, result.Result);
        });
        Button? profileAuto = null;
        profileAuto = QuickButton(ChrisTitusIntegrationService.GetAutoUpdateEnabled(false)
            ? "AUTO UPDATE: ZAPNUTO" : "AUTO UPDATE: VYPNUTO", async () =>
        {
            bool enabled = ChrisTitusIntegrationService.GetAutoUpdateEnabled(false);
            if (!enabled && !await ShowStyledConfirmAsync(
                "Automatické aktualizace PowerShell Profile",
                "Povolit při budoucím spuštění Optimizeru automatickou instalaci nového PowerShell profilu? Skript může měnit nastavení shellu; před aktualizací se uloží stav profilu a při chybě se vrátí zpět. Vyžaduje správce.",
                "Zapnout", "Zrušit", "PowerShell")) return;
            ChrisTitusIntegrationService.SetAutoUpdateEnabled(false, !enabled);
            if (profileAuto != null) profileAuto.Content = enabled ? "AUTO UPDATE: VYPNUTO" : "AUTO UPDATE: ZAPNUTO";
            RefreshStatuses();
        });
        var profileSource = QuickButton("GITHUB", async () =>
        {
            ChrisTitusIntegrationService.OpenPowerShellProfileSource();
            await Task.CompletedTask;
        });

        var titusInstall = PrimaryQuickButton("INSTALOVAT / AKTUALIZOVAT", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                "Titus AI",
                "Nainstalovat/aktualizovat Codex konfiguraci, pravidla a skills z oficiálního repozitáře? Nejdřív proběhne upstream -DryRun. Credentials, sessions a history se tímto krokem nemají měnit.",
                "Pokračovat", "Zrušit", "PowerShell"))
                return;

            var result = await RunCancellableWithProgressAsync(
                "Titus AI",
                "Provádím dry-run a instaluji Codex konfiguraci + skills…",
                ct => ChrisTitusIntegrationService.InstallOrUpdateTitusAiAsync(false, ct));
            if (!result.Completed) return;
            RefreshStatuses();
            ShowActionFeedback("Titus AI", ActionFeedbackKind.Verified, result.Result);
        });
        var titusPlugins = QuickButton("DOPORUČENÉ PLUGINY", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                "Titus AI pluginy",
                "Toto je samostatný opt-in krok. Spustit upstream instalaci s -Plugins a přidat pluginy uvedené v codex-plugins.txt?",
                "Instalovat", "Zrušit", "PowerShell"))
                return;

            var result = await RunCancellableWithProgressAsync(
                "Titus AI pluginy",
                "Ověřuji dry-run a instaluji doporučené Codex pluginy…",
                ct => ChrisTitusIntegrationService.InstallOrUpdateTitusAiAsync(true, ct));
            if (!result.Completed) return;
            RefreshStatuses();
            ShowActionFeedback("Titus AI", ActionFeedbackKind.Verified, result.Result);
        });
        Button? titusAuto = null;
        titusAuto = QuickButton(ChrisTitusIntegrationService.GetAutoUpdateEnabled(true)
            ? "AUTO UPDATE: ZAPNUTO" : "AUTO UPDATE: VYPNUTO", async () =>
        {
            bool enabled = ChrisTitusIntegrationService.GetAutoUpdateEnabled(true);
            if (!enabled && !await ShowStyledConfirmAsync(
                "Automatické aktualizace Titus AI",
                "Povolit při budoucím spuštění Optimizeru automatickou aktualizaci Titus AI konfigurace a skills? Aktualizace proběhne pouze když neběží Codex, nejdřív DryRun; předchozí config.toml se při chybě obnoví. Pluginy se neinstalují automaticky.",
                "Zapnout", "Zrušit", "PowerShell")) return;
            ChrisTitusIntegrationService.SetAutoUpdateEnabled(true, !enabled);
            if (titusAuto != null) titusAuto.Content = enabled ? "AUTO UPDATE: VYPNUTO" : "AUTO UPDATE: ZAPNUTO";
            RefreshStatuses();
        });
        var titusBackups = QuickButton("ZÁLOHY", async () =>
        {
            ChrisTitusIntegrationService.OpenTitusAiBackups();
            await Task.CompletedTask;
        });
        var titusSource = QuickButton("GITHUB", async () =>
        {
            ChrisTitusIntegrationService.OpenTitusAiSource();
            await Task.CompletedTask;
        });

        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var profileCard = IntegrationCard(
            "Chris Titus PowerShell Profile",
            "Profil pro PowerShell 7: PSReadLine, Git helpery, Terminal-Icons, Oh My Posh, zoxide a Nerd Font.",
            _cttPowerShellProfileStatus,
            profileInstall, profileRestore, profileAuto, profileSource);
        var titusCard = IntegrationCard(
            "Titus AI",
            "Volitelná konfigurace, pravidla a skills pro Codex; nejde o samostatný AI model. Pluginy pouze na vyžádání.",
            _titusAiStatus,
            titusInstall, titusAuto, titusPlugins, titusBackups, titusSource);
        Grid.SetColumn(profileCard, 0);
        Grid.SetColumn(titusCard, 1);
        grid.Children.Add(profileCard);
        grid.Children.Add(titusCard);

        ContentStack.Children.Add(BuildSectionSurface(
            "powershell",
            "Chris Titus Tech",
            "GitHub commity • bezpečný rollback • automatické aktualizace jen po zapnutí • dry-run Titus AI",
            grid));
        RefreshStatuses();
        // Remote release/commit lookup is asynchronous and never starts PowerShell.
        // Updates remain user-controlled so upstream scripts cannot run silently.
        var profileStatusLabel = _cttPowerShellProfileStatus;
        var titusStatusLabel = _titusAiStatus;
        SafeTaskService.Forget(CheckChrisTitusUpdatesAsync(token, generation, profileStatusLabel, titusStatusLabel),
            "powershell", "Chris Titus GitHub update check");
    }

    private async Task CheckChrisTitusUpdatesAsync(CancellationToken token, int generation, TextBlock? profileLabel, TextBlock? titusLabel)
    {
        try
        {
            var updates = await ChrisTitusIntegrationService.CheckForUpdatesAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _psCenterLoadGeneration || _section != "powershell") return;
                if (updates.ProfileUpdate && profileLabel != null && ReferenceEquals(_cttPowerShellProfileStatus, profileLabel))
                    profileLabel.Text += " • NOVÁ VERZE K DISPOZICI";
                if (updates.TitusUpdate && titusLabel != null && ReferenceEquals(_titusAiStatus, titusLabel))
                    titusLabel.Text += " • NOVÁ VERZE K DISPOZICI";
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Info("powershell", "GitHub update check unavailable: " + ex.Message); }
    }

    private async Task LoadPowerShellCenterStatusAsync(CancellationToken token, int generation)
    {
        try
        {
            var status = await PowerShellCenterService.GetStatusAsync(token);
            if (token.IsCancellationRequested || generation != _psCenterLoadGeneration || _section != "powershell")
                return;

            if (_psCenterStatus != null)
            {
                static bool Ready(string value) =>
                    !string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("Missing", StringComparison.OrdinalIgnoreCase);

                int ready = new[]
                {
                    status.PSReadLineVersion,
                    status.PSResourceGetVersion,
                    status.PSScriptAnalyzerVersion,
                    status.WinGetClientVersion,
                    status.TerminalIconsVersion,
                    status.OhMyPoshVersion
                }.Count(Ready);

                _psCenterStatus.Text =
                    $"PowerShell {status.PowerShellVersion} • " +
                    $"PSReadLine {(Ready(status.PSReadLineVersion) ? "AKTIVNÍ" : "CHYBÍ")} • " +
                    $"Terminal-Icons {(Ready(status.TerminalIconsVersion) ? "PŘIPRAVENO" : "CHYBÍ")} • " +
                    $"Oh My Posh {(Ready(status.OhMyPoshVersion) ? "NAINSTALOVÁNO" : "CHYBÍ")} • " +
                    $"Core {ready}/6";
                _psCenterStatus.Foreground = ready >= 5 ? UiBrush("SpankySuccessBrush") : UiBrush("SpankyWarningBrush");
            }

        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Info("powershell", "PowerShell Center status cancelled by navigation.");
        }
        catch (Exception ex)
        {
            LogService.Error("powershell", "PowerShell Center status failed", ex);
            if (_psCenterStatus != null && !token.IsCancellationRequested)
                _psCenterStatus.Text = "PowerShell Center status selhal: " + ex.Message;
        }
    }

    internal async Task RunPowerShellCenterSmokeAsync()
    {
        await ShowSectionAsync("powershell");

        for (int i = 0; i < 40 &&
             (_psCenterStatus == null || _shellHealthStatus == null || _cmdOptimizerStatus == null ||
              _cttPowerShellProfileStatus == null || _titusAiStatus == null); i++)
            await Task.Delay(50);

        if (_psCenterStatus == null || _shellHealthStatus == null || _cmdOptimizerStatus == null ||
            _cttPowerShellProfileStatus == null || _titusAiStatus == null)
            throw new InvalidOperationException("PowerShell/Titus UI nebylo vytvořeno do 2 s.");

        // A status read now finishes on a worker and publishes back via the
        // dispatcher. Assert actual hydrated labels, not merely their existence.
        for (int i = 0; i < 40 &&
             (string.IsNullOrWhiteSpace(_cttPowerShellProfileStatus.Text) ||
              string.IsNullOrWhiteSpace(_titusAiStatus.Text)); i++)
            await Task.Delay(50);
        if (_cttPowerShellProfileStatus.Text?.Contains("Auto-update:", StringComparison.Ordinal) != true ||
            _titusAiStatus.Text?.Contains("Auto-update:", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Chris Titus background status did not publish to both WinUI labels.");

        var status = await PowerShellCenterService.GetStatusAsync(_windowLifetimeCts.Token);
        if (string.IsNullOrWhiteSpace(status.PowerShellVersion) || status.PowerShellVersion == "Missing")
            throw new InvalidOperationException("PowerShell 7 status nebyl načten.");

        var modules = await PowerShellCenterService.GetManagedModulesAsync(_windowLifetimeCts.Token);
        if (modules.Count < 5)
            throw new InvalidOperationException($"PowerShell module inventory je neúplný: {modules.Count}.");

        _ = await PowerShellCenterService.IsWinGetClientAvailableAsync(_windowLifetimeCts.Token);
        var profileIntegration = ChrisTitusIntegrationService.GetPowerShellProfileStatus();
        var titusIntegration = ChrisTitusIntegrationService.GetTitusAiStatus();
        if (string.IsNullOrWhiteSpace(profileIntegration.Detail))
            throw new InvalidOperationException("Chris Titus PowerShell Profile status je prázdný.");
        if (string.IsNullOrWhiteSpace(titusIntegration.Detail))
            throw new InvalidOperationException("Titus AI status je prázdný.");

        var shellHealth = await ShellHealthService.AnalyzeAsync(_windowLifetimeCts.Token);
        LogService.Info("POWERSHELL_CENTER_SMOKE_OK modules=" + modules.Count +
            " ps=" + status.PowerShellVersion +
            " psreadline=" + status.PSReadLineVersion +
            " psresourceget=" + status.PSResourceGetVersion +
            " profileManaged=" + profileIntegration.Installed +
            " titusAiManaged=" + titusIntegration.Installed +
            " shellErrors=" + shellHealth.Errors +
            " shellWarnings=" + shellHealth.Warnings);
    }

}
