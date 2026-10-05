using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void ScheduleGpuTweakCards(CancellationToken token)
    {
        if (_gpuTweakQueued || _gpuTweakHost == null || _gpuTweakHost.Children.Count > 0) return;
        _gpuTweakQueued = true;

        var groups = _tweaks
            .GroupBy(t => t.Group)
            .Select(g => (Name: g.Key, Tweaks: g.ToArray()))
            .ToArray();
        int groupIndex = 0;
        int tweakIndex = -1;
        long totalUiMs = 0;

        void QueueNext()
        {
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "gpu" || _gpuTweakHost == null)
                {
                    _gpuTweakQueued = false;
                    return;
                }

                if (groupIndex >= groups.Length)
                {
                    _gpuTweakQueued = false;
                    LogService.Info("performance", $"GPU_TWEAKS_DEFERRED staged=true uiMs={totalUiMs} count={CountClassicTweakCards(_gpuTweakHost)}");
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var group = groups[groupIndex];
                if (tweakIndex < 0)
                {
                    _gpuTweakHost.Children.Add(CreateClassicTweakGroupHeader(group.Name, group.Tweaks.Length));
                    tweakIndex = 0;
                    long stageMs = sw.ElapsedMilliseconds;
                    totalUiMs += stageMs;
                    LogService.Info("performance", $"GPU_TWEAKS_STAGE stage=header group={groupIndex} ms={stageMs}");
                    QueueNext();
                    return;
                }

                _gpuTweakHost.Children.Add(CreateClassicTweakCard(group.Tweaks[tweakIndex]));
                int addedIndex = tweakIndex++;
                long cardMs = sw.ElapsedMilliseconds;
                totalUiMs += cardMs;
                LogService.Info("performance", $"GPU_TWEAKS_STAGE stage=card group={groupIndex} index={addedIndex} ms={cardMs}");

                if (tweakIndex >= group.Tweaks.Length)
                {
                    groupIndex++;
                    tweakIndex = -1;
                }
                QueueNext();
            }))
                _gpuTweakQueued = false;
        }

        QueueNext();
    }

    private async Task RunGpuDeferredStateAfterSettleAsync(string cacheKey, TweakDefinition[] tweaks, bool detect, CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(75, token);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu") return;
            await BuildGpuDeferredStateAsync(cacheKey, tweaks, detect, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task BuildGpuDeferredStateAsync(string cacheKey, TweakDefinition[] tweaks, bool detect, CancellationToken token)
    {
        try
        {
            if (detect)
            {
                var immediate = tweaks.Where(x => !x.LazyDetect).ToArray();
                if (immediate.Length > 0)
                    await _engine.DetectAsync(immediate, token);
                _tweakCacheTime[cacheKey] = DateTime.UtcNow;
            }
            token.ThrowIfCancellationRequested();
            if (_section != "gpu") return;
            RefreshCards();
            await BuildHardwareRecommendedPanelAsync("gpu", tweaks, token);
            CaptureGpuPageCache();

            var lazy = tweaks.Where(x => x.LazyDetect).ToArray();
            if (lazy.Length > 0)
                SafeTaskService.Forget(
                    DetectLazyTweaksAfterSettleAsync("gpu", lazy, token, _navigationGeneration),
                    "tweaks",
                    "Deferred lazy GPU detect");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("gpu", "Deferred GPU state failed", ex); }
    }
}
