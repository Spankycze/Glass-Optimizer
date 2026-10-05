using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Task<GpuDriverInfo?>? _gpuPersistentWarmTask;
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
        _gpuDeferredDetectionTask = null;
        _gpuOfficialRelease = null;
        _gpuExostenzaRelease = null;
        _gpuDownloadedPackage = null;
        InvalidateGpuPageCache();
        LogService.Info("gpu", $"GPU cache generation synchronized generation={generation}");
    }

    private async Task<GpuDriverInfo?> GetOverviewGpuDriverAsync(System.Threading.CancellationToken token)
    {
        SynchronizeGpuDetectionGeneration();
        long generation = _gpuDetectionGeneration;
        if (generation == GpuDriverService.DetectionCacheGeneration && _gpuInfo != null)
            return _gpuInfo;

        _gpuPersistentWarmTask ??= Task.Run(async () =>
            await GpuDriverService.TryWarmPersistentDetectionCacheAsync().ConfigureAwait(false));

        var persisted = await _gpuPersistentWarmTask.WaitAsync(token).ConfigureAwait(false);
        if (generation != GpuDriverService.DetectionCacheGeneration) return null;
        if (persisted != null)
        {
            _gpuInfo ??= persisted;
            return persisted;
        }

        var detected = await GpuDriverService.DetectPrimaryGpuAsync(token).ConfigureAwait(false);
        if (generation != GpuDriverService.DetectionCacheGeneration) return null;
        if (detected != null) _gpuInfo = detected;
        return detected;
    }

    private async Task PrewarmGpuDetectionAfterFirstRenderAsync()
    {
        await Task.Delay(180).ConfigureAwait(false);
        SynchronizeGpuDetectionGeneration();
        long generation = _gpuDetectionGeneration;
        if (_section != "overview" || _gpuInfo != null || generation != GpuDriverService.DetectionCacheGeneration) return;

        _gpuPersistentWarmTask ??= Task.Run(async () =>
            await GpuDriverService.TryWarmPersistentDetectionCacheAsync().ConfigureAwait(false));
        var persisted = await _gpuPersistentWarmTask.ConfigureAwait(false);
        if (generation != GpuDriverService.DetectionCacheGeneration) return;
        if (persisted != null)
        {
            _gpuInfo = persisted;
            return;
        }

        // Persistent-cache miss is rare and the live probe is comparatively expensive.
        // Keep it out of the first seconds after startup and only run it if Overview is still idle.
        await Task.Delay(1800).ConfigureAwait(false);
        if (_section != "overview" || _gpuInfo != null || generation != GpuDriverService.DetectionCacheGeneration) return;
        var detected = await GpuDriverService.DetectPrimaryGpuAsync().ConfigureAwait(false);
        if (generation == GpuDriverService.DetectionCacheGeneration && detected != null) _gpuInfo = detected;
    }
}
