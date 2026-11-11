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
    private int _dnsApplyBusy;
    private sealed record DnsApplyPlan(string[] IPv4, string[] IPv6)
    {
        internal string Primary => IPv4.Concat(IPv6).FirstOrDefault() ?? "—";
        internal string Secondary => IPv4.Concat(IPv6).Skip(1).FirstOrDefault() ?? "—";
    }

    private static DnsApplyPlan BuildDnsApplyPlan(
        DnsBenchmarkResult selected,
        IReadOnlyList<DnsBenchmarkResult> pool)
    {
        static bool IsV4(string address) => IPAddress.TryParse(address, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        static bool IsV6(string address) => IPAddress.TryParse(address, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

        string[] ChooseServers(Func<string, bool> familyMatch)
        {
            var family = new List<DnsBenchmarkResult>(pool.Count);
            foreach (DnsBenchmarkResult item in pool)
            {
                if (item.IsAlive && string.IsNullOrWhiteSpace(item.Error) &&
                    !item.RedirectsNxDomain && item.Protocol == DnsResolverProtocol.UdpTcp &&
                    familyMatch(item.Address))
                    family.Add(item);
            }

            family.Sort(static (a, b) =>
            {
                int score = b.OverallScore.CompareTo(a.OverallScore);
                return score != 0 ? score : a.AverageMs.CompareTo(b.AverageMs);
            });

            DnsBenchmarkResult? primary = null;
            foreach (DnsBenchmarkResult item in family)
            {
                if (!string.Equals(item.Name, selected.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                primary ??= item;
                if (string.Equals(item.Address, selected.Address, StringComparison.OrdinalIgnoreCase))
                {
                    primary = item;
                    break;
                }
            }
            primary ??= family.Count > 0 ? family[0] : null;
            if (primary == null) return Array.Empty<string>();

            DnsBenchmarkResult? companion = null;
            foreach (DnsBenchmarkResult item in family)
            {
                if (string.Equals(item.Address, primary.Address, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(item.Name, primary.Name, StringComparison.OrdinalIgnoreCase))
                {
                    companion = item;
                    break;
                }
            }

            if (companion == null)
            {
                foreach (DnsBenchmarkResult item in family)
                {
                    if (!string.Equals(item.Address, primary.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        companion = item;
                        break;
                    }
                }
            }

            return companion == null
                ? new[] { primary.Address }
                : new[] { primary.Address, companion.Address };
        }

        return new DnsApplyPlan(ChooseServers(IsV4), ChooseServers(IsV6));
    }

    internal static string RunDnsApplyPlanSmoke()
    {
        static DnsBenchmarkResult R(string provider, string address, double ms, double jitter) =>
            new(provider, address, DnsResolverProtocol.UdpTcp,
                ms, ms + 1, ms + 2, ms, ms, ms + 2, jitter,
                9, 9, true, false, null);

        var selected = R("Provider A", "1.1.1.1", 10, 1);
        DnsBenchmarkResult[] pool =
        [
            selected,
            R("Provider A", "1.0.0.1", 11, 1),
            R("Provider B", "9.9.9.9", 12, 1)
        ];

        var plan = BuildDnsApplyPlan(selected, pool);
        if (plan.IPv4.Length < 2 ||
            plan.IPv4[0] != "1.1.1.1" ||
            plan.IPv4[1] != "9.9.9.9")
            throw new InvalidOperationException(
                $"DNS apply plan provider diversity selhala: {string.Join(", ", plan.IPv4)}");

        return "DNS_APPLY_PLAN_SMOKE_OK primary=ProviderA secondary=ProviderB";
    }

    private async Task RunDnsBenchmarkPopupAsync(CancellationToken navigationToken)
    {
        var catalogStatus = await DnsResolverCatalogService.EnsureFreshAsync(TimeSpan.FromDays(7), navigationToken);
        var popupCts = CancellationTokenSource.CreateLinkedTokenSource(navigationToken);
        CancellationToken popupToken = popupCts.Token;
        var overlay = CreateDialogOverlay(12600);
        bool popupClosed = false;
        bool benchmarkFinished = false;
        CancellationTokenRegistration navigationCloseRegistration = default;
        void ClosePopup()
        {
            if (popupClosed) return;
            popupClosed = true;
            try { popupCts.Cancel(); } catch (ObjectDisposedException) { }
            UnmountDialog(overlay);
            navigationCloseRegistration.Dispose();
            if (benchmarkFinished) popupCts.Dispose();
        }

        navigationCloseRegistration = navigationToken.Register(() =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, ClosePopup);
        });

        overlay.Unloaded += (_, _) =>
        {
            // Window shutdown/navigation can unload the overlay without going through ClosePopup.
            // Mark it closed so the benchmark finally block owns disposal and no CTS survives
            // after the dialog visual tree is gone.
            popupClosed = true;
            try { popupCts.Cancel(); } catch (ObjectDisposedException) { }
            navigationCloseRegistration.Dispose();
            if (benchmarkFinished)
                popupCts.Dispose();
        };
        var card = CreateDialogCard(760, 820, z: 36);
        var root = new StackPanel { Spacing = 14 };

        var head = new Grid { ColumnSpacing = 14 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(14),
            Background = AccentSoftBrush(),
            Child = SidebarIcon("network", 22)
        });

        var titles = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "DNS benchmark",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        titles.Children.Add(new TextBlock
        {
            Text = $"{catalogStatus.Count} resolverů • DNS-Hop • dvoufázový test • automatický TOP výběr",
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);

        var headerSectionStack = new StackPanel { Spacing = 12 };
        headerSectionStack.Children.Add(head);
        headerSectionStack.Children.Add(BuildPageSummaryStrip(
            ("Katalog", catalogStatus.Count.ToString(), "SpankyInfoBrush"),
            ("UDP/TCP", catalogStatus.UdpTcp.ToString(), "SpankyInfoBrush"),
            ("DoH/DoT", $"{catalogStatus.Doh}/{catalogStatus.Dot}", "SpankyInfoBrush"),
            ("Rollback", DnsProfileService.HasRollbackSnapshot ? "Ano" : "Ne", DnsProfileService.HasRollbackSnapshot ? "SpankySuccessBrush" : "SpankyInfoBrush")));
        var headerSection = new Border
        {
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            
            Child = headerSectionStack
        };
        headerSection.Translation = new System.Numerics.Vector3(0, 0, 8);
        root.Children.Add(headerSection);
        var status = new TextBlock
        {
            Text = "Připravuji test DNS…",
            FontSize = 13.5,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        var detail = new TextBlock
        {
            Text = "0 / 0",
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        };
        var progressSectionStack = new StackPanel { Spacing = 10 };
        progressSectionStack.Children.Add(status);
        progressSectionStack.Children.Add(detail);

        var track = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = GlassTrackBrush(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var fill = new Border
        {
            Width = 0,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = ProgressBrush(),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        track.Child = fill;
        progressSectionStack.Children.Add(track);

        var summary = new Border
        {
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12)
        };
        var summaryText = new TextBlock
        {
            Text = "Čekám na první výsledky…",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        summary.Child = summaryText;
        progressSectionStack.Children.Add(summary);
        var progressSection = new Border
        {
            Background = DialogInnerStrongBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            
            Child = progressSectionStack
        };
        progressSection.Translation = new System.Numerics.Vector3(0, 0, 6);
        root.Children.Add(progressSection);

        var resultsHeader = new Grid { ColumnSpacing = 10 };
        resultsHeader.ColumnDefinitions.Add(new ColumnDefinition());
        resultsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resultsTitle = new TextBlock
        {
            Text = "Průběžné TOP výsledky",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        resultsHeader.Children.Add(resultsTitle);

        var filter = new ComboBox { MinWidth = 150, Visibility = Visibility.Collapsed };
        foreach (string value in new[] { "Vše", "UDP/TCP", "DoH", "DoT", "DoQ", "DNSSEC", "Bez hijacku", "IPv4", "IPv6" })
            filter.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        filter.SelectedIndex = 0;
        Grid.SetColumn(filter, 1);
        resultsHeader.Children.Add(filter);

        var resultsHost = new StackPanel { Spacing = 5 };
        var resultsScroll = new ScrollViewer
        {
            Content = resultsHost,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var resultsSectionStack = new StackPanel { Spacing = 10 };
        resultsSectionStack.Children.Add(resultsHeader);
        resultsSectionStack.Children.Add(resultsScroll);
        var resultsSection = new Border
        {
            Background = DialogInnerStrongBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            
            Child = resultsSectionStack
        };
        resultsSection.Translation = new System.Numerics.Vector3(0, 0, 6);
        root.Children.Add(resultsSection);

        DnsBenchmarkResult[] finalResults = Array.Empty<DnsBenchmarkResult>();
        void RenderFinalResults()
        {
            string selected = (filter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Vše";
            IEnumerable<DnsBenchmarkResult> query = finalResults;
            query = selected switch
            {
                "UDP/TCP" => query.Where(x => x.Protocol == DnsResolverProtocol.UdpTcp),
                "DoH" => query.Where(x => x.Protocol == DnsResolverProtocol.Doh),
                "DoT" => query.Where(x => x.Protocol == DnsResolverProtocol.Dot),
                "DoQ" => query.Where(x => x.Protocol == DnsResolverProtocol.Doq),
                "DNSSEC" => query.Where(x => x.SupportsDnssec),
                "Bez hijacku" => query.Where(x => !x.RedirectsNxDomain),
                "IPv4" => query.Where(x => IPAddress.TryParse(x.Address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork),
                "IPv6" => query.Where(x => IPAddress.TryParse(x.Address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6),
                _ => query
            };

            resultsHost.Children.Clear();
            foreach (var item in query.Take(60))
            {
                resultsHost.Children.Add(InsightRow(
                    $"{item.Name} • {item.Address}",
                    $"{item.Protocol} • {item.AverageMs:0.0} ms avg • jitter {item.JitterMs:0.0} ms • score {item.OverallScore:0.0} • {item.HealthStatus}"));
            }
        }
        filter.SelectionChanged += (_, _) => RenderFinalResults();

        var applyPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var applyLabel = new TextBlock
        {
            Text = "Použít DNS",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        };
        var resolverPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Vyber DNS pro primární + sekundární adresu",
            MinWidth = 420
        };
        var applyPreview = new Border
        {
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Visibility = Visibility.Collapsed
        };
        var applyPreviewText = new TextBlock
        {
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        applyPreview.Child = applyPreviewText;

        var appliedState = new Border
        {
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Visibility = Visibility.Collapsed
        };
        var appliedStateStack = new StackPanel { Spacing = 4 };
        var appliedStateTitle = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        };
        var appliedStateDetails = new TextBlock
        {
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        appliedStateStack.Children.Add(appliedStateTitle);
        appliedStateStack.Children.Add(appliedStateDetails);
        appliedState.Child = appliedStateStack;

        applyPanel.Children.Add(applyLabel);
        applyPanel.Children.Add(resolverPicker);
        applyPanel.Children.Add(applyPreview);
        applyPanel.Children.Add(appliedState);
        root.Children.Add(applyPanel);

        DnsBenchmarkResult[] applicableResults = Array.Empty<DnsBenchmarkResult>();
        resolverPicker.SelectionChanged += (_, _) =>
        {
            if (resolverPicker.SelectedItem is not ComboBoxItem item || item.Tag is not DnsBenchmarkResult selected)
            {
                applyPreview.Visibility = Visibility.Collapsed;
                return;
            }

            var plan = BuildDnsApplyPlan(selected, applicableResults);
            applyPreview.Visibility = Visibility.Visible;
            applyPreviewText.Text =
                $"Po Apply → Provider: {selected.Name} • Primary DNS: {plan.Primary} • Secondary DNS: {plan.Secondary}" +
                (plan.IPv6.Length > 0 ? $" • IPv6: {string.Join(", ", plan.IPv6)}" : string.Empty);
        };

        var actions = DialogActions();
        var cancelButton = StyledDialogButton("Zrušit", false, () =>
        {
            ClosePopup();
            return Task.CompletedTask;
        });
        Button? rollbackButton = null;
        Button? applyBestButton = null;
        Button? applyFastestButton = null;
        Button? applySelectedButton = null;

        applyBestButton = StyledDialogButton("Použít Best overall", true, async () =>
        {
            if (popupClosed || popupCts.IsCancellationRequested) return;
            var best = applicableResults.OrderByDescending(x => x.OverallScore).FirstOrDefault();
            if (best != null)
                await ApplyBenchmarkDnsAsync(best, applicableResults, status, summaryText, rollbackButton,
                    applyBestButton, appliedState, appliedStateTitle, appliedStateDetails, applyPreviewText, popupCts.Token);
        });
        applyBestButton.Visibility = Visibility.Visible;
        applyBestButton.IsEnabled = false;

        applyFastestButton = StyledDialogButton("Použít nejrychlejší", false, async () =>
        {
            if (popupClosed || popupCts.IsCancellationRequested) return;
            var fastest = applicableResults.OrderBy(x => x.AverageMs).FirstOrDefault();
            if (fastest != null)
                await ApplyBenchmarkDnsAsync(fastest, applicableResults, status, summaryText, rollbackButton,
                    applyFastestButton, appliedState, appliedStateTitle, appliedStateDetails, applyPreviewText, popupCts.Token);
        });
        applyFastestButton.Visibility = Visibility.Visible;
        applyFastestButton.IsEnabled = false;

        applySelectedButton = StyledDialogButton("Použít vybraný pár", false, async () =>
        {
            if (popupClosed || popupCts.IsCancellationRequested) return;
            if (resolverPicker.SelectedItem is ComboBoxItem item && item.Tag is DnsBenchmarkResult selected)
                await ApplyBenchmarkDnsAsync(selected, applicableResults, status, summaryText, rollbackButton,
                    applySelectedButton, appliedState, appliedStateTitle, appliedStateDetails, applyPreviewText, popupCts.Token);
        });
        applySelectedButton.Visibility = Visibility.Visible;
        applySelectedButton.IsEnabled = false;

        rollbackButton = StyledDialogButton("Vrátit předchozí DNS", false, async () =>
        {
            if (popupClosed || popupCts.IsCancellationRequested ||
                Interlocked.Exchange(ref _dnsApplyBusy, 1) != 0) return;
            try
            {
                var progressResult = await RunCancellableWithProgressAsync(
                    "Obnovuji DNS",
                    "Vracím předchozí DNS konfiguraci…",
                    async progressToken =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(popupCts.Token, progressToken);
                        return await DnsProfileService.RollbackAsync(linked.Token);
                    });
                if (!progressResult.Completed)
                {
                    LogService.Info("network", "DNS rollback cancelled by user.");
                    return;
                }
                string result = progressResult.Result;
                if (popupClosed || popupToken.IsCancellationRequested) return;
                RefreshNetworkDnsDashboardStatus();
                status.Text = "Předchozí DNS obnoveno.";
                summaryText.Text = result;
                appliedState.Visibility = Visibility.Visible;
                appliedState.BorderBrush = AccentBrush();
                appliedStateTitle.Foreground = AccentBrush();
                appliedStateTitle.Text = "Předchozí DNS konfigurace obnovena";
                appliedStateDetails.Text = result;
                rollbackButton.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) when (popupToken.IsCancellationRequested)
            {
                LogService.Info("network", "DNS rollback cancelled by popup/navigation.");
            }
            catch (Exception ex)
            {
                LogService.Error("network", "DNS rollback failed", ex);
                if (!popupClosed)
                {
                    status.Text = "Rollback DNS selhal.";
                    summaryText.Text = ex.Message;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _dnsApplyBusy, 0);
            }
        });
        rollbackButton.Visibility = DnsProfileService.HasRollbackSnapshot ? Visibility.Visible : Visibility.Collapsed;

        var applyActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Visible
        };
        applyActions.Children.Add(applyBestButton);
        applyActions.Children.Add(applyFastestButton);
        applyActions.Children.Add(applySelectedButton);
        applyActions.Children.Add(rollbackButton);

        actions.Children.Add(cancelButton);

        var footer = new Border
        {
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 10)
        };
        var footerStack = new StackPanel { Spacing = 8 };
        footerStack.Children.Add(applyActions);
        footerStack.Children.Add(actions);
        footer.Child = footerStack;

        var bodyScroll = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled
        };

        var layout = new Grid { RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(bodyScroll);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);

        card.Child = layout;
        var topClose = MountDialog(overlay, card, ClosePopup);

        bool dnsPopupContractOk =
            topClose != null &&
            actions.Children.Contains(cancelButton) &&
            applyActions.Children.Contains(applyBestButton) &&
            applyActions.Children.Contains(applyFastestButton) &&
            applyActions.Children.Contains(applySelectedButton) &&
            applyActions.Children.Contains(rollbackButton) &&
            footerStack.Children.Contains(applyActions) &&
            footerStack.Children.Contains(actions);

        if (!dnsPopupContractOk)
        {
            LogService.Error("network", "DNS_POPUP_CONTRACT_FAIL", new InvalidOperationException(
                "DNS benchmark popup ztratil povinné Close/Apply/Rollback ovládací prvky."));
            ClosePopup();
            throw new InvalidOperationException(
                "DNS benchmark popup nemá kompletní ovládání. Otevření bylo z bezpečnostních důvodů zrušeno.");
        }

        LogService.Info("network",
            "DNS_POPUP_CONTRACT_OK sharedClose=true escapeClose=true footerClose=true applyBest=true applyFastest=true applySelected=true rollback=true");

        var completed = new List<DnsBenchmarkResult>();
        var benchmarkClock = Stopwatch.StartNew();
        var progress = new Progress<DnsBenchmarkProgress>(p =>
        {
            if (popupClosed) return;
            if (p.Result != null) completed.Add(p.Result);
            double width = track.ActualWidth > 0 ? track.ActualWidth : 610;
            int progressDone = p.TotalQueries > 0 ? p.CompletedQueries : p.Completed;
            int progressTotal = p.TotalQueries > 0 ? p.TotalQueries : p.Total;
            fill.Width = width * progressDone / Math.Max(1d, progressTotal);
            int percent = (int)Math.Round(progressDone * 100d / Math.Max(1, progressTotal));
            status.Text = $"Testuji DNS resolvery… {percent} % • {PhaseLabel(p.Phase)}";

            string eta = "počítám ETA…";
            if (progressDone > 0 && progressDone < progressTotal)
            {
                double remainingSeconds = benchmarkClock.Elapsed.TotalSeconds * (progressTotal - progressDone) / progressDone;
                eta = "ETA ~" + TimeSpan.FromSeconds(remainingSeconds).ToString(@"mm\:ss");
            }
            detail.Text = $"{p.Completed}/{p.Total} resolverů • {p.CompletedQueries}/{p.TotalQueries} dotazů • {benchmarkClock.Elapsed.ToString(@"mm\:ss")} • {eta} • {p.CurrentName}";

            var alive = completed
                .Where(x => x.IsAlive && !x.RedirectsNxDomain)
                .OrderByDescending(x => x.OverallScore)
                .Take(10)
                .ToArray();

            summaryText.Text = alive.Length == 0
                ? $"Hotovo {p.Completed}/{p.Total} • zatím bez validní odpovědi"
                : $"Hotovo {p.Completed}/{p.Total} • odpovědělo {completed.Count(x => x.IsAlive)} • Best overall {alive[0].Name} • score {alive[0].OverallScore:0.0}";

            resultsHost.Children.Clear();
            foreach (var item in alive)
            {
                resultsHost.Children.Add(InsightRow(
                    $"{item.Name}  [{item.Protocol}]  {item.Address}",
                    $"Score {item.OverallScore:0.0} • avg {item.AverageMs:0.0} ms • jitter {item.JitterMs:0.0} • {item.HealthStatus}"));
            }
        });
        try
        {
            var measured = await DnsBenchmarkService.RunAsync(progress, popupToken);
            if (popupClosed || popupToken.IsCancellationRequested) return;
            var alive = measured.Where(x => x.IsAlive).ToArray();
            fill.Width = track.ActualWidth > 0 ? track.ActualWidth : 610;
            status.Text = "Test DNS dokončen.";
            detail.Text = $"{measured.Count} resolverů • odpovědělo {alive.Length}";

            benchmarkClock.Stop();
            finalResults = measured
                .OrderBy(x => x.IsAlive ? 0 : 1)
                .ThenBy(x => x.RedirectsNxDomain ? 1 : 0)
                .ThenByDescending(x => x.OverallScore)
                .ToArray();
            _lastDnsDashboardResults = finalResults;
            if (_section == "network" && _networkDnsChartHost != null)
                RenderDnsDashboardChart(_networkDnsChartHost, _lastDnsDashboardResults);
            filter.Visibility = Visibility.Visible;
            resultsTitle.Text = "Výsledky";
            RenderFinalResults();

            applicableResults = measured
                .Where(x => x.IsAlive &&
                            string.IsNullOrWhiteSpace(x.Error) &&
                            !x.RedirectsNxDomain &&
                            x.Protocol == DnsResolverProtocol.UdpTcp &&
                            IPAddress.TryParse(x.Address, out _))
                .OrderBy(x => x.AverageMs)
                .ToArray();

            resolverPicker.Items.Clear();
            foreach (var item in applicableResults
                .OrderByDescending(x => x.OverallScore)
                .Take(50))
            {
                resolverPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{item.Name} • {item.Address} • score {item.OverallScore:0.0} • {item.AverageMs:0.0} ms",
                    Tag = item
                });
            }
            if (resolverPicker.Items.Count > 0) resolverPicker.SelectedIndex = 0;

            if (applicableResults.Length > 0)
            {
                applyPanel.Visibility = Visibility.Visible;
                applyActions.Visibility = Visibility.Visible;
                applyBestButton.IsEnabled = true;
                applyFastestButton.IsEnabled = true;
                applySelectedButton.IsEnabled = resolverPicker.SelectedItem != null;
            }

            var valid = measured.Where(x => x.IsAlive && string.IsNullOrWhiteSpace(x.Error) && !x.RedirectsNxDomain).ToArray();
            var bestOverall = valid.OrderByDescending(x => x.OverallScore).FirstOrDefault();
            var fastest = valid.OrderBy(x => x.AverageMs).FirstOrDefault();
            var stable = valid.OrderBy(x => x.JitterMs).FirstOrDefault();

            summaryText.Text = valid.Length == 0
                ? "Žádný validní resolver neodpověděl."
                : $"Nejlepší celkově: {bestOverall?.Name} • skóre {bestOverall?.OverallScore:0.0} | Nejrychlejší: {fastest?.Name} • {fastest?.AverageMs:0.0} ms | Nejstabilnější: {stable?.Name} • jitter {stable?.JitterMs:0.0} ms";
            detail.Text = $"{measured.Count} resolverů • odpovědělo {alive.Length} • čas {benchmarkClock.Elapsed.ToString(@"mm\:ss")}";
        }
        catch (OperationCanceledException)
        {
            if (!popupClosed)
            {
                status.Text = "Test DNS zrušen.";
                summaryText.Text = completed.Count == 0
                    ? "Nebyl dokončen žádný resolver."
                    : $"Zpracováno {completed.Count} resolverů před zrušením.";
            }
        }
        catch (Exception ex)
        {
            LogService.Error("network", "DNS benchmark popup failed", ex);
            if (!popupClosed)
            {
                status.Text = "Test DNS selhal.";
                summaryText.Text = ex.Message;
            }
        }
        finally
        {
            benchmarkFinished = true;
            if (!popupClosed)
            {
                cancelButton.Content = "Zavřít";
                cancelButton.Visibility = Visibility.Visible;
                if (topClose != null) topClose.Visibility = Visibility.Visible;
            }
            else
            {
                popupCts.Dispose();
            }
        }
    }

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
