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
    internal async Task RunOptimizationPlanSmokeAsync(CancellationToken token = default)
    {
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var all = new List<TweakDefinition>();
        foreach (string section in new[] { "system", "gpu", "memory", "storage", "network", "ui", "update", "privacy" })
            all.AddRange(CatalogService.GetTweaks(section));
        foreach (string sub in new[] { "Služby", "Ovladače", "Task Scheduler" })
            all.AddRange(CatalogService.GetTweaks("components", sub));
        all = all.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        await _engine.DetectAsync(all, token);
        long detectMs = phase.ElapsedMilliseconds;
        var plan = await RecommendedEngine.BuildAsync(all, token);
        long planMs = phase.ElapsedMilliseconds - detectMs;
        var map = all.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        int candidates = plan.Decisions.Count(x => x.Eligible && x.Enable && map.TryGetValue(x.TweakId, out var t) && !t.Current);
        int correct = plan.Decisions.Count(x => x.Eligible && x.Enable && map.TryGetValue(x.TweakId, out var t) && t.Current);
        int skipped = plan.Decisions.Count(x => !x.Eligible && x.Tier is "Protected" or "AutoProtected" or "Blocked");
        LogService.Info($"OPTIMIZATION_PLAN_SMOKE_OK candidates={candidates} correct={correct} skipped={skipped} decisions={plan.Decisions.Count} detectMs={detectMs} planMs={planMs}");
    }

}
