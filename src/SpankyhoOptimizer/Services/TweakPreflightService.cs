using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record TweakPreflightIssue(string TweakId, string Name, string Severity, string Message);
public sealed record TweakPreflightReport(bool CanApply, IReadOnlyList<TweakPreflightIssue> Issues);

public static class TweakPreflightService
{
    private static readonly Lazy<bool> Administrator = new(IsAdministrator, true);

    public static void Warm()
    {
        var sw = Stopwatch.StartNew();
        _ = Administrator.Value;
        var capabilities = WindowsCapabilityService.Current;
        var sample = CatalogService.GetTweaks("system").Take(12).ToArray();
        var map = sample.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var tweak in sample)
        {
            _ = TweakVersionMatrixService.Check(tweak);
            _ = TweakCompatibilityService.CheckStatic(tweak, capabilities);
        }
        _ = TweakDependencyRulesService.Evaluate(map).ToArray();
        LogService.Info("performance", $"PREFLIGHT_WARM totalMs={sw.ElapsedMilliseconds} sample={sample.Length}");
    }

    public static async Task<TweakPreflightReport> ValidateAsync(TweakDefinition[] dirty, CancellationToken token)
    {
        var perf = Stopwatch.StartNew();
        var issues = new List<TweakPreflightIssue>();
        if (dirty.Length == 0) return new(true, issues);
        bool admin = Administrator.Value;
        long adminMs = perf.ElapsedMilliseconds;
        var map = dirty.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        long mapMs = perf.ElapsedMilliseconds - adminMs;
        HardwareSnapshot? hardware = null;
        if (dirty.Any(x => !string.Equals(x.HardwareScope, "Any", StringComparison.OrdinalIgnoreCase)))
            hardware = await HardwareSnapshotService.GetAsync(false, token);
        long hardwareMs = perf.ElapsedMilliseconds - adminMs - mapMs;
        long loopStart = perf.ElapsedMilliseconds;

        foreach (var tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            if (tweak.RequiresAdmin && !admin)
                issues.Add(new(tweak.Id, tweak.Name, "Block", "Vyžaduje oprávnění správce."));
            var version = TweakVersionMatrixService.Check(tweak);
            if (!version.Supported)
                issues.Add(new(tweak.Id, tweak.Name, "Block", version.Message));
            else if (!version.Tested)
                issues.Add(new(tweak.Id, tweak.Name, "Warn", version.Message));
            var compatibility = TweakCompatibilityService.CheckWithSnapshot(tweak, hardware);
            if (!compatibility.Supported)
                issues.Add(new(tweak.Id, tweak.Name, "Block", compatibility.Reason));

            if (!tweak.Pending) continue;
            foreach (string required in tweak.RequiresTweaks)
            {
                if (!map.TryGetValue(required, out var dep) || !dep.Pending)
                    issues.Add(new(tweak.Id, tweak.Name, "Block", $"Vyžaduje aktivní tweak {required}."));
            }
            foreach (string conflict in tweak.ConflictsWithTweaks)
            {
                if (map.TryGetValue(conflict, out var other) && other.Pending)
                    issues.Add(new(tweak.Id, tweak.Name, "Block", $"Konflikt s tweakem {conflict}."));
            }
        }
        long loopMs = perf.ElapsedMilliseconds - loopStart;
        long depsStart = perf.ElapsedMilliseconds;
        issues.AddRange(TweakDependencyRulesService.Evaluate(map));
        long depsMs = perf.ElapsedMilliseconds - depsStart;
        bool canApply = issues.All(x => !x.Severity.Equals("Block", StringComparison.OrdinalIgnoreCase));
        LogService.Info("performance", $"PREFLIGHT_PHASE count={dirty.Length} adminMs={adminMs} mapMs={mapMs} hardwareMs={hardwareMs} loopMs={loopMs} depsMs={depsMs} totalMs={perf.ElapsedMilliseconds}");
        return new(canApply, issues);
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
