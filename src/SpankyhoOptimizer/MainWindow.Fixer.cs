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
    private Task BuildFixerDiagnosticsAsync(CancellationToken token)
    {
        var host = new StackPanel { Spacing = 7 };
        _fixerDiagnosticsHost = host;
        host.Children.Add(new TextBlock { Text = "Probíhá rychlá kontrola systému…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildDashboardExpander(
            "Kontrola / doporučené opravy",
            "Windows Update • Store • Explorer • síť • Game Bar • GPU",
            host));


        return Task.CompletedTask;
    }

    private async Task LoadFixerDiagnosticsAsync(StackPanel host, CancellationToken token)
    {
        try
        {
            var report = await SystemRepairDiagnosticsService.ScanCachedAsync(token);
            if (token.IsCancellationRequested || _section != "fixer") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "fixer") return;
                host.Children.Clear();
                int healthy = 0, issues = 0;
                foreach (var item in report)
                {
                    if (item.Healthy) healthy++;
                    else issues++;
                }
                host.Children.Add(BuildPageSummaryStrip(
                    ("Kontroly", report.Count.ToString(), "SpankyInfoBrush"),
                    ("OK", healthy.ToString(), "SpankySuccessBrush"),
                    ("K prověření", issues.ToString(), issues == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush")));
                foreach (var item in report)
                {
                    var line = new StackPanel { Spacing = 2 };
                    line.Children.Add(new TextBlock
                    {
                        Text = $"{item.Area} — {(item.Healthy ? "OK" : "Potřebuje pozornost")}",
                        FontWeight = FontWeights.SemiBold,
                        Foreground = item.Healthy ? UiBrush("SpankySuccessBrush") : UiBrush("SpankyWarningBrush")
                    });
                    line.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Foreground = TextSecondaryBrush(), FontSize = 11 });
                    if (!string.IsNullOrWhiteSpace(item.Expected) || !string.IsNullOrWhiteSpace(item.Actual))
                    {
                        line.Children.Add(new TextBlock
                        {
                            Text = $"Očekáváno: {item.Expected ?? "—"}",
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                            Foreground = TextSecondaryBrush(),
                            FontSize = 10.5
                        });
                        line.Children.Add(new TextBlock
                        {
                            Text = $"Aktuálně: {item.Actual ?? item.State}",
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                            Foreground = item.Healthy ? TextSecondaryBrush() : UiBrush("SpankyWarningBrush"),
                            FontSize = 10.5
                        });
                    }
                    if (!item.Healthy)
                    {
                        var repair = CreateFixerRepairAction(item);
                        if (repair != null) line.Children.Add(repair);
                    }
                    host.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = line });
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Fixer V3 diagnostics failed", ex);
            if (!token.IsCancellationRequested) DispatcherQueue.TryEnqueue(() =>
            {
                if (_section == "fixer")
                {
                    host.Children.Clear();
                    host.Children.Add(new TextBlock
                    {
                        Text = "Kontrolu se nepodařilo načíst: " + ex.Message,
                        Foreground = UiBrush("SpankyWarningBrush"),
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                    });
                    host.Children.Add(QuickButton("Zkusit znovu", () =>
                        LoadFixerDiagnosticsAsync(host, token)));
                }
            });
        }
    }

    private UIElement? CreateFixerRepairAction(RepairDiagnostic item)
    {
        return item.Area switch
        {
            "Windows Update" => QuickButton("Otevřít Windows Update", async () => await ShowSectionAsync("update")),
            "Microsoft Store" => QuickButton("Vyčistit cache Microsoft Store", async () => { await ResetStoreCacheAsync(); await Task.Delay(800); if (_section == "fixer") await ShowSectionAsync("fixer"); }),
            "Explorer" => QuickButton("Restartovat Explorer", async () =>
            {
                await WindowsActions.RestartExplorerAsync();
                var verify = await SystemRepairDiagnosticsService.VerifyAreaAsync("Explorer");
                InvalidateFixerDynamicCache();
                if (verify?.Healthy == true)
                    ShowActionFeedback("Explorer", ActionFeedbackKind.Verified, verify.Detail);
                else
                    ShowActionFeedback("Explorer", ActionFeedbackKind.Failed, verify?.Detail ?? "Explorer post-repair kontrola nedostupná.");
                if (_section == "fixer") await ShowSectionAsync("fixer");
            }),
            "Network" => QuickButton("Opravit síť", async () => { await RepairNetworkAsync(); if (_section == "fixer") await ShowSectionAsync("fixer"); }),
            "Škodlivé / zastaralé úpravy" => QuickButton("Opravit špatné tweaky", async () =>
            {
                bool completed = await RunCancellableWithProgressAsync(
                    "Oprava tweaků",
                    "Vrací bezpečné výkonnostní hodnoty a odstraňuje zastaralá omezení…",
                    token => SystemRepairDiagnosticsService.RepairBadTweaksAsync(token));
                if (!completed)
                {
                    LogService.Info("fixer", "Bad tweaks repair cancelled by user.");
                    return;
                }
                var verify = await SystemRepairDiagnosticsService.VerifyAreaAsync("Škodlivé / zastaralé úpravy");
                InvalidateFixerDynamicCache();
                if (verify?.Healthy == true)
                    ShowActionFeedback("Bad tweak repair", ActionFeedbackKind.Verified, verify.Detail);
                else
                    ShowActionFeedback("Bad tweak repair", ActionFeedbackKind.Failed, verify?.Detail ?? "Post-repair kontrola nebyla dostupná.");
                if (_section == "fixer") await ShowSectionAsync("fixer");
            }),
            "Game Bar / Capture" => QuickButton("Otevřít nastavení", OpenGameBarSettingsAsync),
            "GPU driver" => QuickButton("Otevřít GPU centrum", OpenGpuRepairCenterAsync),
            _ => null
        };
    }

    private async Task ShowRepairCenterDiagnosticsAsync()
    {
        var progressResult = await RunCancellableWithProgressAsync(
            "Kontrola systému",
            "Kontroluji Windows Update, Store, Explorer, síť, Game Bar a GPU driver…",
            token => SystemRepairDiagnosticsService.ScanAsync(token));
        if (!progressResult.Completed)
        {
            LogService.Info("fixer", "Fixer diagnostics cancelled by user.");
            return;
        }
        var report = progressResult.Result;
        int healthy = 0, issues = 0;
        foreach (var item in report)
        {
            if (item.Healthy) healthy++;
            else issues++;
        }
        var body = new StackPanel { Spacing = 9 };
        body.Children.Add(BuildPageSummaryStrip(
            ("Kontroly", report.Count.ToString(), "SpankyInfoBrush"),
            ("OK", healthy.ToString(), "SpankySuccessBrush"),
            ("K prověření", issues.ToString(), issues == 0 ? "SpankySuccessBrush" : "SpankyWarningBrush")));
        foreach (var item in report)
        {
            var line = new StackPanel { Spacing = 3 };
            line.Children.Add(new TextBlock
            {
                Text = $"{item.Area} — {item.State}",
                FontWeight = FontWeights.SemiBold,
                Foreground = item.Healthy ? UiBrush("SpankySuccessBrush") : UiBrush("SpankyWarningBrush")
            });
            line.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Foreground = TextSecondaryBrush(), FontSize = 11.5 });
            body.Children.Add(new Border
            {
                Background = SectionCardBackgroundBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 9, 11, 9),
                Child = line
            });
        }
        await ShowStyledContentAsync("Kontrola systému", issues == 0 ? "Všechny kontroly jsou v pořádku." : $"Nalezeno oblastí k prověření: {issues}",
            new ScrollViewer { Content = body, MaxHeight = 560 }, "Zavřít");
    }

    private async Task ResetStoreCacheAsync()
    {
        ProcessRunner.Open("wsreset.exe");
        ShowActionFeedback("Microsoft Store", ActionFeedbackKind.Info, "WSReset byl spuštěn oficiální cestou Windows. Výsledek ověř Windows Store po dokončení.");
        InvalidateFixerDynamicCache();
        await ShowStyledInfoAsync("Microsoft Store", "Spustil jsem oficiální WSReset. Po dokončení se Store může automaticky otevřít.");
    }

    private async Task RepairNetworkAsync()
    {
        if (!await ShowStyledConfirmAsync("Oprava sítě", "Vyčistí DNS cache a resetuje Winsock. Po resetu Winsock může být potřeba restart Windows.", "Opravit síť", "Zrušit", "Fixer")) return;
        bool completed = await RunCancellableWithProgressAsync(
            "Oprava sítě",
            "Resetuji DNS/Winsock…",
            token => SystemRepairDiagnosticsService.FlushNetworkAsync(token));
        if (!completed)
        {
            LogService.Info("fixer", "Network repair cancelled by user.");
            return;
        }
        var verify = await SystemRepairDiagnosticsService.VerifyAreaAsync("Network");
        InvalidateFixerDynamicCache();
        if (verify?.Healthy == true)
        {
            ShowActionFeedback("Network repair", ActionFeedbackKind.RestartRequired,
                "DNS/Winsock reset proběhl a síť po opravě odpovídá. Restart dokončí aplikaci Winsock změn.");
            await ShowStyledInfoAsync("Oprava sítě",
                "DNS cache byla vyčištěna a Winsock resetován. Aktuální konektivita byla po opravě ověřena.\n\n" +
                verify.Detail + "\n\nPro plné dokončení restartuj Windows.");
        }
        else
        {
            string detail = verify?.Detail ?? "Post-repair kontrola sítě nebyla dostupná.";
            ShowActionFeedback("Network repair", ActionFeedbackKind.Failed, detail);
            await ShowStyledInfoAsync("Oprava sítě — kontrola",
                "Reset DNS/Winsock proběhl, ale čerstvá kontrola konektivity není zelená.\n\n" + detail +
                "\n\nRestartuj Windows a potom spusť kontrolu znovu.");
        }
    }

    private Task OpenGpuRepairCenterAsync() => ShowSectionAsync("gpu");

    private Task OpenGameBarSettingsAsync()
    {
        ProcessRunner.Open("ms-settings:gaming-gamebar");
        return Task.CompletedTask;
    }
}
