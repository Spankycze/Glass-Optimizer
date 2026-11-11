using System;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Task<GpuDriverInfo?>? _gpuPersistentWarmTask;
    private DateTime _gpuPersistentWarmStartedUtc = DateTime.MinValue;
    private static readonly TimeSpan GpuWindowWarmTtl = TimeSpan.FromMinutes(10);

    // An in-flight read is shared even during navigation changes. A completed
    // result is reusable only inside the same bounded window; faults and
    // cancellations must always be retried.
    private static Task<GpuDriverInfo?> GetOrStartWindowGpuWarmFlight(
        Task<GpuDriverInfo?>? flight, DateTime startedUtc, DateTime nowUtc,
        Func<Task<GpuDriverInfo?>> start, out bool started)
    {
        if (flight != null && !flight.IsFaulted && !flight.IsCanceled &&
            (!flight.IsCompleted ||
             (nowUtc >= startedUtc && nowUtc - startedUtc < GpuWindowWarmTtl)))
        {
            started = false;
            return flight;
        }
        started = true;
        return start();
    }

    internal static string RunGpuWindowWarmFlightPolicySmoke()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        int readStarts = 0;
        var incomplete = new TaskCompletionSource<GpuDriverInfo?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GpuDriverInfo?> Start() { readStarts++; return incomplete.Task; }
        var a = GetOrStartWindowGpuWarmFlight(null, DateTime.MinValue, now, Start, out bool started);
        if (!started || readStarts != 1 ||
            !ReferenceEquals(a, GetOrStartWindowGpuWarmFlight(a, now, now.AddMinutes(11), Start, out started)) ||
            started || readStarts != 1)
            throw new InvalidOperationException("Concurrent window GPU cache reads were not shared.");
        incomplete.SetResult(null);
        if (!ReferenceEquals(a, GetOrStartWindowGpuWarmFlight(a, now, now.AddMinutes(9), Start, out started)) ||
            started)
            throw new InvalidOperationException("Window GPU cache miss was not reused inside TTL.");

        var replacement = GetOrStartWindowGpuWarmFlight(a, now, now.AddMinutes(11),
            () => Task.FromResult<GpuDriverInfo?>(null), out started);
        if (!started || ReferenceEquals(a, replacement))
            throw new InvalidOperationException("Expired window GPU cache miss was not refreshed.");

        var faulted = Task.FromException<GpuDriverInfo?>(new InvalidOperationException("synthetic"));
        _ = GetOrStartWindowGpuWarmFlight(faulted, now, now.AddMinutes(1),
            () => Task.FromResult<GpuDriverInfo?>(null), out started);
        if (!started)
            throw new InvalidOperationException("Faulted window GPU warm flight was reused.");
        var cancelled = Task.FromCanceled<GpuDriverInfo?>(new System.Threading.CancellationToken(true));
        _ = GetOrStartWindowGpuWarmFlight(cancelled, now, now.AddMinutes(1),
            () => Task.FromResult<GpuDriverInfo?>(null), out started);
        if (!started)
            throw new InvalidOperationException("Cancelled window GPU warm flight was reused.");
        return "GPU_WINDOW_WARM_FLIGHT_POLICY_SMOKE_OK inflightShared=true missTtl=true expiredRefresh=true faultRetry=true cancelRetry=true hardwareUnchanged=true";
    }

    // Keep GPU service/type initialization out of the MainWindow cold-construction path.
    // Generation is synchronized only when deferred Overview/GPU work actually needs it.
    private long _gpuDetectionGeneration;

    private void SynchronizeGpuDetectionGeneration()
    {
        long generation = GpuDriverService.DetectionCacheGeneration;
        if (_gpuDetectionGeneration == generation) return;

        _gpuDetectionGeneration = generation;
        _gpuInfo = null;
        _gpuPersistentWarmTask = null;
        _gpuPersistentWarmStartedUtc = DateTime.MinValue;
        _gpuDeferredDetectionTask = null;
        _gpuOfficialRelease = null;
        _gpuExostenzaRelease = null;
        _gpuDownloadedPackage = null;
        InvalidateGpuPageCache();
        LogService.Info("gpu", $"GPU cache generation synchronized generation={generation}");
    }

    private async Task<GpuDriverInfo?> GetOverviewGpuDriverAsync(System.Threading.CancellationToken token)
    {
        if (!DispatcherQueue.HasThreadAccess)
            throw new System.InvalidOperationException("Overview GPU driver state must be accessed on UI dispatcher.");
        SynchronizeGpuDetectionGeneration();
        long generation = _gpuDetectionGeneration;
        if (generation == GpuDriverService.DetectionCacheGeneration && _gpuInfo != null)
            return _gpuInfo;

        DateTime nowUtc = DateTime.UtcNow;
        _gpuPersistentWarmTask = GetOrStartWindowGpuWarmFlight(
            _gpuPersistentWarmTask, _gpuPersistentWarmStartedUtc, nowUtc,
            () => GpuDriverService.TryWarmPersistentDetectionCacheAsync(), out bool newRead);
        if (newRead) _gpuPersistentWarmStartedUtc = nowUtc;
        GpuDriverInfo? persisted;
        try
        {
            persisted = await _gpuPersistentWarmTask.WaitAsync(token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            // The persisted cache is optional; a corrupt/faulted cache read
            // must still allow the live GPU detector to recover.
            LogService.Error("Overview persisted GPU warm read failed, trying live detection", ex);
            persisted = null;
        }
        if (!DispatcherQueue.HasThreadAccess)
            throw new System.InvalidOperationException("Overview GPU cache state must resume on UI dispatcher.");
        token.ThrowIfCancellationRequested();
        if (generation != GpuDriverService.DetectionCacheGeneration) return null;
        if (persisted != null)
        {
            _gpuInfo ??= persisted;
            return persisted;
        }

        var detected = await GpuDriverService.DetectPrimaryGpuAsync(token);
        if (!DispatcherQueue.HasThreadAccess)
            throw new System.InvalidOperationException("Overview GPU detection state must resume on UI dispatcher.");
        token.ThrowIfCancellationRequested();
        if (generation != GpuDriverService.DetectionCacheGeneration) return null;
        if (detected != null) _gpuInfo = detected;
        return detected;
    }

    private void QueueGpuIntroPrewarm()
    {
        if (_windowLifetimeCts.IsCancellationRequested || _section != "overview" || !UseCompactSectionChrome || _gpuStaticHero != null) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_windowLifetimeCts.IsCancellationRequested || _section != "overview" || !UseCompactSectionChrome || _gpuStaticHero != null) return;
            var actions = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8 };
            _gpuStaticHero = BuildNeutralSectionIntro("Graphics", "GPU, ovladače a herní grafická nastavení na jednom místě.", actions);
            _gpuHeroActionsHost = actions;
        });
    }
}
