using System;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    internal Task RunPageCacheLifetimeSmokeAsync()
    {
        InvalidateUiPageCache();
        if (_uiPageCache != null || _uiVirtualRowsCache != null || _uiVirtualMapCache != null || _contextMenuUiCache != null)
            throw new InvalidOperationException("UI cache lifetime cleanup incomplete.");

        InvalidateNetworkPageCache();
        if (_networkPageCache != null ||
            _networkTelemetryShellHost != null ||
            _networkTelemetryBody != null ||
            _networkLiveBody != null ||
            _networkTweakHost != null ||
            _networkToolsRow != null ||
            _networkToolsCard != null ||
            _networkDnsPanelHost != null ||
            _networkDnsChartHost != null ||
            _networkHeroArtHost != null ||
            _networkAdvancedBody != null ||
            _networkRealtekSummary != null ||
            _networkDriverCard != null)
            throw new InvalidOperationException("Network cache lifetime cleanup incomplete.");

        InvalidateStoragePageCache(includeStaticShell: true);
        if (_storagePageCache != null ||
            _storageStaticHero != null ||
            _storageStaticMetrics != null ||
            _storageSnapshotShellHost != null ||
            _storageTweakHost != null ||
            _storageToolsCard != null ||
            _storageToolsRow != null ||
            _storageMaintenanceCard != null ||
            _storageHealthBody != null ||
            _storageAdvancedBody != null)
            throw new InvalidOperationException("Storage cache lifetime cleanup incomplete.");

        InvalidateGpuPageCache(includeStaticShell: true);
        if (_gpuPageCache != null ||
            _gpuTweakHost != null ||
            _gpuDriverCardsHost != null ||
            _gpuStaticHero != null ||
            _gpuHeroActionsHost != null ||
            _gpuStatsHost != null ||
            _gpuStatsShell != null ||
            _gpuDriverCenterTile != null)
            throw new InvalidOperationException("GPU cache lifetime cleanup incomplete.");

        InvalidateDefenderPageCache(includeStaticShell: true);
        if (_defenderPageCache != null ||
            _defenderStaticHero != null ||
            _defenderStaticMetrics != null ||
            _defenderModulesHost != null ||
            _defenderActionsHost != null)
            throw new InvalidOperationException("Defender cache lifetime cleanup incomplete.");

        InvalidateSpicetifyPageCache();
        if (_spicetifyPageCache != null ||
            _spicetifyActionsBody != null ||
            _spicetifyTweaksBody != null ||
            _spicetifyExtensionsBody != null ||
            _spicetifyThemeBody != null ||
            _spicetifyCachedStatus != null)
            throw new InvalidOperationException("Spicetify cache lifetime cleanup incomplete.");

        InvalidateAppsPageCache();
        if (_appsPageCache != null ||
            _appsWingetStatus != null ||
            _appsDetectedHost != null ||
            _appsDeferredBody != null ||
            _appsLauncherCacheHost != null)
            throw new InvalidOperationException("Apps cache lifetime cleanup incomplete.");

        InvalidateSettingsPageCache();
        if (_settingsPageCache != null || _settingsDeferredBody != null)
            throw new InvalidOperationException("Settings cache lifetime cleanup incomplete.");

        InvalidateRestorePageCache();
        if (_restorePageCache != null ||
            _restoreHistoryHost != null ||
            _restoreBatchHost != null ||
            _restoreSurfaceHost != null)
            throw new InvalidOperationException("Restore cache lifetime cleanup incomplete.");

        InvalidateFixerPageCache();
        if (_fixerPageCache != null ||
            _fixerStaticCache != null ||
            _fixerAppHealthHost != null ||
            _fixerDiagnosticsHost != null ||
            _fixerStaticActionsHost != null)
            throw new InvalidOperationException("Fixer cache lifetime cleanup incomplete.");

        InvalidateBrowserPageCache();
        if ((_browserPageCachesStore?.Count ?? 0) != 0 ||
            (_browserVirtualRowsCacheStore?.Count ?? 0) != 0 ||
            (_browserVirtualMapCacheStore?.Count ?? 0) != 0)
            throw new InvalidOperationException("Browser cache lifetime cleanup incomplete.");

        InvalidateComponentsPageCache();
        if ((_componentsPageCacheStore?.Count ?? 0) != 0 ||
            _taskRowsCache != null ||
            _taskRowsSnapshot != null ||
            _driverRows != null ||
            _driverVirtualList != null ||
            _componentsContextCard != null)
            throw new InvalidOperationException("Components cache lifetime cleanup incomplete.");

        LogService.Info("CACHE_LIFETIME_SMOKE_OK ui network storage gpu defender spicetify apps settings restore fixer browser components");
        return Task.CompletedTask;
    }
}
