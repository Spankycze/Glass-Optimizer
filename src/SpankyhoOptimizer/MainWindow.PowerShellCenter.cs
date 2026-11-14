using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private TextBlock? _cttPowerShellProfileStatus;
    private int _psCenterLoadGeneration;

    private void BuildPowerShellCenterExtras(CancellationToken token)
    {
        int generation = unchecked(++_psCenterLoadGeneration);
        _cttPowerShellProfileStatus = new TextBlock
        {
            Foreground = TextSecondaryBrush(),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };

        int statusRefreshSequence = 0;
        async Task RefreshStatusesAsync(int refreshSequence)
        {
            if (token.IsCancellationRequested || generation != _psCenterLoadGeneration ||
                _section != "powershell") return;
            var profileLabel = _cttPowerShellProfileStatus;
            int navigationGeneration = _navigationGeneration;
            try
            {
                var status = await Task.Run(() =>
                {
                    var profile = ChrisTitusIntegrationService.GetPowerShellProfileStatus();
                    var auto = ChrisTitusIntegrationService.GetAutoUpdateStatusLabel(false);
                    return (profile, auto);
                }, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || generation != _psCenterLoadGeneration ||
                        refreshSequence != statusRefreshSequence ||
                        navigationGeneration != _navigationGeneration || _section != "powershell" ||
                        !ReferenceEquals(profileLabel, _cttPowerShellProfileStatus)) return;
                    if (profileLabel != null)
                    {
                        profileLabel.Text = status.profile.Detail + "\n" + status.auto;
                        profileLabel.Foreground = status.profile.Installed
                            ? UiBrush("SpankySuccessBrush") : TextSecondaryBrush();
                    }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogService.Error("powershell", "Titus PowerShell status read failed", ex);
            }
        }
        void RefreshStatuses()
        {
            int refreshSequence = ++statusRefreshSequence;
            SafeTaskService.Forget(RefreshStatusesAsync(refreshSequence),
                "powershell", "Titus PowerShell status");
        }

        var install = PrimaryQuickButton("INSTALOVAT / AKTUALIZOVAT", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                "Titus PowerShell",
                "Stáhnout aktuální immutable commit z oficiálního repozitáře, ověřit setup.ps1 a spustit jeho instalaci? Existující profil se zálohuje jako oldprofile-*.ps1.",
                "Pokračovat", "Zrušit", "PowerShell"))
                return;

            var result = await RunCancellableWithProgressAsync(
                "Titus PowerShell",
                "Stahuji a instaluji profil z oficiálního GitHub repozitáře…",
                ct => ChrisTitusIntegrationService.InstallOrUpdatePowerShellProfileAsync(ct));
            if (!result.Completed) return;
            RefreshStatuses();
            ShowActionFeedback("Titus PowerShell", ActionFeedbackKind.Verified, result.Result);
        });
        var restore = QuickButton("OBNOVIT ZÁLOHU", async () =>
        {
            var result = await RunCancellableWithProgressAsync(
                "Titus PowerShell",
                "Obnovuji poslední oldprofile zálohu…",
                ct => ChrisTitusIntegrationService.RestoreLatestPowerShellProfileBackupAsync(ct));
            if (!result.Completed) return;
            RefreshStatuses();
            ShowActionFeedback("Titus PowerShell", ActionFeedbackKind.Verified, result.Result);
        });
        Button? auto = null;
        auto = QuickButton(ChrisTitusIntegrationService.GetAutoUpdateEnabled(false)
            ? "AUTO UPDATE: ZAPNUTO" : "AUTO UPDATE: VYPNUTO", async () =>
        {
            bool enabled = ChrisTitusIntegrationService.GetAutoUpdateEnabled(false);
            if (!enabled && !await ShowStyledConfirmAsync(
                "Automatické aktualizace Titus PowerShell",
                "Povolit při budoucím spuštění Optimizeru automatickou aktualizaci profilu z GitHubu? Před aktualizací se uloží stav profilu a při chybě se vrátí zpět. Vyžaduje správce.",
                "Zapnout", "Zrušit", "PowerShell")) return;
            ChrisTitusIntegrationService.SetAutoUpdateEnabled(false, !enabled);
            if (auto != null) auto.Content = enabled ? "AUTO UPDATE: VYPNUTO" : "AUTO UPDATE: ZAPNUTO";
            RefreshStatuses();
        });
        var source = QuickButton("GITHUB", async () =>
        {
            ChrisTitusIntegrationService.OpenPowerShellProfileSource();
            await Task.CompletedTask;
        });

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = "Chris Titus PowerShell Profile",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        body.Children.Add(new TextBlock
        {
            Text = "Jediná podporovaná PowerShell integrace: profil pro PowerShell 7 z ChrisTitusTech/powershell-profile. Ostatní shell nástroje se už nenabízejí.",
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(_cttPowerShellProfileStatus);
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(install);
        actions.Children.Add(restore);
        actions.Children.Add(auto);
        actions.Children.Add(source);
        body.Children.Add(actions);

        ContentStack.Children.Add(BuildSectionSurface(
            "powershell",
            "Titus PowerShell",
            "GitHub commity • bezpečný rollback • pouze profil, žádné Titus AI",
            body));
        RefreshStatuses();
        var profileStatusLabel = _cttPowerShellProfileStatus;
        SafeTaskService.Forget(CheckChrisTitusUpdatesAsync(token, generation, profileStatusLabel),
            "powershell", "Titus PowerShell GitHub update check");
    }

    private async Task CheckChrisTitusUpdatesAsync(CancellationToken token, int generation, TextBlock? profileLabel)
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
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Info("powershell", "GitHub update check unavailable: " + ex.Message); }
    }

    internal async Task RunPowerShellCenterSmokeAsync()
    {
        await ShowSectionAsync("powershell");

        for (int i = 0; i < 40 &&
             (_cttPowerShellProfileStatus == null || string.IsNullOrWhiteSpace(_cttPowerShellProfileStatus.Text)); i++)
            await Task.Delay(50);

        if (_cttPowerShellProfileStatus == null ||
            string.IsNullOrWhiteSpace(_cttPowerShellProfileStatus.Text))
            throw new InvalidOperationException("Titus PowerShell UI nebylo vytvořeno do 2 s.");
        if (_cttPowerShellProfileStatus.Text?.Contains("Auto-update:", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Titus PowerShell status neobsahuje auto-update stav.");

        var profile = ChrisTitusIntegrationService.GetPowerShellProfileStatus();
        if (string.IsNullOrWhiteSpace(profile.Detail))
            throw new InvalidOperationException("Titus PowerShell status je prázdný.");

        LogService.Info("TITUS_POWERSHELL_SMOKE_OK profileManaged=" + profile.Installed +
            " autoUpdate=" + ChrisTitusIntegrationService.GetAutoUpdateEnabled(false));
    }
}
