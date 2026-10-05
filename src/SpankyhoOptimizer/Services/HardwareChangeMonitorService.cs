using System;
using System.Management;

namespace SpankyhoOptimizer.Services;

public static class HardwareChangeMonitorService
{
    private static readonly object Gate = new();
    private static ManagementEventWatcher? _watcher;
    private static bool _started;
    private static bool _exitHooked;
    private static long _lastInvalidationTick;
    private static long _startedTick;

    public static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            try
            {
                _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent"));
                _watcher.EventArrived += OnDeviceChanged;
                _watcher.Start();
                _startedTick = Environment.TickCount64;
                if (!_exitHooked)
                {
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    _exitHooked = true;
                }
                _started = true;
                LogService.Info("hardware", "PnP change monitor started");
            }
            catch (Exception ex) { LogService.Error("hardware", "PnP change monitor unavailable", ex); }
        }
    }
    private static void OnProcessExit(object? sender, EventArgs e) => Stop();

    private static void OnDeviceChanged(object sender, EventArrivedEventArgs e)
    {
        long now = Environment.TickCount64;
        long previous = System.Threading.Interlocked.Exchange(ref _lastInvalidationTick, now);
        if (previous != 0 && now - previous < 500) return;
        try
        {
            GpuDriverService.InvalidateDetectionCache();
            PerformanceProfilesService.InvalidateNicCache();
            SystemInsightService.InvalidateNetwork(dropPersistent: false, invalidateAnalyzer: false);
            SystemInsightService.InvalidateStorage(dropPersistent: false);
            bool startupSettle = _startedTick != 0 && now - _startedTick < 1500;
            HardwareSnapshotService.InvalidateVolatile(dropPersistentDevices: !startupSettle);
            DriverInventoryService.Invalidate();
            LogService.Info(
                "hardware",
                startupSettle
                    ? "PnP startup-settle event -> volatile caches invalidated; persistent present-device inventory retained"
                    : "PnP change -> volatile hardware caches invalidated; persistent present-device inventory dropped");
        }
        catch (Exception ex) { LogService.Error("hardware", "PnP invalidation failed", ex); }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started) return;
            try { _watcher?.Stop(); } catch (Exception ex) { LogService.Error("hardware", "PnP monitor stop failed", ex); }
            try { if (_watcher != null) _watcher.EventArrived -= OnDeviceChanged; } catch { }
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            _started = false;
        }
    }
}
