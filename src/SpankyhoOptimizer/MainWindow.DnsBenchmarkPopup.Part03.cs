using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task ApplyBenchmarkDnsAsync(
        DnsBenchmarkResult selected,
        IReadOnlyList<DnsBenchmarkResult> pool,
        TextBlock status,
        TextBlock summary,
        Button? rollbackButton,
        Button? sourceButton,
        Border appliedState,
        TextBlock appliedStateTitle,
        TextBlock appliedStateDetails,
        TextBlock applyPreviewText,
        CancellationToken token)
    {
        object? originalButtonContent = sourceButton?.Content;
        if (Interlocked.Exchange(ref _dnsApplyBusy, 1) != 0)
        {
            status.Text = "DNS změna už probíhá…";
            return;
        }
        try
        {
            var plan = BuildDnsApplyPlan(selected, pool);
            if (plan.IPv4.Length == 0 && plan.IPv6.Length == 0)
                throw new InvalidOperationException("Vybraný resolver nemá použitelnou IP adresu pro systémové DNS.");

            applyPreviewText.Text = $"Bude nastaveno → Primární: {plan.Primary} • Sekundární: {plan.Secondary}" +
                                    (plan.IPv6.Length > 0 ? $" • IPv6: {string.Join(", ", plan.IPv6)}" : string.Empty);

            appliedState.Visibility = Visibility.Visible;
            appliedState.BorderBrush = AccentBrush();
            appliedStateTitle.Foreground = AccentBrush();
            appliedStateTitle.Text = $"Nastavuji DNS: {selected.Name}";
            appliedStateDetails.Text = $"Primární: {plan.Primary} • Sekundární: {plan.Secondary} • probíhá změna a ověření…";

            if (sourceButton != null)
            {
                sourceButton.IsEnabled = false;
                sourceButton.Content = "Nastavuji…";
            }

            applyPreviewText.Text =
                $"Nastavuji → Primární DNS: {plan.Primary} • Sekundární DNS: {plan.Secondary}" +
                (plan.IPv6.Length > 0 ? $" • IPv6: {string.Join(", ", plan.IPv6)}" : string.Empty);
            await Task.Yield();

            var progressResult = await RunCancellableWithProgressAsync(
                "Používám DNS",
                $"Nastavuji {selected.Name} • Primární {plan.Primary} • Sekundární {plan.Secondary}…",
                async progressToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                    return await DnsProfileService.ApplyServersAsync(plan.IPv4, plan.IPv6, linked.Token);
                });
            if (!progressResult.Completed)
            {
                LogService.Info("network", $"DNS apply cancelled: {selected.Name}");
                if (!token.IsCancellationRequested)
                {
                    status.Text = "DNS změna byla zrušena.";
                    appliedState.BorderBrush = WarningBrush();
                    appliedStateTitle.Foreground = WarningBrush();
                    appliedStateTitle.Text = "DNS změna byla zrušena";
                    appliedStateDetails.Text = "Operace byla přerušena. Zkontroluj aktuální DNS; předchozí nastavení lze obnovit ze zálohy, pokud je dostupná.";
                    if (sourceButton != null) sourceButton.Content = originalButtonContent;
                }
                return;
            }
            var applyResult = progressResult.Result;
            if (token.IsCancellationRequested) return;

            if (rollbackButton != null) rollbackButton.Visibility = Visibility.Visible;
            RefreshNetworkDnsDashboardStatus();

            status.Text = $"DNS změněno: {selected.Name} • ověřuji výkon…";
            appliedState.BorderBrush = SuccessBrush();
            appliedStateTitle.Foreground = SuccessBrush();
            appliedStateTitle.Text = "DNS změněno a ověřeno ✓";
            appliedStateDetails.Text =
                $"Poskytovatel: {selected.Name} • Primární: {applyResult.Primary} • Sekundární: {applyResult.Secondary}" +
                (applyResult.IPv6.Length > 0 ? $" • IPv6: {string.Join(", ", applyResult.IPv6)}" : string.Empty) +
                $" • Ověřeno {DateTime.Now:HH:mm:ss}" +
                (applyResult.VerifiedAdapters.Length > 0 ? $" • Adaptéry: {string.Join(" | ", applyResult.VerifiedAdapters)}" : string.Empty);

            var retest = await DnsBenchmarkService.RetestUdpAsync(selected.Name, selected.Address, token);
            status.Text = retest.IsAlive
                ? $"DNS nastaveno a ověřeno: {selected.Name}"
                : $"DNS nastaveno, ale kontrolní test neodpověděl: {selected.Name}";

            if (retest.IsAlive)
            {
                appliedStateDetails.Text +=
                    $" • Kontrolní test {retest.AverageMs:0.0} ms • jitter {retest.JitterMs:0.0} ms • DNSSEC {(retest.SupportsDnssec ? "ANO" : "NE")}";
                summary.Text =
                    $"Použito ✓  Primární: {applyResult.Primary} • Sekundární: {applyResult.Secondary} • " +
                    $"kontrolní test {retest.AverageMs:0.0} ms • {retest.HealthStatus}";
                if (sourceButton != null) sourceButton.Content = "Použito ✓";
            }
            else
            {
                appliedState.BorderBrush = WarningBrush();
                appliedStateTitle.Foreground = WarningBrush();
                appliedStateTitle.Text = "DNS změněno, kontrolní test bez odpovědi";
                appliedStateDetails.Text += " • Nastavení DNS bylo ověřeno, ale kontrolní DNS dotazy neodpověděly.";
                summary.Text =
                    $"DNS bylo změněno na Primární {applyResult.Primary} • Sekundární {applyResult.Secondary}, " +
                    "ale kontrolní test neodpověděl. Můžeš použít Vrátit předchozí DNS.";
                if (sourceButton != null) sourceButton.Content = "Použito • bez kontrolního testu";
            }

            ShowActionFeedback("DNS benchmark", retest.IsAlive ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                retest.IsAlive
                    ? $"Primární {applyResult.Primary} • Sekundární {applyResult.Secondary} • kontrolní test {retest.AverageMs:0.0} ms"
                    : $"Primární {applyResult.Primary} • Sekundární {applyResult.Secondary} • kontrolní test selhal");
        }
        catch (OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                status.Text = "DNS změna byla zrušena.";
                appliedStateTitle.Text = "DNS změna byla zrušena";
                appliedStateDetails.Text = "Operace byla přerušena. Zkontroluj aktuální DNS; předchozí nastavení lze obnovit ze zálohy, pokud je dostupná.";
                if (sourceButton != null) sourceButton.Content = originalButtonContent;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("network", "DNS benchmark apply failed", ex);
            if (!token.IsCancellationRequested)
            {
                status.Text = "Aplikace DNS selhala.";
                summary.Text = ex.Message;
                appliedState.Visibility = Visibility.Visible;
                appliedState.BorderBrush = WarningBrush();
                appliedStateTitle.Foreground = WarningBrush();
                appliedStateTitle.Text = "DNS změna selhala";
                appliedStateDetails.Text = ex.Message;
                if (sourceButton != null) sourceButton.Content = originalButtonContent;
                ShowActionFeedback("DNS benchmark", ActionFeedbackKind.Failed, ex.Message);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _dnsApplyBusy, 0);
            if (!token.IsCancellationRequested)
            {
                if (rollbackButton != null)
                    rollbackButton.Visibility = DnsProfileService.HasRollbackSnapshot ? Visibility.Visible : Visibility.Collapsed;
                RefreshNetworkDnsDashboardStatus();
                if (sourceButton != null) sourceButton.IsEnabled = true;
            }
        }
    }

    private async Task UpdateDnsCatalogAsync(TextBlock statusText, CancellationToken token)
    {
        try
        {
            var progressResult = await RunCancellableWithProgressAsync(
                "Aktualizuji DNS katalog",
                "Stahuji aktuální resolver list z DNS-Hop…",
                async progressToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                    return await DnsResolverCatalogService.UpdateAsync(linked.Token);
                });
            if (!progressResult.Completed)
            {
                LogService.Info("network", "DNS catalog update cancelled by user/navigation.");
                return;
            }
            var result = progressResult.Result;
            if (token.IsCancellationRequested) return;
            DnsBenchmarkService.InvalidateCache();

            statusText.Text = $"DNS-Hop katalog aktualizován: {result.Count} endpointů • UDP/TCP {result.UdpTcp} • DoH {result.Doh} • DoT {result.Dot} • DoQ {result.Doq}.";
            ShowActionFeedback("DNS katalog", ActionFeedbackKind.Verified, $"Aktualizováno {result.Count} endpointů.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "DNS catalog update failed", ex);
            if (!token.IsCancellationRequested)
            {
                statusText.Text = "Aktualizace DNS katalogu selhala: " + ex.Message;
                ShowActionFeedback("DNS katalog", ActionFeedbackKind.Failed, ex.Message);
            }
        }
    }

    private static string PhaseLabel(string phase) => phase switch
    {
        "preflight" => "rychlý předvýběr všech resolverů",
        "cached" => "cached latency",
        "uncached" => "uncached latency",
        "dotcom" => ".com NS",
        "dnssec-broken" => "DNSSEC broken-signature",
        "dnssec-control" => "DNSSEC control",
        "hijack" => "NXDOMAIN / hijack",
        "done" => "resolver hotov",
        _ => "připravuji"
    };
}
