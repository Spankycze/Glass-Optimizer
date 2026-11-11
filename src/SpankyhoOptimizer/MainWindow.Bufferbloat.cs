using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _bufferbloatRunning;
    private BufferbloatDiagnosticService.Report? _lastBufferbloatReport;

    private UIElement BuildBufferbloatPanel()
    {
        var status = new TextBlock
        {
            Text = "Změř odezvu bez zátěže, při stahování a odesílání. " +
                   "Test se spouští jen ručně, zatím nic neměří.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush()
        };
        var body = new StackPanel { Spacing = 7, Tag = "bufferbloat-readonly" };
        body.Children.Add(new TextBlock
        {
            Text = "Bufferbloat • SQM / CAKE / fq_codel",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        body.Children.Add(status);
        body.Children.Add(PrimaryQuickButton("Spustit test odezvy při zátěži",
            async () => await RunBufferbloatTestAsync(status)));
        body.Children.Add(new TextBlock
        {
            Text = "Maximálně 192 MiB download + 32 MiB upload. " +
                   "Omezený test nemusí vytížit rychlou 2Gb/s přípojku; " +
                   "bez dostatečné zátěže nevydává závěr. Nastavení Windows ani routeru nemění.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush()
        });
        return BuildSectionSurface("network", "Odezva při zátěži",
            "Nová funkce • bufferbloat a doporučení SQM • pouze na vyžádání", body);
    }

    private async Task RunBufferbloatTestAsync(TextBlock status)
    {
        if (Interlocked.CompareExchange(ref _bufferbloatRunning, 1, 0) != 0) return;
        int generation = _navigationGeneration;
        try
        {
            if (!await ShowStyledConfirmAsync("Bufferbloat – test internetové zátěže",
                "Spustit jednorázové měření pingu při stahování a odesílání přes Cloudflare? " +
                "Test může přenést až 192 MiB download a 32 MiB upload, " +
                "krátkodobě zvýší vytížení linky a může ovlivnit stream. " +
                "Nezmění žádné nastavení síťového adaptéru, DNS ani routeru.",
                "Spustit měření", "Zrušit", "Internet • volitelná diagnostika"))
                return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _loadCts?.Token ?? _windowLifetimeCts.Token,
                _windowLifetimeCts.Token);
            status.Text = "Probíhá test: bez zátěže → download → upload. " +
                          "Výsledek se objeví po dokončení všech tří fází.";
            var report = await BufferbloatDiagnosticService.RunAsync(linked.Token);
            if (linked.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "network") return;
            string Comparison(BufferbloatDiagnosticService.Phase p) =>
                p.Received == 0
                    ? "bez ICMP odpovědí"
                    : $"průměr {p.AverageMs:0.0} ms, P95 {p.P95Ms:0.0} ms, " +
                      $"jitter {p.JitterMs:0.0} ms, ztráty {p.Sent-p.Received}/{p.Sent}";
            string history = _lastBufferbloatReport is { HasValidComparison: true } previous &&
                             report.HasValidComparison
                ? $"\nProti minulému testu: download delta " +
                  $"{report.DownloadDeltaMs-previous.DownloadDeltaMs:+0.0;-0.0;0.0} ms, upload " +
                  $"{report.UploadDeltaMs-previous.UploadDeltaMs:+0.0;-0.0;0.0} ms."
                : "";
            _lastBufferbloatReport = report;
            status.Text =
                $"Bez zátěže: {Comparison(report.Idle)}" +
                $"\nDownload: {Comparison(report.Download)}" +
                $" • přenos {report.Download.TransferBytes/1048576.0:0.0} MiB" +
                $" • +{report.DownloadDeltaMs:0.0} ms" +
                $"\nUpload: {Comparison(report.Upload)}" +
                $" • přenos {report.Upload.TransferBytes/1048576.0:0.0} MiB" +
                $" • +{report.UploadDeltaMs:0.0} ms" +
                $"\n{BufferbloatDiagnosticService.Interpretation(report)}" + history +
                "\nSQM/CAKE/fq_codel se nastavuje na routeru. " +
                "Optimizer žádné změny neprovádí.";
        }
        catch (OperationCanceledException) { if (_section == "network") status.Text = "Test zrušen; neúplná data se nepoužívají."; }
        catch (Exception ex)
        {
            LogService.Error("network", "Bufferbloat diagnostic failed", ex);
            if (_section == "network") status.Text =
                "Bufferbloat test nemá úplná data: " + ex.Message;
        }
        finally { Interlocked.Exchange(ref _bufferbloatRunning, 0); }
    }
}
