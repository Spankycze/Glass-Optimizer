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
    private void BuildShellHealthCard(CancellationToken token, int generation)
    {
        var root = new StackPanel { Spacing = 10 };
        _shellHealthStatus = new TextBlock
        {
            Text = "Připraveno k auditu shellu.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(_shellHealthStatus);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("AUDIT SHELLU", async () =>
        {
            var result = await RunCancellableWithProgressAsync(
                "Shell Health",
                "Kontroluji PATH, PowerShell profil, CMD AutoRun a Windows Terminal…",
                async ct => await ShellHealthService.AnalyzeAsync(ct));
            if (!result.Completed) return;

            var report = result.Result;
            if (_shellHealthStatus != null)
                _shellHealthStatus.Text =
                    $"Shell Health: Errors {report.Errors} • Warnings {report.Warnings} • Findings {report.Findings.Count}";

            if (report.Findings.Count == 0)
            {
                ShowActionFeedback("Shell Health", ActionFeedbackKind.Verified, "Nenalezen žádný problém.");
                return;
            }

            string details = string.Join(Environment.NewLine, report.Findings.Take(50).Select(x =>
                $"{x.Severity} • {x.Area} • {x.Message}" +
                (x.Repairable ? " • Repairable" : string.Empty)));
            await ShowMessageAsync("Shell Health", details);
        }));        actions.Children.Add(QuickButton("REPAIR RECOMMENDED", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                "Shell Repair",
                "Opravit pouze bezpečné odchylky? User PATH se před změnou zálohuje. Neexistující PATH cesty ani vlastní CMD AutoRun příkazy se nemažou.",
                "Opravit", "Zrušit", "PowerShell"))
                return;

            string repaired = ShellHealthService.RepairRecommended();
            var report = await ShellHealthService.AnalyzeAsync(token);
            if (_shellHealthStatus != null)
                _shellHealthStatus.Text =
                    $"Shell Health: Errors {report.Errors} • Warnings {report.Warnings} • Findings {report.Findings.Count}";
            ShowActionFeedback("Shell Repair", ActionFeedbackKind.Verified, repaired);
        }));

        actions.Children.Add(QuickButton("STARTUP COST", async () =>
        {
            var result = await RunCancellableWithProgressAsync(
                "PowerShell startup cost",
                "Měřím cenu jednotlivých PowerShell integrací v čistém pwsh procesu…",
                async ct => await ShellHealthService.MeasureKnownProfileCostsAsync(ct));
            if (!result.Completed) return;

            var items = result.Result;
            string details = string.Join(Environment.NewLine, items.Select(x =>
                x.Available
                    ? $"{x.Name}: {Math.Round(x.Milliseconds, 1)} ms"
                    : $"{x.Name}: Missing / probe failed"));

            double normal = await BenchmarkPowerShellStartupAsync(false, 5, token);
            double bare = await BenchmarkPowerShellStartupAsync(true, 5, token);
            double overhead = normal >= 0 && bare >= 0 ? Math.Max(0, normal - bare) : -1;

            if (_shellHealthStatus != null)
                _shellHealthStatus.Text =
                    $"PowerShell startup {FmtMs(normal)} • -NoProfile {FmtMs(bare)} • profile overhead {FmtMs(overhead)}";

            await ShowMessageAsync(
                "PowerShell Startup Cost",
                $"Celý startup: {FmtMs(normal)}{Environment.NewLine}" +
                $"-NoProfile: {FmtMs(bare)}{Environment.NewLine}" +
                $"Profile overhead: {FmtMs(overhead)}{Environment.NewLine}{Environment.NewLine}{details}");
        }));

        actions.Children.Add(QuickButton("FAST PROFILE", async () =>
        {
            int removed = PowerShellCenterService.ApplyFastStartupProfile();
            double normal = await BenchmarkPowerShellStartupAsync(false, 5, token);
            double bare = await BenchmarkPowerShellStartupAsync(true, 5, token);
            double overhead = normal >= 0 && bare >= 0 ? Math.Max(0, normal - bare) : -1;
            if (_shellHealthStatus != null)
                _shellHealthStatus.Text =
                    $"Fast Profile: odstraněno {removed} volitelných startup integrací • startup {FmtMs(normal)} • overhead {FmtMs(overhead)}";
            ShowActionFeedback("PowerShell Fast Profile", ActionFeedbackKind.Verified,
                removed > 0
                    ? $"Odstraněno volitelných startup integrací: {removed}. Záloha profilu vytvořena."
                    : "Nebyla nalezena žádná naše volitelná startup integrace.");
        }));

        actions.Children.Add(QuickButton("RESTORE PROFILE", async () =>
        {
            bool restored = PowerShellCenterService.RestoreFastStartupProfileBackup();
            if (_shellHealthStatus != null)
                _shellHealthStatus.Text = restored
                    ? "PowerShell startup profil obnoven ze zálohy."
                    : "Startup záloha profilu nebyla nalezena.";
            ShowActionFeedback("PowerShell Fast Profile",
                restored ? ActionFeedbackKind.Verified : ActionFeedbackKind.Applied,
                restored ? "Původní profil byl obnoven." : "Záloha nebyla nalezena.");
            await Task.CompletedTask;
        }));        root.Children.Add(actions);

        root.Children.Add(new TextBlock
        {
            Text = "Repair Recommended opravuje pouze bezpečné věci: duplicitní/prázdné User PATH položky a poškozený Terminal JSON se zálohou. Chybějící PATH cesty, vlastní CMD AutoRun a syntaktické chyby profilu pouze nahlásí.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush(),
            FontSize = 11
        });

        ContentStack.Children.Add(BuildSectionSurface(
            "powershell",
            "Shell Health & Startup Cost",
            "PATH • PowerShell profile parser • CMD AutoRun • Terminal JSON • per-module startup timing",
            root));
    }

    private static async Task<double> BenchmarkPowerShellStartupAsync(bool noProfile, int repeats, CancellationToken token)
    {
        const string exe = @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (!System.IO.File.Exists(exe)) return -1;

        double total = 0;
        int completed = 0;
        for (int i = 0; i < Math.Max(1, repeats); i++)
        {
            token.ThrowIfCancellationRequested();
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    Arguments = noProfile ? "-NoLogo -NoProfile -NonInteractive -Command exit" : "-NoLogo -NonInteractive -Command exit",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!process.Start()) continue;
            await process.WaitForExitAsync(token);
            sw.Stop();
            if (process.ExitCode != 0) continue;
            total += sw.Elapsed.TotalMilliseconds;
            completed++;
        }

        return completed == 0 ? -1 : total / completed;
    }
}