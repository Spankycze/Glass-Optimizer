using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _networkPageCache;
    private StackPanel? _networkTelemetryShellHost;
    private bool _networkTelemetryShellQueued;
    private StackPanel? _networkTelemetryBody;
    private StackPanel? _networkLiveBody;
    private StackPanel? _networkTweakHost;
    private bool _networkTweakQueued;
    private TextBlock? _networkRealtekSummary;
    private Border? _networkDriverCard;
    private CancellationToken _networkDriverActionToken;
    private DateTime _networkCachedPanelsAt;
    private DateTime _networkPageCacheUsedAt;
    private long _networkPageRevisionStamp;

    private bool TryAttachNetworkPageCache(CancellationToken token, out bool refreshStarted)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        refreshStarted = false;
        if (_networkPageCache == null) return false;
        if (_networkAdvancedBody == null)
        {
            InvalidateNetworkPageCache();
            return false;
        }

        foreach (var element in _networkPageCache) AttachCachedElement(element);
        long attachMs = perf.ElapsedMilliseconds;

        RefreshCardsIfTweakRevisionChanged(ref _networkPageRevisionStamp);
        long refreshCardsMs = perf.ElapsedMilliseconds - attachMs;

        if (_networkHeroArtHost is { Children.Count: 0 })
            ScheduleNetworkHeroIllustration(token);
        if (_networkDnsPanelHost is { Children.Count: 0 })
            ScheduleNetworkDnsControlPanel(token);
        if (_networkTelemetryShellHost is { Children.Count: 0 })
            ScheduleNetworkTelemetryShell(token);
        if (_networkTweakHost is { Children.Count: 0 })
            ScheduleNetworkTweakCards(token);
        if (_networkAdvancedBody != null && _networkAdvancedBody.Children.Count < 3)
            ScheduleNetworkAdvancedControls(_networkAdvancedBody);
        long scheduleMs = perf.ElapsedMilliseconds - attachMs - refreshCardsMs;

        var now = DateTime.UtcNow;
        _networkPageCacheUsedAt = now;
        if (now - _networkCachedPanelsAt >= TimeSpan.FromSeconds(20))
        {
            _networkCachedPanelsAt = now;
            RefreshNetworkCachedPanels(token);
            refreshStarted = true;
        }
        long refreshPanelsMs = perf.ElapsedMilliseconds - attachMs - refreshCardsMs - scheduleMs;

        if (perf.ElapsedMilliseconds >= 3)
            LogService.Info("performance",
                $"NETWORK_CACHE_ATTACH attachMs={attachMs} refreshCardsMs={refreshCardsMs} scheduleMs={scheduleMs} refreshPanelsMs={refreshPanelsMs} totalMs={perf.ElapsedMilliseconds}");
        return true;
    }
    private void CaptureNetworkPageCache()
    {
        _networkPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _networkCachedPanelsAt = DateTime.UtcNow;
        _networkPageCacheUsedAt = DateTime.UtcNow;
        _networkPageRevisionStamp = CurrentTweakRevisionStamp();
    }

    private void RefreshNetworkCachedPanels(CancellationToken token)
    {
        if (_networkTelemetryBody != null)
            SafeTaskService.Forget(LoadNetworkTelemetryAsync(_networkTelemetryBody, token), "network", "Cached network telemetry");
        if (_networkLiveBody != null)
            SafeTaskService.Forget(LoadNetworkLiveStatusAsync(_networkLiveBody, token), "network", "Cached network live status");
        if (_networkRealtekSummary != null)
            SafeTaskService.Forget(LoadDriverSummaryAsync(_networkRealtekSummary, token), "network", "Cached Realtek summary");
    }

    private void RefreshNetworkLiveAfterMutation(CancellationToken token)
    {
        if (token.IsCancellationRequested || _section != "network") return;

        _networkCachedPanelsAt = DateTime.UtcNow;
        SafeTaskService.Forget(HydrateNetworkHeroAsync(token), "network", "Refresh network hero after mutation");
        if (_networkTelemetryBody != null)
            SafeTaskService.Forget(LoadNetworkTelemetryAsync(_networkTelemetryBody, token), "network", "Refresh telemetry after mutation");
        if (_networkLiveBody != null)
            SafeTaskService.Forget(LoadNetworkLiveStatusAsync(_networkLiveBody, token), "network", "Refresh live status after mutation");
    }

    private void InvalidateNetworkPageCache()
    {
        _networkPageCache = null;
        _networkTelemetryShellHost = null;
        _networkTelemetryShellQueued = false;
        _networkTelemetryBody = null;
        _networkLiveBody = null;
        _networkDnsPanelHost = null;
        _networkDnsPanelQueued = false;
        _networkDnsChartHost = null;
        _networkDnsStatus = null;
        _networkHeroState = null;
        _networkHeroArtHost = null;
        _networkHeroArtQueued = false;
        _networkMtuStatus = null;
        _networkTweakHost = null;
        _networkTweakQueued = false;
        _networkToolsRow = null;
        _networkToolsCard = null;
        _networkToolsQueued = false;
        _networkAdvancedBody = null;
        _networkAdvancedLoadGeneration++;
        _networkAdvancedLoadQueued = false;
        _networkRealtekSummary = null;
        _networkDriverCard = null;
        _networkDriverActionToken = default;
        _networkCachedPanelsAt = default;
        _networkPageCacheUsedAt = default;
        _networkPageRevisionStamp = 0;
    }
}

