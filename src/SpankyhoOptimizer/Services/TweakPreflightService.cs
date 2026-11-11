using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record TweakPreflightIssue(string TweakId, string Name, string Severity, string Message);
public sealed record TweakPreflightReport(bool CanApply, IReadOnlyList<TweakPreflightIssue> Issues);

public static class TweakPreflightService
{
    private static readonly Lazy<bool> Administrator = new(WorkerPrivilegePolicy.IsAdministrator, true);

    public static void Warm()
    {
        var sw = Stopwatch.StartNew();
        _ = Administrator.Value;
        var capabilities = WindowsCapabilityService.Current;
        var sample = CatalogService.GetTweaks("system").Take(12).ToArray();
        var map = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var tweak in sample)
            map.TryAdd(tweak.Id, tweak);
        foreach (var tweak in sample)
        {
            _ = TweakVersionMatrixService.Check(tweak);
            _ = TweakCompatibilityService.CheckStatic(tweak, capabilities);
        }
        _ = TweakDependencyRulesService.Evaluate(map).ToArray();
        LogService.Info("performance", $"PREFLIGHT_WARM totalMs={sw.ElapsedMilliseconds} sample={sample.Length}");
    }

    // Synchronous, read-only fail-closed gate. The mutation engine calls it
    // BEFORE starting even backup I/O; full compatibility checks still run
    // alongside backup capture for already-valid batches.
    internal static IReadOnlyList<TweakPreflightIssue> FindBatchConflicts(
        IReadOnlyList<TweakDefinition> dirty, CancellationToken token)
    {
        var issues = new List<TweakPreflightIssue>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TweakDefinition tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            if (tweak is null || string.IsNullOrWhiteSpace(tweak.Id))
            {
                issues.Add(new("", "Neplatný tweak", "Block",
                    "Prázdná identita tweaku; dávka nebyla spuštěna."));
                continue;
            }
            if (!ids.Add(tweak.Id))
                issues.Add(new(tweak.Id, tweak.Name, "Block",
                    "Duplicitní identita tweaku v dávce; před použitím zkontroluj výběr."));
        }
        if (issues.Any(x => x.Severity == "Block"))
            return issues;

        var registryTargets = new Dictionary<string, TweakDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (TweakDefinition tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            if (tweak.RegistryHive is not { } hive ||
                string.IsNullOrWhiteSpace(tweak.RegistryPath) ||
                string.IsNullOrWhiteSpace(tweak.RegistryValueName))
                continue;
            string target = $"{(int)hive}|{tweak.RegistryPath.TrimEnd('\\')}|{tweak.RegistryValueName}";
            if (!registryTargets.TryAdd(target, tweak))
            {
                TweakDefinition previous = registryTargets[target];
                // Even identically enabled registry definitions can have
                // different disabled/original-value behavior. One physical
                // registry value must have only one owner in an apply batch;
                // a warning would still allow duplicate native writes.
                issues.Add(new(tweak.Id, tweak.Name, "Block",
                    $"Duplicitní cíl registru jako {previous.Id}; změny zablokovány."));
            }
        }
        return issues;
    }

    // Cheap, read-only admission for known unsupported Windows builds and
    // capabilities. Run before backup persistence; full hardware-dependent
    // validation still runs asynchronously after this early gate.
    internal static IReadOnlyList<TweakPreflightIssue> FindStaticCompatibilityBlocks(
        IReadOnlyList<TweakDefinition> dirty, CancellationToken token)
    {
        var issues = new List<TweakPreflightIssue>();
        foreach (TweakDefinition tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            var version = TweakVersionMatrixService.Check(tweak);
            if (!version.Supported)
                issues.Add(new(tweak.Id, tweak.Name, "Block", version.Message));
        }
        // A known unsupported Windows build needs neither expensive capability
        // discovery nor backup I/O. Reject it before probing the host.
        if (issues.Count != 0) return issues;

        var capabilities = WindowsCapabilityService.Current;
        foreach (TweakDefinition tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            var compatibility = TweakCompatibilityService.CheckStatic(tweak, capabilities);
            if (!compatibility.Supported)
                issues.Add(new(tweak.Id, tweak.Name, "Block", compatibility.Reason));
        }
        return issues;
    }

    // Validate selection-only dependencies before backup persistence or
    // hardware enumeration. The full preflight still independently validates
    // compatibility and reports non-blocking relationship warnings.
    internal static IReadOnlyList<TweakPreflightIssue> FindDependencyBlocks(
        IReadOnlyList<TweakDefinition> dirty, CancellationToken token)
    {
        var issues = new List<TweakPreflightIssue>();
        var map = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (TweakDefinition tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            if (tweak is null || string.IsNullOrWhiteSpace(tweak.Id) ||
                !map.TryAdd(tweak.Id, tweak))
            {
                issues.Add(new(tweak?.Id ?? "", tweak?.Name ?? "Neplatný tweak",
                    "Block", "Neplatná nebo duplicitní identita v dávce."));
            }
        }
        if (issues.Count != 0) return issues;

        foreach (TweakDefinition tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            if (!tweak.Pending) continue;

            foreach (string required in tweak.RequiresTweaks)
            {
                if (!map.TryGetValue(required, out var dependency) || !dependency.Pending)
                    issues.Add(new(tweak.Id, tweak.Name, "Block",
                        $"Vyžaduje aktivní tweak {required}."));
            }
            foreach (string conflict in tweak.ConflictsWithTweaks)
            {
                if (map.TryGetValue(conflict, out var other) && other.Pending)
                    issues.Add(new(tweak.Id, tweak.Name, "Block",
                        $"Konflikt s tweakem {conflict}."));
            }
        }

        issues.AddRange(TweakDependencyRulesService.Evaluate(map)
            .Where(issue => issue.Severity.Equals("Block", StringComparison.OrdinalIgnoreCase)));
        return issues;
    }

    public static async Task<TweakPreflightReport> ValidateAsync(TweakDefinition[] dirty, CancellationToken token)
    {
        var perf = Stopwatch.StartNew();
        var issues = new List<TweakPreflightIssue>();
        if (dirty.Length == 0) return new(true, issues);
        bool admin = Administrator.Value;
        long adminMs = perf.ElapsedMilliseconds;
        issues.AddRange(FindBatchConflicts(dirty, token));
        if (issues.Any(x => x.Severity == "Block"))
            return new(false, issues);
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
                issues.Add(new(tweak.Id, tweak.Name, "Warn", "Při použití změny bude vyžádáno potvrzení UAC."));
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

}
