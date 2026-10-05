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

        var secondary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        secondary.Children.Add(QuickButton("FAST PROFILE", async () =>
        {
            int removed = PowerShellCenterService.ApplyFastStartupProfile();
            ShowActionFeedback(
                "Fast Profile",
                ActionFeedbackKind.Verified,
                removed > 0
                    ? $"Odstraněno {removed} volitelných startup integrací."
                    : "Profil už je optimalizovaný.");
            await Task.CompletedTask;
        }));
        secondary.Children.Add(QuickButton("OPRAVIT SHELL", async () =>
        {
            string repaired = ShellHealthService.RepairRecommended();
            ShowActionFeedback("Shell Repair", ActionFeedbackKind.Verified, repaired);
            await Task.CompletedTask;
        }));
        quick.Children.Add(secondary);

        ContentStack.Children.Add(BuildSectionSurface(
            "powershell",
            "Shell Quick Setup",
            "Jedno místo pro PowerShell 7 • CMD • Clink • CLI nástroje • doporučené nastavení",
            quick));
        long quickSetupMs = phase.ElapsedMilliseconds;

        BuildShellHealthCard(token, generation);
        long shellHealthMs = phase.ElapsedMilliseconds - quickSetupMs;

        long kickoffStart = phase.ElapsedMilliseconds;
        SafeTaskService.Forget(
            LoadPowerShellCenterStatusAsync(token, generation),
            "powershell",
            "PowerShell Center status");
        SafeTaskService.Forget(
            LoadShellToolkitStatusAsync(token, generation),
            "powershell",
            "CMD / CLI status");
        long kickoffMs = phase.ElapsedMilliseconds - kickoffStart;

        LogService.Info("performance",
            $"POWERSHELL_EXTRAS_PHASE quickSetupMs={quickSetupMs} shellHealthMs={shellHealthMs} kickoffMs={kickoffMs} totalMs={phase.ElapsedMilliseconds}");
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

    internal async Task RunPowerShellPrewarmSmokeAsync()
    {
        for (int i = 0; i < 40 && _prewarmedPowerShellHero == null; i++)
            await Task.Delay(50);

        if (_prewarmedPowerShellHero == null)
            throw new InvalidOperationException("PowerShell hero prewarm nebyl připraven do 2 s.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("powershell");

        if (_prewarmedPowerShellHero != null)
            throw new InvalidOperationException("PowerShell hero prewarm nebyl spotřebován při navigaci.");

        LogService.Info("performance", $"POWERSHELL_PREWARM_SMOKE_OK navigationMs={sw.ElapsedMilliseconds}");
    }

    internal async Task RunPowerShellCenterSmokeAsync()
    {
        await ShowSectionAsync("powershell");

        for (int i = 0; i < 40 &&
             (_psCenterStatus == null || _shellHealthStatus == null || _cmdOptimizerStatus == null); i++)
            await Task.Delay(50);

        if (_psCenterStatus == null || _shellHealthStatus == null || _cmdOptimizerStatus == null)
            throw new InvalidOperationException("Shell Quick Setup UI nebylo vytvořeno do 2 s.");

        var status = await PowerShellCenterService.GetStatusAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(status.PowerShellVersion) || status.PowerShellVersion == "Missing")
            throw new InvalidOperationException("PowerShell 7 status nebyl načten.");

        var modules = await PowerShellCenterService.GetManagedModulesAsync(CancellationToken.None);
        if (modules.Count < 5)
            throw new InvalidOperationException($"PowerShell module inventory je neúplný: {modules.Count}.");

        _ = await PowerShellCenterService.IsWinGetClientAvailableAsync(CancellationToken.None);
        var shellHealth = await ShellHealthService.AnalyzeAsync(CancellationToken.None);
        LogService.Info("POWERSHELL_CENTER_SMOKE_OK modules=" + modules.Count +
            " ps=" + status.PowerShellVersion +
            " psreadline=" + status.PSReadLineVersion +
            " psresourceget=" + status.PSResourceGetVersion +
            " shellErrors=" + shellHealth.Errors +
            " shellWarnings=" + shellHealth.Warnings);
    }

}
