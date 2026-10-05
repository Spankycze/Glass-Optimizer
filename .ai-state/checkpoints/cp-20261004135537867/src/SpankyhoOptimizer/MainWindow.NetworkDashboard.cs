using System;
using System.Collections.Generic;
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
    private StackPanel? _networkDnsPanelHost;
    private bool _networkDnsPanelQueued;
    private StackPanel? _networkDnsChartHost;
    private TextBlock? _networkDnsStatus;
    private TextBlock? _networkHeroState;
    private Grid? _networkHeroArtHost;
    private bool _networkHeroArtQueued;
    private TextBlock? _networkMtuStatus;
    private IReadOnlyList<DnsBenchmarkResult>? _lastDnsDashboardResults;

    private Border BuildNetworkDashboardHero(CancellationToken token)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        var root = new Grid { ColumnSpacing = 18, MinHeight = UseCompactSectionChrome ? 0 : 176 };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = UseCompactSectionChrome ? new GridLength(0) : new GridLength(320) });

        var left = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = "Internet / Síť",
            FontSize = UseCompactSectionChrome ? 32 : 22,
            FontWeight = UseCompactSectionChrome ? FontWeights.Light : FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        left.Children.Add(new TextBlock
        {
            Text = "Připojení, DNS, MTU a síťová karta v jednom přehledném dashboardu.",
            FontSize = 12.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        _networkHeroState = new TextBlock
        {
            Text = "Zjišťuji aktivní připojení…",
            FontSize = 11.5,
            Foreground = TextSecondaryBrush()
        };
        left.Children.Add(_networkHeroState);
        long shellMs = perf.ElapsedMilliseconds;

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        badges.Children.Add(Badge("LIVE LINK", "SpankySuccessBrush"));
        badges.Children.Add(Badge("DNS", "SpankyInfoBrush"));
        badges.Children.Add(Badge("MTU", "SpankyInfoBrush"));
        badges.Children.Add(Badge("OVĚŘENO", "SpankySuccessBrush"));
        if (!UseCompactSectionChrome) left.Children.Add(badges);
        long badgesMs = perf.ElapsedMilliseconds;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 5, 0, 0)
        };
        actions.Children.Add(PrimaryQuickButton("Automatické ladění AI", () => ShowSectionAsync("ai-runtime")));
        actions.Children.Add(QuickButton("Ruční nastavení sítě", ApplyNicPerformanceAsync));
        left.Children.Add(actions);
        long actionsMs = perf.ElapsedMilliseconds;

        root.Children.Add(left);
        var artHost = new Grid
        {
            Width = UseCompactSectionChrome ? 0 : 300,
            Height = UseCompactSectionChrome ? 0 : 142,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _networkHeroArtHost = artHost;
        Grid.SetColumn(artHost, 1);
        root.Children.Add(artHost);
        long artHostMs = perf.ElapsedMilliseconds;
        if (!UseCompactSectionChrome) ScheduleNetworkHeroIllustration(token);
        long artScheduleMs = perf.ElapsedMilliseconds;

        var hydrateTask = Task.Run(() => HydrateNetworkHeroAsync(token), token);
        long hydrateTaskRunMs = perf.ElapsedMilliseconds;
        SafeTaskService.ForgetDeferred(
            hydrateTask,
            "network", "Network dashboard hero status");
        long deferredKickoffMs = perf.ElapsedMilliseconds;
        var card = UseCompactSectionChrome ? new Border { Tag = "workspace-intro", Padding = new Thickness(0,12,0,22), Child = root } : BuildDashboardCard(root, new Thickness(20, 17, 20, 17));
        long cardMs = perf.ElapsedMilliseconds;
        LogService.Info("performance",
            $"NETWORK_HERO_BUILD shellMs={shellMs} badgesMs={badgesMs - shellMs} actionsMs={actionsMs - badgesMs} artHostMs={artHostMs - actionsMs} artScheduleMs={artScheduleMs - artHostMs} taskRunMs={hydrateTaskRunMs - artScheduleMs} observeMs={deferredKickoffMs - hydrateTaskRunMs} kickoffMs={deferredKickoffMs - artHostMs} cardMs={cardMs - deferredKickoffMs} totalMs={cardMs}");
        return card;
    }

    private void ScheduleNetworkHeroIllustration(CancellationToken token)
    {
        if (_networkHeroArtQueued || _networkHeroArtHost == null || _networkHeroArtHost.Children.Count > 0) return;
        _networkHeroArtQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _networkHeroArtQueued = false;
            if (token.IsCancellationRequested || _section != "network" || _networkHeroArtHost == null || _networkHeroArtHost.Children.Count > 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _networkHeroArtHost.Children.Add(BuildNetworkHeroIllustration());
            LogService.Info("performance", $"NETWORK_HERO_ART_DEFERRED ms={sw.ElapsedMilliseconds}");
        });
    }

    private FrameworkElement BuildNetworkHeroIllustration()
    {
        var grid = new Grid { Width = 300, Height = 142, HorizontalAlignment = HorizontalAlignment.Right };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        Border Node(string label, string detail)
        {
            var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(OverviewGlowIcon("network", 20, NetworkAccentBrush()));
            stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = TextPrimaryBrush(), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 9.5, Foreground = TextSecondaryBrush(), HorizontalAlignment = HorizontalAlignment.Center });
            return new Border
            {
                Background = SectionCardBackgroundBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(_darkTheme ? 1.2 : 1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10),
                Child = stack
            };
        }

        FrameworkElement Link()
        {
            var line = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 20 };
            line.Children.Add(new Border { Height = 2, Background = AccentSoftBrush(), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = AccentBrush(), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return line;
        }

        var pc = Node("PC", "Ethernet");
        Grid.SetColumn(pc, 0); grid.Children.Add(pc);
        var link1 = Link(); Grid.SetColumn(link1, 1); grid.Children.Add(link1);
        var router = Node("ROUTER", "Gateway"); Grid.SetColumn(router, 2); grid.Children.Add(router);
        var link2 = Link(); Grid.SetColumn(link2, 3); grid.Children.Add(link2);
        var internet = Node("INTERNET", "DNS / MTU"); Grid.SetColumn(internet, 4); grid.Children.Add(internet);

        return grid;
    }

    private async Task HydrateNetworkHeroAsync(CancellationToken token)
    {
        try
        {
            var nicTask = SystemInsightService.GetActiveNetworkAsync(token);
            var latencyTask = NetworkDiagnosticsService.MeasureAsync(samples: 3, token: token);
            await Task.WhenAll(nicTask, latencyTask).ConfigureAwait(false);
            var nic = await nicTask.ConfigureAwait(false);
            var latency = await latencyTask.ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "network" || _networkHeroState == null) return;
                _networkHeroState.Text = nic == null
                    ? "Aktivní adaptér nenalezen."
                    : $"{nic.Description} • {nic.SpeedMbps:0} Mb/s • MTU {nic.Mtu}" +
                      (latency.Received > 0 ? $" • {latency.AvgMs:0.0} ms" : string.Empty);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Error("network", "Network hero hydration failed", ex);
        }
    }

    private FrameworkElement BuildNetworkMetricTile(string label, string value, string detail, string brushKey = "SpankyInfoBrush")
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = TextSecondaryBrush(), CharacterSpacing = 70 });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = UiBrush(brushKey), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.2 : 1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 9, 12, 9),
            Child = stack
        };
    }

    private void RefreshNetworkDnsDashboardStatus()
    {
        if (_networkDnsStatus == null) return;
        _networkDnsStatus.Text =
            DnsProfileService.GetActiveDnsSummary() +
            $" • {DnsBenchmarkCatalog.Servers.Length} resolverů v katalogu" +
            (DnsProfileService.HasRollbackSnapshot ? " • rollback připraven" : string.Empty);
    }

    private FrameworkElement BuildMtuPacketGraphic()
    {
        var root = new Grid { ColumnSpacing = 9, Margin = new Thickness(0, 2, 0, 2) };
        for (int i = 0; i < 3; i++) root.ColumnDefinitions.Add(new ColumnDefinition());

        Border Packet(string title, string detail, double width, bool accent, bool fragmented, int column)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = accent ? AccentBrush() : TextPrimaryBrush() });
            var packetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 16 };
            if (fragmented)
            {
                packetRow.Children.Add(new Border { Width = width * .46, Height = 10, CornerRadius = new CornerRadius(4), Background = UiBrush("SpankyWarningBrush") });
                packetRow.Children.Add(new Border { Width = width * .46, Height = 10, CornerRadius = new CornerRadius(4), Background = UiBrush("SpankyWarningBrush") });
            }
            else
            {
                packetRow.Children.Add(new Border { Width = width, Height = 10, CornerRadius = new CornerRadius(4), Background = accent ? AccentBrush() : AccentSoftBrush() });
            }
            stack.Children.Add(packetRow);
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 9.8, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
            var card = new Border
            {
                Background = SectionCardBackgroundBrush(),
                BorderBrush = accent ? AccentBrush() : SectionBorderBrush(),
                BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Child = stack
            };
            Grid.SetColumn(card, column);
            return card;
        }

        root.Children.Add(Packet("Menší paket", "Kompatibilní, ale s vyšším overheadem.", 54, false, false, 0));
        root.Children.Add(Packet("Optimální", "Největší stabilní paket bez fragmentace.", 92, true, false, 1));
        root.Children.Add(Packet("Příliš velký", "Fragmentace nebo ztráta na trase.", 90, false, true, 2));
        return root;
    }

    private void RenderDnsDashboardChart(StackPanel host, IReadOnlyList<DnsBenchmarkResult>? results)
    {
        host.Children.Clear();
        host.Children.Add(new TextBlock
        {
            Text = "TOP 5 RESOLVERŮ",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = TextSecondaryBrush()
        });

        var top = results?
            .Where(x => x.IsAlive && string.IsNullOrWhiteSpace(x.Error) && !x.RedirectsNxDomain)
            .OrderByDescending(x => x.OverallScore)
            .ThenBy(x => x.AverageMs)
            .Take(5)
            .ToArray() ?? Array.Empty<DnsBenchmarkResult>();

        if (top.Length == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "Po benchmarku se tady zobrazí graf nejlepších resolverů.",
                FontSize = 11,
                Foreground = TextSecondaryBrush(),
                Margin = new Thickness(0, 4, 0, 6)
            });
            foreach (double width in new[] { 190d, 154d, 128d, 104d, 82d })
                host.Children.Add(new Border { Width = width, Height = 7, CornerRadius = new CornerRadius(4), Background = AccentSoftBrush(), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3), Opacity = .45 });
            return;
        }

        foreach (var item in top)
        {
            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock { Text = item.Name, FontSize = 10.5, Foreground = TextPrimaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis });
            var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = SurfaceElevatedBrush(), VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border
            {
                Width = Math.Max(18, Math.Min(170, item.OverallScore * 1.7)),
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = AccentBrush(),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            track.Child = fill;
            Grid.SetColumn(track, 1); row.Children.Add(track);
            var value = new TextBlock { Text = $"{item.AverageMs:0.0} ms", FontSize = 10.5, Foreground = TextSecondaryBrush(), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 2); row.Children.Add(value);
            host.Children.Add(row);
        }
    }
}
