using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private readonly record struct DeferredTweakPlan(TweakDefinition[] Immediate, TweakDefinition[] Lazy);

    // GPU and Storage page cache rebuilds share the same deferred sequence:
    // split lazy detection, detect the rest, then queue lazy work after settle.
    private static DeferredTweakPlan SplitDeferredTweaks(TweakDefinition[] tweaks)
    {
        if (tweaks.Length == 0)
            return new DeferredTweakPlan(Array.Empty<TweakDefinition>(), Array.Empty<TweakDefinition>());

        var immediate = new List<TweakDefinition>(tweaks.Length);
        var lazy = new List<TweakDefinition>();
        foreach (TweakDefinition tweak in tweaks)
        {
            if (tweak.LazyDetect) lazy.Add(tweak);
            else immediate.Add(tweak);
        }
        return new DeferredTweakPlan(immediate.ToArray(), lazy.ToArray());
    }

    private async Task DetectDeferredTweaksAsync(
        string cacheKey, TweakDefinition[] immediate, bool detect, CancellationToken token)
    {
        if (!detect) return;
        if (immediate.Length > 0)
            await _engine.DetectAsync(immediate, token);
        _tweakCacheTime[cacheKey] = DateTime.UtcNow;
    }

    private void QueueLazyTweaks(string section, TweakDefinition[] lazy, CancellationToken token)
    {
        if (lazy.Length == 0) return;
        SafeTaskService.Forget(
            DetectLazyTweaksAfterSettleAsync(section, lazy, token, _navigationGeneration),
            "tweaks",
            "Deferred lazy " + (section == "gpu" ? "GPU" : "Storage") + " detect");
    }
}
