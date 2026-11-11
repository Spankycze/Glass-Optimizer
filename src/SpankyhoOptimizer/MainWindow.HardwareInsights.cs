using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private UIElement? _memoryRecommendedPanel;
    private DateTime _memoryRecommendedAt;

    private void InvalidateMemoryRecommendedPanel()
    {
        _memoryRecommendedPanel = null;
        _memoryRecommendedAt = default;
    }

    private Task BuildNetworkTelemetryAsync(CancellationToken token)
    {
        _networkTelemetryShellHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(_networkTelemetryShellHost);
        ScheduleNetworkTelemetryShell(token);
        return Task.CompletedTask;
    }

    private void ScheduleNetworkTelemetryShell(CancellationToken token)
    {
        StackPanel? host = _networkTelemetryShellHost;
        int generation = _navigationGeneration;
        if (host == null || host.Children.Count > 0 ||
            (_networkTelemetryShellQueued && _networkTelemetryShellQueuedGeneration == generation)) return;
        _networkTelemetryShellQueued = true;
        _networkTelemetryShellQueuedGeneration = generation;
        bool accepted = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Never release a newer navigation's queue ticket or populate a
            // replacement host from this stale callback.
            if (generation != _navigationGeneration ||
                _networkTelemetryShellQueuedGeneration != generation ||
                !ReferenceEquals(_networkTelemetryShellHost, host)) return;
            _networkTelemetryShellQueued = false;
            _networkTelemetryShellQueuedGeneration = -1;
            if (token.IsCancellationRequested || _section != "network" ||
                host.Children.Count > 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var body = new StackPanel { Spacing = 8 };
            _networkTelemetryBody = body;
            body.Children.Add(new TextBlock { Text = "Načítám stav adaptéru…", Foreground = TextSecondaryBrush() });
            host.Children.Add(BuildSectionSurface("network", "Připojení", "Aktivní adaptér • rychlost • ping • MTU • RSS", body));
            LogService.Info("performance", $"NETWORK_TELEMETRY_SHELL_DEFERRED ms={sw.ElapsedMilliseconds}");
            var kickoff = System.Diagnostics.Stopwatch.StartNew();
            var telemetryTask = LoadNetworkTelemetryAsync(body, token);
            if (kickoff.ElapsedMilliseconds >= 8)
                LogService.Info("performance", $"NETWORK_TELEMETRY_KICKOFF ms={kickoff.ElapsedMilliseconds}");
            SafeTaskService.Forget(telemetryTask, "network", "Lazy network telemetry");
        });
        if (!accepted && _networkTelemetryShellQueuedGeneration == generation &&
            ReferenceEquals(_networkTelemetryShellHost, host))
        {
            _networkTelemetryShellQueued = false;
            _networkTelemetryShellQueuedGeneration = -1;
        }
    }

    private async Task LoadNetworkTelemetryAsync(StackPanel body, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            // All three nominally async services can perform synchronous cache
            // reads / native initialization before their first await. Those
            // cold costs belong on a worker, not the WinUI dispatcher.
            var (nic, latency, caps) = await Task.Run(async () =>
            {
                var nicTask = SystemInsightService.GetActiveNetworkAsync(token);
                var capsTask = PerformanceProfilesService.GetNicCapabilitiesAsync(token);
                var latencyTask = NetworkDiagnosticsService.MeasureAsync(samples: 4, token: token);
                await Task.WhenAll(nicTask, latencyTask).ConfigureAwait(false);
                var nic = await nicTask.ConfigureAwait(false);
                var latency = await latencyTask.ConfigureAwait(false);
                IReadOnlyDictionary<string, string>? caps = null;
                try { caps = await capsTask.ConfigureAwait(false); }
                catch (Exception capEx) { LogService.Error("network", "Telemetry NIC capability read failed", capEx); }
                return (nic, latency, caps);
            }, token).ConfigureAwait(false);
            string rssState = caps != null && caps.TryGetValue("RSS", out var rssValue) ? rssValue : "Neznámé";
            string rscState = caps != null && caps.TryGetValue("RSC", out var rscValue) ? rscValue : "Neznámé";
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "network" ||
                !ReferenceEquals(_networkTelemetryBody, body)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "network" ||
                    !ReferenceEquals(_networkTelemetryBody, body)) return;
                body.Children.Clear();
                if (nic == null) body.Children.Add(new TextBlock { Text = "Aktivní síťový adaptér nebyl nalezen.", Foreground = TextSecondaryBrush() });
                else
                {
                    bool rssEnabled = rssState.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                    bool rscEnabled = rscState.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                    string rssDisplay = rssEnabled ? "Zapnuto" : rssState.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? "Vypnuto" : rssState;
                    string rscDisplay = rscEnabled ? "Zapnuto" : rscState.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? "Vypnuto" : rscState;
                    string rssDetail = rssEnabled && nic.RssQueues > 0
                        ? $"{nic.RssQueues} front"
                        : rssDisplay;

                    // Reflow network status metrics at the actual content width.
                    // Four hardwired columns truncated labels in compact windows.
                    var speedTile = BuildNetworkMetricTile("Rychlost", $"{nic.SpeedMbps:0} Mb/s", nic.Description, "SpankySuccessBrush");
                    var pingTile = BuildNetworkMetricTile("Ping", latency.Received == 0 ? "N/A" : $"{latency.AvgMs:0.0} ms",
                        latency.Received == 0 ? "Bez ICMP odpovědi" : $"jitter {latency.JitterMs:0.0} ms • loss {latency.LossPercent:0.0} %");
                    var mtuTile = BuildNetworkMetricTile("MTU", nic.Mtu.ToString(), "DNS zůstává oddělené");
                    var rssTile = BuildNetworkMetricTile("RSS", rssDisplay, rssDetail,
                        rssEnabled ? "SpankySuccessBrush" : "SpankyWarningBrush");
                    body.Children.Add(BuildDashboardMetricGrid(
                        speedTile, pingTile, mtuTile, rssTile));

                    var detailRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 3, 0, 0) };
                    detailRow.Children.Add(Badge($"RSC {rscDisplay}", rscEnabled ? "SpankySuccessBrush" : "SpankyInfoBrush"));
                    detailRow.Children.Add(Badge($"{nic.DriverProvider} {nic.DriverVersion}".Trim(), "SpankyInfoBrush"));
                    body.Children.Add(detailRow);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Lazy network telemetry failed", ex);
            if (!token.IsCancellationRequested) DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "network" ||
                    !ReferenceEquals(_networkTelemetryBody, body)) return;
                body.Children.Clear();
                body.Children.Add(new TextBlock
                {
                    Text = "Aktuální stav sítě se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                body.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadNetworkTelemetryAsync(body, _loadCts?.Token ?? _windowLifetimeCts.Token)));
                ShowActionFeedback("Síť — připojení", ActionFeedbackKind.Failed, ex.Message);
            });
        }
    }

    private Task BuildStorageHealthAsync(CancellationToken token)
    {
        var body = new StackPanel { Spacing = 8 };
        _storageHealthBody = body;
        body.Children.Add(new TextBlock { Text = "Načítám stav NVMe / SSD…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("storage", "NVMe / SSD health", "Stav disků bez zbytečných systémových zásahů", body));
        SafeTaskService.Forget(LoadStorageHealthAsync(body, token), "storage", "Lazy storage health");
        return Task.CompletedTask;
    }

    private async Task LoadStorageHealthAsync(StackPanel body, CancellationToken token)
    {
        int generation = _navigationGeneration;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Task<IReadOnlyList<StorageDeviceInsight>> diskTask = SystemInsightService.GetStorageAsync(token);
        try
        {
            if (!diskTask.IsCompleted)
            {
                LogService.Info("performance", $"STORAGE_HEALTH_UI_FALLBACK waitMs={sw.ElapsedMilliseconds} immediate=true");
                RenderStorageHealth(body, Array.Empty<StorageDeviceInsight>(), BuildFastStorageFallback(), generation);
                SafeTaskService.Forget(CompleteStorageHealthAsync(diskTask, body, generation, token), "storage", "Deferred storage health details");
                return;
            }

            LogService.Info("performance", $"STORAGE_HEALTH_UI_READY waitMs={sw.ElapsedMilliseconds} cached=true");
            RenderStorageHealth(body, await diskTask.ConfigureAwait(false), null, generation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Storage insight failed", ex);
            if (!token.IsCancellationRequested && generation == _navigationGeneration && _section == "storage" && ReferenceEquals(_storageHealthBody, body))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "storage" || !ReferenceEquals(_storageHealthBody, body) || body.Parent == null) return;
                    body.Children.Clear();
                    body.Children.Add(new TextBlock
                    {
                        Text = "Stav NVMe / SSD se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    body.Children.Add(QuickButton("Načíst znovu", () =>
                        LoadStorageHealthAsync(body, token)));
                    ShowActionFeedback("NVMe / SSD health", ActionFeedbackKind.Failed, ex.Message);
                });
            }
        }
    }

    private async Task CompleteStorageHealthAsync(
        Task<IReadOnlyList<StorageDeviceInsight>> diskTask,
        StackPanel body,
        int generation,
        CancellationToken token)
    {
        try
        {
            var disks = await diskTask.ConfigureAwait(false);
            if (!token.IsCancellationRequested &&
                generation == _navigationGeneration &&
                _section == "storage" &&
                ReferenceEquals(_storageHealthBody, body))
                RenderStorageHealth(body, disks, null, generation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Error("Deferred storage insight failed", ex);
            if (!token.IsCancellationRequested && generation == _navigationGeneration && _section == "storage" && ReferenceEquals(_storageHealthBody, body))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "storage" || !ReferenceEquals(_storageHealthBody, body) || body.Parent == null) return;
                    body.Children.Clear();
                    body.Children.Add(new TextBlock
                    {
                        Text = "Detailní stav disků se nepodařilo dokončit: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    body.Children.Add(QuickButton("Načíst znovu", () =>
                        LoadStorageHealthAsync(body, token)));
                });
            }
        }
    }
    private static string BuildFastStorageFallback() =>
        "Detailní stav disků se načítá na pozadí.";

    private void RenderStorageHealth(
        StackPanel body,
        IReadOnlyList<StorageDeviceInsight> disks,
        string? fallback,
        int generation)
    {
        if (generation != _navigationGeneration ||
            _section != "storage" ||
            !ReferenceEquals(_storageHealthBody, body)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _navigationGeneration ||
                _section != "storage" ||
                !ReferenceEquals(_storageHealthBody, body)) return;
            body.Children.Clear();
            if (disks.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = fallback ?? "Detailní stav disků se načítá na pozadí.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
                return;
            }

            int healthyCount = disks.Count(x => x.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase));
            int trimCount = disks.Count(x => x.TrimEnabled);
            double hottest = disks.Where(x => x.TemperatureC.HasValue && x.TemperatureC.Value > 0)
                .Select(x => x.TemperatureC!.Value)
                .DefaultIfEmpty(0)
                .Max();
            body.Children.Add(BuildDashboardMetricGrid(
                BuildDashboardMetric("Disky", disks.Count.ToString(), "Detekované jednotky"),
                BuildDashboardMetric("Healthy", $"{healthyCount}/{disks.Count}", "Storage health", healthyCount == disks.Count ? "SpankySuccessBrush" : "SpankyWarningBrush"),
                BuildDashboardMetric("TRIM", $"{trimCount}/{disks.Count}", "Zapnuto", trimCount == disks.Count ? "SpankySuccessBrush" : "SpankyWarningBrush"),
                BuildDashboardMetric("Teplota", hottest > 0 ? $"{hottest:0} °C" : "N/A", "Nejvyšší dostupná")));

            foreach (var d in disks)
            {
                var item = new StackPanel { Spacing = 4 };
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                title.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
                title.Children.Add(Badge(d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? "HEALTHY" : d.Health.ToUpperInvariant(), d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? "SpankySuccessBrush" : "SpankyWarningBrush"));
                item.Children.Add(title);
                item.Children.Add(new TextBlock { Text = $"{d.MediaType} • {d.BusType} • {d.SizeGb:0.0} GB • {d.Operational}", FontSize = 11, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
                string temp = d.TemperatureC.HasValue && d.TemperatureC.Value > 0 ? $" • {d.TemperatureC.Value:0} °C" : string.Empty;
                string firmware = string.IsNullOrWhiteSpace(d.Firmware) ? "FW N/A" : "FW " + d.Firmware;
                item.Children.Add(new TextBlock { Text = $"TRIM {(d.TrimEnabled ? "zapnutý" : "vypnutý")} • {firmware}{temp} • Driver {d.DriverProvider} {d.DriverVersion}", FontSize = 10.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = item });
            }
        });
    }

    private async Task BuildHardwareRecommendedPanelAsync(string section, IReadOnlyList<TweakDefinition> tweaks, CancellationToken token)
    {
        if (section is not ("memory" or "gpu" or "network" or "storage")) return;
        if (section == "memory" && _memoryRecommendedPanel != null && DateTime.UtcNow - _memoryRecommendedAt < TimeSpan.FromSeconds(20))
        {
            ContentStack.Children.Add(_memoryRecommendedPanel);
            return;
        }
        var plan = await RecommendedEngine.BuildAsync(tweaks, token);
        if (token.IsCancellationRequested || _section != section) return;
        var map = tweaks.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        int eligible = plan.Decisions.Count(x => x.Eligible && x.Enable);
        int active = plan.Decisions.Count(x => x.Eligible && x.Enable && map.TryGetValue(x.TweakId, out var t) && t.Current);
        int pending = plan.Decisions.Count(x => x.Eligible && x.Enable && map.TryGetValue(x.TweakId, out var t) && !t.Current);
        int skipped = plan.Decisions.Count(x => !x.Eligible && x.Tier is "Protected" or "AutoProtected" or "Blocked");
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(new TextBlock { Text = $"DOPORUČENO {eligible}   •   UŽ SPRÁVNĚ {active}   •   K APLIKACI {pending}   •   PŘESKOČENO {skipped}", FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        foreach (var d in plan.Decisions.Where(x => x.Eligible || x.Tier is "Protected" or "AutoProtected").Take(8))
        {
            string name = map.TryGetValue(d.TweakId, out var t) ? t.Name : d.TweakId;
            body.Children.Add(new TextBlock { Text = $"{(d.Eligible ? "✓" : "—")} {name} — {d.Reason}", FontSize = 10.5, Foreground = d.Eligible ? TextSecondaryBrush() : UiBrush("SpankyWarningBrush"), TextWrapping = TextWrapping.Wrap });
        }
        if (section == "memory")
            body.Children.Add(QuickButton("BIOS / RAM / ReBAR – zkontrolovat", ShowFirmwareReadinessAsync));
        var panel = BuildSectionSurface(section, "Doporučení podle hardwaru", plan.HardwareSummary, body);
        if (section == "memory")
        {
            _memoryRecommendedPanel = panel;
            _memoryRecommendedAt = DateTime.UtcNow;
        }
        ContentStack.Children.Add(panel);
    }

    private FrameworkElement InsightRow(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, FontSize = 10.5, CharacterSpacing = 80, Foreground = TextSecondaryBrush(), FontWeight = FontWeights.SemiBold });
        var text = new TextBlock { Text = value, FontSize = 11.5, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }

}
