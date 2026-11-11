using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void ScheduleGpuTweakCards(CancellationToken token)
    {
        var host = _gpuTweakHost;
        if (host == null || ReferenceEquals(_gpuTweakQueuedHost, host) || host.Children.Count > 0) return;
        int generation = _navigationGeneration;
        _gpuTweakQueuedHost = host;

        var groups = new List<(string Name, List<TweakDefinition> Tweaks)>();
        var groupIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (TweakDefinition tweak in _tweaks)
        {
            if (!groupIndexes.TryGetValue(tweak.Group, out int index))
            {
                index = groups.Count;
                groupIndexes[tweak.Group] = index;
                groups.Add((tweak.Group, new List<TweakDefinition>()));
            }
            groups[index].Tweaks.Add(tweak);
        }
        int groupIndex = 0;
        int tweakIndex = -1;
        double totalUiMs = 0;
        int slices = 0;

        void QueueNext()
        {
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "gpu" ||
                    !ReferenceEquals(_gpuTweakHost, host))
                {
                    if (ReferenceEquals(_gpuTweakQueuedHost, host)) _gpuTweakQueuedHost = null;
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int processed = 0;
                // Do several cheap cards in one UI dispatch, but yield after
                // ~3 ms (or 6 elements) rather than monopolizing a frame.
                while (groupIndex < groups.Count && processed < 6 &&
                       (processed == 0 || sw.Elapsed.TotalMilliseconds < 3))
                {
                    var group = groups[groupIndex];
                    if (tweakIndex < 0)
                    {
                        host.Children.Add(CreateClassicTweakGroupHeader(group.Name, group.Tweaks.Count));
                        tweakIndex = 0;
                    }
                    else
                    {
                        host.Children.Add(CreateClassicTweakCard(group.Tweaks[tweakIndex++]));
                        if (tweakIndex >= group.Tweaks.Count)
                        {
                            groupIndex++;
                            tweakIndex = -1;
                        }
                    }
                    processed++;
                }
                slices++;
                totalUiMs += sw.Elapsed.TotalMilliseconds;
                if (groupIndex < groups.Count)
                    QueueNext();
                else
                {
                    if (ReferenceEquals(_gpuTweakQueuedHost, host)) _gpuTweakQueuedHost = null;
                    LogService.Info("performance",
                        $"GPU_TWEAKS_DEFERRED staged=true uiMs={totalUiMs:F2} slices={slices} count={CountClassicTweakCards(host)}");
                }
            }))
            {
                if (ReferenceEquals(_gpuTweakQueuedHost, host)) _gpuTweakQueuedHost = null;
            }
        }

        QueueNext();
    }

    private async Task CompleteGpuTweaksAfterFirstFrameAsync(string cacheKey, bool staleDetection, CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(35, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu") return;

            List<TweakDefinition> tweaks;
            if (!_tweakCache.TryGetValue(cacheKey, out tweaks!))
            {
                tweaks = await Task.Run(() => CatalogService.GetTweaks("gpu"), token).ConfigureAwait(false);
                _tweakCache[cacheKey] = tweaks;
            }

            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu") return;
            TweakDefinition[] snapshot = tweaks.ToArray();

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "gpu") return;

                _tweaks = tweaks;
                _gpuTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_gpuTweakHost);
                ScheduleGpuTweakCards(token);

                SafeTaskService.Forget(
                    RunGpuDeferredStateAfterSettleAsync(cacheKey, snapshot, staleDetection, token, generation),
                    "gpu",
                    "Deferred GPU state");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("gpu", "Deferred GPU catalog load failed", ex);
        }
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
            TweakDefinition[] immediate;
            TweakDefinition[] lazy;
            if (tweaks.Length == 0)
            {
                immediate = Array.Empty<TweakDefinition>();
                lazy = Array.Empty<TweakDefinition>();
            }
            else
            {
                var immediateList = new List<TweakDefinition>(tweaks.Length);
                var lazyList = new List<TweakDefinition>();
                foreach (TweakDefinition tweak in tweaks)
                {
                    if (tweak.LazyDetect) lazyList.Add(tweak);
                    else immediateList.Add(tweak);
                }
                immediate = immediateList.ToArray();
                lazy = lazyList.ToArray();
            }

            if (detect)
            {
                if (immediate.Length > 0)
                    await _engine.DetectAsync(immediate, token);
                _tweakCacheTime[cacheKey] = DateTime.UtcNow;
            }
            token.ThrowIfCancellationRequested();
            if (_section != "gpu") return;
            RefreshCards();
            await BuildHardwareRecommendedPanelAsync("gpu", tweaks, token);
            CaptureGpuPageCache();

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
